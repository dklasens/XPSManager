using XpsManager.Core;

internal static class PowerSettingsChecks
{
    public static void Run()
    {
        var bios = new FakeBios();
        var power = new PowerSettings(bios);
        var initial = power.Read();
        power.Apply(initial, initial.ToChange());
        Check(bios.Writes.Count == 0, "unchanged state causes no writes");
        Reject(() => power.Apply(initial, initial.ToChange() with { ThermalMode = "Invalid" }));
        Reject(() => power.Apply(initial, initial.ToChange() with { ChargeMode = "Custom", ChargeStart = 78, ChargeStop = 80 }));
        Check(bios.Writes.Count == 0, "validation precedes all writes");

        var higher = power.Apply(initial, initial.ToChange() with { ChargeMode = "Custom", ChargeStart = 85, ChargeStop = 95 });
        Check(string.Join(",", bios.Writes) == "PrimaryBattChargeCfg,CustomChargeStop,CustomChargeStart", "raise stop before raising start");
        bios.Writes.Clear();
        power.Apply(higher, higher.ToChange() with { ChargeStart = 50, ChargeStop = 60 });
        Check(string.Join(",", bios.Writes) == "CustomChargeStart,CustomChargeStop", "lower start before lowering stop");

        var stale = power.Read();
        bios.Change(PowerSettings.ThermalAttr, "Quiet"); // Service change while the user edits charging.
        bios.Writes.Clear();
        var merged = power.Apply(stale, stale.ToChange() with { ChargeStop = 70 });
        Check(merged.ThermalMode == "Quiet" && !bios.Writes.Contains(PowerSettings.ThermalAttr), "preserve unedited concurrent thermal change");

        bios.Attributes[PowerSettings.ThermalAttr] = bios.Attributes[PowerSettings.ThermalAttr] with { IsReadOnly = true };
        var locked = power.Read();
        Reject(() => power.Apply(locked, locked.ToChange() with { ThermalMode = "Cool" }));
        Reject(() => power.SetThermal("Cool"));
        bios.Attributes[PowerSettings.ChargeStartAttr] = bios.Attributes[PowerSettings.ChargeStartAttr] with { IsReadOnly = true };
        locked = power.Read();
        Reject(() => power.Apply(locked, locked.ToChange() with { ChargeStart = 55 }));

        var failing = new FakeBios { FailAttribute = PowerSettings.ChargeModeAttr };
        var failurePower = new PowerSettings(failing);
        var before = failurePower.Read();
        Reject(() => failurePower.Apply(before, before.ToChange() with { ThermalMode = "Cool", ChargeMode = "Custom" }));
        Check(failurePower.Read().ThermalMode == "Cool", "partial failure is not misreported as atomic rollback");
        Check(failing.Writes.SequenceEqual([PowerSettings.ThermalAttr]), "stop writing immediately on failure");

        var ignored = new FakeBios { IgnoreWrites = true };
        Reject(() => new PowerSettings(ignored).SetThermal("Cool"));
        Console.WriteLine("PASS: BIOS validation, read-only attributes, write ordering, concurrent edits and failure checks");
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }

    static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or BiosException) { return; }
        throw new Exception("FAILED: unsafe/unsuccessful operation was accepted");
    }

    sealed class FakeBios : IBiosClient
    {
        int _depth;
        public string? FailAttribute { get; init; }
        public bool IgnoreWrites { get; init; }
        public List<string> Writes { get; } = [];
        public Dictionary<string, BiosAttribute> Attributes { get; } = new()
        {
            [PowerSettings.ThermalAttr] = new(PowerSettings.ThermalAttr, "Thermal", "Optimized", "Optimized", false, ["Optimized", "Quiet", "Cool", "UltraPerformance"]),
            [PowerSettings.ChargeModeAttr] = new(PowerSettings.ChargeModeAttr, "Charging", "Adaptive", "Adaptive", false, ["Adaptive", "Custom"]),
            [PowerSettings.ChargeStartAttr] = new(PowerSettings.ChargeStartAttr, "Start", "50", "50", false, [], 50, 95),
            [PowerSettings.ChargeStopAttr] = new(PowerSettings.ChargeStopAttr, "Stop", "80", "80", false, [], 55, 100)
        };

        public IDisposable EnterOperation() { _depth++; return new Scope(() => _depth--); }
        public IReadOnlyDictionary<string, BiosAttribute> ReadAttributes(IEnumerable<string> names)
        {
            Check(_depth > 0, "reads are coordinated");
            return names.ToDictionary(n => n, n => Attributes[n]);
        }
        public void Change(string name, string value) => Attributes[name] = Attributes[name] with { CurrentValue = value };
        public void SetAttribute(string name, string value)
        {
            Check(_depth > 0, "writes are coordinated");
            if (name == FailAttribute) throw new BiosException("Injected firmware failure");
            Check(!Attributes[name].IsReadOnly, "never attempt a read-only write");
            if (name is PowerSettings.ChargeStartAttr or PowerSettings.ChargeStopAttr)
            {
                Check(Attributes[PowerSettings.ChargeModeAttr].CurrentValue == "Custom", "custom mode precedes threshold writes");
                var start = name == PowerSettings.ChargeStartAttr ? int.Parse(value) : int.Parse(Attributes[PowerSettings.ChargeStartAttr].CurrentValue);
                var stop = name == PowerSettings.ChargeStopAttr ? int.Parse(value) : int.Parse(Attributes[PowerSettings.ChargeStopAttr].CurrentValue);
                Check(stop - start >= 5, "every intermediate threshold pair is valid");
            }
            Writes.Add(name);
            if (!IgnoreWrites) Change(name, value);
        }
        sealed class Scope(Action release) : IDisposable { public void Dispose() => release(); }
    }
}
