param([Parameter(Mandatory)][string]$Msi, [string]$Version = '')
$ErrorActionPreference = 'Stop'
if (!$Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props'))).Project.PropertyGroup.Version }
$path = (Resolve-Path -LiteralPath $Msi).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($path, 0)
function Read-Row([string]$Query, [int]$Columns) {
    $view = $db.OpenView($Query)
    [void]$view.Execute()
    $record = $view.Fetch()
    if (!$record) { throw "Missing MSI row: $Query" }
    $result = for ($i = 1; $i -le $Columns; $i++) {
        $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @([int]$i))
    }
    [void]$view.Close()
    return $result
}
function Require($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
$versionValue = Read-Row "SELECT Value FROM Property WHERE Property = 'ProductVersion'" 1
Require ($versionValue -eq $Version) 'Incorrect MSI version.'
$service = Read-Row 'SELECT Name, ServiceType, StartType, StartName, Arguments FROM ServiceInstall' 5
Require ($service[0] -eq 'XpsManagerService' -and $service[1] -eq '16' -and $service[2] -eq '2' -and
    $service[3] -eq 'LocalSystem' -and $service[4] -eq '--service') 'Incorrect service definition.'
$control = Read-Row 'SELECT Name, Event, Wait FROM ServiceControl' 3
Require ($control[0] -eq 'XpsManagerService' -and $control[1] -eq '163' -and $control[2] -eq '1') 'Missing start/stop/uninstall service controls.'
$acl = Read-Row "SELECT SDDLText FROM MsiLockPermissionsEx WHERE LockObject = 'InstallService'" 1
Require ($acl -eq 'D:P(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLORC;;;IU)') 'Service ACL differs from reviewed permissions.'
$cabinet = Read-Row 'SELECT Cabinet FROM Media' 1
Require ($cabinet.StartsWith('#')) 'MSI must embed its payload.'
$files = Read-Row "SELECT FileName FROM File WHERE File = 'AppExe'" 1
Require ($files -match 'XpsManager.exe') 'App executable is not included.'
Write-Output 'PASS: MSI version, service lifecycle, service permissions and embedded payload'
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
