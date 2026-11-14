using XpsManager.Core;

internal static class ChargeBoostChecks
{
    public static void Run()
    {
        var clock = new FakeClock();
        var bios = new FakeBios();
        var store = new MemoryStore();
        var power = new PowerSettings(bios);
        var boost = new ChargeBoost(power, store, clock);
        bios.BeforeWrite = () => Check(store.State is not null, "deadline persisted before firmware write");
        boost.Start();
        Check(bios.Mode == "Express" && store.State?.ExpiresAt == clock.Now.AddHours(24), "ExpressCharge with exactly 24-hour UTC deadline");
        Reject(boost.Start);
        var deadline = store.State!.ExpiresAt;
        clock.Now = deadline.AddTicks(-1);
        boost.RestoreIfExpired();
        Check(bios.Writes == 1 && store.State is not null, "no early restoration");
        clock.Now = deadline;
        // New controller stands for service/app restart: the store alone drives recovery.
        new ChargeBoost(power, store, clock).RestoreIfExpired();
        Check(bios.Mode == "PrimAcUse" && store.State is null, "restores at expiry after restart");
        boost.RestoreIfExpired();
        Check(bios.Writes == 2, "completed timer is idempotent");

        boost.Start();
        boost.ReturnNow();
        Check(bios.Mode == "PrimAcUse" && store.State is null, "early return restores and clears deadline");
        boost.ReturnNow();
        Check(bios.Writes == 4, "early return is idempotent");

        boost.Start();
        clock.Now = clock.Now.AddDays(2);
        bios.FailWrite = true;
        Reject(boost.RestoreIfExpired);
        Check(store.State is { Error.Length: > 0 } && bios.Mode == "Express", "failed overdue restoration retains deadline and error");
        bios.FailWrite = false;
        new ChargeBoost(power, store, clock).RestoreIfExpired();
        Check(bios.Mode == "PrimAcUse" && store.State is null, "retry after sleep/offline period succeeds");

        bios.IgnoreWrites = true;
        Reject(boost.Start);
        Check(store.State is { Error.Length: > 0 }, "failed start verification retains recovery intent");
        boost.ReturnNow(); // Already PrimAcUse; no write needed.
        Check(store.State is null, "can cancel failed start");
        bios.IgnoreWrites = false;

        store.FailWrite = true;
        var before = bios.Writes;
        Reject(boost.Start);
        Check(bios.Writes == before && bios.Mode == "PrimAcUse", "storage failure cannot enable ExpressCharge");
        store.FailWrite = false;

        bios.ReadOnly = true;
        Reject(boost.Start);
        Check(store.State is null && bios.Writes == before, "read-only mode rejected before persisting");
        bios.ReadOnly = false;
        bios.Values = ["Express"];
        Reject(boost.Start);
        Check(store.State is null && bios.Writes == before, "missing return mode rejected");
        bios.Values = ["PrimAcUse", "Standard"];
        Reject(boost.Start);
        Check(store.State is null && bios.Writes == before, "missing ExpressCharge rejected without Standard fallback");
        bios.Values = ["PrimAcUse", "Express"];

        boost.Start();
        bios.IgnoreWrites = true;
        Reject(boost.ReturnNow);
        Check(store.State is not null && bios.Mode == "Express", "ignored restore write retains recovery intent");
        bios.IgnoreWrites = false;
        store.FailWrite = true;
        Reject(boost.ReturnNow);
        Check(store.State is not null && bios.Mode == "PrimAcUse", "failed deadline cleanup retains retry after verified restoration");
        store.FailWrite = false;
        boost.ReturnNow();
        Check(store.State is null, "retry clears saved deadline without duplicate firmware write");
        Console.WriteLine("PASS: 24-hour ExpressCharge, restart/resume recovery, early return, unsupported/read-only BIOS, persistence and write failures");
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAILED: " + message);
    }

    static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is BiosException or InvalidOperationException or System.IO.IOException) { return; }
        throw new Exception("FAILED: expected operation to fail");
    }

    sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class MemoryStore : IChargeBoostStore
    {
        public ChargeBoostState? State;
        public bool FailWrite;
        public ChargeBoostState? Read() => State;
        public void Write(ChargeBoostState? state)
        {
            if (FailWrite) throw new System.IO.IOException("Injected storage failure");
            State = state;
        }
    }

    sealed class FakeBios : IBiosClient
    {
        int _depth;
        public string Mode = "PrimAcUse";
        public bool ReadOnly, FailWrite, IgnoreWrites;
        public string[] Values = ["Express", "PrimAcUse"];
        public int Writes;
        public Action? BeforeWrite;
        public IDisposable EnterOperation() { _depth++; return new Scope(() => _depth--); }
        public IReadOnlyDictionary<string, BiosAttribute> ReadAttributes(IEnumerable<string> names)
        {
            Check(_depth > 0, "BIOS reads are coordinated");
            Check(names.SequenceEqual([PowerSettings.ChargeModeAttr]), "timer only reads charging mode");
            return new Dictionary<string, BiosAttribute>
            {
                [PowerSettings.ChargeModeAttr] = new(PowerSettings.ChargeModeAttr, "Charging", Mode, "PrimAcUse", ReadOnly, Values)
            };
        }
        public void SetAttribute(string name, string value)
        {
            Check(_depth > 0 && !ReadOnly, "writes are coordinated and writable");
            Check(name == PowerSettings.ChargeModeAttr, "never modifies thermal settings or custom thresholds");
            BeforeWrite?.Invoke();
            if (FailWrite) throw new BiosException("Injected firmware failure");
            Writes++;
            if (!IgnoreWrites) Mode = value;
        }
        sealed class Scope(Action release) : IDisposable { public void Dispose() => release(); }
    }
}
