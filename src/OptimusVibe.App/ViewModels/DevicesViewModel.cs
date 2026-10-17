using System.Collections.ObjectModel;
using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Display;
using OptimusVibe.Logging;

namespace OptimusVibe.App.ViewModels;

/// <summary>One match rule row in the device form: a key (click to cycle) and a value (wildcards * and ?).</summary>
public sealed class MatchRowViewModel : ObservableObject
{
    private string _key;
    private string _value;

    public MatchRowViewModel(DevicesViewModel owner, string key, string value)
    {
        _key = key;
        _value = value;
        CycleKeyCommand = new RelayCommand(() =>
        {
            var keys = DevicesService.MatchKeys;
            var i = Array.FindIndex(keys, k => k.Equals(Key, StringComparison.OrdinalIgnoreCase));
            Key = keys[(i + 1) % keys.Length];
        });
        RemoveCommand = new RelayCommand(() => owner.RemoveRule(this));
    }

    public string Key { get => _key; set => Set(ref _key, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public ICommand CycleKeyCommand { get; }
    public ICommand RemoveCommand { get; }
}

/// <summary>One device in the Settings → Devices list.</summary>
public sealed class DeviceItemViewModel(DeviceEntry entry, bool isCurrent, bool canMoveUp, bool canMoveDown, string? presetsFromName) : ObservableObject
{
    public DeviceEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public string Name => Entry.DisplayName;
    public bool HasName => !string.IsNullOrWhiteSpace(Entry.Def.Name);
    public string MatchSummary => Entry.MatchSummary;
    public bool IsCurrent { get; } = isCurrent;
    public bool CanMoveUp { get; } = canMoveUp;
    public bool CanMoveDown { get; } = canMoveDown;
    public bool HasPresetsFrom => presetsFromName is not null;
    public string PresetsFromText => presetsFromName is null ? "" : $"Uses {presetsFromName}'s presets when a game has none of its own";
    public string IdLine => HasName ? $"{Id} · {MatchSummary}" : MatchSummary;
}

public enum DeviceFormMode { None, Add, Edit, Rename }

/// <summary>
/// Settings → Devices: the devices.yaml list with add (prefilled from this machine), edit, rename, reorder and delete.
/// Work runs off the UI thread through the busy gate; changes are synced like library.yaml when AutoSync is on, and the
/// device is re-detected afterwards.
/// </summary>
public sealed class DevicesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private IReadOnlyList<DeviceEntry> _entries = [];
    private DeviceFormMode _mode;
    private string _name = "";
    private string _id = "";
    private bool _idEdited;
    private bool _settingId;
    private string _width = "", _height = "", _refresh = "";
    private string _defaultPreset = "";
    private bool _useMachineName;
    private string _machineName = "";
    private string? _formHint;
    private DeviceItemViewModel? _pendingDelete;

    public DevicesViewModel(MainViewModel main)
    {
        _main = main;
        Drivers =
        [
            new("", "Auto", "Work it out from the graphics card"),
            new("nvidia", "NVIDIA"),
            new("amd", "AMD"),
            new("none", "None"),
        ];
        bool Idle() => !main.IsBusy;
        AddThisCommand = new AsyncRelayCommand(AddThisAsync, () => Idle() && main.Session is not null && !IsFormOpen);
        AddManualCommand = new RelayCommand(OpenBlank, () => Idle() && main.Session is not null && !IsFormOpen);
        EditCommand = new RelayCommand(p => OpenEdit(p as DeviceItemViewModel, DeviceFormMode.Edit), _ => Idle() && !IsFormOpen);
        RenameCommand = new RelayCommand(p => OpenEdit(p as DeviceItemViewModel, DeviceFormMode.Rename), _ => Idle() && !IsFormOpen);
        MoveUpCommand = new AsyncRelayCommand(p => MoveAsync(p as DeviceItemViewModel, -1), _ => Idle() && !IsFormOpen);
        MoveDownCommand = new AsyncRelayCommand(p => MoveAsync(p as DeviceItemViewModel, +1), _ => Idle() && !IsFormOpen);
        DeleteCommand = new RelayCommand(p => AskDelete(p as DeviceItemViewModel), _ => Idle() && !IsFormOpen);
        ConfirmDeleteCommand = new AsyncRelayCommand(ConfirmDeleteAsync, () => Idle() && PendingDelete is not null);
        CancelDeleteCommand = new RelayCommand(() => PendingDelete = null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => Idle() && CanSave);
        CancelCommand = new RelayCommand(CloseForm);
        AddRuleCommand = new RelayCommand(() => AddRule("cpu", ""));
        UseCurrentDisplayCommand = new RelayCommand(UseCurrentDisplay);
        SetDriverCommand = new RelayCommand(p => { if (p is OptionViewModel o) foreach (var d in Drivers) d.IsOn = d == o; });
        SetPresetsFromCommand = new RelayCommand(p => { if (p is OptionViewModel o) foreach (var d in PresetsFromOptions) d.IsOn = d == o; });
        Rules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRules));
    }

    public ObservableCollection<DeviceItemViewModel> Items { get; } = new();
    public ObservableCollection<MatchRowViewModel> Rules { get; } = new();
    public ObservableCollection<OptionViewModel> Drivers { get; }
    public ObservableCollection<OptionViewModel> PresetsFromOptions { get; } = new();

    public bool HasItems => Items.Count > 0;
    public bool HasRules => Rules.Count > 0;

    public ICommand AddThisCommand { get; }
    public ICommand AddManualCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand UseCurrentDisplayCommand { get; }
    public ICommand SetDriverCommand { get; }
    public ICommand SetPresetsFromCommand { get; }

    // ---------------------------------------------------------------- list

    public async Task RefreshAsync()
    {
        var session = _main.Session;
        if (session is null)
        {
            Items.Clear();
            OnPropertyChanged(nameof(HasItems));
            return;
        }
        IReadOnlyList<DeviceEntry> entries;
        try
        {
            entries = await Task.Run(() => new DevicesService(session.Paths).Load());
        }
        catch (Exception ex)
        {
            Log.Warn($"Devices list: {ex.Message}");
            entries = [];
        }
        _entries = entries;
        var current = session.Device.Name;
        Items.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var from = e.Def.PresetsFrom?.Trim();
            string? fromName = !string.IsNullOrEmpty(from)
                ? DeviceNames.Display(from, entries.FirstOrDefault(x => x.Id == from)?.Def)
                : null;
            Items.Add(new DeviceItemViewModel(e, string.Equals(e.Id, current, StringComparison.OrdinalIgnoreCase), i > 0, i < entries.Count - 1, fromName));
        }
        OnPropertyChanged(nameof(HasItems));
    }

    // ---------------------------------------------------------------- form state

    public bool IsFormOpen => _mode != DeviceFormMode.None;
    public bool IsAdd => _mode == DeviceFormMode.Add;
    public bool IsRename => _mode == DeviceFormMode.Rename;
    public bool ShowFullForm => _mode is DeviceFormMode.Add or DeviceFormMode.Edit;
    public bool IdEditable => _mode == DeviceFormMode.Add;

    public string FormTitle => _mode switch
    {
        DeviceFormMode.Add => "Add a device",
        DeviceFormMode.Edit => "Edit device",
        DeviceFormMode.Rename => "Rename device",
        _ => "",
    };

    public string SaveLabel => _mode == DeviceFormMode.Add ? "Add device" : "Save";

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            if (_mode == DeviceFormMode.Add && !_idEdited) SetIdSilently(DevicesService.UniqueId(DevicesService.Slug(value), _entries.Select(e => e.Id)));
            RaiseFormState();
        }
    }

    public string Id
    {
        get => _id;
        set
        {
            if (!Set(ref _id, value)) return;
            if (!_settingId) _idEdited = true;
            RaiseFormState();
        }
    }

    private void SetIdSilently(string id)
    {
        _settingId = true;
        try { Id = id; }
        finally { _settingId = false; }
    }

    public string? IdError => _mode == DeviceFormMode.Add ? DevicesService.ValidateId(_id, _entries.Select(e => e.Id)) : null;
    public bool HasIdError => IdError is not null;

    public string DisplayWidth { get => _width; set => Set(ref _width, value); }
    public string DisplayHeight { get => _height; set => Set(ref _height, value); }
    public string DisplayRefresh { get => _refresh; set => Set(ref _refresh, value); }
    public string DefaultPreset { get => _defaultPreset; set => Set(ref _defaultPreset, value); }

    public string MachineName => _machineName;
    public string MachineToggleText => $"Also match this computer's name ({_machineName})";

    /// <summary>Adds / removes the <c>machine</c> rule (it tells two identical machines apart).</summary>
    public bool UseMachineName
    {
        get => _useMachineName;
        set
        {
            if (!Set(ref _useMachineName, value)) return;
            var existing = Rules.FirstOrDefault(r => r.Key.Equals("machine", StringComparison.OrdinalIgnoreCase));
            if (value && existing is null) AddRule("machine", _machineName);
            else if (!value && existing is not null) Rules.Remove(existing);
        }
    }

    public string? FormHint { get => _formHint; private set { if (Set(ref _formHint, value)) OnPropertyChanged(nameof(HasFormHint)); } }
    public bool HasFormHint => _formHint is not null;

    public bool CanSave => IsFormOpen && !string.IsNullOrWhiteSpace(_name) && (_mode != DeviceFormMode.Add || IdError is null);

    private void RaiseFormState()
    {
        OnPropertyChanged(nameof(IdError));
        OnPropertyChanged(nameof(HasIdError));
        OnPropertyChanged(nameof(CanSave));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetMode(DeviceFormMode mode)
    {
        _mode = mode;
        OnPropertyChanged(nameof(IsFormOpen));
        OnPropertyChanged(nameof(IsAdd));
        OnPropertyChanged(nameof(IsRename));
        OnPropertyChanged(nameof(ShowFullForm));
        OnPropertyChanged(nameof(IdEditable));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(FormTitle));
        RaiseFormState();
    }

    public void CloseForm()
    {
        SetMode(DeviceFormMode.None);
        FormHint = null;
    }

    /// <summary>B closes an open form or delete prompt first.</summary>
    public bool CancelTransient()
    {
        if (IsFormOpen) { CloseForm(); return true; }
        if (PendingDelete is not null) { PendingDelete = null; return true; }
        return false;
    }

    private void ResetFields(string id, string name)
    {
        _idEdited = false;
        _name = name;
        OnPropertyChanged(nameof(Name));
        SetIdSilently(id);
        Rules.Clear();
        DisplayWidth = DisplayHeight = DisplayRefresh = "";
        DefaultPreset = "";
        _useMachineName = false;
        OnPropertyChanged(nameof(UseMachineName));
        FormHint = null;
    }

    private void BuildPresetsFromOptions(string? selfId, string? selected)
    {
        PresetsFromOptions.Clear();
        PresetsFromOptions.Add(new OptionViewModel("", "None", "Only this device's own presets") { IsOn = string.IsNullOrEmpty(selected) });
        foreach (var e in _entries.Where(e => e.Id != selfId))
            PresetsFromOptions.Add(new OptionViewModel(e.Id, e.DisplayName) { IsOn = e.Id == selected });
        if (!PresetsFromOptions.Any(o => o.IsOn)) PresetsFromOptions[0].IsOn = true;
    }

    private void SetDriver(string? driver)
    {
        var v = driver?.ToLowerInvariant() ?? "";
        foreach (var d in Drivers) d.IsOn = d.Value == v;
        if (!Drivers.Any(d => d.IsOn)) Drivers[0].IsOn = true;
    }

    private void SetDisplay(DisplayModeDef? d)
    {
        DisplayWidth = d?.Width?.ToString() ?? "";
        DisplayHeight = d?.Height?.ToString() ?? "";
        DisplayRefresh = d?.Refresh?.ToString() ?? "";
    }

    private void AddRule(string key, string value) => Rules.Add(new MatchRowViewModel(this, key, value));

    public void RemoveRule(MatchRowViewModel row)
    {
        Rules.Remove(row);
        if (row.Key.Equals("machine", StringComparison.OrdinalIgnoreCase))
        {
            _useMachineName = false;
            OnPropertyChanged(nameof(UseMachineName));
        }
    }

    private void UseCurrentDisplay()
    {
        try
        {
            if (DisplayManager.GetCurrent() is { } cur) SetDisplay(new DisplayModeDef { Width = cur.Width, Height = cur.Height, Refresh = cur.Refresh });
        }
        catch (Exception ex)
        {
            Log.Warn($"Current display mode: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- open the form

    private void OpenBlank()
    {
        _machineName = Environment.MachineName;
        OnPropertyChanged(nameof(MachineName));
        OnPropertyChanged(nameof(MachineToggleText));
        ResetFields("", "");
        BuildPresetsFromOptions(null, null);
        SetDriver("");
        AddRule("gpu", "");
        SetMode(DeviceFormMode.Add);
    }

    private async Task AddThisAsync()
    {
        var session = _main.Session;
        if (session is null) return;
        DeviceSuggestion? s = null;
        var ok = await _main.RunAsync("Looking at this machine", () => Task.Run(() => s = new DevicesService(session.Paths).SuggestForThisMachine()));
        if (!ok || s is null) return;

        _machineName = s.MachineName;
        OnPropertyChanged(nameof(MachineName));
        OnPropertyChanged(nameof(MachineToggleText));
        ResetFields(s.Id, s.Name);
        foreach (var (k, v) in s.Match) AddRule(k, v);
        _useMachineName = s.Match.ContainsKey("machine");
        OnPropertyChanged(nameof(UseMachineName));
        SetDisplay(s.Display);
        SetDriver(s.Driver);
        DefaultPreset = s.DefaultPreset;
        BuildPresetsFromOptions(null, s.PresetsFrom);
        FormHint = s.ExistingMatch is { } clash
            ? $"“{DeviceNames.Display(clash, _entries.FirstOrDefault(e => e.Id == clash)?.Def)}” already matches this machine, so its computer name was added to tell the two apart."
            : "Worked out from this machine's hardware. Change anything that looks wrong.";
        SetMode(DeviceFormMode.Add);
    }

    private void OpenEdit(DeviceItemViewModel? item, DeviceFormMode mode)
    {
        if (item is null) return;
        var def = item.Entry.Def;
        ResetFields(item.Id, def.Name ?? "");
        if (mode == DeviceFormMode.Rename && string.IsNullOrWhiteSpace(def.Name)) { _name = item.Name; OnPropertyChanged(nameof(Name)); }
        _machineName = Environment.MachineName;
        OnPropertyChanged(nameof(MachineName));
        OnPropertyChanged(nameof(MachineToggleText));
        foreach (var (k, v) in def.Match) AddRule(k, v);
        _useMachineName = def.Match.ContainsKey("machine");
        OnPropertyChanged(nameof(UseMachineName));
        SetDisplay(def.Display);
        SetDriver(def.Driver);
        DefaultPreset = def.DefaultPreset ?? "";
        BuildPresetsFromOptions(item.Id, def.PresetsFrom?.Trim());
        FormHint = mode == DeviceFormMode.Edit
            ? "The id stays the same: presets and saves are filed under it."
            : "Only the name changes. The id (" + item.Id + ") stays, because presets and saves are filed under it.";
        SetMode(mode);
        _editingId = item.Id;
    }

    private string? _editingId;

    // ---------------------------------------------------------------- save

    private static int? Num(string s) => int.TryParse(s.Trim(), out var n) ? n : null;

    private DisplayModeDef? ReadDisplay()
    {
        if (string.IsNullOrWhiteSpace(_width) && string.IsNullOrWhiteSpace(_height) && string.IsNullOrWhiteSpace(_refresh)) return null;
        var w = Num(_width);
        var h = Num(_height);
        var r = Num(_refresh);
        if (w is null || h is null || (!string.IsNullOrWhiteSpace(_refresh) && r is null))
            throw new ConfigException("Display: give width, height and refresh as whole numbers (or clear all three to leave the display alone).");
        return new DisplayModeDef { Width = w, Height = h, Refresh = r };
    }

    private async Task SaveAsync()
    {
        var session = _main.Session;
        if (session is null || !CanSave) return;
        var mode = _mode;
        var name = _name.Trim();
        var id = mode == DeviceFormMode.Add ? _id.Trim() : _editingId ?? _id;

        Dictionary<string, string> match;
        DisplayModeDef? display;
        try
        {
            match = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Rules.Where(r => !string.IsNullOrWhiteSpace(r.Key) || !string.IsNullOrWhiteSpace(r.Value)))
                match[r.Key.Trim()] = r.Value;
            display = mode == DeviceFormMode.Rename ? null : ReadDisplay();
        }
        catch (ConfigException ex)
        {
            _main.Notify(ex.Message, error: true);
            return;
        }
        var driver = Drivers.FirstOrDefault(d => d.IsOn)?.Value;
        var from = PresetsFromOptions.FirstOrDefault(o => o.IsOn)?.Value;
        var preset = _defaultPreset.Trim();

        var ok = await _main.RunAsync(mode == DeviceFormMode.Add ? "Adding device" : "Saving device", () => Task.Run(() =>
        {
            var svc = new DevicesService(session.Paths);
            switch (mode)
            {
                case DeviceFormMode.Add: svc.Add(id, name, match, display, driver, preset, from); break;
                case DeviceFormMode.Edit: svc.Update(id, name, match, display, driver, preset, from); break;
                default: svc.Rename(id, name); break;
            }
            Log.Info($"Devices: {mode} {id}");
        }));
        if (!ok) return;

        CloseForm();
        var verb = mode switch { DeviceFormMode.Add => "added", DeviceFormMode.Edit => "updated", _ => "renamed" };
        _main.Notify($"{name}: {verb}.");
        await AfterChangeAsync($"chore(devices): {verb} {id}");
    }

    private async Task MoveAsync(DeviceItemViewModel? item, int delta)
    {
        var session = _main.Session;
        if (item is null || session is null) return;
        var ok = await _main.RunAsync("Reordering devices", () => Task.Run(() => new DevicesService(session.Paths).Move(item.Id, delta)));
        if (ok)
        {
            _main.Notify("Devices are matched top to bottom: the first one that fits this machine is used.");
            await AfterChangeAsync($"chore(devices): move {item.Id}");
        }
    }

    // ---------------------------------------------------------------- delete

    public DeviceItemViewModel? PendingDelete
    {
        get => _pendingDelete;
        private set
        {
            if (!Set(ref _pendingDelete, value)) return;
            OnPropertyChanged(nameof(HasPendingDelete));
            OnPropertyChanged(nameof(DeleteText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasPendingDelete => _pendingDelete is not null;

    public string DeleteText => _pendingDelete is null ? "" :
        (_pendingDelete.IsCurrent ? $"{_pendingDelete.Name} is the device this machine is using right now. Deleting it leaves this machine without a profile until you add or pick another. " : "") +
        $"Delete {_pendingDelete.Name}? Its presets in the game files and its saves stay in the repo; they just won't be used.";

    private void AskDelete(DeviceItemViewModel? item) => PendingDelete = item;

    private async Task ConfirmDeleteAsync()
    {
        var session = _main.Session;
        var item = PendingDelete;
        if (item is null || session is null) return;
        DeviceDeleteResult? result = null;
        var ok = await _main.RunAsync("Deleting device", () => Task.Run(() =>
            result = new DevicesService(session.Paths).Delete(item.Id, session.Device.Name, confirmed: true)));
        PendingDelete = null;
        if (!ok || result is null) return;
        _main.Notify(result.Message, error: !result.Deleted);
        if (result.Deleted) await AfterChangeAsync($"chore(devices): remove {item.Id}");
    }

    // ---------------------------------------------------------------- after a change

    /// <summary>Publish devices.yaml (AutoSync), re-detect this machine and reload; say so if it now matches another device.</summary>
    private async Task AfterChangeAsync(string commitMessage)
    {
        var session = _main.Session;
        if (session is null) return;
        var before = session.Device.Name;
        try
        {
            var settings = await SettingsStore.LoadAsync();
            if (settings.AutoSync) await _main.SyncFilesQuietlyAsync(commitMessage, session.Paths.DevicesFile);
        }
        catch (Exception ex)
        {
            Log.Warn($"Devices sync: {ex.Message}");
        }
        await _main.LoadAsync(rescan: false);
        await RefreshAsync();
        if (_main.Session is { } now && !string.Equals(now.Device.Name, before, StringComparison.OrdinalIgnoreCase))
            _main.Notify($"This machine now matches {now.Device.DisplayName}.");
    }
}
