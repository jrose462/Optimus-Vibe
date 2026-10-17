using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OptimusVibe.App.Services;
using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Library;
using OptimusVibe.Logging;
using Microsoft.Win32;

namespace OptimusVibe.App.ViewModels;

/// <summary>What child view models need from the window: the session, a busy gate, and user feedback.</summary>
public interface IAppShell
{
    AppSession? Session { get; }
    bool IsBusy { get; }

    /// <summary>Runs <paramref name="work"/> with the busy indicator on; exceptions are logged and shown. False on failure.</summary>
    Task<bool> RunAsync(string busyText, Func<Task> work);

    void Notify(string message, bool error = false);

    /// <summary>Opens the Report a problem panel (prefilled with the game and its active preset, when known).</summary>
    void OpenReport(string? gameId, string? preset);

    /// <summary>Re-reads one game (after use/apply/save...) and updates its list entry and detail in place.</summary>
    Task ReloadGameAsync(string gameId);

    /// <summary>Whether the game can be started (its yaml or a detected install knows how).</summary>
    bool CanPlay(GameDef game);

    /// <summary>Start the game the library way (detected install, overlay, minimize).</summary>
    Task PlayAsync(GameDef game);
}

public enum AppState { Loading, Setup, Ready }

/// <summary>Pages inside the Ready state.</summary>
public enum AppPage { Library, Manage, Detail, Settings }

