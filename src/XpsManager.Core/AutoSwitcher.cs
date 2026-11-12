using System.Runtime.InteropServices;

namespace XpsManager.Core;

/// <summary>Event-driven thermal policy. There is no periodic BIOS polling.</summary>
public sealed class AutoSwitcher : IDisposable
{
    readonly object _gate = new();
    readonly SwitchDebounce _debounce = new();
    readonly Timer _timer;
    readonly Action<string, bool> _log;
    readonly EffectiveCallback _callback;
    IntPtr _registration;
    bool _disposed;
    (WindowsPowerMode Mode, bool? Ac)? _lastInput;
    PowerSettings? _settings;

    public AutoSwitcher(Action<string, bool> log)
    {
        _log = log;
        _timer = new Timer(Apply);
        _callback = (_, _) => Signal("power mode changed");
    }

    public void Start()
    {
        int result = PowerRegisterForEffectivePowerModeNotifications(2, _callback, IntPtr.Zero, out _registration);
        if (result != 0) Marshal.ThrowExceptionForHR(result);
        Signal("service started", true);
    }

    public void Signal(string trigger, bool force = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce.Signal(Environment.TickCount64, trigger, force);
            _timer.Change(2000, Timeout.Infinite);
        }
    }

    void Apply(object? unused)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_debounce.Take(Environment.TickCount64) is not { } signal)
            {
                _timer.Change(_debounce.Remaining(Environment.TickCount64), Timeout.Infinite);
                return;
            }
            try
            {
                var config = AutoSwitchStore.Read();
                if (!config.Enabled) { _lastInput = null; return; }
                var power = SystemInfo.Power();
                var input = (Mode: SystemInfo.ReadPowerMode(power), Ac: power.OnAc);
                // Ignore transient Game Mode notifications and duplicate safety-net events.
                if (!signal.Force && _lastInput == input) return;
                var trigger = _lastInput is { } previous && previous.Ac != input.Ac
                    ? input.Ac == true ? "plugged in" : input.Ac == false ? "unplugged" : signal.Trigger
                    : signal.Trigger;
                _lastInput = input;
                if (input.Mode == WindowsPowerMode.Custom) return;
                _settings ??= new PowerSettings(new BiosClient(BiosClient.DefaultKeyCachePath));
                var thermal = _settings.ReadThermal();
                var decision = AutoSwitchPolicy.Evaluate(input.Mode, config, thermal.PossibleValues);
                if (decision.Error is { } error) { Report(null, trigger, error); return; }
                if (decision.Target is not { } target) { AutoSwitchStore.Record(null, trigger, null); return; }
                // Recheck the master switch just before writing (disable may have arrived during WMI).
                if (!AutoSwitchStore.Read().Enabled) return;
                if (thermal.CurrentValue == target) { AutoSwitchStore.Record(null, trigger, null); return; }
                _settings.SetThermal(target);
                Report(target, trigger, null);
            }
            catch (Exception e)
            {
                _settings = null; // Reconnect the WMI provider at the next event.
                Report(null, signal.Trigger, e.Message);
            }
        }
    }

    void Report(string? mode, string trigger, string? error)
    {
        try { AutoSwitchStore.Record(mode, trigger, error); }
        catch (Exception e) { _log("Could not write service status: " + e.Message, true); }
        _log(error ?? $"Set thermal mode to {mode} ({trigger}).", error is not null);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _timer.Dispose(); }
        // Unregister outside the gate: Windows waits for in-flight callbacks.
        if (_registration != IntPtr.Zero) PowerUnregisterFromEffectivePowerModeNotifications(_registration);
        _registration = IntPtr.Zero;
        GC.KeepAlive(_callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void EffectiveCallback(int mode, IntPtr context);
    [DllImport("powrprof.dll")]
    static extern int PowerRegisterForEffectivePowerModeNotifications(uint version, EffectiveCallback callback, IntPtr context, out IntPtr registration);
    [DllImport("powrprof.dll")]
    static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr registration);
}
