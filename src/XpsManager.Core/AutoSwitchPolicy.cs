namespace XpsManager.Core;

public enum WindowsPowerMode { EnergySaver, BestPowerEfficiency, Balanced, BetterPerformance, BestPerformance, Custom }

public sealed record AutoSwitchConfiguration(bool Enabled, IReadOnlyDictionary<WindowsPowerMode, string> Mapping)
{
    public static AutoSwitchConfiguration Default => new(false, new Dictionary<WindowsPowerMode, string>
    {
        [WindowsPowerMode.EnergySaver] = "Quiet",
        [WindowsPowerMode.BestPowerEfficiency] = "Quiet",
        [WindowsPowerMode.Balanced] = "Optimized",
        [WindowsPowerMode.BetterPerformance] = "Optimized",
        [WindowsPowerMode.BestPerformance] = "UltraPerformance"
    });
}

public sealed record PolicyDecision(string? Target, string? Error = null);

public static class AutoSwitchPolicy
{
    public static PolicyDecision Evaluate(WindowsPowerMode mode, AutoSwitchConfiguration config, IEnumerable<string> possible)
    {
        if (!config.Enabled || mode == WindowsPowerMode.Custom ||
            !config.Mapping.TryGetValue(mode, out var target) || string.IsNullOrEmpty(target)) return new(null);
        var values = possible.ToArray();
        // Dell firmware uses both spellings. Resolve only these known aliases, then validate against this BIOS.
        if (target == "UltraPerformance" && !values.Contains(target) && values.Contains("Performance")) target = "Performance";
        else if (target == "Performance" && !values.Contains(target) && values.Contains("UltraPerformance")) target = "UltraPerformance";
        return values.Contains(target, StringComparer.Ordinal)
            ? new(target) : new(null, $"Unsupported thermal mapping '{target}' for {mode}; skipped.");
    }
}

/// <summary>Pure trailing-edge debounce; the owner serializes access and supplies a monotonic clock.</summary>
public sealed class SwitchDebounce
{
    long? _due;
    string _trigger = "";
    bool _force;
    public int Remaining(long now) => _due is { } due ? (int)Math.Max(1, due - now) : Timeout.Infinite;
    public void Signal(long now, string trigger, bool force = false)
    {
        _due = now + 2000;
        _trigger = trigger;
        _force |= force;
    }
    public (string Trigger, bool Force)? Take(long now)
    {
        if (_due is null || now < _due) return null;
        _due = null;
        var result = (_trigger, _force);
        _force = false;
        return result;
    }
}
