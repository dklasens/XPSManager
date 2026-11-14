using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using XpsManager.Core;

namespace XpsManager.App;

public static class ServiceInstaller
{
    public const string Name = "XpsManagerService";
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "XPS Manager");
    public static string InstalledExe => Path.Combine(InstallDirectory, "XpsManager.exe");
    public static bool ManagedByInstaller
    {
        get
        {
            using var machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SOFTWARE\XpsManager\Installer");
            return key?.GetValue("Managed") is int value && value == 1;
        }
    }

    static void RequirePortableInstallation()
    {
        if (ManagedByInstaller)
            throw new InvalidOperationException("This copy is managed by Windows Installer. Run the newer MSI to update, or use Windows Settings > Apps > Installed apps to uninstall XPS Manager.");
    }

    public static ServiceControllerStatus? Status()
    {
        using var service = new ServiceController(Name);
        try { return service.Status; }
        catch (InvalidOperationException e) when (e.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 }) { return null; }
    }

    public static bool UpdateAvailable => !ManagedByInstaller && File.Exists(InstalledExe) &&
        Version.TryParse(FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion, out var installed) &&
        Assembly.GetExecutingAssembly().GetName().Version > installed;

    public static void Install(AutoSwitchConfiguration config)
    {
        RequirePortableInstallation();
        var source = PublishedExe();
        ProtectDirectory();
        if (!File.Exists(InstalledExe) || UpdateAvailable) ReplaceBinary(source);
        EnsureEventSource();
        if (Status() is null)
            Sc("create", Name, "binPath=", $"\"{InstalledExe}\" --service", "start=", "delayed-auto", "obj=", "LocalSystem", "DisplayName=", "XPS Manager");
        else
            Sc("config", Name, "binPath=", $"\"{InstalledExe}\" --service", "start=", "delayed-auto", "obj=", "LocalSystem");
        Sc("description", Name, "Follows Windows power mode and restores timed ExpressCharge sessions. Managed by XPS Manager.");
        Sc("failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/30000/restart/30000");
        ProtectService();
        Save(config);
    }

    public static void Save(AutoSwitchConfiguration config)
    {
        AutoSwitchStore.Write(config);
        if (Status() is null)
        {
            if (config.Enabled) throw new InvalidOperationException("Install the background service from the published XpsManager.exe first.");
            return;
        }
        using var service = new ServiceController(Name);
        if (service.Status != ServiceControllerStatus.Running)
        {
            if (!config.Enabled) return;
            if (service.Status == ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        service.ExecuteCommand(128);
    }

    public static void Update()
    {
        RequirePortableInstallation();
        var source = PublishedExe();
        ProtectDirectory();
        ProtectService();
        var wasRunning = Status() == ServiceControllerStatus.Running;
        Stop();
        try { ReplaceBinary(source); }
        finally
        {
            if (wasRunning)
            {
                using var service = new ServiceController(Name);
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
    }

    public static void EnsureChargeBoostService()
    {
        if (Status() is null) Install(AutoSwitchStore.Read());
        if (!File.Exists(InstalledExe) ||
            !Version.TryParse(FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion, out var version) ||
            version < new Version(2, 1, 0))
        {
            if (ManagedByInstaller)
                throw new InvalidOperationException("Install XPS Manager 2.1 or newer using the MSI before starting the charging timer.");
            Update();
        }
        using var service = new ServiceController(Name);
        if (service.Status == ServiceControllerStatus.StopPending)
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        // Prove service control works before saving a deadline or touching firmware.
        service.ExecuteCommand(129);
    }

    public static void Remove()
    {
        RequirePortableInstallation();
        if (string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Open your downloaded copy of XPS Manager to remove the installed service copy.");
        CheckPath();
        // Do not remove the recovery mechanism while a temporary charging mode is active.
        if (new ChargeBoostStore().Read() is not null)
            new ChargeBoost(new PowerSettings(new BiosClient()), new ChargeBoostStore()).ReturnNow();
        if (Status() is not null) { Stop(); Sc("delete", Name); }
        if (EventLog.SourceExists("XPS Manager")) EventLog.DeleteEventSource("XPS Manager");
        AutoSwitchStore.Remove();
        if (File.Exists(InstalledExe)) File.Delete(InstalledExe);
        // Never recursively delete an install directory that may contain unrelated files.
        if (Directory.Exists(InstallDirectory) && !Directory.EnumerateFileSystemEntries(InstallDirectory).Any()) Directory.Delete(InstallDirectory);
    }

    static void Stop()
    {
        if (Status() is null) return;
        using var service = new ServiceController(Name);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    static string PublishedExe()
    {
        // A framework-dependent build output needs adjacent DLLs. Only install the actual single-file bundle.
#pragma warning disable IL3000
        if (!string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location))
#pragma warning restore IL3000
            throw new InvalidOperationException("Service installation needs the published single-file XpsManager.exe. Run dotnet publish with the SingleFile profile, then open dist\\XpsManager.exe.");
        return Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable.");
    }

    static void CheckPath()
    {
        for (var dir = new DirectoryInfo(InstallDirectory); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The service install path must not contain junctions or symbolic links.");
        if (File.Exists(InstalledExe) && (File.GetAttributes(InstalledExe) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The installed executable must not be a symbolic link.");
    }

    static void ProtectDirectory()
    {
        CheckPath();
        var directory = Directory.CreateDirectory(InstallDirectory);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }

    static void ReplaceBinary(string source)
    {
        if (string.Equals(source, InstalledExe, StringComparison.OrdinalIgnoreCase)) return;
        Stop();
        // Create a fresh protected file and atomically replace; a failed copy leaves the old exe intact.
        var staged = Path.Combine(InstallDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(source, staged);
            // SCM can report Stopped before the process has released its image mapping.
            // Retry only the replacement; never delete the working copy first.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(staged, InstalledExe, true); break; }
                catch (Exception e) when (attempt < 40 && e is IOException or UnauthorizedAccessException)
                { Thread.Sleep(250); }
            }
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    static void EnsureEventSource()
    {
        if (!EventLog.SourceExists("XPS Manager")) EventLog.CreateEventSource("XPS Manager", "Application");
    }

    static void ProtectService() => Sc("sdset", Name,
        // SYSTEM/admins: full control. Interactive users: query/interrogate only, no custom commands.
        "D:P(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLORC;;;IU)");

    static void Sc(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start service control.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"Service operation failed ({process.ExitCode}): {output} {error}".Trim());
    }
}
