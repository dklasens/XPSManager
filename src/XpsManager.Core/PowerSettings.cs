namespace XpsManager.Core;

/// <param name="ShortLabel">Compact label for tight controls; defaults to <paramref name="Label"/>.</param>
public sealed record SettingOption(string Value, string Label, string Description, string? ShortLabel = null)
{
    public string ShortLabel { get; } = ShortLabel ?? Label;
}

/// <summary>The power-related BIOS settings the app manages, as currently set.</summary>
public sealed record PowerState(
    string ThermalMode,
    string ChargeMode,
    int ChargeStart,
    int ChargeStop,
    IReadOnlyList<SettingOption> ThermalModes,
    IReadOnlyList<SettingOption> ChargeModes,
    (int Min, int Max) ChargeStartRange,
    (int Min, int Max) ChargeStopRange)
{
    public PowerChange ToChange() => new(ThermalMode, ChargeMode, ChargeStart, ChargeStop);
}

public sealed record PowerChange(string ThermalMode, string ChargeMode, int ChargeStart, int ChargeStop);

public sealed class PowerSettings(IBiosClient bios)
{
    public const string ThermalAttr = "ThermalManagement";
    public const string ChargeModeAttr = "PrimaryBattChargeCfg";
    public const string ChargeStartAttr = "CustomChargeStart";
    public const string ChargeStopAttr = "CustomChargeStop";
    public const string CustomChargeMode = "Custom";
    public const string ExpressChargeMode = "Express";
    public const string PrimarilyAcChargeMode = "PrimAcUse";

    public BiosAttribute ReadChargeMode()
    {
        using var operation = EnterOperation();
        return Require(bios.ReadAttributes([ChargeModeAttr]), ChargeModeAttr);
    }

    /// <summary>Changes only charging policy, preserving thermal mode and custom thresholds.</summary>
    public void SetChargeMode(string value)
    {
        using var operation = EnterOperation();
        var current = ReadChargeMode();
        if (current.CurrentValue == value) return;
        if (current.IsReadOnly || !current.PossibleValues.Contains(value, StringComparer.Ordinal))
            throw new BiosException($"Charge mode '{value}' is not writable on this BIOS.");
        bios.SetAttribute(ChargeModeAttr, value);
        if (ReadChargeMode().CurrentValue != value)
            throw new BiosException("Charge mode did not verify after writing.");
    }

    /// <summary>Dell requires the stop threshold to be at least this far above the start.</summary>
    public const int MinChargeGap = 5;

    public IDisposable EnterOperation() => bios.EnterOperation();

    public BiosAttribute ReadThermal()
    {
        using var operation = EnterOperation();
        return Require(bios.ReadAttributes([ThermalAttr]), ThermalAttr);
    }

    /// <summary>Service boundary: can write only the thermal attribute, after fresh validation.</summary>
    public void SetThermal(string value)
    {
        using var operation = EnterOperation();
        var current = ReadThermal();
        if (current.IsReadOnly || !current.PossibleValues.Contains(value, StringComparer.Ordinal))
            throw new BiosException($"Thermal mode '{value}' is not writable on this BIOS.");
        if (current.CurrentValue == value) return;
        bios.SetAttribute(ThermalAttr, value);
        if (ReadThermal().CurrentValue != value) throw new BiosException("Thermal mode did not verify after writing.");
    }

    static readonly SettingOption[] KnownThermalModes =
    [
        new("Optimized", "Optimized", "Balanced performance, fan noise and temperature. BIOS default."),
        new("Cool", "Cool", "Keeps the chassis cooler; performance and fans are reduced to match."),
        new("Quiet", "Quiet", "Keeps fan noise down at the cost of performance."),
        new("Performance", "Ultra Performance", "Maximum sustained performance; more heat and fan noise.", "Ultra"),
        new("UltraPerformance", "Ultra Performance", "Maximum sustained performance; more heat and fan noise.", "Ultra"),
    ];

    static readonly SettingOption[] KnownChargeModes =
    [
        new("Adaptive", "Adaptive", "BIOS learns your usage and adjusts charging to suit."),
        new("Standard", "Standard", "Charges fully at a standard rate."),
        new("Express", "ExpressCharge", "Charges faster; more wear on the battery over time.", "Express"),
        new("PrimAcUse", "Primarily AC use", "Holds a lower charge to extend life when mostly plugged in.", "Mostly AC"),
        new(CustomChargeMode, "Custom", "You choose when charging starts and stops."),
    ];

