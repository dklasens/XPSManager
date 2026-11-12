using Microsoft.Win32;
using System.Globalization;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace XpsManager.Core;

public sealed record AutoSwitchStatus(string Mode, DateTimeOffset? AppliedAt, string Trigger, string Error);

public static class AutoSwitchStore
{
    public const string KeyPath = @"SOFTWARE\XpsManager\AutoSwitch";
    static RegistryKey Machine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static AutoSwitchConfiguration Read()
    {
        using var machine = Machine();
        using var key = machine.OpenSubKey(KeyPath);
        var defaults = AutoSwitchConfiguration.Default;
        if (key?.GetValue("Configuration") is string json)
        {
            // One registry value is atomic, so an event never observes half of a mapping edit.
            var saved = JsonSerializer.Deserialize<AutoSwitchConfiguration>(json)
                ?? throw new InvalidDataException("Automatic settings are empty. Save them again in XPS Manager.");
            if (saved.Mapping is null) throw new InvalidDataException("Automatic mappings are missing. Save them again in XPS Manager.");
            return saved with { Mapping = defaults.Mapping.ToDictionary(p => p.Key, p => saved.Mapping.GetValueOrDefault(p.Key) ?? p.Value) };
        }
        var mapping = defaults.Mapping.ToDictionary(p => p.Key, p => key?.GetValue("Map." + p.Key) as string ?? p.Value);
        return new(key?.GetValue("Enabled") is int enabled && enabled == 1, mapping);
    }

    public static void Write(AutoSwitchConfiguration config)
    {
        using var machine = Machine();
        using var key = machine.CreateSubKey(KeyPath);
        var acl = new RegistrySecurity();
        acl.SetAccessRuleProtection(true, false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new RegistryAccessRule(sid, RegistryRights.FullControl, InheritanceFlags.ContainerInherit,
                PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), RegistryRights.ReadKey,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        key.SetAccessControl(acl);
        // Legacy readers see disabled while the diagnostic mirrors are replaced.
        key.SetValue("Enabled", 0);
        foreach (var mode in AutoSwitchConfiguration.Default.Mapping.Keys)
            key.SetValue("Map." + mode, config.Mapping.GetValueOrDefault(mode) ?? "", RegistryValueKind.String);
        key.SetValue("Enabled", config.Enabled ? 1 : 0);
        key.SetValue("Configuration", JsonSerializer.Serialize(config), RegistryValueKind.String);
    }

    public static AutoSwitchStatus Status()
    {
        using var machine = Machine();
        using var key = machine.OpenSubKey(KeyPath);
        string Get(string name) => key?.GetValue(name) as string ?? "";
        return new(Get("LastAppliedMode"), DateTimeOffset.TryParse(Get("LastAppliedAt"), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var at) ? at : null, Get("LastTrigger"), Get("LastError"));
    }

    public static void Record(string? mode, string trigger, string? error)
    {
        using var machine = Machine();
        using var key = machine.CreateSubKey(KeyPath);
        key.SetValue("LastError", error ?? "");
        if (mode is null) return;
        key.SetValue("LastAppliedMode", mode);
        key.SetValue("LastAppliedAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        key.SetValue("LastTrigger", trigger);
    }

    public static void Remove()
    {
        using var machine = Machine();
        machine.DeleteSubKeyTree(KeyPath, false);
    }

    public static bool DellOptimizerInstalled()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var item = uninstall.OpenSubKey(name);
                if ((item?.GetValue("DisplayName") as string)?.Contains("Dell Optimizer", StringComparison.OrdinalIgnoreCase) == true) return true;
            }
        }
        return false;
    }
}
