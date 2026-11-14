namespace XpsManager.Core;

/// <summary>Checks the saved deadline without accessing firmware until restoration is due.</summary>
public sealed class ChargeBoostMonitor(Action<string, bool> log) : IDisposable
{
    readonly object _gate = new();
    readonly ChargeBoostStore _store = new();
    Timer? _timer;
    bool _disposed;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer = new Timer(Check, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }
    }

    public void Signal()
    {
        lock (_gate)
            if (!_disposed) _timer?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    void Check(object? unused)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_store.Read() is not { } state || state.ExpiresAt > DateTimeOffset.UtcNow) return;
                new ChargeBoost(new PowerSettings(new BiosClient()), _store).RestoreIfExpired();
            }
            catch (Exception e) { log("Charging timer: " + e.Message, true); }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _timer?.Dispose(); }
    }
}
