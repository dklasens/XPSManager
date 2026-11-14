using System.Diagnostics;
using XpsManager.Core;
using System.ServiceProcess;

// Usage (elevated):
//   xpsm status
//   xpsm thermal <Optimized|Cool|Quiet|Performance>
//   xpsm charge <Adaptive|Standard|Express|PrimAcUse>
//   xpsm charge Custom <start> <stop>
try
{
    if (args is ["auto", var action])
    {
        using var service = new ServiceController("XpsManagerService");
        if (action is "on" or "off")
        {
            var status = service.Status; // Fail before saving when the service isn't installed.
            AutoSwitchStore.Write(AutoSwitchStore.Read() with { Enabled = action == "on" });
            if (status != ServiceControllerStatus.Running && action == "on")
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Running) service.ExecuteCommand(128);
        }
        else if (action != "status") throw new ArgumentException("usage: xpsm auto status|on|off");
        var config = AutoSwitchStore.Read();
        Console.WriteLine($"automatic: {(config.Enabled ? "on" : "off")}");
        try { Console.WriteLine($"service: {service.Status}"); }
        catch (InvalidOperationException) { Console.WriteLine("service: not installed"); }
        foreach (var row in config.Mapping) Console.WriteLine($"{row.Key}: {row.Value}");
        Console.WriteLine(AutoSwitchStore.Status());
        return 0;
    }
    var sw = Stopwatch.StartNew();
    var power = new PowerSettings(new BiosClient());
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
            Console.Error.WriteLine("usage: xpsm [status | thermal <mode> | charge <mode> [start stop] | auto status|on|off]");
            return 2;
    }

    Print(state);
    return 0;
}
catch (Exception e) when (e is BiosException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
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
