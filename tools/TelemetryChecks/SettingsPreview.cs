using XpsManager.App;
using XpsManager.Core;

// Writable slider values keep preview bindings identical to production without touching the BIOS.
internal sealed class SettingsPreview
{
    public string AppVersion => "v" + typeof(MainViewModel).Assembly.GetName().Version!.ToString(3);
    public bool CanEditCharging { get; init; } = true;
    public bool CanStartBoost { get; init; }
    public bool CanReturnFromBoost { get; init; }
    public bool BoostActive { get; init; }
    public string BoostStatus { get; init; } = "ExpressCharge to 100% for 24 hours, then return to Primarily AC Use. Works with the app closed.";
    public required DeviceInfo Device { get; init; }
    public required TelemetryViewModel Live { get; init; }
    public required AutomaticViewModel Automatic { get; init; }
    public string ActiveThermal { get; init; } = "";
    public string ActiveCharge { get; init; } = "";
    public string PendingThermal { get; init; } = "";
    public string PendingCharge { get; init; } = "";
    public bool HasPendingThermal { get; init; }
    public bool HasPendingCharge { get; init; }
    public string Status { get; init; } = "";
    public bool StatusIsError { get; init; }
    public bool CanRefresh { get; init; }
    public bool IsLoaded { get; init; }
    public bool CanApply { get; init; }
    public string ApplyLabel { get; init; } = "";
    public bool IsCustomCharge { get; init; }
    public int StartMin { get; init; }
    public int StartMax { get; init; }
    public int StopMin { get; init; }
    public int StopMax { get; init; }
    public double ChargeStart { get; set; }
    public double ChargeStop { get; set; }
    public string SelectedThermalDescription { get; init; } = "";
    public string SelectedChargeDescription { get; init; } = "";
    public OptionViewModel[] ThermalOptions { get; init; } = [];
    public OptionViewModel[] ChargeOptions { get; init; } = [];
}
