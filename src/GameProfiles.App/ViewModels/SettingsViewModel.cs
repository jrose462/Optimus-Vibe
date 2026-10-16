using System.Collections.ObjectModel;
using System.Windows.Input;
using GameProfiles.App.Services;
using GameProfiles.Config;
using GameProfiles.Logging;
using GameProfiles.Sync;

namespace GameProfiles.App.ViewModels;

/// <summary>A launcher scan toggle or a library-mode choice.</summary>
public sealed class OptionViewModel(string value, string label, string? hint = null) : ObservableObject
{
    private bool _isOn;
    public string Value { get; } = value;
    public string Label { get; } = label;
    public string? Hint { get; } = hint;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
}

/// <summary>Settings page: sync, GitHub account + repo, library scanning, device. Each change is saved immediately.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loading;
    private bool _isGitHub;
    private string _providerText = "";
    private bool _autoSync = true;
    private bool _writeSteamLaunchOptions = true;
    private string? _forcedProfile;
    private bool _libraryDirty;

    public SettingsViewModel(MainViewModel main, GitHubAccountViewModel account)
    {
        _main = main;
        Account = account;
        Stores =
        [
            new("steam", "Steam"),
            new("battlenet", "Battle.net"),
            new("ea", "EA app"),
            new("xbox", "Xbox / Game Pass"),
            new("epic", "Epic Games"),
            new("gog", "GOG Galaxy"),
        ];
        foreach (var s in Stores) s.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OptionViewModel.IsOn)) _ = SaveStoresAsync(); };
        Modes =
        [
            new("auto", "Automatic", "Library on the handheld and small screens, list on the desktop"),
            new("grid", "Library", "Cover grid, made for a controller"),
            new("list", "Manage", "Game list with presets side by side"),
        ];

        bool Idle() => !main.IsBusy;
        SyncNowCommand = main.SyncCommand;
        UseLocalGitCommand = main.PickConfigFolderCommand;
        SetModeCommand = new AsyncRelayCommand(p => SetModeAsync(p as OptionViewModel), _ => Idle());
        ForceDeviceCommand = main.ForceDeviceCommand;
        AutoDetectDeviceCommand = new AsyncRelayCommand(() => main.ForceDeviceAsync(null), () => Idle() && _forcedProfile is not null);
        CloseCommand = new RelayCommand(main.CloseSettings);
        OpenReportCommand = main.OpenReportCommand;
    }

    public GitHubAccountViewModel Account { get; }
    public UpdatesViewModel Updates => _main.Updates;
    public ObservableCollection<OptionViewModel> Stores { get; }
    public ObservableCollection<OptionViewModel> Modes { get; }
    public ObservableCollection<string> DeviceChoices { get; } = new();

    /// <summary>The saved provider is the GitHub account (vs. a local git checkout).</summary>
    public bool IsGitHub
    {
        get => _isGitHub;
        set => Set(ref _isGitHub, value);
    }

    public string ProviderText { get => _providerText; private set => Set(ref _providerText, value); }

    public bool AutoSync
    {
        get => _autoSync;
        set { if (Set(ref _autoSync, value) && !_loading) _ = SaveAsync(s => s.AutoSync = value, value ? "Auto-sync on" : "Auto-sync off"); }
    }

    public bool WriteSteamLaunchOptions
    {
        get => _writeSteamLaunchOptions;
        set
        {
            if (!Set(ref _writeSteamLaunchOptions, value) || _loading) return;
            _libraryDirty = true;
            _ = SaveAsync(s => s.WriteSteamLaunchOptions = value,
                value ? "Steam launch options will be kept up to date" : "Steam launch options won't be touched");
        }
    }

    public string DeviceName => _main.DeviceName;
    public string DeviceReason => _main.DeviceReason;
    public string? DeviceDescription => _main.DeviceDescription;
    public string ConfigRoot => _main.ConfigRoot;
    public bool IsForced => _forcedProfile is not null;
    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public ICommand SyncNowCommand { get; }
    public ICommand UseLocalGitCommand { get; }
    public ICommand SetModeCommand { get; }
    public ICommand ForceDeviceCommand { get; }
    public ICommand AutoDetectDeviceCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand OpenReportCommand { get; }

    /// <summary>The page should scroll to a section ("updates", "account"); raised once the page is shown.</summary>
    public event Action<string>? SectionRequested;

    public void RequestSection(string section) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            () => SectionRequested?.Invoke(section));

    /// <summary>Called when the page opens: re-read settings, provider description and devices.</summary>
    public async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            var session = _main.Session;
            AppSettings s = await SettingsStore.LoadAsync();
            IsGitHub = SyncService.IsGitHub(s);
            AutoSync = s.AutoSync;
            WriteSteamLaunchOptions = s.WriteSteamLaunchOptions;
            _forcedProfile = s.ForcedProfile;
            foreach (var o in Stores) o.IsOn = s.ScanStores.Contains(o.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var m in Modes) m.IsOn = m.Value.Equals(s.LibraryMode, StringComparison.OrdinalIgnoreCase);
            if (!Modes.Any(m => m.IsOn)) Modes[0].IsOn = true;

            ProviderText = "…";
            ProviderText = await Task.Run(() => SyncService.Describe(session?.Paths, s));

            var devices = await Task.Run(() =>
            {
                try { return session?.Store.LoadDevices().Devices.Keys.ToList() ?? new List<string>(); }
                catch (Exception ex) { Log.Warn($"devices.yaml: {ex.Message}"); return new List<string>(); }
            });
            DeviceChoices.Clear();
            foreach (var d in devices) DeviceChoices.Add(d);
            OnPropertyChanged(string.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Loading settings failed", ex);
            _main.Notify($"Couldn't read settings: {ex.Message}", error: true);
        }
        finally
        {
            _loading = false;
        }
        await Account.RefreshAsync();
        if (Account.IsSignedIn && !Account.HasRepos) _ = Account.LoadReposAsync();
    }

    /// <summary>Launcher toggles or the Steam option changed: rescan when leaving the page.</summary>
    public bool ConsumeLibraryDirty()
    {
        var d = _libraryDirty;
        _libraryDirty = false;
        return d;
    }

    private async Task SaveStoresAsync()
    {
        if (_loading) return;
        _libraryDirty = true;
        var stores = Stores.Where(s => s.IsOn).Select(s => s.Value).ToList();
        await SaveAsync(s => s.ScanStores = stores, null);
    }

    private async Task SetModeAsync(OptionViewModel? mode)
    {
        if (mode is null) return;
        foreach (var m in Modes) m.IsOn = m == mode;
        await SaveAsync(s => s.LibraryMode = mode.Value, null);
        _main.ApplyLibraryMode(mode.Value);
    }

    private async Task SaveAsync(Action<AppSettings> change, string? toast)
    {
        try
        {
            await SettingsStore.UpdateAsync(change);
            if (toast is not null) _main.Notify(toast + ".");
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            _main.Notify($"Couldn't save settings: {ex.Message}", error: true);
        }
    }

    public bool CancelTransient() => Account.CancelTransient();
}
