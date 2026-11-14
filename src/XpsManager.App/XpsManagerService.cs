using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using XpsManager.Core;

namespace XpsManager.App;

public sealed class XpsManagerService : ServiceBase
{
    AutoSwitcher? _switcher;
    ChargeBoostMonitor? _chargeBoost;
    readonly List<IntPtr> _notifications = [];

    public XpsManagerService()
    {
        ServiceName = ServiceInstaller.Name;
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        try
        {
            _switcher = new AutoSwitcher(Log);
            foreach (var id in new[] { "5d3e9a59-e9d5-4b00-a6bd-ff34ff516548", "e00958c0-c213-4ace-ac77-fecced2eeea5", "6fe69556-704a-47a0-8f24-c28d936fda47" })
            {
                var guid = new Guid(id); // AC/DC, Energy saver, console display state (Modern Standby safety net).
                var handle = RegisterPowerSettingNotification(ServiceHandle, ref guid, 1);
                if (handle == IntPtr.Zero) throw new Win32Exception();
                _notifications.Add(handle);
            }
            _switcher.Start();
            _chargeBoost = new ChargeBoostMonitor(Log);
            _chargeBoost.Start();
        }
        catch (Exception e) { Log("Service start failed: " + e.Message, true); OnStop(); throw; }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        bool resume = status is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeCritical;
        _switcher?.Signal(resume ? "resumed" : "power setting changed", resume);
        if (resume) _chargeBoost?.Signal();
        return true;
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        if (change.Reason == SessionChangeReason.SessionUnlock) _switcher?.Signal("session unlocked");
    }

    protected override void OnCustomCommand(int command)
    {
        if (command == 128) _switcher?.Signal("configuration changed", true);
        if (command == 129) _chargeBoost?.Signal();
    }

    protected override void OnStop()
    {
        foreach (var handle in _notifications) UnregisterPowerSettingNotification(handle);
        _notifications.Clear();
        _switcher?.Dispose();
        _switcher = null;
        _chargeBoost?.Dispose();
        _chargeBoost = null;
    }
    protected override void OnShutdown() => OnStop();

    static void Log(string message, bool error)
    {
        try { EventLog.WriteEntry("XPS Manager", message, error ? EventLogEntryType.Error : EventLogEntryType.Information); }
        catch (Exception) { /* Logging must not crash a native callback or the service. Status is also in HKLM. */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
