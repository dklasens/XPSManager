param([int]$Seconds = 120)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PowerProbe {
 [DllImport("powrprof.dll")] public static extern uint PowerGetEffectiveOverlayScheme(out Guid value);
 [StructLayout(LayoutKind.Sequential)] public struct Power { public byte Ac,Flag,Percent,Saver; public int Life,Full; }
 [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out Power status);
}
'@
$path = Join-Path $PSScriptRoot '..\artifacts\power-transitions.txt'
$last = ''
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    $overlay = [guid]::Empty
    $power = New-Object PowerProbe+Power
    [PowerProbe]::PowerGetEffectiveOverlayScheme([ref]$overlay) | Out-Null
    [PowerProbe]::GetSystemPowerStatus([ref]$power) | Out-Null
    $thermal = Get-CimInstance -Namespace 'root\dcim\sysman\biosattributes' -ClassName EnumerationAttribute | Where-Object AttributeName -eq 'ThermalManagement'
    $value = "AC=$($power.Ac) Saver=$($power.Saver) Overlay=$overlay Thermal=$($thermal.CurrentValue)"
    if ($value -ne $last) { "$(Get-Date -Format o) $value" | Add-Content -LiteralPath $path; $last = $value }
    Start-Sleep -Seconds 1
}
