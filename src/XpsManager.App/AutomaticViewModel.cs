using System.Collections.ObjectModel;
using System.ServiceProcess;
using XpsManager.Core;

namespace XpsManager.App;

public sealed class MappingRow(string label, string hint, WindowsPowerMode mode, string selected) : Observable
{
    public string Label { get; } = label;
    public string Hint { get; } = hint;
    public WindowsPowerMode Mode { get; } = mode;
    string _selected = selected;
    public string Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class AutomaticViewModel : Observable
{
    bool _enabled, _busy, _installed, _error, _optimizer, _update;
    bool _operationFailed;
    string _status = "Checking background service…";
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); OnPropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !IsBusy;
    public bool Installed { get => _installed; private set => Set(ref _installed, value); }
    public bool StatusIsError { get => _error; private set => Set(ref _error, value); }
    public bool OptimizerInstalled { get => _optimizer; private set => Set(ref _optimizer, value); }
    public bool UpdateAvailable { get => _update; private set => Set(ref _update, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ObservableCollection<SettingOption> Options { get; } = [new("", "No change", "Leave the current thermal mode in place.")];
    public ObservableCollection<MappingRow> Rows { get; } = [];

    public void SetOptions(IEnumerable<SettingOption> options)
    {
        var available = options.ToArray();
        if (Options.Skip(1).SequenceEqual(available)) return;
        Options.Clear();
        Options.Add(new("", "No change", "Leave the current thermal mode in place."));
        foreach (var option in available) Options.Add(option);
    }

    public Task LoadAsync() => Run("Checking background service…", () => { }, true);

    void ReadState()
    {
        var config = AutoSwitchStore.Read();
        OptimizerInstalled = AutoSwitchStore.DellOptimizerInstalled();
        Enabled = config.Enabled;
        Rows.Clear();
        Rows.Add(new("Best power efficiency", "Also used for Energy saver", WindowsPowerMode.BestPowerEfficiency, config.Mapping[WindowsPowerMode.BestPowerEfficiency]));
        Rows.Add(new("Balanced", "Also used for Better performance", WindowsPowerMode.Balanced, config.Mapping[WindowsPowerMode.Balanced]));
        Rows.Add(new("Best performance", "Maximum performance in Windows", WindowsPowerMode.BestPerformance, config.Mapping[WindowsPowerMode.BestPerformance]));
        foreach (var row in Rows)
        {
            if (row.Selected == "UltraPerformance" && !Options.Any(o => o.Value == row.Selected) && Options.Any(o => o.Value == "Performance")) row.Selected = "Performance";
            else if (row.Selected == "Performance" && !Options.Any(o => o.Value == row.Selected) && Options.Any(o => o.Value == "UltraPerformance")) row.Selected = "UltraPerformance";
        }
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        if (IsBusy || _operationFailed) return;
        try
        {
            var service = ServiceInstaller.Status();
            Installed = service is not null;
            UpdateAvailable = Installed && ServiceInstaller.UpdateAvailable;
            var config = AutoSwitchStore.Read();
            var last = AutoSwitchStore.Status();
            StatusIsError = last.Error.Length > 0 || (config.Enabled && service != ServiceControllerStatus.Running);
            Status = service is null ? "Off. Enable to install the background service."
                : !config.Enabled ? "Off. Background service installed; automatic switching is idle."
                : service != ServiceControllerStatus.Running ? $"Enabled, but service is {service}. Save automatic settings to start it."
                : last.Error.Length > 0 ? "On. " + last.Error
                : last.AppliedAt is { } at ? $"On. Last set {(last.Mode is "Performance" or "UltraPerformance" ? "Ultra Performance" : last.Mode)} at {at.LocalDateTime:HH:mm} ({last.Trigger})."
                : "On. Following Windows power mode; waiting for the first change.";
        }
        catch (Exception e) { StatusIsError = true; Status = e.Message; }
    }

    AutoSwitchConfiguration Configuration()
    {
        var mapping = Rows.ToDictionary(r => r.Mode, r => r.Selected ?? "");
        mapping[WindowsPowerMode.EnergySaver] = mapping[WindowsPowerMode.BestPowerEfficiency];
        mapping[WindowsPowerMode.BetterPerformance] = mapping[WindowsPowerMode.Balanced];
        return new(Enabled, mapping);
    }

    public Task SaveAsync()
    {
        var config = Configuration();
        return Run("Saving automatic settings…", () =>
        {
            if (config.Enabled && ServiceInstaller.Status() is null) ServiceInstaller.Install(config);
            else ServiceInstaller.Save(config);
        });
    }
    public Task ToggleAsync(bool enabled) { Enabled = enabled; return SaveAsync(); }
    public Task RemoveAsync() => Run("Removing background service…", ServiceInstaller.Remove, true);
    public Task UpdateAsync() => Run("Updating background service…", ServiceInstaller.Update);

    async Task Run(string message, Action operation, bool reload = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        _operationFailed = false;
        StatusIsError = false;
        Status = message;
        try
        {
            await Task.Run(operation);
            IsBusy = false;
            if (reload) ReadState(); else RefreshStatus();
        }
        catch (Exception e)
        {
            _operationFailed = true;
            StatusIsError = true;
            Status = e.Message;
            try { Enabled = AutoSwitchStore.Read().Enabled; } catch { }
        }
        finally { IsBusy = false; }
    }
}
