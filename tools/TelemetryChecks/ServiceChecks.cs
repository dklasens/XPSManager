using System.Diagnostics;
using System.IO;
using System.Management;
using System.ServiceProcess;
using XpsManager.App;
using XpsManager.Core;

/// <summary>Explicit opt-in elevated hardware checks. Never run on CI.</summary>
internal static class ServiceChecks
{
    public static void Run(string publishedExe)
    {
        var saved = AutoSwitchStore.Read();
        var power = new PowerSettings(new BiosClient(BiosClient.DefaultKeyCachePath));
        var original = power.ReadThermal().CurrentValue;
        try
        {
            Require(ServiceInstaller.Status() == ServiceControllerStatus.Running, "service running");
            ServiceInstaller.Save(saved with { Enabled = false });
            Thread.Sleep(2500);
            power.SetThermal("Cool");
            Thread.Sleep(3500);
            Require(power.ReadThermal().CurrentValue == "Cool", "disabled service leaves manual mode untouched");

            ServiceInstaller.Save(saved with { Enabled = true });
            Thread.Sleep(3500);
            var expected = AutoSwitchPolicy.Evaluate(SystemInfo.ReadPowerMode(SystemInfo.Power()), saved with { Enabled = true }, power.ReadThermal().PossibleValues).Target;
            Require(expected is not null && power.ReadThermal().CurrentValue == expected, "enable reload applies current mapping");
            power.SetThermal("Cool");
            Thread.Sleep(5000);
            Require(power.ReadThermal().CurrentValue == "Cool", "manual override persists without a mode change");

            var invalid = saved.Mapping.ToDictionary(p => p.Key, _ => "NotAThermalMode");
            ServiceInstaller.Save(new(true, invalid));
            Thread.Sleep(3500);
            Require(power.ReadThermal().CurrentValue == "Cool" && AutoSwitchStore.Status().Error.Contains("Unsupported"), "invalid configuration logged and skipped");
            ServiceInstaller.Save(saved with { Enabled = true });
            Thread.Sleep(3500);
            Require(AutoSwitchStore.Status().Error.Length == 0, "valid configuration clears prior error");

            using var query = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service WHERE Name='XpsManagerService'");
            using var result = query.Get();
            var pid = result.Cast<ManagementObject>().Select(s => Convert.ToInt32(s["ProcessId"])).Single();
            using (var process = Process.GetProcessById(pid))
            {
                Require(string.Equals(process.MainModule?.FileName, ServiceInstaller.InstalledExe, StringComparison.OrdinalIgnoreCase), "crash target is installed service only");
                process.Kill();
                process.WaitForExit(5000);
            }
            Thread.Sleep(8500);
            Require(ServiceInstaller.Status() == ServiceControllerStatus.Running, "SCM restarts crashed service");

            ServiceInstaller.Remove();
            Require(ServiceInstaller.Status() is null && !File.Exists(ServiceInstaller.InstalledExe) && !Directory.Exists(ServiceInstaller.InstallDirectory), "uninstall removes service and protected copy");
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(AutoSwitchStore.KeyPath)) Require(key is null, "uninstall removes registry settings");
            Require(!EventLog.SourceExists("XPS Manager"), "uninstall removes event source");
            RunPublished(publishedExe, "--install-service");
            Require(ServiceInstaller.Status() == ServiceControllerStatus.Running, "reinstall starts service");
            RunPublished(publishedExe, "--install-service");
            Require(ServiceInstaller.Status() == ServiceControllerStatus.Running, "install is repeatable");
            RunPublished(publishedExe, "--update-service");
            Require(ServiceInstaller.Status() == ServiceControllerStatus.Running, "update replaces executable and restarts service");
        }
        finally
        {
            if (ServiceInstaller.Status() is null) RunPublished(publishedExe, "--install-service");
            ServiceInstaller.Save(saved);
            if (!saved.Enabled) power.SetThermal(original);
        }
    }

    static void RunPublished(string path, string argument)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(path), argument) { UseShellExecute = false, CreateNoWindow = true })!;
        if (!process.WaitForExit(45000)) throw new Exception("Published service operation timed out.");
        Require(process.ExitCode == 0, argument);
    }

    static void Require(bool value, string name)
    {
        if (!value) throw new Exception("FAILED: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
