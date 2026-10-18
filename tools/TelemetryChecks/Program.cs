using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XpsManager.Core;
using XpsManager.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
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
            BatteryTelemetry.Decode(0x80000000, 2, -10106, 12923, 60403, 68607, 23, 70000),
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
    }

    static void Check(bool passed, string name) { if (!passed) throw new Exception($"Failed: {name}"); }
}
