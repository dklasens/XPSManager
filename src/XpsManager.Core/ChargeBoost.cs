using Microsoft.Win32;
using System.Text.Json;

namespace XpsManager.Core;

public sealed record ChargeBoostState(DateTimeOffset ExpiresAt, string Error = "");

public interface IChargeBoostStore
{
    ChargeBoostState? Read();
    void Write(ChargeBoostState? state);
}

/// <summary>One atomic value in the existing admin/SYSTEM-writable settings key.</summary>
public sealed class ChargeBoostStore : IChargeBoostStore
{
    public ChargeBoostState? Read()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(AutoSwitchStore.KeyPath);
        if (key?.GetValue("ChargeBoost") is not string json) return null;
        return JsonSerializer.Deserialize<ChargeBoostState>(json)
            ?? throw new InvalidDataException("The saved ExpressCharge timer could not be read.");
    }

    public void Write(ChargeBoostState? state)
    {
        using var key = AutoSwitchStore.OpenWritableKey();
        if (state is null) key.DeleteValue("ChargeBoost", false);
        else key.SetValue("ChargeBoost", JsonSerializer.Serialize(state), RegistryValueKind.String);
        key.Flush(); // Commit recovery intent before changing firmware.
    }
}

/// <summary>Persistent 24-hour override. Every transition shares the cross-process BIOS gate.</summary>
public sealed class ChargeBoost(PowerSettings settings, IChargeBoostStore store, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Start()
    {
        using var operation = settings.EnterOperation();
        if (store.Read() is not null)
            throw new InvalidOperationException("A charging timer is already saved. Return to Primarily AC Use before starting another.");
        var mode = settings.ReadChargeMode();
        if (mode.IsReadOnly || !mode.PossibleValues.Contains(PowerSettings.ExpressChargeMode)
            || !mode.PossibleValues.Contains(PowerSettings.PrimarilyAcChargeMode))
            throw new BiosException("This BIOS must support writable ExpressCharge and Primarily AC Use modes.");

        var state = new ChargeBoostState(_clock.GetUtcNow().AddHours(24));
        store.Write(state);
        try { settings.SetChargeMode(PowerSettings.ExpressChargeMode); }
        catch (Exception e)
        {
            // A failed verification can follow a successful firmware write. Keep the deadline
            // so even a crash or failed rollback cannot strand ExpressCharge indefinitely.
            store.Write(state with { Error = "ExpressCharge could not be verified: " + e.Message });
            throw;
        }
    }

    public void ReturnNow()
    {
        using var operation = settings.EnterOperation();
        if (store.Read() is { } state) Restore(state);
    }

    public void RestoreIfExpired()
    {
        using var operation = settings.EnterOperation();
        if (store.Read() is { } state && state.ExpiresAt <= _clock.GetUtcNow()) Restore(state);
    }

    void Restore(ChargeBoostState state)
    {
        try
        {
            settings.SetChargeMode(PowerSettings.PrimarilyAcChargeMode);
            store.Write(null); // Only forget the deadline after firmware read-back succeeds.
        }
        catch (Exception e)
        {
            store.Write(state with { Error = "Could not restore Primarily AC Use: " + e.Message });
            throw;
        }
    }
}
