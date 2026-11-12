# Read-only access checks. Run from a NON-elevated terminal; no service command or setting is changed.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ServiceAccessProbe {
 public static IntPtr OpenManager() { return OpenSCManager(null,null,1); }
 [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenSCManager(string machine, string database, uint access);
 [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenService(IntPtr manager, string name, uint access);
 [DllImport("advapi32.dll")] public static extern bool CloseServiceHandle(IntPtr handle);
}
'@
$manager = [ServiceAccessProbe]::OpenManager()
if ($manager -eq [IntPtr]::Zero) { throw 'Cannot open Service Control Manager.' }
try {
    foreach ($entry in @(@('Query',4,$true),@('ChangeConfig',2,$false),@('Start',16,$false),@('Stop',32,$false),@('CustomReload',256,$false))) {
        $handle = [ServiceAccessProbe]::OpenService($manager,'XpsManagerService',$entry[1])
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $allowed = $handle -ne [IntPtr]::Zero
        if ($allowed) { [ServiceAccessProbe]::CloseServiceHandle($handle) | Out-Null }
        if ($allowed -ne $entry[2] -or (!$allowed -and $errorCode -ne 5)) { throw "Unexpected permission for $($entry[0]): allowed=$allowed error=$errorCode" }
        "PASS: $($entry[0]) allowed=$allowed"
    }
} finally { [ServiceAccessProbe]::CloseServiceHandle($manager) | Out-Null }

try {
    $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\XpsManager\AutoSwitch',$true)
    if ($key) { $key.Dispose(); throw 'Unexpected write access to service configuration.' }
} catch [System.Security.SecurityException] { 'PASS: registry write access denied' }
catch [System.UnauthorizedAccessException] { 'PASS: registry write access denied' }

try {
    $file=[IO.File]::Open('C:\Program Files\XPS Manager\XpsManager.exe',[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
    $file.Dispose()
    throw 'Unexpected write access to installed executable.'
} catch [System.UnauthorizedAccessException] { 'PASS: executable write access denied' }
