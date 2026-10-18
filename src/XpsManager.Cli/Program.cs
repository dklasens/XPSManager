using System.Diagnostics;
using XpsManager.Core;

// Usage (elevated):
//   xpsm status
//   xpsm thermal <Optimized|Cool|Quiet|Performance>
//   xpsm charge <Adaptive|Standard|Express|PrimAcUse>
//   xpsm charge Custom <start> <stop>
try
{
    var sw = Stopwatch.StartNew();
    var power = new PowerSettings(new BiosClient(BiosClient.DefaultKeyCachePath));
    var state = power.Read();
    Console.WriteLine($"# read in {sw.ElapsedMilliseconds} ms");

    switch (args)
    {
        case [] or ["status"]:
            break;
        case ["thermal", var mode]:
            state = Apply(power, state, state.ToChange() with { ThermalMode = mode });
            break;
        case ["charge", var mode]:
            state = Apply(power, state, state.ToChange() with { ChargeMode = mode });
            break;
        case ["charge", var mode, var start, var stop]:
            state = Apply(power, state, state.ToChange() with { ChargeMode = mode, ChargeStart = int.Parse(start), ChargeStop = int.Parse(stop) });
            break;
        default:
            Console.Error.WriteLine("usage: xpsm [status | thermal <mode> | charge <mode> [start stop]]");
            return 2;
    }

    Print(state);
    return 0;
}
catch (Exception e) when (e is BiosException or ArgumentException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static PowerState Apply(PowerSettings power, PowerState state, PowerChange change)
{
    var sw = Stopwatch.StartNew();
    var result = power.Apply(state, change);
    Console.WriteLine($"# applied + verified in {sw.ElapsedMilliseconds} ms");
    return result;
}

static void Print(PowerState s)
{
    Console.WriteLine($"thermal: {s.ThermalMode}   [{string.Join('|', s.ThermalModes.Select(o => o.Value))}]");
    Console.WriteLine($"charge:  {s.ChargeMode}   [{string.Join('|', s.ChargeModes.Select(o => o.Value))}]");
    Console.WriteLine($"custom:  {s.ChargeStart}-{s.ChargeStop}%   (start {s.ChargeStartRange.Min}-{s.ChargeStartRange.Max}, stop {s.ChargeStopRange.Min}-{s.ChargeStopRange.Max})");
}