    public PowerState Read()
    {
        using var operation = EnterOperation();
        var attrs = bios.ReadAttributes([ThermalAttr, ChargeModeAttr, ChargeStartAttr, ChargeStopAttr]);
        var thermal = Require(attrs, ThermalAttr);
        var charge = Require(attrs, ChargeModeAttr);
        var start = Require(attrs, ChargeStartAttr);
        var stop = Require(attrs, ChargeStopAttr);

        return new PowerState(
            ThermalMode: thermal.CurrentValue,
            ChargeMode: charge.CurrentValue,
            ChargeStart: int.Parse(start.CurrentValue),
            ChargeStop: int.Parse(stop.CurrentValue),
            ThermalModes: OptionsFor(thermal, KnownThermalModes),
            ChargeModes: OptionsFor(charge, KnownChargeModes),
            ChargeStartRange: start.IsReadOnly ? (int.Parse(start.CurrentValue), int.Parse(start.CurrentValue)) : (start.Min ?? 0, start.Max ?? 100),
            ChargeStopRange: stop.IsReadOnly ? (int.Parse(stop.CurrentValue), int.Parse(stop.CurrentValue)) : (stop.Min ?? 0, stop.Max ?? 100));
    }

    /// <summary>Writes only what differs from <paramref name="current"/>, then re-reads and verifies.</summary>
    public PowerState Apply(PowerState current, PowerChange desired)
    {
        using var operation = EnterOperation();
        var fresh = Read();
        // Preserve fields the caller did not edit, even if another process changed them.
        desired = desired with
        {
            ThermalMode = desired.ThermalMode == current.ThermalMode ? fresh.ThermalMode : desired.ThermalMode,
            ChargeMode = desired.ChargeMode == current.ChargeMode ? fresh.ChargeMode : desired.ChargeMode,
            ChargeStart = desired.ChargeStart == current.ChargeStart ? fresh.ChargeStart : desired.ChargeStart,
            ChargeStop = desired.ChargeStop == current.ChargeStop ? fresh.ChargeStop : desired.ChargeStop
        };
        current = fresh;
        Validate(current, desired);

        if (desired.ThermalMode != current.ThermalMode)
            bios.SetAttribute(ThermalAttr, desired.ThermalMode);

        if (desired.ChargeMode != current.ChargeMode)
            bios.SetAttribute(ChargeModeAttr, desired.ChargeMode);

        // Thresholds are only writable in Custom mode. Order the two writes so that
        // start < stop holds after each one, whichever direction they move.
        if (desired.ChargeMode == CustomChargeMode)
        {
            var writes = new List<(string Attr, int Value)>();
            if (desired.ChargeStart != current.ChargeStart) writes.Add((ChargeStartAttr, desired.ChargeStart));
            if (desired.ChargeStop != current.ChargeStop) writes.Add((ChargeStopAttr, desired.ChargeStop));
            if (desired.ChargeStop > current.ChargeStop) writes.Reverse();
            foreach (var (attr, value) in writes)
                bios.SetAttribute(attr, value.ToString());
        }

        var after = Read();
        var mismatch =
            after.ThermalMode != desired.ThermalMode ||
            after.ChargeMode != desired.ChargeMode ||
            (desired.ChargeMode == CustomChargeMode &&
             (after.ChargeStart != desired.ChargeStart || after.ChargeStop != desired.ChargeStop));
        if (mismatch)
            throw new BiosException(
                $"BIOS accepted the change but reads back differently: " +
                $"thermal={after.ThermalMode}, charge={after.ChargeMode} {after.ChargeStart}-{after.ChargeStop}%.");

        return after;
    }

    static void Validate(PowerState current, PowerChange desired)
    {
        if (current.ThermalMode != desired.ThermalMode && current.ThermalModes.All(o => o.Value != desired.ThermalMode))
            throw new ArgumentException($"Unsupported thermal mode '{desired.ThermalMode}'.");
        if (current.ChargeMode != desired.ChargeMode && current.ChargeModes.All(o => o.Value != desired.ChargeMode))
            throw new ArgumentException($"Unsupported charge mode '{desired.ChargeMode}'.");
        if (desired.ChargeMode != CustomChargeMode)
            return;

        var (startMin, startMax) = current.ChargeStartRange;
        var (stopMin, stopMax) = current.ChargeStopRange;
        if (desired.ChargeStart < startMin || desired.ChargeStart > startMax)
            throw new ArgumentException($"Charge start must be {startMin}-{startMax}%.");
        if (desired.ChargeStop < stopMin || desired.ChargeStop > stopMax)
            throw new ArgumentException($"Charge stop must be {stopMin}-{stopMax}%.");
        if (desired.ChargeStop - desired.ChargeStart < MinChargeGap)
            throw new ArgumentException($"Charge stop must be at least {MinChargeGap}% above start.");
    }

    static BiosAttribute Require(IReadOnlyDictionary<string, BiosAttribute> attrs, string name) =>
        attrs.TryGetValue(name, out var a) ? a : throw new BiosException($"BIOS attribute '{name}' not found.");

    // Show only what this BIOS offers; fall back to the raw value for anything unrecognised.
    static IReadOnlyList<SettingOption> OptionsFor(BiosAttribute attr, SettingOption[] known) =>
        (attr.IsReadOnly ? new[] { attr.CurrentValue } : attr.PossibleValues)
            .Select(v => known.FirstOrDefault(k => k.Value == v) ?? new SettingOption(v, v, ""))
            .ToList();
}
