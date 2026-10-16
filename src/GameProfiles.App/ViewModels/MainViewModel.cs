using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using GameProfiles.App.Services;
using GameProfiles.Config;
using GameProfiles.Engine;
using GameProfiles.Logging;
using Microsoft.Win32;

namespace GameProfiles.App.ViewModels;

/// <summary>What child view models need from the window: the session, a busy gate, and user feedback.</summary>
public interface IAppShell
{
    AppSession? Session { get; }
    bool IsBusy { get; }

    /// <summary>Runs <paramref name="work"/> with the busy indicator on; exceptions are logged and shown. False on failure.</summary>
    Task<bool> RunAsync(string busyText, Func<Task> work);

    void Notify(string message, bool error = false);

    /// <summary>Re-reads one game (after use/apply/save...) and updates its list entry and detail in place.</summary>
    Task ReloadGameAsync(string gameId);
}

public enum AppState { Loading, Setup, Ready }

public sealed class MainViewModel : ObservableObject, IAppShell
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private AppState _state = AppState.Loading;
    private AppSession? _session;
    private GameItemViewModel? _selectedGame;
    private bool _isBusy;
    private string? _busyText;
    private string _statusText = "Starting…";
    private bool _statusIsError;
    private string? _toastText;
    private bool _toastIsError;
    private string? _setupMessage;
    private string? _setupConfigRoot;

    public MainViewModel()
    {
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText = null; };
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(), () => !IsBusy);
        SyncCommand = new AsyncRelayCommand(SyncAsync, () => !IsBusy && Session is not null);
        PickConfigFolderCommand = new AsyncRelayCommand(PickConfigFolderAsync, () => !IsBusy);
        ForceDeviceCommand = new AsyncRelayCommand(p => ForceDeviceAsync(p as string), _ => !IsBusy);
        LaunchCommand = new AsyncRelayCommand(() => SelectedGame?.Detail?.LaunchAsync() ?? Task.CompletedTask,
            () => !IsBusy && SelectedGame?.Detail?.CanLaunch == true);
        DismissToastCommand = new RelayCommand(() => ToastText = null);
    }

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
        }
    }

    public bool IsLoading => State == AppState.Loading;
    public bool IsSetup => State == AppState.Setup;
    public bool IsReady => State == AppState.Ready;

    public AppSession? Session
    {
        get => _session;
        private set
        {
            if (!Set(ref _session, value)) return;
            OnPropertyChanged(nameof(DeviceName));
            OnPropertyChanged(nameof(DeviceReason));
            OnPropertyChanged(nameof(DeviceDescription));
            OnPropertyChanged(nameof(ConfigRoot));
        }
    }

    public string DeviceName => Session?.Device.Name ?? "no device";
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
            OnPropertyChanged(nameof(SelectedDetail));
        }
    }

    public GameDetailViewModel? SelectedDetail => SelectedGame?.Detail;

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
    public bool ToastIsError { get => _toastIsError; private set => Set(ref _toastIsError, value); }

    public string? SetupMessage { get => _setupMessage; private set => Set(ref _setupMessage, value); }
    public ObservableCollection<string> DeviceChoices { get; } = new();
    public bool HasDeviceChoices => DeviceChoices.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand PickConfigFolderCommand { get; }
    public ICommand ForceDeviceCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand DismissToastCommand { get; }

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
            Notify($"{busyText} failed: {ex.Message}", error: true);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    public void Notify(string message, bool error = false)
    {
        SetStatus(message, error);
        ToastIsError = error;
        ToastText = message;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(error ? 9 : 5);
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
    }

    /// <summary>B / Escape: close whatever transient panel is open. False when nothing was open.</summary>
    public bool CancelTransient() => SelectedDetail?.CancelTransient() ?? false;

    // ---------------------------------------------------------------- loading

    public async Task LoadAsync()
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
            return;
        }

        Session = result.Session;
        Games.Clear();
        foreach (var snap in result.Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            Games.Add(new GameItemViewModel(snap));
        State = AppState.Ready;
        SelectedGame = Games.FirstOrDefault(g => g.Id == keepId) ?? Games.FirstOrDefault();
        var errors = Games.Count(g => g.HasError);
        SetStatus($"{Games.Count} game(s) for {Session.Device.Name}{(errors > 0 ? $" — {errors} with config errors" : "")}", errors > 0);
        Log.Info($"App loaded: device {Session.Device.Name} ({Session.Device.Reason}), config {Session.Paths.ConfigRoot}, {Games.Count} game(s)");
    }

    private void EnterSetup(string? message, List<string> deviceChoices, string? configRoot)
    {
        Session = null;
        _setupConfigRoot = configRoot;
        OnPropertyChanged(nameof(ConfigRoot));
        SetupMessage = message;
        DeviceChoices.Clear();
        foreach (var d in deviceChoices) DeviceChoices.Add(d);
        OnPropertyChanged(nameof(HasDeviceChoices));
        Games.Clear();
        SelectedGame = null;
        State = AppState.Setup;
    }

    // ---------------------------------------------------------------- setup

    private async Task PickConfigFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pick the config folder of your GameProfiles repo (the one with devices.yaml)",
            InitialDirectory = _setupConfigRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        // Accept the repo root too: use its config\ subfolder.
        if (!File.Exists(Path.Combine(folder, "devices.yaml")) && File.Exists(Path.Combine(folder, "config", "devices.yaml")))
            folder = Path.Combine(folder, "config");
        if (!File.Exists(Path.Combine(folder, "devices.yaml")))
        {
            Notify($"{folder} has no devices.yaml — pick the repo's config folder.", error: true);
            return;
        }

        var dataDir = AppPaths.DefaultDataDir;
        var settings = AppPaths.LoadSettings(dataDir);
        settings.ConfigRoot = Path.GetFullPath(folder);
        AppPaths.SaveSettings(dataDir, settings);
        Log.Info($"Config root set to {settings.ConfigRoot}");
        if (Environment.GetEnvironmentVariable("GAMEPROFILES_CONFIG") is { Length: > 0 } env)
            Notify($"Saved, but GAMEPROFILES_CONFIG ({env}) overrides it for this session.", error: true);
        await LoadAsync();
    }

    private async Task ForceDeviceAsync(string? device)
    {
        if (string.IsNullOrEmpty(device)) return;
        var dataDir = AppPaths.DefaultDataDir;
        var settings = AppPaths.LoadSettings(dataDir);
        settings.ForcedProfile = device;
        AppPaths.SaveSettings(dataDir, settings);
        Log.Info($"Forced device profile set to {device}");
        await LoadAsync();
    }

    // ---------------------------------------------------------------- sync

    private async Task SyncAsync()
    {
        var session = Session;
        if (session is null) return;
        string message = "";
        bool ok = false;
        var ran = await RunAsync("Syncing config repo", () => Task.Run(() =>
        {
            // Same as `gameprofiles sync`.
            var git = new GitSync(session.Paths.RepoRoot);
            var commit = git.Commit("chore(saves): sync manual saves", session.Paths.SavesDir);
            var sync = git.Sync();
            ok = commit.Ok && sync.Ok;
            message = $"Sync: {commit.Message}; {sync.Message}";
            Log.Info(message);
        }));
        if (!ran) return;
        Notify(message, error: !ok);
        if (ok) await LoadAsync(); // a pull may have brought presets or saves from the other device
    }
}
