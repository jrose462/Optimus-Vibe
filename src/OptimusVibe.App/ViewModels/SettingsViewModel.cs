using System.Collections.ObjectModel;
using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Logging;
using OptimusVibe.Sync;

namespace OptimusVibe.App.ViewModels;

/// <summary>A launcher scan toggle or a library-mode choice.</summary>
public sealed class OptionViewModel(string value, string label, string? hint = null) : ObservableObject
{
    private bool _isOn;
    public string Value { get; } = value;
    public string Label { get; } = label;
    public string? Hint { get; } = hint;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
}

/// <summary>A theme card in Settings → Appearance: name, blurb and the brushes its mini preview paints (read from the palette).</summary>
public sealed class ThemeOptionViewModel : ObservableObject
{
    private bool _isOn;

    public ThemeOptionViewModel(ThemeInfo info)
    {
        Info = info;
        PageBrush = ThemeManager.PreviewBrush(info.Id, "PageBrush");
        GlowBrush = ThemeManager.PreviewBrush(info.Id, "PageGlowBrush");
        PatternBrush = ThemeManager.PreviewBrush(info.Id, "PagePatternBrush");
        CardBrush = ThemeManager.PreviewBrush(info.Id, "CardBrush");
        CardBorderBrush = ThemeManager.PreviewBrush(info.Id, "CardBorderBrush");
        AccentBrush = ThemeManager.PreviewBrush(info.Id, "AccentBrush");
        FeatureBrush = ThemeManager.PreviewBrush(info.Id, "FeatureBrush");
        TextBrush = ThemeManager.PreviewBrush(info.Id, "TextBrush");
        TextFaintBrush = ThemeManager.PreviewBrush(info.Id, "TextFaintBrush");
    }

