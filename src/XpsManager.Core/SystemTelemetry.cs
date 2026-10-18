namespace XpsManager.Core;

/// <summary>Intel RAPL power domains, in watts. Package includes cores and graphics; memory is separate.</summary>
public sealed record PowerBreakdown(double? PackageW, double? CoresW, double? GraphicsW, double? MemoryW);

/// <param name="PCoreMhz">Average effective clock across performance cores.</param>
/// <param name="LimitPercent">Windows' "% Performance Limit": below 100 means firmware is holding the CPU back.</param>
public sealed record CpuActivity(double? PCoreMhz, double? ECoreMhz, int PCores, int ECores, double? LoadPercent, double? LimitPercent);

/// <param name="BusyPercent">Busiest GPU engine, the same measure Task Manager uses.</param>
/// <param name="BusyEngine">That engine's type, e.g. "3D" or "Video decode".</param>
public sealed record GpuActivity(GpuClocks? Clocks, double? BusyPercent, string? BusyEngine);

public sealed record TelemetrySample(DateTime At, BatterySnapshot Battery, PowerStatus Power, string? WindowsMode,
    DisplayState Display, PowerBreakdown Rapl, CpuActivity Cpu, GpuActivity Gpu);

/// <summary>
/// One read-only snapshot of everything the app shows live. Construct once and call
/// <see cref="Read"/> on a timer from a background thread; the first read has no rate values.
/// Nothing here changes any setting.
/// </summary>
public sealed class TelemetrySampler : IDisposable
{
    readonly object _gate = new();
    readonly PdhQuery _pdh = new();
    readonly IntPtr _rapl, _perf, _freq, _limit, _utility, _gpuEngines;
    readonly Dictionary<string, int> _coreClass = SystemInfo.CoreClasses();
    readonly int _pClass;
    readonly IntelGraphics? _gpu = IntelGraphics.TryOpen();
    int _reads;
    int? _brightness;

    public TelemetrySampler()
    {
        _rapl = _pdh.Add(@"\Energy Meter(*)\Power");
        _perf = _pdh.Add(@"\Processor Information(*)\% Processor Performance");
        _freq = _pdh.Add(@"\Processor Information(*)\Processor Frequency");
        _limit = _pdh.Add(@"\Processor Information(_Total)\% Performance Limit");
        _utility = _pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        _gpuEngines = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        _pClass = _coreClass.Count > 0 ? _coreClass.Values.Max() : 0;
        _pdh.Collect();
    }

    public TelemetrySample Read()
    {
        lock (_gate) return ReadLocked();
    }

    TelemetrySample ReadLocked()
    {
        _pdh.Collect();
        var power = SystemInfo.Power();
        // Brightness goes through WMI (~20 ms), so refresh it every few reads rather than every second.
        bool readBrightness = _reads++ % 3 == 0;
        var display = SystemInfo.Display(readBrightness);
        if (readBrightness) _brightness = display.Brightness;

        var rapl = _pdh.Values(_rapl);
        double? Watts(string domain) => rapl.TryGetValue($"RAPL_Package0_{domain}", out var mw) ? mw / 1000 : null;

        var clocks = _gpu?.Read();
        var (busy, engine) = BusiestEngine(_pdh.Values(_gpuEngines));

        return new TelemetrySample(
            DateTime.UtcNow,
            BatteryTelemetry.Read(),
            power,
            SystemInfo.WindowsPowerMode(power),
            display with { Brightness = _brightness },
            new PowerBreakdown(Watts("PKG"), Watts("PP0"), Watts("PP1"), Watts("DRAM")),
            Cpu(),
            new GpuActivity(clocks, busy, engine));
    }

