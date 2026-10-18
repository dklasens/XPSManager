using System.Windows.Threading;
using XpsManager.Core;

namespace XpsManager.App;

/// <summary>
/// Live, read-only readings for the readout band and the Activity view. Samples once a second
/// while the window is open and not minimised; nothing here changes a setting.
/// </summary>
public sealed class TelemetryViewModel : Observable, IDisposable
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly BatteryEstimator _estimator = new();
    TelemetrySampler? _sampler;
    TelemetrySample? _s;
    bool _reading, _disposed;

    public TelemetryViewModel() => _timer.Tick += Tick;

    // A fixture sample, for previews and render checks.
    public TelemetryViewModel(TelemetrySample sample) => _s = sample;

    // ---- Readout band

    public string BatteryPercent => _s?.Power.BatteryPercent is { } p ? $"{p}%" : "—";

    public string SourceLine
    {
        get
        {
            if (_s is not { } s) return "READING…";
            if (s.Battery.State == "Charging")
                return BatteryEstimator.TimeToFull(s.Battery) is { } full ? $"CHARGING · FULL IN ~{Duration(full).ToUpperInvariant()}" : "CHARGING";
            if (s.Power.OnAc == true) return "ON AC";
            if (s.Power.OnAc == false)
                return TimeLeft is { } left ? $"ON BATTERY · {Duration(left).ToUpperInvariant()} LEFT" : "ON BATTERY";
            return "SOURCE UNKNOWN";
        }
    }

    public string WindowsMode => _s?.WindowsMode ?? "—";

    public string Display => _s?.Display is { } d
        ? string.Join(" · ", new[]
            {
                d.RefreshHz is { } hz ? d.Dynamic ? $"Dynamic {hz} Hz" : $"{hz} Hz" : null,
                d.Brightness is { } b ? $"{b}%" : null
            }.Where(x => x is not null)) is { Length: > 0 } text ? text : "—"
        : "—";

    public string DisplayDetail => _s?.Display is { RefreshHz: { } hz } d
        ? d.Dynamic && d.BaseRefreshHz is { } floor
            ? $"Built-in display: {hz} Hz selected with dynamic refresh rate, so Windows drops to {floor} Hz when nothing on screen is moving. Brightness follows."
            : $"Built-in display: {hz} Hz refresh rate. Brightness follows."
        : "Refresh rate and brightness.";

    public string BatteryLabel => _s?.Battery.State == "Charging" ? "CHARGING" : "BATTERY";

    public string BatteryValue => _s?.Battery switch
    {
        null => "—",
        { State: "Idle" } => "Idle",
        { Watts: { } w } => Watts(Math.Abs(w)),
        _ => "—"
    };

    public string Package => Watts(_s?.Rapl.PackageW);
    public string PCoreClock => Ghz(_s?.Cpu.PCoreMhz);
    public string GpuClock => Mhz(_s?.Gpu.Clocks?.ClockMhz);

    public bool CpuLimited => CpuLimitPercent is not null;
    public string CpuStripLabel => CpuLimited ? "P-CORES · LIMITED" : "P-CORES";
    public bool GpuLimited => GpuLimitReason is not null;
    public string GpuStripLabel => GpuLimited ? "GPU · LIMITED" : "GPU";

    // ---- Activity: power

    public string BatteryDrawLabel => _s?.Battery.State == "Charging" ? "Charging at" : "Battery draw";
    public string Cores => Watts(_s?.Rapl.CoresW);
    public string Graphics => Watts(_s?.Rapl.GraphicsW);
    public string Memory => Watts(_s?.Rapl.MemoryW);

    /// <summary>Battery draw not accounted for by the chip or memory: display, SSD, Wi-Fi, fans, losses.</summary>
    public string Everything => _s is { Battery: { State: "Discharging", Watts: { } w }, Rapl: { PackageW: { } pkg, MemoryW: { } mem } }
        ? Watts(Math.Max(0, Math.Abs(w) - pkg - mem))
        : "—";

    // ---- Activity: battery

    TimeSpan? TimeLeft => _s is null ? null : _estimator.TimeLeft(_s.Battery) ?? _s.Power.WindowsEstimate;

    public string TimeLeftText => _s?.Battery.State == "Discharging" && TimeLeft is { } t ? Duration(t) : "—";

    public string TimeLeftDetail
    {
        get
        {
            var windows = _s?.Power.WindowsEstimate is { } w ? $" Windows estimates {Duration(w)}." : "";
            return _estimator.AverageW is { } avg
                ? $"At the last minute's average draw of {avg:0.0} W.{windows}"
                : $"Windows' estimate until this app has {BatteryEstimator.MinCoverage.TotalSeconds:0} s of readings.";
        }
    }

    public string UsedSinceOpened => _s is null ? "—" : $"{_estimator.UsedWh:0.0} Wh";
    public string Capacity => $"{Number(_s?.Battery.RemainingWh)} / {Number(_s?.Battery.FullWh)} Wh";
    public string Cycles => _s?.Battery.Cycles?.ToString() ?? "—";
    public string Health => _s?.Battery.HealthPercent is { } h ? $"{h:0.0}%" : "—";
    public string HealthDetail => _s?.Battery.DesignWh is { } design
        ? $"Full-charge capacity / design capacity ({design:0.0} Wh). Not the current charge."
        : "Design capacity not reported.";
    public string Note => _s?.Battery.Note ?? "";
    public bool HasNote => Note.Length > 0;

    // ---- Activity: processor

    public string PCoresLabel => _s?.Cpu.PCores is > 0 and var n ? $"P-cores ({n})" : "P-cores";
    public string ECoresLabel => _s?.Cpu.ECores is > 0 and var n ? $"E-cores ({n})" : "E-cores";
    public string ECoreClock => Ghz(_s?.Cpu.ECoreMhz);
    public string Load => _s?.Cpu.LoadPercent is { } l ? $"{l:0}%" : "—";

    double? CpuLimitPercent => _s?.Cpu.LimitPercent is { } p && p < 99.5 ? p : null;
    public string CpuLimit => _s?.Cpu.LimitPercent is null ? "—" : CpuLimitPercent is { } p ? $"Held to {p:0}%" : "None";

    // ---- Activity: graphics

    public string GpuClockDetail => _s?.Gpu.Clocks is { } c
        ? $"Requested {Mhz(c.RequestMhz)} · maximum {Mhz(c.MaxMhz)}"
        : "Intel graphics control library not available.";
    public string GpuBusy => _s?.Gpu.BusyPercent is { } b ? _s.Gpu.BusyEngine is { } e ? $"{b:0}% · {e}" : $"{b:0}%" : "—";
    public string MediaClock => Mhz(_s?.Gpu.Clocks?.MediaMhz);
    string? GpuLimitReason => _s?.Gpu.Clocks is { } c ? IntelGraphics.DescribeThrottle(c.Throttle) : null;
    public string GpuLimit => _s?.Gpu.Clocks is null ? "—" : GpuLimitReason ?? "None";

    // ---- Formatting

    static string Watts(double? w) => w is { } v ? $"{v:0.0} W" : "—";
    static string Ghz(double? mhz) => mhz is { } v ? $"{v / 1000:0.0} GHz" : "—";
    static string Mhz(double? mhz) => mhz is { } v ? $"{v:0} MHz" : "—";
    static string Number(double? v) => v is { } n ? $"{n:0.0}" : "—";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} m" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} m";

    // ---- Sampling

    public async Task StartAsync()
    {
        await RefreshAsync();
        if (!_disposed) _timer.Start();
    }

    /// <summary>Stop sampling while minimised; reading counters every second costs battery too.</summary>
    public void Pause() => _timer.Stop();

    public void Resume()
    {
        if (_disposed || _timer.IsEnabled) return;
        _timer.Start();
        _ = RefreshAsync();
    }

    async void Tick(object? sender, EventArgs e) => await RefreshAsync();

    async Task RefreshAsync()
    {
        if (_reading || _disposed) return;
        _reading = true;
        try
        {
            if (_sampler is null)
            {
                var created = await Task.Run(() => new TelemetrySampler());
                if (_disposed) { created.Dispose(); return; }
                _sampler = created;
            }
            var sample = await Task.Run(_sampler.Read);
            if (_disposed) return;
            _s = sample;
            _estimator.Add(sample.At, sample.Battery);
            OnPropertyChanged(string.Empty);
        }
        finally { _reading = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        var sampler = _sampler;
        _sampler = null;
        if (sampler is not null) Task.Run(sampler.Dispose);
    }
}
