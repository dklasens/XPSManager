param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part numeric version.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release = Join-Path $repo 'artifacts/release'
$package = Join-Path $repo ('artifacts/release-package/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $release,$package -Force | Out-Null
$msiName = "XpsManager-$Version-win-x64.msi"
$zipName = "XpsManager-$Version-win-x64.zip"
Copy-Item -LiteralPath (Join-Path $repo "installer/bin/Release/$msiName") -Destination $package
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $repo "docs/releases/v$Version.md") -Destination (Join-Path $release 'RELEASE-NOTES.md')
$msiHash = (Get-FileHash -LiteralPath (Join-Path $package $msiName) -Algorithm SHA256).Hash.ToLowerInvariant()
"$msiHash  $msiName" | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt') -Encoding ascii
@{
    version = $Version
    commit = $env:GITHUB_SHA
    run = if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null }
    signed = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'build-info.json') -Encoding utf8
Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $release $zipName) -Force
# Validate the archive's actual installer bytes, not just the presence of a .zip file.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $release $zipName))
try {
    $entry = $archive.GetEntry($msiName)
    if (!$entry) { throw 'Release ZIP does not contain the MSI at its root.' }
    $stream = $entry.Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $archivedHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
    if ($archivedHash -ne $msiHash) { throw 'The installer in the ZIP does not match the built MSI.' }
} finally { $archive.Dispose() }
Copy-Item -LiteralPath (Join-Path $package $msiName),(Join-Path $package 'build-info.json') -Destination $release
$zipHash = (Get-FileHash -LiteralPath (Join-Path $release $zipName) -Algorithm SHA256).Hash.ToLowerInvariant()
@("$msiHash  $msiName", "$zipHash  $zipName") | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Output "PASS: packaged $zipName and verified the embedded MSI checksum."
