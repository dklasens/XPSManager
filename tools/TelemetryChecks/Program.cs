using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XpsManager.Core;
using XpsManager.App;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception e)
        {
            // Test failures must produce a nonzero CI/console result, never a Windows crash dialog.
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    static void Run(string[] args)
    {
        var discharge = BatteryTelemetry.Decode(0x80000000, 2, -10106, 12923, 63873, 68607, 23);
        Check(discharge.State == "Discharging" && discharge.Watts == -10.106 && discharge.Volts == 12.923
            && discharge.RemainingWh == 63.873 && discharge.FullWh == 68.607 && discharge.Cycles == 23, "absolute units");
        Check(BatteryTelemetry.Decode(0, 5, 20000, 12000, 1, 2, 1).State == "Charging", "charging on AC");
        Check(BatteryTelemetry.Decode(0, 1, 0, 12000, 1, 2, 1).State == "Idle", "idle on AC");
        var unknown = BatteryTelemetry.Decode(0, 0, int.MinValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, 0);
        Check(unknown.Watts is null && unknown.Volts is null && unknown.RemainingWh is null && unknown.FullWh is null && unknown.Cycles is null, "unknown values");
        var relative = BatteryTelemetry.Decode(0x40000000, 2, -50, 12000, 80, 100, 23);
        Check(relative.Watts is null && relative.RemainingWh is null && relative.FullWh is null && relative.Volts == 12, "relative units");
        Console.WriteLine("PASS: battery decoding checks");
        var health = BatteryTelemetry.Decode(0, 2, -10000, 12000, 63000, 68600, 23, 70000);
        Check(Math.Abs(health.HealthPercent!.Value - 98) < 0.001, "health uses full capacity, not remaining charge");
        Check(BatteryTelemetry.Decode(0, 1, 0, 12000, 1, 2, 1, 0).HealthPercent is null, "zero design capacity");
        Check(BatteryTelemetry.Decode(0x40000000, 1, 0, 12000, 1, 2, 1, 3).HealthPercent is null, "relative health unavailable");
        Check(unknown.HealthPercent is null, "unknown health unavailable");
        Console.WriteLine("PASS: battery health checks");

        EstimatorChecks();
        Console.WriteLine("PASS: battery estimator checks");
        GpuChecks();
        Console.WriteLine("PASS: GPU decoding checks");
        AutoSwitchChecks();
        Console.WriteLine("PASS: automatic thermal policy and debounce checks");
        BatteryIdentityChecks();
        Console.WriteLine("PASS: battery firmware identity checks");

        if (args.Contains("--live"))
        {
            // Like the app, sample off the UI thread: the Intel library doesn't initialise on an STA thread.
            Console.WriteLine(Task.Run(() =>
            {
                using var sampler = new TelemetrySampler();
                sampler.Read();
                Thread.Sleep(1000);
                return sampler.Read();
            }).Result);
            if (IntelGraphics.OpenError is { } error) Console.WriteLine($"Intel graphics library: {error}");
        }
        if (args.Contains("--render")) Render();
        if (args is ["--service-checks", var exe]) ServiceChecks.Run(exe);
    }

    static void AutoSwitchChecks()
    {
        string[] possible = ["Quiet", "Cool", "Optimized", "UltraPerformance"];
        var config = AutoSwitchConfiguration.Default with { Enabled = true };
        foreach (var (mode, expected) in config.Mapping)
            Check(AutoSwitchPolicy.Evaluate(mode, config, possible).Target == expected, $"default {mode}");
        Check(AutoSwitchPolicy.Evaluate(WindowsPowerMode.BestPerformance, config with { Enabled = false }, possible).Target is null, "disabled never writes");
        Check(AutoSwitchPolicy.Evaluate(WindowsPowerMode.Custom, config, possible).Target is null, "unknown overlay never writes");
        var missing = AutoSwitchPolicy.Evaluate(WindowsPowerMode.BestPerformance, config, ["Quiet"]);
        Check(missing.Target is null && missing.Error is not null, "BIOS rejects unsupported mapping");
        Check(AutoSwitchPolicy.Evaluate(WindowsPowerMode.BestPerformance, config, ["Performance"]).Target == "Performance", "legacy BIOS performance spelling");
        var mapping = config.Mapping.ToDictionary(p => p.Key, p => p.Value);
        mapping[WindowsPowerMode.Balanced] = "";
        Check(AutoSwitchPolicy.Evaluate(WindowsPowerMode.Balanced, config with { Mapping = mapping }, possible).Target is null, "explicit no change");
        mapping[WindowsPowerMode.Balanced] = "performance";
        Check(AutoSwitchPolicy.Evaluate(WindowsPowerMode.Balanced, config with { Mapping = mapping }, possible).Error is not null, "exact BIOS value required");
        mapping[WindowsPowerMode.Custom] = "Performance";
        Check(AutoSwitchPolicy.Evaluate(WindowsPowerMode.Custom, config with { Mapping = mapping }, possible).Target is null, "custom mapping cannot override unknown-mode safety");

        var debounce = new SwitchDebounce();
        Check(debounce.Take(5000) is null, "no timer-driven policy without an event");
        debounce.Signal(100, "unplugged", true);
        Check(debounce.Take(2099) is null, "wait full debounce interval");
        debounce.Signal(2000, "plugged in");
        Check(debounce.Take(2100) is null && debounce.Take(3999) is null, "new event extends deadline");
        Check(debounce.Take(4000) is { Trigger: "plugged in", Force: true }, "coalesces to final event and preserves forced reload");
        Check(debounce.Take(6000) is null, "single application per burst");
        debounce.Signal(7000, "power mode changed");
        Check(debounce.Take(9000) is { Force: false }, "force reset after taking event");
    }

    static void BatteryIdentityChecks()
    {
        const string ppid = "CN0ABC12ABCDE1234567A00"; // synthetic identifier, never real machine data
        byte[] Record(byte type, params string[] strings) => new byte[] { type, 4, 0, 0 }
            .Concat(System.Text.Encoding.ASCII.GetBytes(string.Join('\0', strings) + "\0\0")).ToArray();
        var battery = Record(22, "DELL ABC123", ppid);
        Check(BatteryIdentity.FindPpid(battery) == ppid, "Dell type 22 PPID");
        Check(BatteryIdentity.FindPpid(Record(1, "DELL ABC123", ppid)) is null, "ignore identifiers outside battery record");
        Check(BatteryIdentity.FindPpid(Record(22, "OTHER ABC123", ppid)) is null, "do not infer Dell identity on other batteries");
        Check(BatteryIdentity.FindPpid(battery.Concat(battery).ToArray()) is null, "multiple batteries are ambiguous");
        Check(BatteryIdentity.FindPpid(battery[..^1]) is null, "truncated string table");
        Check(BatteryIdentity.FindPpid([22, 0, 0, 0]) is null, "invalid structure length");
    }

    static void EstimatorChecks()
    {
        var t0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var tenWatts = BatteryTelemetry.Decode(0x80000000, 2, -10000, 12000, 50000, 68000, 23);
        var estimator = new BatteryEstimator();
        estimator.Add(t0, tenWatts);
        Check(estimator.AverageW is null && estimator.TimeLeft(tenWatts) is null, "no estimate from one reading");
        for (int i = 1; i <= 59; i++) estimator.Add(t0.AddSeconds(i), tenWatts);
        Check(estimator.AverageW is null, "no estimate before a full minute");
        estimator.Add(t0.AddSeconds(60), tenWatts);
        Check(Math.Abs(estimator.AverageW!.Value - 10) < 1e-9, "steady draw averages to itself");
        Check(Math.Abs(estimator.TimeLeft(tenWatts)!.Value.TotalHours - 5) < 1e-6, "50 Wh at 10 W is 5 h");
        Check(Math.Abs(estimator.UsedWh - 10 * 60 / 3600.0) < 1e-9, "energy integrates watts over time");

        // A spike moves the average only part of the way: smoothing over about a minute.
        var spike = BatteryTelemetry.Decode(0x80000000, 2, -40000, 12000, 50000, 68000, 23);
        estimator.Add(t0.AddSeconds(61), spike);
        Check(estimator.AverageW is > 10 and < 11, "one-second spike barely moves the average");

        // A gap (sleep, minimised) restarts the average but keeps the session total.
        var used = estimator.UsedWh;
        estimator.Add(t0.AddSeconds(90), tenWatts);
        Check(estimator.AverageW is null && estimator.UsedWh == used, "gap restarts averaging");

        var charging = BatteryTelemetry.Decode(0x80000000, 4, 20000, 12000, 48000, 68000, 23);
        estimator.Add(t0.AddSeconds(91), charging);
        Check(estimator.AverageW is null, "charging clears the discharge average");
        Check(Math.Abs(BatteryEstimator.TimeToFull(charging)!.Value.TotalHours - 1) < 1e-6, "20 Wh to go at 20 W is 1 h");
        Check(BatteryEstimator.TimeToFull(tenWatts) is null, "no time-to-full while discharging");

        Check(TelemetryViewModel.Duration(TimeSpan.FromMinutes(250)) == "4 h 10 m", "hours and minutes");
        Check(TelemetryViewModel.Duration(TimeSpan.FromSeconds(20)) == "1 m", "under a minute rounds up to 1 m");
    }

    static void GpuChecks()
    {
        var engines = new Dictionary<string, double>
        {
            ["pid_1_luid_0x0_0x13e93_phys_0_eng_0_engtype_3D"] = 20,
            ["pid_2_luid_0x0_0x13e93_phys_0_eng_0_engtype_3D"] = 25,      // same engine, other process: summed
            ["pid_1_luid_0x0_0x13e93_phys_0_eng_3_engtype_VideoDecode"] = 30,
            ["pid_3_luid_0x0_0x2_phys_0_eng_0_engtype_Neural"] = 90,       // NPU: ignored
            ["pid_1_luid_0x0_0x13e93_phys_0_eng_9_engtype_"] = 70,         // untyped housekeeping: ignored
        };
        var (busy, engine) = TelemetrySampler.BusiestEngine(engines);
        Check(busy == 45 && engine == "3D", "busiest engine sums processes and skips NPU");
        Check(TelemetrySampler.BusiestEngine(new Dictionary<string, double>()).Percent is null, "no engines, no value");
        var idle = TelemetrySampler.BusiestEngine(new Dictionary<string, double> { ["pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D"] = 0.2 });
        Check(idle.Percent == 0.2 && idle.Engine is null, "idle GPU names no engine");

        Check(IntelGraphics.DescribeThrottle(0) is null, "no throttle");
        Check(IntelGraphics.DescribeThrottle(IntelGraphics.ThrottleSoftwareRange | IntelGraphics.ThrottleHardwareRange) is null, "ranges aren't limits");
        Check(IntelGraphics.DescribeThrottle(IntelGraphics.ThrottleBurstPower | IntelGraphics.ThrottleThermal) == "Power limit, Thermal", "combined reasons");
    }

    static void Render()
    {
        var app = new Application();
        foreach (var name in new[] { "Tokens", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/XpsManager;component/Themes/{name}.xaml") });
        var sample = new TelemetrySample(
            DateTime.UtcNow,
            BatteryTelemetry.Decode(0x80000000, 2, -10106, 12923, 60403, 68607, 23, 70000)
                with { Serial = "00E1", Ppid = "CN0ABC12ABCDE1234567A00" },
            new PowerStatus(false, 88, TimeSpan.FromMinutes(272)),
            "Best power efficiency",
            new DisplayState(120, true, 60, 60),
            new PowerBreakdown(3.2, 1.8, 0.1, 1.1),
            new CpuActivity(3100, 2000, 4, 12, 23, 85),
            new GpuActivity(new GpuClocks(900, 900, 2500, 400, 0), 4, "3D"));
        var panel = new ActivityPanel { Width = 544, DataContext = new TelemetryViewModel(sample) };
        var border = new System.Windows.Controls.Border { Background = (Brush)app.FindResource("Paper"), Padding = new Thickness(28), Child = panel };
        border.Measure(new Size(600, 1000));
        border.Arrange(new Rect(border.DesiredSize));
        border.UpdateLayout();
        var bitmap = new RenderTargetBitmap(600, (int)Math.Ceiling(border.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(border);
        Directory.CreateDirectory("artifacts");
        using var file = File.Create("artifacts/activity-panel.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
        Console.WriteLine("Rendered artifacts/activity-panel.png (fixture data)");
        RenderAutomatic(app);
        RenderSettings(app, sample);
    }

    static void RenderSettings(Application app, TelemetrySample sample)
    {
        var automatic = new AutomaticViewModel();
        automatic.SetOptions([new("Quiet", "Quiet", ""), new("Cool", "Cool", ""), new("Optimized", "Optimized", ""), new("UltraPerformance", "Ultra Performance", "")]);
        automatic.Rows.Add(new("Best power efficiency", "Also used for Energy saver", WindowsPowerMode.BestPowerEfficiency, "Quiet"));
        automatic.Rows.Add(new("Balanced", "Also used for Better performance", WindowsPowerMode.Balanced, "Optimized"));
        automatic.Rows.Add(new("Best performance", "Maximum performance in Windows", WindowsPowerMode.BestPerformance, "UltraPerformance"));
        OptionViewModel Option(string value, string label, string? shortLabel = null) => new(new(value, label, "", shortLabel), value == "Quiet", _ => { });
        var window = new MainWindow { DataContext = new
        {
            Device = new DeviceInfo("XPS 14 DA14260", "fixture"), Live = new TelemetryViewModel(sample),
            ActiveThermal = "Quiet", ActiveCharge = "Custom 50–80%", PendingThermal = "", PendingCharge = "",
            HasPendingThermal = false, HasPendingCharge = false, Status = "Preview · fixture data", StatusIsError = false,
            CanRefresh = true, IsLoaded = true, CanApply = false, ApplyLabel = "Write to BIOS", IsCustomCharge = false,
            SelectedThermalDescription = "Keeps fan noise down at the cost of performance.",
            SelectedChargeDescription = "You choose when charging starts and stops.", Automatic = automatic,
            ThermalOptions = new[] { Option("Optimized", "Optimized"), Option("Cool", "Cool"), Option("Quiet", "Quiet"), Option("UltraPerformance", "Ultra Performance", "Ultra") },
            ChargeOptions = new[] { Option("Adaptive", "Adaptive"), Option("Standard", "Standard"), Option("Express", "Express"), Option("PrimAcUse", "Primarily AC", "Mostly AC"), Option("Custom", "Custom") }
        }};
        // Render the actual content without showing the window or invoking its live Loaded handler.
        var content = (FrameworkElement)window.Content;
        ((System.Windows.Controls.DockPanel)content).Background = (Brush)app.FindResource("Paper");
        content.Width = 600;
        content.Height = 820;
        content.Measure(new Size(600, 820));
        content.Arrange(new Rect(0, 0, 600, 820));
        app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(600, 820, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        using var output = File.Create("artifacts/settings-v2.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
        Console.WriteLine("Rendered artifacts/settings-v2.png (fixture data)");
    }

    static void RenderAutomatic(Application app)
    {
        var model = new AutomaticViewModel();
        model.SetOptions([new("Quiet", "Quiet", ""), new("Optimized", "Optimized", ""), new("Performance", "Ultra Performance", "")]);
        model.Rows.Add(new("Best power efficiency", "Also used for Energy saver", WindowsPowerMode.BestPowerEfficiency, "Quiet"));
        model.Rows.Add(new("Balanced", "Also used for Better performance", WindowsPowerMode.Balanced, "Optimized"));
        model.Rows.Add(new("Best performance", "Maximum performance in Windows", WindowsPowerMode.BestPerformance, "Performance"));
        var panel = new AutomaticPanel { Width = 528, DataContext = model };
        var border = new System.Windows.Controls.Border { Background = (Brush)app.FindResource("Paper"), Padding = new Thickness(28), Child = panel };
        border.Measure(new Size(584, 1000));
        border.Arrange(new Rect(border.DesiredSize));
        app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        border.Measure(new Size(584, 1000));
        border.Arrange(new Rect(border.DesiredSize));
        border.UpdateLayout();
        var bitmap = new RenderTargetBitmap(584, (int)Math.Ceiling(border.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(border);
        using var output = File.Create("artifacts/automatic-panel.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
        Console.WriteLine("Rendered artifacts/automatic-panel.png (fixture data)");
    }

    static void Check(bool passed, string name) { if (!passed) throw new Exception($"Failed: {name}"); }
}
