using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using XpsManager.App;
using XpsManager.Core;

/// <summary>Read-only access requests using an admin-disabled token. No settings or service commands are sent.</summary>
internal static class AccessChecks
{
    public static void Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var bytes = new byte[adminSid.BinaryLength];
        adminSid.GetBinaryForm(bytes, 0);
        var sid = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, sid, bytes.Length);
            var disabled = new SidAndAttributes { Sid = sid };
            if (!CreateRestrictedToken(identity.AccessToken, 1, 1, ref disabled, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
                throw new Win32Exception();
            using (restricted) WindowsIdentity.RunImpersonated(restricted, Probe);
        }
        finally { Marshal.FreeHGlobal(sid); }
    }

    static void Probe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new Exception("Access probe must not run with administrator rights.");
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            foreach (var access in new uint[] { 4, 2, 16, 32, 256, 0x10000, 0x40000, 0x80000 })
            {
                var handle = OpenService(manager, ServiceInstaller.Name, access);
                var error = Marshal.GetLastWin32Error();
                if (handle != IntPtr.Zero) CloseServiceHandle(handle);
                if (access == 4 ? handle == IntPtr.Zero : handle != IntPtr.Zero || error != 5)
                    throw new Exception($"Unexpected service access: {access:x}, allowed={handle != IntPtr.Zero}, error={error}");
            }
        }
        finally { CloseServiceHandle(manager); }
        Denied(() =>
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(AutoSwitchStore.KeyPath, true);
            if (key is null) throw new Exception("Configuration key missing; cannot validate permissions.");
        });
        Denied(() =>
        {
            using var file = File.Open(ServiceInstaller.InstalledExe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        });
        Console.WriteLine("PASS: restricted token can query, but cannot manage service or write configuration/executable");
    }

    static void Denied(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { return; }
        throw new Exception("Unexpected write access for a restricted token.");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags, uint disableCount,
        ref SidAndAttributes disabled, uint deletePrivilegeCount, IntPtr privileges, uint restrictCount,
        IntPtr restrictSids, out SafeAccessTokenHandle restricted);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr handle);
}