public sealed class MainViewModel : ObservableObject, IAppShell
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private AppState _state = AppState.Loading;
    private AppPage _page = AppPage.Manage;
    private AppPage _pageBeforeSettings = AppPage.Manage;
    private bool _pageChosen;
    private AppSession? _session;
    private GameItemViewModel? _selectedGame;
    private LibraryTileViewModel? _detailTile;
    private bool _isBusy;
    private string? _busyText;
    private string _statusText = "Starting…";
    private bool _statusIsError;
    private string? _toastText;
    private bool _toastIsError;
    private string? _toastActionText;
    private Action? _toastAction;
    private string? _setupMessage;
    private string _setupStep = "choose";
    private string? _setupConfigRoot;
    private IReadOnlyList<LaunchOptionChange>? _steamChanges;
    private LibraryTileViewModel? _launching;
    private bool _gamepad;
    private GameDetailViewModel? _hookedDetail;

    public MainViewModel()
    {
        ShowSavesCommand = new RelayCommand(ShowSaves, () => IsReady);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText = null; };
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(rescan: true), () => !IsBusy);
        SyncCommand = new AsyncRelayCommand(SyncAsync, () => !IsBusy && Session is not null);
        PickConfigFolderCommand = new AsyncRelayCommand(PickConfigFolderAsync, () => !IsBusy);
        SetupGitHubCommand = new RelayCommand(StartGitHubStep);
        SetupBackCommand = new RelayCommand(() => SetupStep = "choose");
        ForceDeviceCommand = new AsyncRelayCommand(p => ForceDeviceAsync(p as string), _ => !IsBusy);
        LaunchCommand = new AsyncRelayCommand(() => SelectedGame?.Detail?.LaunchAsync() ?? Task.CompletedTask,
            () => !IsBusy && SelectedGame?.Detail?.CanLaunch == true);
        DismissToastCommand = new RelayCommand(() => ToastText = null);
        ShowLibraryCommand = new RelayCommand(() => SwitchMode(AppPage.Library), () => IsReady);
        ShowManageCommand = new RelayCommand(() => SwitchMode(AppPage.Manage), () => IsReady);
        OpenSettingsCommand = new RelayCommand(OpenSettings, () => IsReady);
        CloseSettingsCommand = new RelayCommand(CloseSettings);
        BackToLibraryCommand = new RelayCommand(CloseDetail);
        ApplySteamCommand = new AsyncRelayCommand(ApplySteamAsync, () => !IsBusy && HasSteamBanner);
        ReviewSteamCommand = new RelayCommand(OpenSteamReview, () => !IsBusy && HasSteamBanner);
        DismissSteamCommand = new RelayCommand(() => SteamChanges = null);
        ToastActionCommand = new RelayCommand(() =>
        {
            var action = _toastAction;
            ToastText = null;
            action?.Invoke();
        });
        OpenReportCommand = new RelayCommand(() =>
        {
            var detail = IsReady && Page is AppPage.Manage or AppPage.Detail ? SelectedDetail : null;
            OpenReport(detail?.GameId, detail?.ActivePresetName);
        });

        Account = new GitHubAccountViewModel(this);
        Library = new LibraryViewModel(this);
        Updates = new UpdatesViewModel(this);
        Report = new ReportViewModel(this);
        SteamReview = new SteamReviewViewModel(this);
        Report.PropertyChanged += OnOverlayChanged;
        SteamReview.PropertyChanged += OnOverlayChanged;
        Settings = new SettingsViewModel(this, Account);
    }

    public LibraryViewModel Library { get; }
    public SettingsViewModel Settings { get; }
    public GitHubAccountViewModel Account { get; }
    public UpdatesViewModel Updates { get; }
    public ReportViewModel Report { get; }
    public SteamReviewViewModel SteamReview { get; }

    /// <summary>A modal panel (report / Steam review) covers the page: the page underneath is not interactive.</summary>
    public bool IsModalOpen => Report.IsOpen || SteamReview.IsOpen;

    private void OnOverlayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportViewModel.IsOpen)) OnPropertyChanged(nameof(IsModalOpen));
    }

    /// <summary>The window minimizes after a launch and comes back when the game (our wrapper) exits.</summary>
    public event Action? MinimizeRequested;
    public event Action? RestoreRequested;

    // ---------------------------------------------------------------- state

    public AppState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsSetup));
            OnPropertyChanged(nameof(IsReady));
            OnPageChanged();
        }
    }

    public bool IsLoading => State == AppState.Loading;
    public bool IsSetup => State == AppState.Setup;
    public bool IsReady => State == AppState.Ready;

    public AppPage Page
    {
        get => _page;
        private set
        {
            if (Set(ref _page, value)) OnPageChanged();
        }
    }

    private void OnPageChanged()
    {
        OnPropertyChanged(nameof(IsLibraryPage));
        OnPropertyChanged(nameof(IsManagePage));
        OnPropertyChanged(nameof(IsDetailPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsLibraryMode));
        OnPropertyChanged(nameof(ControllerHint));
        OnPropertyChanged(nameof(ShowConfigLine));
        RaiseNav();
        CommandManager.InvalidateRequerySuggested();
    }

    public bool IsLibraryPage => IsReady && Page == AppPage.Library;
    public bool IsManagePage => IsReady && Page == AppPage.Manage;
    public bool IsDetailPage => IsReady && Page == AppPage.Detail;
    public bool IsSettingsPage => IsReady && Page == AppPage.Settings;

    /// <summary>Header toggle: Library (grid + its detail pages) vs. Manage (list).</summary>
    public bool IsLibraryMode => Page is AppPage.Library or AppPage.Detail || (Page == AppPage.Settings && _pageBeforeSettings != AppPage.Manage);

    /// <summary>The config path line under the title: useful in Manage, noise on the handheld library.</summary>
    public bool ShowConfigLine => !IsReady || Page == AppPage.Manage;

    /// <summary>True while a gamepad is connected; the quiet hint line at the bottom only shows then.</summary>
    public bool IsGamepadConnected
    {
        get => _gamepad;
        set { if (Set(ref _gamepad, value)) OnPropertyChanged(nameof(ControllerHint)); }
    }

    public string ControllerHint => !IsReady
        ? "A select  ·  B back"
        : Page switch
        {
            AppPage.Library => "A play  ·  X presets  ·  LB RB filter  ·  Menu settings",
            AppPage.Detail => "A select  ·  B back  ·  LB RB switch tabs",
            AppPage.Settings => "A select  ·  B back",
            _ => "A select  ·  B back  ·  LB RB switch tabs  ·  X launch",
        };

    // ---------------------------------------------------------------- bottom navigation

    private bool OnSavesTab => SelectedDetail is { SelectedTabIndex: GameDetailViewModel.ManualTab } && Page is AppPage.Manage or AppPage.Detail;

    public bool NavLibrary => IsReady && (Page == AppPage.Library || (Page == AppPage.Detail && !OnSavesTab));
    public bool NavManage => IsReady && Page == AppPage.Manage && !OnSavesTab;
    public bool NavSaves => IsReady && OnSavesTab;
    public bool NavSettings => IsReady && Page == AppPage.Settings;

    public ICommand ShowSavesCommand { get; }

    private void RaiseNav()
    {
        OnPropertyChanged(nameof(NavLibrary));
        OnPropertyChanged(nameof(NavManage));
        OnPropertyChanged(nameof(NavSaves));
        OnPropertyChanged(nameof(NavSettings));
    }

    /// <summary>Saves: the open game's Manual saves tab (the Manage list when no game page is open).</summary>
    private void ShowSaves()
    {
        if (Page is not (AppPage.Manage or AppPage.Detail)) SwitchMode(AppPage.Manage);
        if (SelectedDetail is { HasGame: true } d) d.SelectedTabIndex = GameDetailViewModel.ManualTab;
        RaiseNav();
    }

    private void HookDetail(GameDetailViewModel? detail)
    {
        if (_hookedDetail == detail) return;
        if (_hookedDetail is not null) _hookedDetail.PropertyChanged -= OnDetailChanged;
        _hookedDetail = detail;
        if (detail is not null) detail.PropertyChanged += OnDetailChanged;
    }

    private void OnDetailChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameDetailViewModel.SelectedTabIndex) or "") RaiseNav();
    }

    public AppSession? Session
    {
        get => _session;
        private set
        {
            if (!Set(ref _session, value)) return;
            OnPropertyChanged(nameof(DeviceName));
            OnPropertyChanged(nameof(DeviceFriendly));
            OnPropertyChanged(nameof(DeviceReason));
            OnPropertyChanged(nameof(DeviceDescription));
            OnPropertyChanged(nameof(ConfigRoot));
        }
    }

    public string DeviceName => Session?.Device.Name ?? "no device";

    /// <summary>"desktop", "Ally X" — for friendly sentences.</summary>
    public string DeviceFriendly => DeviceName.Equals("ally-x", StringComparison.OrdinalIgnoreCase)
        ? "Ally X"
        : DeviceName.Replace('-', ' ');
    public string DeviceReason => Session?.Device.Reason ?? "";
    public string? DeviceDescription => Session?.Device.Def?.Description;
    public string ConfigRoot => Session?.Paths.ConfigRoot ?? _setupConfigRoot ?? "config folder not set";

    public ObservableCollection<GameItemViewModel> Games { get; } = new();

    public GameItemViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!Set(ref _selectedGame, value)) return;
            value?.EnsureDetail(this);
            HookDetail(value?.Detail);
            OnPropertyChanged(nameof(SelectedDetail));
            RaiseNav();
        }
    }

    public GameDetailViewModel? SelectedDetail => SelectedGame?.Detail;

    /// <summary>The library tile whose page is open (Detail page).</summary>
    public LibraryTileViewModel? DetailTile
    {
        get => _detailTile;
        private set { if (Set(ref _detailTile, value)) OnPropertyChanged(nameof(DetailHasConfig)); }
    }

    /// <summary>The open library game has a config here: show the presets (else the "queued for Claude" card).</summary>
    public bool DetailHasConfig => DetailTile?.GameItem is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public string? ToastText
    {
        get => _toastText;
        private set
        {
            if (Set(ref _toastText, value)) OnPropertyChanged(nameof(HasToast));
        }
    }

    public bool HasToast => ToastText is not null;
    public string? ToastActionText { get => _toastActionText; private set { if (Set(ref _toastActionText, value)) OnPropertyChanged(nameof(HasToastAction)); } }
    public bool HasToastAction => ToastActionText is not null;
    public bool ToastIsError { get => _toastIsError; private set => Set(ref _toastIsError, value); }

    public string? SetupMessage { get => _setupMessage; private set => Set(ref _setupMessage, value); }
    public ObservableCollection<string> DeviceChoices { get; } = new();
    public bool HasDeviceChoices => DeviceChoices.Count > 0;

    // ---------------------------------------------------------------- steam banner

    public IReadOnlyList<LaunchOptionChange>? SteamChanges
    {
        get => _steamChanges;
        private set
        {
            if (!Set(ref _steamChanges, value)) return;
            OnPropertyChanged(nameof(HasSteamBanner));
            OnPropertyChanged(nameof(SteamBannerText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSteamBanner => SteamChanges is { Count: > 0 };

    public string SteamBannerText => SteamChanges is { Count: var n }
        ? $"{n} Steam launch option{(n == 1 ? "" : "s")} to update. Steam has to be closed first so your presets are used."
        : "";

    // ---------------------------------------------------------------- launch overlay

    public LibraryTileViewModel? Launching
    {
        get => _launching;
        private set { if (Set(ref _launching, value)) OnPropertyChanged(nameof(IsLaunching)); }
    }

    public bool IsLaunching => Launching is not null;

    public ICommand RefreshCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand PickConfigFolderCommand { get; }
    public ICommand SetupGitHubCommand { get; }
    public ICommand SetupBackCommand { get; }

    /// <summary>First-run step: "choose" (GitHub or a local folder) or "github" (sign in, then create / pick the config repo).</summary>
    public string SetupStep
    {
        get => _setupStep;
        private set
        {
            if (!Set(ref _setupStep, value)) return;
            OnPropertyChanged(nameof(SetupIsChoose));
            OnPropertyChanged(nameof(SetupIsGitHub));
        }
    }

    private void StartGitHubStep()
    {
        SetupStep = "github";
        _ = Account.PrepareSetupAsync();
    }

    public bool SetupIsChoose => SetupStep == "choose";
    public bool SetupIsGitHub => SetupStep == "github";
    public ICommand ForceDeviceCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand DismissToastCommand { get; }
    public ICommand ShowLibraryCommand { get; }
    public ICommand ShowManageCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public ICommand BackToLibraryCommand { get; }
    public ICommand ApplySteamCommand { get; }
    public ICommand ReviewSteamCommand { get; }
    public ICommand DismissSteamCommand { get; }
    public ICommand ToastActionCommand { get; }
    public ICommand OpenReportCommand { get; }

    // ---------------------------------------------------------------- shell

    public async Task<bool> RunAsync(string busyText, Func<Task> work)
    {
        if (IsBusy) return false;
        IsBusy = true;
        BusyText = busyText;
        SetStatus(busyText + "…");
        try
        {
            await work();
            if (StatusText == busyText + "…") SetStatus("Ready");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"{busyText} failed", ex);
            Notify($"{busyText} failed: {Friendly(ex)}", error: true);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private static string Friendly(Exception ex) => ex is NotImplementedException
        ? "this feature isn't available in this build yet."
        : ex.Message;

    public void Notify(string message, bool error = false) => ShowToast(message, error, null, null);

    /// <summary>A toast with one button (e.g. "Open" for a filed issue); stays up longer so it can be reached.</summary>
    public void NotifyWithAction(string message, string actionText, Action action) => ShowToast(message, false, actionText, action);

    public void OpenReport(string? gameId, string? preset) => Report.Open(gameId, preset);

    private void ShowToast(string message, bool error, string? actionText, Action? action)
    {
        _toastAction = action;
        ToastActionText = actionText;
        SetStatus(message, error);
        ToastIsError = error;
        ToastText = message;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(error ? 9 : actionText is null ? 5 : 20);
        _toastTimer.Start();
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
    }

    public async Task ReloadGameAsync(string gameId)
    {
        var session = Session;
        if (session is null) return;
        var snap = await Task.Run(() => GameSnapshot.Load(session, gameId));
        var item = Games.FirstOrDefault(g => string.Equals(g.Id, gameId, StringComparison.OrdinalIgnoreCase));
        item?.Update(snap);
        if (item == SelectedGame) OnPropertyChanged(nameof(SelectedDetail));
        Library.RefreshBadges();
    }

    /// <summary>B / Escape: close whatever is open on the current page. False when nothing was (manage list: B then focuses the list).</summary>
    public bool CancelTransient()
    {
        if (SteamReview.IsOpen)
        {
            SteamReview.Close();
            return true;
        }
        if (Report.IsOpen)
        {
            Report.Close();
            return true;
        }
        if (!IsReady) return Account.CancelTransient();
        switch (Page)
        {
            case AppPage.Settings:
                if (!Settings.CancelTransient()) CloseSettings();
                return true;
            case AppPage.Detail:
                if (!(SelectedDetail?.CancelTransient() ?? false)) CloseDetail();
                return true;
            case AppPage.Library:
                return false;
            default:
                return SelectedDetail?.CancelTransient() ?? false;
        }
    }

    // ---------------------------------------------------------------- pages

    private void SwitchMode(AppPage page)
    {
        if (page == AppPage.Library && Page == AppPage.Detail)
        {
            CloseDetail();
            return;
        }
        if (Page == AppPage.Settings) Account.CancelTransient();
        Page = page;
        var mode = page == AppPage.Library ? "grid" : "list";
        _ = SettingsStore.UpdateAsync(s => s.LibraryMode = mode);
    }

    /// <summary>Settings → view mode changed (auto / grid / list): that's where B returns to.</summary>
    public void ApplyLibraryMode(string mode)
    {
        _pageBeforeSettings = ResolveMode(mode);
        OnPropertyChanged(nameof(IsLibraryMode));
    }

    /// <summary>auto: the library grid on the handheld (ally-x) or a small work area, the list otherwise.</summary>
    private AppPage ResolveMode(string? mode)
    {
        switch ((mode ?? "auto").ToLowerInvariant())
        {
            case "grid": return AppPage.Library;
            case "list": return AppPage.Manage;
        }
        var area = SystemParameters.WorkArea;
        bool handheld = string.Equals(Session?.Device.Name, "ally-x", StringComparison.OrdinalIgnoreCase);
        return handheld || area.Width < 1600 || area.Height < 900 ? AppPage.Library : AppPage.Manage;
    }

    public void OpenSettings() => OpenSettings(null);

    /// <summary>Opens Settings, optionally scrolled to a section ("updates", "account").</summary>
    public void OpenSettings(string? section)
    {
        if (!IsReady) return;
        if (Page != AppPage.Settings) _pageBeforeSettings = Page;
        Page = AppPage.Settings;
        _ = Settings.RefreshAsync();
        if (section is not null) Settings.RequestSection(section);
    }

    public void CloseSettings()
    {
        if (Page != AppPage.Settings) return;
        Account.CancelTransient();
        Page = _pageBeforeSettings == AppPage.Settings ? AppPage.Manage : _pageBeforeSettings;
        if (Settings.ConsumeLibraryDirty()) _ = Library.ScanAsync(rescanLaunchers: true);
    }

    public void OpenLibraryDetail(LibraryTileViewModel tile)
    {
        Library.SelectedTile = tile;
        DetailTile = tile;
        if (tile.GameItem is { } item) SelectedGame = item;
        OnPropertyChanged(nameof(DetailHasConfig));
        Page = AppPage.Detail;
    }

    public void CloseDetail()
    {
        SelectedDetail?.CancelTransient();
        Page = AppPage.Library;
    }

    // ---------------------------------------------------------------- loading

    public Task ReloadAllAsync() => LoadAsync(rescan: true);

    public async Task LoadAsync(bool rescan = true)
    {
        var keepId = SelectedGame?.Id;
        var firstLoad = Session is null;
        if (firstLoad) State = AppState.Loading;

        StartupResult? result = null;
        var ok = await RunAsync(firstLoad ? "Detecting device and loading games" : "Refreshing",
            async () => result = await Task.Run(StartupResult.Load));
        if (!ok || result is null)
        {
            if (Session is null) EnterSetup("Could not load the config — see the status bar and the log.", [], null);
            return;
        }

        if (result.Session is null)
        {
            EnterSetup(result.SetupMessage, result.DeviceChoices, result.ConfigRoot);
            SetStatus(result.SetupMessage ?? "Setup needed", error: true);
            _ = Account.RefreshAsync();
            return;
        }

        if (Session is not null && Session.Paths.ConfigRoot != result.Session.Paths.ConfigRoot) Library.Clear();
        Session = result.Session;
        Games.Clear();
        foreach (var snap in result.Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            Games.Add(new GameItemViewModel(snap));

        if (!_pageChosen)
        {
            AppSettings settings;
            try { settings = await SettingsStore.LoadAsync(); }
            catch (Exception) { settings = new AppSettings(); }
            Page = ResolveMode(settings.LibraryMode);
            _pageChosen = true;
        }
        State = AppState.Ready;
        SelectedGame = Games.FirstOrDefault(g => g.Id == keepId) ?? Games.FirstOrDefault();
        Library.RefreshBadges();
        if (Page == AppPage.Detail && DetailTile?.GameItem is { } detailItem) SelectedGame = detailItem;
        OnPropertyChanged(nameof(DetailHasConfig));

        var errors = Games.Count(g => g.HasError);
        SetStatus($"{Games.Count} game(s) for {Session.Device.Name}{(errors > 0 ? $" — {errors} with config errors" : "")}", errors > 0);
        Log.Info($"App loaded: device {Session.Device.Name} ({Session.Device.Reason}), config {Session.Paths.ConfigRoot}, {Games.Count} game(s)");

        _ = Library.ScanAsync(rescanLaunchers: rescan || Library.Detected.Count == 0);
    }

    private void EnterSetup(string? message, List<string> deviceChoices, string? configRoot)
    {
        if (State != AppState.Setup) SetupStep = "choose";
        Session = null;
        _setupConfigRoot = configRoot;
        OnPropertyChanged(nameof(ConfigRoot));
        SetupMessage = message;
        DeviceChoices.Clear();
        foreach (var d in deviceChoices) DeviceChoices.Add(d);
        OnPropertyChanged(nameof(HasDeviceChoices));
        Games.Clear();
        Library.Clear();
        SelectedGame = null;
        DetailTile = null;
        State = AppState.Setup;
    }

    /// <summary>Library scan finished: launch buttons in detail pages may have become possible.</summary>
    public void OnLibraryChanged()
    {
        foreach (var g in Games) g.Detail?.RefreshLaunch();
        OnPropertyChanged(nameof(DetailHasConfig));
    }

    // ---------------------------------------------------------------- setup

    private async Task PickConfigFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder for your game settings (your config repo checkout, or any folder)",
            InitialDirectory = _setupConfigRoot ?? Session?.Paths.RepoRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        // Accept the repo root too: use its config\ subfolder.
        if (!File.Exists(Path.Combine(folder, "devices.yaml")) && File.Exists(Path.Combine(folder, "config", "devices.yaml")))
            folder = Path.Combine(folder, "config");
        if (!File.Exists(Path.Combine(folder, "devices.yaml")))
        {
            // No Optimus-Vibe settings here yet: offer to create the generic starter set in <folder>\config.
            var target = Path.Combine(folder, "config");
            var ask = MessageBox.Show(
                $"{folder} has no Optimus-Vibe settings yet.\n\nCreate a starter set in {target}?\n(Example devices, a game template and empty library files. Nothing existing is overwritten.)",
                "Optimus-Vibe", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;
            try
            {
                var written = SeedConfig.WriteTo(target);
                Log.Info($"Seeded {target} with the starter config ({written.Count} file(s))");
                folder = target;
            }
            catch (Exception ex)
            {
                Log.Error("Seeding the folder failed", ex);
                Notify($"Couldn't create the starter set: {ex.Message}", error: true);
                return;
            }
            if (!Directory.Exists(Path.Combine(Directory.GetParent(target)!.FullName, ".git")))
                Notify("Created. This folder is not a git repo, so settings won't sync between machines until you make it one (git init, or clone your config repo here).");
        }

        var root = Path.GetFullPath(folder);
        try
        {
            await SettingsStore.UpdateAsync(s =>
            {
                s.ConfigRoot = root;
                s.SyncProvider = "git"; // a local checkout is synced with the git CLI
            });
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            Notify($"Couldn't save settings: {ex.Message}", error: true);
            return;
        }
        Log.Info($"Config root set to {root} (git checkout)");
        if (AppPaths.Env("CONFIG") is { Length: > 0 } env)
            Notify($"Saved, but OPTIMUSVIBE_CONFIG ({env}) overrides it for this session.", error: true);
        if (Page == AppPage.Settings) CloseSettings();
        await LoadAsync();
    }

    /// <summary>Force a device profile (null = auto-detect again), then reload.</summary>
    public async Task ForceDeviceAsync(string? device)
    {
        if (device is { Length: 0 }) return;
        try
        {
            await SettingsStore.UpdateAsync(s => s.ForcedProfile = device);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            Notify($"Couldn't save settings: {ex.Message}", error: true);
            return;
        }
        Log.Info(device is null ? "Forced device profile cleared" : $"Forced device profile set to {device}");
        await LoadAsync(rescan: false);
        if (Page == AppPage.Settings) _ = Settings.RefreshAsync();
    }

    // ---------------------------------------------------------------- sync

    private async Task SyncAsync()
    {
        var session = Session;
        if (session is null) return;
        GitResult? result = null;
        var ran = await RunAsync("Syncing config repo", () => Task.Run(() =>
        {
            result = SyncService.CommitAndSync(session.Paths, "chore(sync): sync manual saves and library", SyncService.SharedPaths(session.Paths));
            Log.Info($"Sync: {result.Message}");
        }));
        if (!ran || result is null) return;
        Notify($"Sync: {result.Message}", error: !result.Ok);
        if (result.Ok) await LoadAsync(rescan: false); // a pull may have brought presets, saves or opt-outs from the other device
    }

    /// <summary>Publish a few changed files (request queue, library.yaml) without a success toast. Skipped while busy.</summary>
    public async Task SyncFilesQuietlyAsync(string message, params string[] files)
    {
        var session = Session;
        if (session is null) return;
        if (IsBusy)
        {
            Log.Info($"Sync skipped (busy): {message} — published with the next sync");
            return;
        }
        GitResult? result = null;
        await RunAsync("Syncing", () => Task.Run(() => result = SyncService.CommitAndSync(session.Paths, message, files)));
        if (result is { Ok: false }) Notify($"Sync failed: {result.Message}", error: true);
        else if (result is not null) Log.Info($"Synced: {message} ({result.Message})");
    }

    // ---------------------------------------------------------------- steam launch options

    /// <summary>Changes that need Steam closed: show the banner (never a modal).</summary>
    public void OfferSteamChanges(IReadOnlyList<LaunchOptionChange> changes) => SteamChanges = changes.Count > 0 ? changes : null;

    private void OpenSteamReview()
    {
        if (SteamChanges is { Count: > 0 } changes) SteamReview.Open(changes);
    }

    private async Task ApplySteamAsync()
    {
        var changes = SteamChanges;
        if (changes is null || changes.Count == 0) return;
        await ApplySteamChangesAsync(changes, []);
    }

    /// <summary>
    /// Close Steam if it runs, then write <paramref name="changes"/>. <paramref name="skipKeys"/> ("steam:appid" of the
    /// games the user unticked) go to steamLaunchOptionsSkip afterwards, synced when AutoSync is on, so they aren't proposed again.
    /// </summary>
    public async Task<bool> ApplySteamChangesAsync(IReadOnlyList<LaunchOptionChange> changes, IReadOnlyList<string> skipKeys)
    {
        var session = Session;
        if (session is null || changes.Count == 0) return false;
        var ok = await RunAsync("Closing Steam and updating launch options", () => Task.Run(() =>
        {
            if (SteamLaunchOptions.IsSteamRunning() && !SteamLaunchOptions.ExitSteam(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Steam didn't close within 30 seconds — close it yourself, then try again.");
            SteamLaunchOptions.Apply(changes);
            foreach (var key in skipKeys) LibraryService.SetSteamLaunchOptionSkip(session.Store, key, true);
        }));
        if (!ok) return false;
        SteamChanges = null;
        Notify(skipKeys.Count == 0
            ? $"Steam launch options updated for {changes.Count} game(s). You can start Steam again."
            : $"Steam launch options updated for {changes.Count} game(s); {skipKeys.Count} left as they are. You can start Steam again.");
        if (skipKeys.Count > 0)
        {
            var settings = await SettingsStore.LoadAsync();
            if (settings.AutoSync)
                await SyncFilesQuietlyAsync($"chore(library): keep {skipKeys.Count} Steam launch option(s) as they are", session.Paths.LibraryFile);
            _ = Library.ScanAsync(rescanLaunchers: false); // tiles pick up the new skip flags
        }
        return true;
    }

    // ---------------------------------------------------------------- play

    public bool CanPlay(GameDef game) =>
        Library.FindByGameId(game.Id)?.CanPlay ?? GameLauncher.CanLaunch(game);

    public async Task PlayAsync(GameDef game)
    {
        if (Library.FindByGameId(game.Id) is { } tile)
        {
            await PlayTileAsync(tile);
            return;
        }
        string? message = null;
        var ok = await RunAsync($"Launching {game.Name}", () => Task.Run(() => message = GameLauncher.Launch(game)));
        if (ok && message is not null) Notify(message);
    }

    /// <summary>Managed → headless `run` (preset applied, restored after); otherwise a plain launcher start. Then minimize.</summary>
    public async Task PlayTileAsync(LibraryTileViewModel tile)
    {
        var managed = tile.Status == TileStatus.Managed && tile.Entry.Game is not null;
        LaunchHandle? handle = null;
        var ok = await RunAsync($"Starting {tile.Name}", () => Task.Run(() =>
            handle = GameLauncher.Play(tile.Name, tile.Entry.Game, tile.Entry.Detected, managed)));
        if (!ok || handle is null) return;

        Launching = tile;
        Notify(handle.Message);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Launching = null;
        MinimizeRequested?.Invoke();
        if (handle.Process is { } p) _ = RestoreWhenExitedAsync(p, tile.Name);
    }

    private async Task RestoreWhenExitedAsync(Process process, string name)
    {
        try
        {
            using (process)
                await process.WaitForExitAsync();
            Log.Info($"{name}: launch process exited — restoring the window");
            RestoreRequested?.Invoke();
            Notify($"Welcome back from {name}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Waiting for {name}: {ex.Message}");
        }
    }
}
