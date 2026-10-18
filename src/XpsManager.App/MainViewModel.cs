using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using XpsManager.Core;

namespace XpsManager.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class OptionViewModel(SettingOption option, bool isActive, Action<OptionViewModel> onSelected) : Observable
{
    bool _isSelected = isActive;

    public string Value => option.Value;
    public string Label => option.Label;
    public string ShortLabel => option.ShortLabel;
    public string Description => option.Description;

    /// <summary>What the BIOS currently has set.</summary>
    public bool IsActive { get; } = isActive;

    /// <summary>Screen readers get the "active" marker the cobalt square shows visually.</summary>
    public string AutomationStatus => IsActive ? "Active in BIOS" : "";

    /// <summary>What the user has picked in the UI.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value) && value) onSelected(this); }
    }
}

public sealed class MainViewModel : Observable
{
    PowerSettings? _settings;
    PowerState? _state;
    bool _isBusy;
    bool _isApplying;
    bool _isLoaded;
    string _status = "Reading BIOS settings…";
    bool _statusIsError;
    double _chargeStart, _chargeStop;
    int _startMin, _startMax = 100, _stopMin, _stopMax = 100;

    public DeviceInfo Device { get; } = SystemInfo.Device();

    /// <summary>Live readings for the readout band and the Activity view.</summary>
    public TelemetryViewModel Live { get; } = new();

