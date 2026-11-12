param([switch]$WriteThermal)
$ErrorActionPreference = 'Stop'
$stage = Join-Path $env:ProgramFiles ('XPS Manager Verification ' + [guid]::NewGuid().ToString('N'))
$taskName = 'XpsManager-SystemVerification-' + [guid]::NewGuid().ToString('N')
if (Test-Path -LiteralPath $stage) { throw "Verification folder already exists: $stage" }
New-Item -ItemType Directory -Path $stage | Out-Null
$body = @'
$ErrorActionPreference = 'Stop'
Start-Transcript -Path '__STAGE__\result.txt'
try {
    whoami
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class Overlay { [DllImport("powrprof.dll")] public static extern uint PowerGetEffectiveOverlayScheme(out Guid value); }'
    $overlay = [guid]::Empty
    $result = [Overlay]::PowerGetEffectiveOverlayScheme([ref]$overlay)
    "Overlay result=$result value=$overlay"
    $ns = 'root\dcim\sysman\biosattributes'
    $thermal = Get-CimInstance -Namespace $ns -ClassName EnumerationAttribute | Where-Object AttributeName -eq 'ThermalManagement'
    $thermal | Select-Object AttributeName,CurrentValue,PossibleValue | Format-List
    if (__WRITE__) {
        $original = $thermal.CurrentValue
        $target = if ($original -eq 'Quiet') { 'Cool' } else { 'Quiet' }
        $interface = Get-CimInstance -Namespace $ns -ClassName BIOSAttributeInterface
        try {
            $set = Invoke-CimMethod -InputObject $interface -MethodName SetAttribute -Arguments @{ SecType=[uint32]0; SecHndCount=[uint32]0; SecHandle=[byte[]]@(); AttributeName='ThermalManagement'; AttributeValue=$target }
            $set | Format-List
            Get-CimInstance -Namespace $ns -ClassName EnumerationAttribute | Where-Object AttributeName -eq 'ThermalManagement' | Select-Object CurrentValue
        } finally {
            Invoke-CimMethod -InputObject $interface -MethodName SetAttribute -Arguments @{ SecType=[uint32]0; SecHndCount=[uint32]0; SecHandle=[byte[]]@(); AttributeName='ThermalManagement'; AttributeValue=$original } | Format-List
            'Restored:'
            Get-CimInstance -Namespace $ns -ClassName EnumerationAttribute | Where-Object AttributeName -eq 'ThermalManagement' | Select-Object CurrentValue
        }
    }
} catch { $_ | Out-String | Write-Output } finally { Stop-Transcript; New-Item -ItemType File -Path '__STAGE__\done' | Out-Null }
'@
$body.Replace('__STAGE__', $stage).Replace('__WRITE__', $(if ($WriteThermal) {'$true'} else {'$false'})) | Set-Content -LiteralPath (Join-Path $stage 'probe.ps1')
try {
    $arguments = [Security.SecurityElement]::Escape('-NoProfile -ExecutionPolicy Bypass -File "' + $stage + '\probe.ps1"')
    $xml = @"
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Principals><Principal id="System"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT2M</ExecutionTimeLimit></Settings><Actions Context="System"><Exec><Command>C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe</Command><Arguments>$arguments</Arguments></Exec></Actions></Task>
"@
    $xml | Set-Content -LiteralPath (Join-Path $stage 'task.xml') -Encoding Unicode
    & schtasks.exe /Create /TN $taskName /XML (Join-Path $stage 'task.xml')
    if ($LASTEXITCODE -ne 0) { throw 'Could not register SYSTEM probe' }
    & schtasks.exe /Run /TN $taskName
    $deadline = (Get-Date).AddSeconds(90)
    while (!(Test-Path -LiteralPath (Join-Path $stage 'done')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Copy-Item -LiteralPath (Join-Path $stage 'result.txt') -Destination (Join-Path $PSScriptRoot '..\artifacts\system-verification.txt')
} finally {
    & schtasks.exe /Delete /TN $taskName /F
    # Remove only the files we created; never recursively delete a computed path.
    foreach ($file in 'probe.ps1','result.txt','done','task.xml') { Remove-Item -LiteralPath (Join-Path $stage $file) -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $stage -ErrorAction SilentlyContinue
}