    public ThemeInfo Info { get; }
    public string Value => Info.Id;
    public string Label => Info.Name;
    public string Hint => Info.Blurb;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }

    public System.Windows.Media.Brush? PageBrush { get; }
    public System.Windows.Media.Brush? GlowBrush { get; }
    public System.Windows.Media.Brush? PatternBrush { get; }
    public System.Windows.Media.Brush? CardBrush { get; }
    public System.Windows.Media.Brush? CardBorderBrush { get; }
    public System.Windows.Media.Brush? AccentBrush { get; }
    public System.Windows.Media.Brush? FeatureBrush { get; }
    public System.Windows.Media.Brush? TextBrush { get; }
    public System.Windows.Media.Brush? TextFaintBrush { get; }
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

        Themes = new(ThemeManager.Themes.Select(t => new ThemeOptionViewModel(t)));
        foreach (var t in Themes) t.IsOn = t.Value == ThemeManager.Current;
        Densities = new(Density.Modes.Select(m => new OptionViewModel(m.Id, m.Name, m.Blurb)));
        foreach (var d in Densities) d.IsOn = d.Value == Density.Mode;
        Devices = new DevicesViewModel(main);
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.DeviceName) or nameof(MainViewModel.DeviceReason) or nameof(MainViewModel.DeviceDescription))
                OnPropertyChanged(e.PropertyName);
        };

        bool Idle() => !main.IsBusy;
        SyncNowCommand = main.SyncCommand;
        UseLocalGitCommand = main.PickConfigFolderCommand;
        SetThemeCommand = new AsyncRelayCommand(p => SetThemeAsync(p as ThemeOptionViewModel), _ => true);
        SetDensityCommand = new AsyncRelayCommand(p => SetDensityAsync(p as OptionViewModel), _ => true);
        SetModeCommand = new AsyncRelayCommand(p => SetModeAsync(p as OptionViewModel), _ => Idle());
        ForceDeviceCommand = main.ForceDeviceCommand;
        AutoDetectDeviceCommand = new AsyncRelayCommand(() => main.ForceDeviceAsync(null), () => Idle() && _forcedProfile is not null);
        CloseCommand = new RelayCommand(main.CloseSettings);
        OpenReportCommand = main.OpenReportCommand;
    }

    public GitHubAccountViewModel Account { get; }
    public GameLabViewModel Lab => _main.Lab;
    public UpdatesViewModel Updates => _main.Updates;
    public ObservableCollection<OptionViewModel> Stores { get; }
    public ObservableCollection<OptionViewModel> Modes { get; }
    public ObservableCollection<DeviceChoice> DeviceChoices { get; } = new();
    public ObservableCollection<ThemeOptionViewModel> Themes { get; }
    public ObservableCollection<OptionViewModel> Densities { get; }
    public DevicesViewModel Devices { get; }

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


    private bool _autoPull = true;
    private string _lastUpdatedText = "Never";

    /// <summary>Pull new presets in the background and before a game starts.</summary>
    public bool AutoPull
    {
        get => _autoPull;
        set { if (Set(ref _autoPull, value) && !_loading) _ = SaveAsync(s => s.AutoPull = value, value ? "New presets will arrive by themselves" : "Presets are only updated when you sync"); }
    }

    public string LastUpdatedText { get => _lastUpdatedText; private set => Set(ref _lastUpdatedText, value); }

    /// <summary>"Last updated 3 min ago" from the last successful pull on this machine.</summary>
    public void RefreshLastUpdated()
    {
        var t = _main.LastUpdatedUtc;
        if (t is null) { LastUpdatedText = "Last updated: not yet"; return; }
        var ago = DateTime.UtcNow - t.Value;
        LastUpdatedText = "Last updated " + (ago < TimeSpan.FromMinutes(1) ? "just now"
            : ago < TimeSpan.FromHours(1) ? $"{(int)ago.TotalMinutes} min ago"
            : ago < TimeSpan.FromDays(1) ? $"{(int)ago.TotalHours} h ago"
            : t.Value.ToLocalTime().ToString("MMM d HH:mm"));
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
    public ICommand SetThemeCommand { get; }
    public ICommand SetDensityCommand { get; }
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
            AutoPull = s.AutoPull;
            RefreshLastUpdated();
            WriteSteamLaunchOptions = s.WriteSteamLaunchOptions;
            _forcedProfile = s.ForcedProfile;
            foreach (var o in Stores) o.IsOn = s.ScanStores.Contains(o.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var m in Modes) m.IsOn = m.Value.Equals(s.LibraryMode, StringComparison.OrdinalIgnoreCase);
            if (!Modes.Any(m => m.IsOn)) Modes[0].IsOn = true;

            ProviderText = "…";
            ProviderText = await Task.Run(() => SyncService.Describe(session?.Paths, s));

            var theme = ThemeManager.Normalize(s.Theme);
            foreach (var t in Themes) t.IsOn = t.Value == theme;
            var density = Density.Normalize(s.Density);
            foreach (var d in Densities) d.IsOn = d.Value == density;

            var devices = await Task.Run(() =>
            {
                try { return session?.Store.LoadDevices().Devices.Select(kv => new DeviceChoice(kv.Key, DeviceNames.Display(kv.Key, kv.Value))).ToList() ?? new List<DeviceChoice>(); }
                catch (Exception ex) { Log.Warn($"devices.yaml: {ex.Message}"); return new List<DeviceChoice>(); }
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
        await Devices.RefreshAsync();
        await Account.RefreshAsync();
        if (Account.IsSignedIn && !Account.HasRepos) _ = Account.LoadReposAsync();
        _ = Lab.RefreshAsync();
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

    private async Task SetThemeAsync(ThemeOptionViewModel? theme)
    {
        if (theme is null) return;
        ThemeManager.Apply(theme.Value); // instant; styles use DynamicResource
        foreach (var t in Themes) t.IsOn = t == theme;
        await SaveAsync(s => s.Theme = theme.Value, null);
    }

    private async Task SetDensityAsync(OptionViewModel? density)
    {
        if (density is null) return;
        Density.SetMode(density.Value); // instant: re-evaluate the open window, the styles use the density tokens
        if (System.Windows.Application.Current?.MainWindow is { } window) Density.Apply(window);
        foreach (var d in Densities) d.IsOn = d == density;
        await SaveAsync(s => s.Density = density.Value, null);
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

    public bool CancelTransient() => Devices.CancelTransient() || Account.CancelTransient();
}