    public ObservableCollection<OptionViewModel> ThermalOptions { get; } = [];
    public ObservableCollection<OptionViewModel> ChargeOptions { get; } = [];

    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) Refresh(); } }
    public bool IsLoaded { get => _isLoaded; private set => Set(ref _isLoaded, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public int StartMin { get => _startMin; private set => Set(ref _startMin, value); }
    public int StartMax { get => _startMax; private set => Set(ref _startMax, value); }
    public int StopMin { get => _stopMin; private set => Set(ref _stopMin, value); }
    public int StopMax { get => _stopMax; private set => Set(ref _stopMax, value); }

    // Keep stop at least MinChargeGap above start by nudging the other slider.
    public double ChargeStart
    {
        get => _chargeStart;
        set
        {
            if (!Set(ref _chargeStart, Math.Round(value))) return;
            if (ChargeStop < _chargeStart + PowerSettings.MinChargeGap)
                ChargeStop = Math.Min(StopMax, _chargeStart + PowerSettings.MinChargeGap);
            Refresh();
        }
    }

    public double ChargeStop
    {
        get => _chargeStop;
        set
        {
            if (!Set(ref _chargeStop, Math.Round(value))) return;
            if (ChargeStart > _chargeStop - PowerSettings.MinChargeGap)
                ChargeStart = Math.Max(StartMin, _chargeStop - PowerSettings.MinChargeGap);
            Refresh();
        }
    }

    public bool IsCustomCharge => SelectedCharge == PowerSettings.CustomChargeMode;

    public string ActiveThermal =>
        ThermalOptions.FirstOrDefault(o => o.IsActive)?.Label ?? "—";

    public string ActiveCharge =>
        _state is null ? "—" : DescribeCharge(_state.ChargeMode, _state.ChargeStart, _state.ChargeStop);

    /// <summary>"→ Quiet" when the selection differs from the BIOS; empty otherwise.</summary>
    public string PendingThermal =>
        _state is not null && SelectedThermal is { } t && t != _state.ThermalMode
            ? "→ " + ThermalOptions.First(o => o.Value == t).Label
            : "";

    public bool HasPendingThermal => PendingThermal.Length > 0;
    public bool HasPendingCharge => PendingCharge.Length > 0;

    public string PendingCharge
    {
        get
        {
            if (_state is null || SelectedCharge is not { } c) return "";
            var pending = DescribeCharge(c, (int)ChargeStart, (int)ChargeStop);
            return pending == ActiveCharge ? "" : "→ " + pending;
        }
    }

    string DescribeCharge(string mode, int start, int stop) =>
        mode == PowerSettings.CustomChargeMode
            ? $"Custom {start}–{stop}%"
            : ChargeOptions.FirstOrDefault(o => o.Value == mode)?.Label ?? mode;

    public string ApplyLabel => _isApplying ? "Writing…" : "Write to BIOS";

    public string SelectedThermalDescription => ThermalOptions.FirstOrDefault(o => o.IsSelected)?.Description ?? "";
    public string SelectedChargeDescription => ChargeOptions.FirstOrDefault(o => o.IsSelected)?.Description ?? "";

    public bool HasChanges => _state is not null && !Equals(Pending(), EffectiveCurrent());
    public bool CanApply => HasChanges && !IsBusy;
    public bool CanRefresh => !IsBusy && _settings is not null;

    string? SelectedThermal => ThermalOptions.FirstOrDefault(o => o.IsSelected)?.Value;
    string? SelectedCharge => ChargeOptions.FirstOrDefault(o => o.IsSelected)?.Value;

    public Task LoadAsync() => RunAsync("Reading BIOS…", () =>
    {
        _settings ??= new PowerSettings(new BiosClient(BiosClient.DefaultKeyCachePath));
        return _settings.Read();
    }, "Read at");

    public async Task ApplyAsync()
    {
        if (_settings is null || _state is null || !HasChanges) return;
        var (settings, state, change) = (_settings, _state, Pending());
        _isApplying = true;
        OnPropertyChanged(nameof(ApplyLabel));
        try
        {
            await RunAsync("Writing to BIOS…", () => settings.Apply(state, change), "Verified at");
        }
        finally
        {
            _isApplying = false;
            OnPropertyChanged(nameof(ApplyLabel));
        }
    }

    public void Revert() { if (_state is not null) Populate(_state); }

    async Task RunAsync(string busyText, Func<PowerState> work, string doneText)
    {
        IsBusy = true;
        StatusIsError = false;
        Status = busyText;
        try
        {
            var state = await Task.Run(work);
            Populate(state);
            IsLoaded = true;
            Status = $"{doneText} {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception e) when (e is BiosException or ArgumentException)
        {
            StatusIsError = true;
            Status = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    void Populate(PowerState state)
    {
        _state = state;

        ThermalOptions.Clear();
        foreach (var o in state.ThermalModes)
            ThermalOptions.Add(new OptionViewModel(o, o.Value == state.ThermalMode, sel => Deselect(ThermalOptions, sel)));

        ChargeOptions.Clear();
        foreach (var o in state.ChargeModes)
            ChargeOptions.Add(new OptionViewModel(o, o.Value == state.ChargeMode, sel => Deselect(ChargeOptions, sel)));

        (StartMin, StartMax) = state.ChargeStartRange;
        (StopMin, StopMax) = state.ChargeStopRange;
        _chargeStart = state.ChargeStart;
        _chargeStop = state.ChargeStop;
        OnPropertyChanged(nameof(ChargeStart));
        OnPropertyChanged(nameof(ChargeStop));
        OnPropertyChanged(nameof(ActiveThermal));
        OnPropertyChanged(nameof(ActiveCharge));
        Refresh();
    }

    // Radio buttons generated by an ItemsControl don't reliably clear each other, so do it here.
    void Deselect(ObservableCollection<OptionViewModel> options, OptionViewModel selected)
    {
        foreach (var o in options)
            if (o != selected && o.IsSelected) o.IsSelected = false;
        Refresh();
    }

    PowerChange Pending() => new(
        SelectedThermal ?? _state!.ThermalMode,
        SelectedCharge ?? _state!.ChargeMode,
        (int)ChargeStart,
        (int)ChargeStop);

    // Thresholds only matter in Custom mode, so ignore them when comparing otherwise.
    PowerChange EffectiveCurrent()
    {
        var current = _state!.ToChange();
        return SelectedCharge == PowerSettings.CustomChargeMode
            ? current
            : current with { ChargeStart = (int)ChargeStart, ChargeStop = (int)ChargeStop };
    }

    void Refresh()
    {
        OnPropertyChanged(nameof(IsCustomCharge));
        OnPropertyChanged(nameof(SelectedThermalDescription));
        OnPropertyChanged(nameof(SelectedChargeDescription));
        OnPropertyChanged(nameof(PendingThermal));
        OnPropertyChanged(nameof(PendingCharge));
        OnPropertyChanged(nameof(HasPendingThermal));
        OnPropertyChanged(nameof(HasPendingCharge));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRefresh));
    }
}