    CpuActivity Cpu()
    {
        var perf = _pdh.Values(_perf);
        var freq = _pdh.Values(_freq);
        var (pTotal, pCount, eTotal, eCount) = (0.0, 0, 0.0, 0);
        foreach (var (instance, cls) in _coreClass)
        {
            if (!perf.TryGetValue(instance, out var percent) || !freq.TryGetValue(instance, out var nominal)) continue;
            var mhz = nominal * percent / 100;
            if (cls == _pClass) { pTotal += mhz; pCount++; } else { eTotal += mhz; eCount++; }
        }
        int pCores = _coreClass.Values.Count(c => c == _pClass);
        return new CpuActivity(
            pCount > 0 ? pTotal / pCount : null,
            eCount > 0 ? eTotal / eCount : null,
            pCores, _coreClass.Count - pCores,
            _pdh.Value(_utility) is { } u ? Math.Min(u, 100) : null,
            _pdh.Value(_limit));
    }

    /// <summary>
    /// GPU Engine instances look like "pid_123_luid_0x0_0x13e93_phys_0_eng_0_engtype_3D". Sum each engine
    /// across processes, then report the busiest engine. NPU ("neural") and housekeeping engines are skipped.
    /// </summary>
    public static (double? Percent, string? Engine) BusiestEngine(IReadOnlyDictionary<string, double> instances)
    {
        var engines = new Dictionary<string, (string Type, double Total)>();
        foreach (var (name, value) in instances)
        {
            int luid = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            int type = name.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (luid < 0 || type < 0) continue;
            var label = EngineLabel(name[(type + 9)..]);
            if (label is null) continue;
            var key = name[luid..type];
            engines[key] = (label, engines.GetValueOrDefault(key).Total + value);
        }
        if (engines.Count == 0) return (null, null);
        var top = engines.Values.MaxBy(e => e.Total);
        return (Math.Min(top.Total, 100), top.Total >= 1 ? top.Type : null);
    }

    static string? EngineLabel(string type) => type.ToLowerInvariant() switch
    {
        "3d" => "3D",
        "compute" => "Compute",
        "copy" => "Copy",
        "videodecode" => "Video decode",
        "videoencode" => "Video encode",
        "videoprocessing" => "Video processing",
        _ => null
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _gpu?.Dispose();
            _pdh.Dispose();
        }
    }
}

/// <summary>
/// Smooths battery draw over about a minute so time-left doesn't jump with every spike, and totals
/// the energy drawn while discharging. Resets when the battery stops discharging or after a gap
/// (sleep, paused sampling).
/// </summary>
public sealed class BatteryEstimator
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinCoverage = TimeSpan.FromSeconds(60);
    static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(5);

    DateTime? _last;
    double? _averageW;
    TimeSpan _covered;

    /// <summary>Energy drawn from the battery since the app opened, in Wh.</summary>
    public double UsedWh { get; private set; }

    public double? AverageW => _covered >= MinCoverage ? _averageW : null;

    public void Add(DateTime at, BatterySnapshot battery)
    {
        var gap = _last is { } last ? at - last : TimeSpan.Zero;
        _last = at;
        if (battery.State != "Discharging" || battery.Watts is not { } signed)
        {
            (_averageW, _covered) = (null, TimeSpan.Zero);
            return;
        }
        var watts = Math.Abs(signed);
        if (_averageW is not { } average || gap > MaxGap)
        {
            (_averageW, _covered) = (watts, TimeSpan.Zero);
            return;
        }
        var alpha = 1 - Math.Exp(-gap.TotalSeconds / Window.TotalSeconds);
        _averageW = average + alpha * (watts - average);
        _covered += gap;
        UsedWh += watts * gap.TotalHours;
    }

    /// <summary>Remaining energy at the smoothed draw; null until there's enough data.</summary>
    public TimeSpan? TimeLeft(BatterySnapshot battery) =>
        AverageW is { } w && w > 0.5 && battery.RemainingWh is { } wh ? TimeSpan.FromHours(wh / w) : null;

    /// <summary>Rough time to full while charging. Charging slows near full, so treat as a lower bound.</summary>
    public static TimeSpan? TimeToFull(BatterySnapshot battery) =>
        battery.State == "Charging" && battery.Watts is { } w && w > 1 && battery.FullWh is { } full && battery.RemainingWh is { } now && full > now
            ? TimeSpan.FromHours((full - now) / w)
            : null;
}
