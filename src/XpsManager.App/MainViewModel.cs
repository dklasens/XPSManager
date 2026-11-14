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
    readonly ChargeBoostStore _boostStore = new();
    ChargeBoostState? _boostState;
    bool _boostReadFailed;
    string _boostStatus = "ExpressCharge to 100% for 24 hours, then return to Primarily AC Use. Works with the app closed.";

    public string AppVersion => "v" + (typeof(MainViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    public bool BoostActive => _boostState is not null;
    public bool CanEditCharging => !IsBusy && !BoostActive && !_boostReadFailed;
    public bool CanStartBoost => CanEditCharging && !HasChanges && _state is not null
        && _state.ChargeModes.Any(o => o.Value == PowerSettings.ExpressChargeMode)
        && _state.ChargeModes.Any(o => o.Value == PowerSettings.PrimarilyAcChargeMode);
    public bool CanReturnFromBoost => BoostActive && !IsBusy && !HasChanges;
    public string BoostStatus => _boostStatus;

    public void RefreshBoostStatus()
    {
        if (IsBusy) return;
        try
        {
            _boostState = _boostStore.Read();
            _boostReadFailed = false;
            _boostStatus = _boostState is { } state
                ? !string.IsNullOrEmpty(state.Error) ? state.Error + " The return timer is still saved; you can retry Return now."
                : state.ExpiresAt <= DateTimeOffset.UtcNow ? "Return to Primarily AC Use is due. The background service will retry until it succeeds."
                : $"Returns to Primarily AC Use on {state.ExpiresAt.LocalDateTime:ddd d MMM, HH:mm}. You can close the app."
                : "ExpressCharge to 100% for 24 hours, then return to Primarily AC Use. Works with the app closed.";
            if (_boostState is not null && ServiceInstaller.Status() != System.ServiceProcess.ServiceControllerStatus.Running)
                _boostStatus += " Background service is not running. Use Return now or restart the service in Windows.";
            if (_boostState is null && _state is not null && !_state.ChargeModes.Any(o => o.Value == PowerSettings.ExpressChargeMode))
                _boostStatus = "ExpressCharge is not available on this BIOS.";
            else if (_boostState is null && _state is not null && !_state.ChargeModes.Any(o => o.Value == PowerSettings.PrimarilyAcChargeMode))
                _boostStatus = "Primarily AC Use is not available on this BIOS, so the charging timer cannot be started.";
        }
        catch (Exception e) { _boostReadFailed = true; _boostStatus = "Cannot read the charging timer: " + e.Message; }
        OnPropertyChanged(nameof(BoostStatus));
        Refresh();
    }

    public async Task RefreshChargeBoostAsync()
    {
        if (IsBusy) return;
        var previous = _boostState;
        RefreshBoostStatus();
        if (previous != _boostState && !_boostReadFailed && _settings is not null)
            await RefreshOnActivationAsync();
    }

    public async Task SetChargeBoostAsync(bool start)
    {
        if (_settings is null || (start ? !CanStartBoost : !CanReturnFromBoost)) return;
        IsBusy = true;
        StatusIsError = false;
        Status = start ? "Starting 24-hour ExpressCharge…" : "Restoring Primarily AC Use…";
        try
        {
            var settings = _settings;
            var state = await Task.Run(() =>
            {
                var boost = new ChargeBoost(settings, _boostStore);
                if (start) { ServiceInstaller.EnsureChargeBoostService(); boost.Start(); }
                else boost.ReturnNow();
                return settings.Read();
            });
            Populate(state);
            Status = start ? "ExpressCharge enabled for 24 hours." : "Primarily AC Use restored.";
        }
        catch (Exception e)
        {
            StatusIsError = true;
            Status = e.Message;
            try { Populate(await Task.Run(_settings.Read)); }
            catch { Status += " Re-read BIOS settings before retrying."; }
        }
        finally { IsBusy = false; RefreshBoostStatus(); }
    }

    public DeviceInfo Device { get; } = SystemInfo.Device();

    /// <summary>Live readings for the readout band and the Activity view.</summary>
    public TelemetryViewModel Live { get; } = new();
    public AutomaticViewModel Automatic { get; } = new();

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
        _settings ??= new PowerSettings(new BiosClient());
        return _settings.Read();
    }, "Read at");

    public async Task ApplyAsync()
    {
        if (_settings is null || _state is null || !HasChanges || IsBusy) return;
        var (settings, state, change) = (_settings, _state, Pending());
        _isApplying = true;
        OnPropertyChanged(nameof(ApplyLabel));
        try
        {
            await RunAsync("Writing to BIOS…", () =>
            {
                using var operation = settings.EnterOperation();
                if (_boostStore.Read() is not null && (change.ChargeMode != state.ChargeMode ||
                    (change.ChargeMode == PowerSettings.CustomChargeMode &&
                     (change.ChargeStart != state.ChargeStart || change.ChargeStop != state.ChargeStop))))
                    throw new BiosException("Use Return now to end the ExpressCharge timer before changing charging modes.");
                return settings.Apply(state, change);
            }, "Verified at");
        }
        finally
        {
            _isApplying = false;
            OnPropertyChanged(nameof(ApplyLabel));
        }
    }

    public void Revert() { if (_state is not null) Populate(_state); }

    public async Task RefreshOnActivationAsync()
    {
        if (_settings is null || IsBusy) return;
        var pending = HasChanges ? Pending() : null;
        var previous = _state;
        await LoadAsync();
        if (pending is null || previous is null) return;
        if (pending.ThermalMode != previous.ThermalMode)
            foreach (var option in ThermalOptions) option.IsSelected = option.Value == pending.ThermalMode;
        if (pending.ChargeMode != previous.ChargeMode)
            foreach (var option in ChargeOptions) option.IsSelected = option.Value == pending.ChargeMode;
        if (pending.ChargeStart != previous.ChargeStart) ChargeStart = pending.ChargeStart;
        if (pending.ChargeStop != previous.ChargeStop) ChargeStop = pending.ChargeStop;
    }

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
        catch (Exception e) when (e is BiosException or ArgumentException or FormatException or OverflowException
            or System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        {
            StatusIsError = true;
            Status = e.Message;
            // Firmware writes are not a transaction. Show what actually survived a partial failure.
            if (_isApplying && _settings is not null)
            {
                try { Populate(await Task.Run(_settings.Read)); Status += " Current BIOS settings have been re-read."; }
                catch (Exception readError) when (readError is BiosException or ArgumentException or FormatException or OverflowException)
                { Status += " Some settings may have changed. Re-read before retrying."; }
            }
        }
        finally
        {
            IsBusy = false;
            RefreshBoostStatus();
        }
    }

    void Populate(PowerState state)
    {
        _state = state;
        Automatic.SetOptions(state.ThermalModes);

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
        OnPropertyChanged(nameof(BoostActive));
        OnPropertyChanged(nameof(CanEditCharging));
        OnPropertyChanged(nameof(CanStartBoost));
        OnPropertyChanged(nameof(CanReturnFromBoost));
    }
}
