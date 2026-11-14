param([Parameter(Mandatory)][string]$Msi, [Parameter(Mandatory)][string]$OlderMsi)
$ErrorActionPreference = 'Stop'
# This intentionally installs/uninstalls software. Never run it on an owner's machine.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer lifecycle checks may run only on a disposable GitHub-hosted runner.'
}
if (Get-Service XpsManagerService -ErrorAction SilentlyContinue) { throw 'Refusing to touch a pre-existing service.' }
$app = Join-Path $env:ProgramFiles 'XPS Manager/XpsManager.exe'
$configKey = 'HKLM:\SOFTWARE\XpsManager\AutoSwitch'
if ((Test-Path -LiteralPath (Split-Path $app)) -or (Test-Path $configKey)) { throw 'Runner is not clean.' }
$logs = Join-Path $PSScriptRoot '../artifacts/installer-tests'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$script:step = 0
function Require($Condition, [string]$Message) { if (!$Condition) { throw $Message }; "PASS: $Message" }
function Run-Msi([string]$Action, [string]$Package, [int[]]$Allowed = @(0,3010)) {
    $script:step++
    $full = (Resolve-Path -LiteralPath $Package).Path
    $log = [IO.Path]::GetFullPath((Join-Path $logs "$($script:step).log"))
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/msiexec.exe') `
        -ArgumentList @($Action, "`"$full`"", '/qn', '/norestart', '/l*v', "`"$log`"") -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(120000)) { throw "MSI timed out. See $log" }
    Require ($process.ExitCode -in $Allowed) "MSI $Action exit $($process.ExitCode)"
}
function Verify-Installed {
    Require (Test-Path -LiteralPath $app) 'installed executable exists'
    Require ((Get-Service XpsManagerService).Status -eq 'Running') 'service starts without Dell hardware while disabled'
    $service = Get-CimInstance Win32_Service -Filter "Name='XpsManagerService'"
    Require ($service.StartName -eq 'LocalSystem' -and $service.PathName -eq "`"$app`" --service") 'quoted protected service path and account'
    foreach ($target in @((Split-Path $app), $app)) {
        $acl = Get-Acl -LiteralPath $target
        foreach ($rule in $acl.Access) {
            $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
            if ($rule.AccessControlType -eq 'Allow' -and $sid -notin @('S-1-5-18','S-1-5-32-544','S-1-3-0')) {
                Require (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::WriteData) -eq 0) "no ordinary-user writes to $target"
            }
        }
    }
    & (Join-Path $PSScriptRoot 'TelemetryChecks/bin/Release/net10.0-windows/TelemetryChecks.exe') --access-checks
    if ($LASTEXITCODE) { throw 'Restricted-token permission checks failed.' }
}

Run-Msi '/i' $Msi
Verify-Installed
$initial = Get-ItemProperty $configKey
Require (!$initial.Configuration -and !$initial.Enabled) 'fresh install leaves automatic switching off'
# Custom DISABLED configuration exercises preservation without any BIOS writes on the CI host.
$saved = '{"Enabled":false,"Mapping":{"BestPowerEfficiency":"Cool","EnergySaver":"Cool","Balanced":"Quiet","BetterPerformance":"Quiet","BestPerformance":"UltraPerformance"}}'
Set-ItemProperty $configKey -Name Configuration -Value $saved
Run-Msi '/fa' $Msi
Verify-Installed
Require ((Get-ItemProperty $configKey).Configuration -eq $saved) 'repair preserves automatic mappings'
Run-Msi '/x' $Msi
Require (!(Get-Service XpsManagerService -ErrorAction SilentlyContinue)) 'uninstall removes the service'
Require (!(Test-Path -LiteralPath $app)) 'uninstall removes the executable'
Require ((Get-ItemProperty $configKey).Configuration -eq $saved) 'uninstall retains user settings for reinstall'
Run-Msi '/i' $OlderMsi
Run-Msi '/i' $Msi
Verify-Installed
Require ((Get-ItemProperty $configKey).Configuration -eq $saved) 'major upgrade preserves automatic mappings'
Run-Msi '/i' $OlderMsi @(1603)
Verify-Installed
Run-Msi '/x' $Msi
Require (!(Get-Service XpsManagerService -ErrorAction SilentlyContinue)) 'final uninstall removes the service'
Require (!(Test-Path -LiteralPath $app)) 'final uninstall removes app payload'
