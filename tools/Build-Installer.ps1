param([string]$Version = '')
$ErrorActionPreference = 'Stop'
if (!$Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props'))).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part numeric version.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# A fresh payload prevents removed/renamed assemblies from leaking into a later installer.
# Do not recursively delete a directory in a OneDrive-backed or redirected workspace.
$payload = Join-Path $repo ('artifacts/installer-payload/' + [Guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $repo 'src/XpsManager.App') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none -p:Version=$Version -o $payload
if ($LASTEXITCODE) { throw 'App publish failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $payload -Recurse
$licenses = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repo 'src/XpsManager.App/Assets/Fonts') -Filter '*.txt' |
    Copy-Item -Destination $licenses
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$runtime = Get-Content -LiteralPath (Join-Path $payload 'XpsManager.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $package = Join-Path $packageRoot ($framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version)
    foreach ($notice in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        $noticeFile = Join-Path $package $notice
        if (Test-Path -LiteralPath $noticeFile) {
            Copy-Item -LiteralPath $noticeFile -Destination (Join-Path $licenses ($framework.name + '-' + $notice + '.txt'))
        }
    }
}
# WiX incremental inputs do not reliably detect a changed preprocessor payload path.
dotnet build (Join-Path $repo 'installer/XpsManager.Installer.wixproj') -t:Rebuild -c Release -p:Version=$Version -p:PayloadDirectory=$payload
if ($LASTEXITCODE) { throw 'MSI build failed.' }
