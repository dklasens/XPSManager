using System.Security.AccessControl;
using System.Security.Principal;

namespace XpsManager.Core;

/// <summary>Serializes complete BIOS operations across the app, CLI and service.</summary>
internal sealed class BiosOperationGate : IDisposable
{
    readonly Mutex _mutex;

    public BiosOperationGate()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new BiosException("Access denied to the Dell BIOS interface. Run as administrator.");
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(sid, null), MutexRights.FullControl, AccessControlType.Allow));
        try { _mutex = MutexAcl.Create(false, @"Global\XpsManager.BiosOperation.v1", out _, security); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or WaitHandleCannotBeOpenedException or IOException)
        { throw new BiosException("Cannot coordinate BIOS access. Run as administrator; if the problem persists, restart Windows.", e); }
        try
        {
            if (!_mutex.WaitOne(TimeSpan.FromSeconds(15)))
                throw new BiosException("Another BIOS operation is still running. Wait, then re-read settings.");
        }
        catch (AbandonedMutexException) { /* Ownership was acquired; all operations re-read the firmware. */ }
        catch { _mutex.Dispose(); throw; }
    }

    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
