using System.Runtime.InteropServices;

// All native dependencies used by Core are Windows system DLLs or Intel's driver-installed
// ControlLib.dll in System32. Never search beside a downloaded elevated executable or in PATH/CWD.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
