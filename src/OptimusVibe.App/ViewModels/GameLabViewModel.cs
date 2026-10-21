using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Library;
using OptimusVibe.Logging;
using OptimusVibe.Outbox;
using OptimusVibe.Sync;

namespace OptimusVibe.App.ViewModels;

public enum LabStepState { Pending, Current, Done, Skipped }

/// <summary>One line of the game-lab wizard: a mark, a title, a one-line why and its action.</summary>
public sealed class LabStepViewModel : ObservableObject
{
    private LabStepState _state = LabStepState.Pending;
    private string? _detail;
    private string? _warning;
    private bool _expanded;

    public LabStepViewModel(int number, string id, string title, string why, string actionText, Func<LabStepViewModel, object?, Task> run)
    {
        Number = number;
        Id = id;
        Title = title;
        Why = why;
        ActionText = actionText;
        RunCommand = new AsyncRelayCommand(p => run(this, p));
        ToggleCommand = new RelayCommand(() => Expanded = !Expanded);
    }

    public int Number { get; }
    public string Id { get; }
    public string Title { get; }
    public string Why { get; }
    public string ActionText { get; }
    public ICommand RunCommand { get; }
    public ICommand ToggleCommand { get; }

    public LabStepState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            foreach (var n in new[] { nameof(IsDone), nameof(IsCurrent), nameof(IsSkipped), nameof(IsPending), nameof(ShowBody), nameof(ShowRerun) })
                OnPropertyChanged(n);
        }
    }

    public string? Detail { get => _detail; set { if (Set(ref _detail, value)) OnPropertyChanged(nameof(HasDetail)); } }
    public string? Warning { get => _warning; set { if (Set(ref _warning, value)) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);

    /// <summary>A finished step reopened with "Run again".</summary>
    public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value)) { OnPropertyChanged(nameof(ShowBody)); OnPropertyChanged(nameof(ShowRerun)); } } }

    public bool IsDone => State == LabStepState.Done;
    public bool IsCurrent => State == LabStepState.Current;
    public bool IsSkipped => State == LabStepState.Skipped;
    public bool IsPending => State == LabStepState.Pending;
    public bool ShowBody => IsCurrent || Expanded;
    public bool ShowRerun => (IsDone || IsSkipped) && !Expanded;

    public bool IsRepo => Id == "repo";
    public bool IsScopes => Id == "scopes";
    public bool IsSecret => Id == "secret";
    public bool IsGeneric => !IsSecret;
    public bool IsVariables => Id == "variables";
}

/// <summary>
/// Settings → Game lab: the step-by-step wizard (GameLabSetup behind it), the status panel, and the app-wide lab behaviour
/// (background pass, automatic requests). Network work runs off the UI thread; actions take the shared busy gate.
/// </summary>
public sealed class GameLabViewModel : ObservableObject
{
    public const string IntroText =
        "The game lab finds recommended graphics settings for your games without you running anything. This app collects evidence from " +
        "this PC (settings files, folder listings, registry keys) and files an issue in your private config repo. A GitHub Actions workflow " +
        "there has Claude research the game with your own Claude subscription, validates what it wrote, merges it automatically, and the app pulls the new presets.";

    public const string CostText =
        "It costs Claude usage (each research run uses your Claude Pro/Max subscription; a daily cap keeps it in check) and GitHub Actions minutes " +
        "on your private repo (a monthly allowance is included; Windows minutes count double).";

    private static readonly TimeSpan CycleInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PendingInterval = TimeSpan.FromMinutes(2);
    private readonly System.Windows.Threading.DispatcherTimer _pendingTimer;

    private readonly MainViewModel _main;
    private readonly GameLabService _service = new();
    private readonly LabThrottle _throttle = new(CycleInterval);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private IReadOnlyList<LabIssue> _issues = [];
    private LabRepoRef? _repo;
    private string? _repoProblem;
    private bool _signedIn;
    private bool _isReady;
    private bool _autoRequest = true;
    private bool _loading;
    private bool _isChecking;
    private bool _checked;
    private int _dailyMax = GameLabContract.DefaultDailyMax;
    private GameLabStatus? _status;
    private string? _checkError;
    private DateTime _lastNetworkCheck = DateTime.MinValue;

    public GameLabViewModel(MainViewModel main, GitHubAccountViewModel account)
    {
        _main = main;
        Account = account;
        Steps =
        [
            new(1, "repo", "A config repo on GitHub", "The lab runs in your private config repo, so it has to live on GitHub.", "Go to Config repo", RunStepAsync),
            new(2, "scopes", "Sign in with the extra permissions", "“workflow” lets the app install the lab workflow, “project” lets it keep a private board.", "Sign in again", RunStepAsync),
            new(3, "labels", "Request labels", "Labels show where each request is: queued, researching, needs evidence, in review, done.", "Create labels", RunStepAsync),
            new(4, "workflow", "Install the lab workflow", "Adds the workflow and its prompts to your repo in one commit.", "Install workflow", RunStepAsync),
            new(5, "secret", "Claude token", "Lets the workflow use your Claude subscription. It is stored as an encrypted repo secret and never kept on this PC.", "Save token", RunStepAsync),
            new(6, "variables", "Daily cap and model", "Limits how many research runs happen per day and which model does them.", "Save limits", RunStepAsync),
            new(7, "settings", "Repository settings", "Lets Actions open and merge the pull request that carries the new presets.", "Update settings", RunStepAsync),
            new(8, "board", "Project board (optional)", "A private board that shows each request moving from Requested to Done. Skipped without the project permission.", "Create board", RunStepAsync),
            new(9, "test", "Test request (optional)", "Checks settings sync for a game that is already configured, to prove it all works.", "Send test request", RunStepAsync),
        ];
        Models =
        [
            new("claude-sonnet-5-5", "Sonnet (recommended)", "Fast and good at this"),
            new("claude-opus-5-5", "Opus", "Slower, uses more of your allowance"),
            new("claude-haiku-5-5", "Haiku", "Quickest and lightest"),
        ];
        Models[0].IsOn = true;

        CheckAgainCommand = new AsyncRelayCommand(() => RefreshAsync(network: true, force: true), () => !IsChecking);
        SetModelCommand = new RelayCommand(p => { if (p is OptionViewModel m) foreach (var o in Models) o.IsOn = o == m; });
        DailyMaxUpCommand = new RelayCommand(() => DailyMax++);
        DailyMaxDownCommand = new RelayCommand(() => DailyMax--);
        RunNowCommand = new AsyncRelayCommand(RunNowAsync, () => !main.IsBusy && IsReady);
        OpenBoardCommand = new AsyncRelayCommand(OpenBoardAsync, () => IsReady);
        OpenRepoCommand = new RelayCommand(() => OpenUrl($"https://github.com/{_repo?.Repo}"), () => _repo is not null);
        GoToRepoCommand = new RelayCommand(() => _main.Settings.RequestSection("repo"));
        OpenTokenCommand = new RelayCommand(() => OpenUrl("https://github.com/settings/tokens/new?scopes=repo,workflow,project&description=Optimus-Vibe%20game%20lab"));
        OpenSettingsCommand = new RelayCommand(() => _main.OpenSettings("gamelab"));
        CopyTokenHintCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText("claude setup-token"); }
            catch (Exception ex) { Log.Warn($"Clipboard: {ex.Message}"); }
        });

        account.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitHubAccountViewModel.IsSignedIn) && _main.IsSettingsPage) _ = RefreshAsync(network: true, force: true);
        };
        // Signing in again as the same user doesn't flip IsSignedIn, so the wizard listens for every completed sign-in too.
        account.SignedIn += (_, _) => { if (_main.IsSettingsPage) _ = RefreshAsync(network: true, force: true); };

        // While a request is in flight, check every two minutes so a merged result is pulled and announced soon after the
        // workflow finishes (otherwise the pass only runs at start-up and after each automatic sync).
        _pendingTimer = new System.Windows.Threading.DispatcherTimer { Interval = PendingInterval };
        _pendingTimer.Tick += (_, _) =>
        {
            if (_issues.Any(i => i.IsOpen && i.Status is LabStatus.Queued or LabStatus.Researching or LabStatus.NeedsEvidence))
                TriggerCycle(force: true);
        };
        _pendingTimer.Start();
    }

    public GitHubAccountViewModel Account { get; }
    public ObservableCollection<LabStepViewModel> Steps { get; }
    public ObservableCollection<OptionViewModel> Models { get; }

    public ICommand CheckAgainCommand { get; }
    public ICommand SetModelCommand { get; }
    public ICommand DailyMaxUpCommand { get; }
    public ICommand DailyMaxDownCommand { get; }
    public ICommand RunNowCommand { get; }
    public ICommand OpenBoardCommand { get; }
    public ICommand OpenRepoCommand { get; }
    public ICommand GoToRepoCommand { get; }
    public ICommand OpenTokenCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CopyTokenHintCommand { get; }

    public string Intro => IntroText;
    public string Cost => CostText;

    // ---------------------------------------------------------------- state

    /// <summary>Every required step is done: per-game actions and the background work are on.</summary>
    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (!Set(ref _isReady, value)) return;
            OnPropertyChanged(nameof(RoutesToLab));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>New games without presets get a lab request instead of only the local queue.</summary>
    public bool RoutesToLab => IsReady && AutoRequest;

    public bool AutoRequest
    {
        get => _autoRequest;
        set
        {
            if (!Set(ref _autoRequest, value)) return;
            OnPropertyChanged(nameof(RoutesToLab));
            if (!_loading) _ = SaveAsync(s => s.GameLabAutoRequest = value);
        }
    }

    public int DailyMax { get => _dailyMax; set => Set(ref _dailyMax, Math.Clamp(value, 1, 50)); }

    public string? RepoProblem { get => _repoProblem; private set { if (Set(ref _repoProblem, value)) OnPropertyChanged(nameof(HasRepoProblem)); } }
    public bool HasRepoProblem => RepoProblem is not null;
    public string RepoText => _repo is null ? "" : $"{_repo.Repo} ({_repo.Source})";
    public bool IsChecking { get => _isChecking; private set { if (Set(ref _isChecking, value)) CommandManager.InvalidateRequerySuggested(); } }

    public string? CheckError { get => _checkError; private set { if (Set(ref _checkError, value)) OnPropertyChanged(nameof(HasCheckError)); } }
    public bool HasCheckError => CheckError is not null;

    /// <summary>The status panel: shown once the lab is set up.</summary>
    public bool ShowStatus => IsReady;

    public string StatusText => _status?.Summary ?? (IsReady ? "Game lab is ready" : "Game lab is not set up yet");
    public int Queued => Issues.Count(i => i.IsOpen && i.Status == LabStatus.Queued);
    public int Researching => Issues.Count(i => i.IsOpen && i.Status == LabStatus.Researching);
    public int NeedsEvidence => Issues.Count(i => i.IsOpen && i.Status == LabStatus.NeedsEvidence);
    public int InReview => Issues.Count(i => i.IsOpen && i.Status is LabStatus.Review or LabStatus.Failed);
    public int OpenCount => Issues.Count(i => i.IsOpen);

    public string CountsText => OpenCount == 0
        ? "No open requests."
        : $"{OpenCount} open: {Queued} queued, {Researching} researching, {NeedsEvidence} need evidence, {InReview} in review.";

    public IReadOnlyList<LabIssue> Issues
    {
        get => _issues;
        private set
        {
            _issues = value;
            foreach (var n in new[] { nameof(Queued), nameof(Researching), nameof(NeedsEvidence), nameof(InReview), nameof(OpenCount), nameof(CountsText), nameof(StatusText) })
                OnPropertyChanged(n);
            IssuesChanged?.Invoke();
        }
    }

    public event Action? IssuesChanged;

    public string? RepoName => _repo?.Repo;

    // ---------------------------------------------------------------- loading

    /// <summary>Once per app load (config is ready): remember whether the lab was set up last time.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var s = await SettingsStore.LoadAsync();
            ApplySettings(s);
            var paths = _main.Session?.Paths;
            (_repo, _) = await Task.Run(() => _service.ResolveRepo(s, paths));
            if (_repo is null && IsReady) IsReady = false;
            OnPropertyChanged(nameof(RepoText));
        }
        catch (Exception ex)
        {
            Log.Warn($"Game lab init: {ex.Message}");
        }
    }

    private void ApplySettings(AppSettings s)
    {
        _loading = true;
        try
        {
            AutoRequest = s.GameLabAutoRequest;
            IsReady = s.GameLabReady || (GameLabService.IsDemo && GameLabService.Demo == "ready");
            DailyMax = s.GameLabDailyMax;
            foreach (var m in Models) m.IsOn = m.Value.Equals(s.GameLabModel, StringComparison.OrdinalIgnoreCase);
            if (!Models.Any(m => m.IsOn)) Models[0].IsOn = true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Settings opened (or Check again): work out the wizard. Local facts always; GitHub is only asked (read-only) when the repo is
    /// right and a sign-in exists, and not more often than every 30 s unless forced.
    /// </summary>
    public async Task RefreshAsync(bool network = true, bool force = false)
    {
        if (IsChecking) return;
        IsChecking = true;
        try
        {
            var s = await SettingsStore.LoadAsync();
            ApplySettings(s);
            var session = _main.Session;
            (_repo, RepoProblem) = await Task.Run(() => _service.ResolveRepo(s, session?.Paths));
            OnPropertyChanged(nameof(RepoText));
            _signedIn = await Task.Run(_service.HasToken);

            if (_repo is null || !_signedIn)
            {
                SetLocalStates();
                if (IsReady) { IsReady = false; await SaveAsync(x => x.GameLabReady = false); }
                _status = null;
                OnPropertyChanged(nameof(ShowStatus));
                OnPropertyChanged(nameof(StatusText));
                return;
            }
            if (!network || (!force && _checked && DateTime.UtcNow - _lastNetworkCheck < TimeSpan.FromSeconds(30))) return;

            var conn = await Task.Run(() => _service.Connect(s, session?.Paths, out _));
            if (conn is null) { SetLocalStates(); return; }
            GameLabStatus status;
            try { status = await Task.Run(() => _service.CheckAsync(conn, session?.Paths.DataDir)); }
            catch (Exception ex)
            {
                Log.Warn($"Game lab check failed: {ex.Message}");
                CheckError = $"Couldn't check your repo: {ex.Message}";
                return;
            }
            CheckError = status.Error is null ? null : $"Couldn't read the requests: {status.Error}";
            _checked = true;
            _lastNetworkCheck = DateTime.UtcNow;
            _status = status;
            Apply(status);
            Issues = await Task.Run(async () => { try { return await _service.ListIssuesAsync(conn); } catch (Exception) { return (IReadOnlyList<LabIssue>)status.Open; } });
            var ready = status.Ready;
            if (ready != s.GameLabReady && !GameLabService.IsDemo) await SaveAsync(x => x.GameLabReady = ready);
            IsReady = ready;
            OnPropertyChanged(nameof(ShowStatus));
            OnPropertyChanged(nameof(StatusText));
        }
        catch (Exception ex)
        {
            Log.Error("Game lab refresh failed", ex);
            CheckError = ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void SetLocalStates()
    {
        foreach (var st in Steps) { st.Detail = null; st.Warning = null; st.Expanded = false; st.State = LabStepState.Pending; }
        var repo = Steps[0];
        if (_repo is null)
        {
            repo.Detail = RepoProblem;
            repo.State = LabStepState.Current;
            return;
        }
        repo.State = LabStepState.Done;
        repo.Detail = $"Using {RepoText}.";
        Steps[1].Detail = "Sign in to GitHub first (GitHub account above).";
        Steps[1].State = LabStepState.Current;
    }

    private void Apply(GameLabStatus status)
    {
        Steps[0].State = LabStepState.Done;
        Steps[0].Detail = $"Using {RepoText}.";
        foreach (var st in Steps.Skip(1))
        {
            var core = status.Steps.FirstOrDefault(x => x.Name == st.Id);
            if (core is null) continue;
            st.Detail = core.Detail;
            st.Warning = core.Warning;
            st.Expanded = false;
            st.State = core.Done ? LabStepState.Done : core.Skipped ? LabStepState.Skipped : LabStepState.Pending;
        }
        // The optional test step only matters once the rest works; "no request yet" is not a failure.
        var current = Steps.FirstOrDefault(x => x.State == LabStepState.Pending);
        if (current is not null) current.State = LabStepState.Current;
    }

    // ---------------------------------------------------------------- wizard actions

    private async Task RunStepAsync(LabStepViewModel step, object? parameter)
    {
        switch (step.Id)
        {
            case "repo":
                GoToRepoCommand.Execute(null);
                return;
            case "scopes":
                // One sign-in for the whole app: scroll to the GitHub account card, where the code appears.
                _main.Settings.RequestSection("account");
                if (Account.SignInCommand.CanExecute(null)) Account.SignInCommand.Execute(null);
                else _main.Notify("Set up the GitHub sign-in in the GitHub account section first.", error: true);
                return;
        }

        string? token = null;
        if (step.Id == "secret")
        {
            if (parameter is PasswordBox box)
            {
                token = box.Password.Trim();
                box.Clear();                       // gone from the UI as soon as it is read
            }
            if (string.IsNullOrEmpty(token))
            {
                _main.Notify("Paste the token first. Run `claude setup-token` in a terminal to get one.", error: true);
                return;
            }
        }

        var s = await SettingsStore.LoadAsync();
        var session = _main.Session;
        var conn = await Task.Run(() => _service.Connect(s, session?.Paths, out _));
        if (conn is null)
        {
            _main.Notify("Sign in to GitHub and pick a config repo first.", error: true);
            return;
        }

        var model = Models.FirstOrDefault(m => m.IsOn)?.Value ?? GameLabContract.DefaultModel;
        var daily = DailyMax;
        var options = new GameLabSetupOptions
        {
            ClaudeToken = token,
            DailyMax = step.Id == "variables" ? daily : null,
            Model = step.Id == "variables" ? model : null,
            DataDir = session?.Paths.DataDir,
            RunTestRequest = step.Id == "test" && session is not null ? _service.TestRequest(conn, session, _main.Library.Detected) : null,
        };

        SetupResult? result = null;
        var ok = await _main.RunAsync(step.Title, async () => result = await Task.Run(() => _service.RunStepAsync(conn, step.Id, options)));
        token = null;
        if (!ok || result is null) return;

        if (step.Id == "variables" && result.Ok)
            await SaveAsync(x => { x.GameLabDailyMax = daily; x.GameLabModel = model; });
        if (result.NeedsLogin)
            _main.Notify(result.Message + " Use “Sign in again” in step 2.", error: true);
        else
            _main.Notify(result.Message, error: !result.Ok);
        await RefreshAsync(network: true, force: true);
        if (IsReady) TriggerCycle(force: true);
    }

    // ---------------------------------------------------------------- status panel

    private async Task RunNowAsync()
    {
        var s = await SettingsStore.LoadAsync();
        var conn = await Task.Run(() => _service.Connect(s, _main.Session?.Paths, out _));
        if (conn is null) return;
        string? message = null;
        if (!await _main.RunAsync("Starting the game lab", async () => message = await Task.Run(() => _service.RunNowAsync(conn)))) return;
        _main.Notify(message ?? "Done.");
        TriggerCycle(force: true);
    }

    private async Task OpenBoardAsync()
    {
        var s = await SettingsStore.LoadAsync();
        var conn = await Task.Run(() => _service.Connect(s, _main.Session?.Paths, out _));
        if (conn is null) return;
        var url = await Task.Run(() => _service.BoardUrlAsync(conn));
        if (url is null) _main.Notify("There is no game-lab board yet. Finish the “Project board” step (it needs the project permission).");
        else OpenUrl(url);
    }

    /// <summary>The queue's home: the project board, else the repo's open lab requests.</summary>
    public async Task OpenQueueAsync()
    {
        string? url = null;
        try
        {
            var s = await SettingsStore.LoadAsync();
            var conn = await Task.Run(() => _service.Connect(s, _main.Session?.Paths, out _));
            url = conn is null ? null : await Task.Run(() => _service.BoardUrlAsync(conn));
        }
        catch (Exception ex) { Log.Warn($"Game lab: finding the board failed: {ex.Message}"); } // offline: fall back to the issues page
        if (url is not null) OpenUrl(url);
        else if (_repo is not null) OpenUrl($"https://github.com/{_repo.Repo}/issues?q=is%3Aissue+is%3Aopen+label%3Agame-lab");
        else _main.OpenSettings("gamelab");
    }

    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            Log.Error($"Opening {url} failed", ex);
            _main.Notify($"Couldn't open the browser — go to {url}", error: true);
        }
    }

    private async Task SaveAsync(Action<AppSettings> change)
    {
        try { await SettingsStore.UpdateAsync(change); }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            _main.Notify($"Couldn't save settings: {ex.Message}", error: true);
        }
    }

    // ---------------------------------------------------------------- per-game support

    public LabGameViewModel ForGame(Func<LabGameContext> context) => new(this, _main, context);

    /// <summary>The target the evidence is collected for (install folder from the latest launcher scan).</summary>
    public LabTarget TargetFor(GameDef? game, DetectedGame? detected, string name) => LabTarget.From(detected, game, name);

    public DetectedGame? DetectedFor(GameDef game) => LibraryService.FindInstall(game, _main.Library.Detected);

    public Task<EvidencePreview> PreviewAsync(LabTarget target, string? verifyRun = null)
    {
        var session = _main.Session ?? throw new InvalidOperationException("No config loaded.");
        return Task.Run(() => GameLabService.Preview(session, target, verifyRun));
    }

    public async Task<LabRequestOutcome?> FileRequestAsync(LabKind kind, LabTarget target, string? verifyRun = null)
    {
        var s = await SettingsStore.LoadAsync();
        var session = _main.Session;
        if (session is null) return null;
        var conn = await Task.Run(() => _service.Connect(s, session.Paths, out _));
        if (conn is null)
        {
            _main.Notify("Sign in to GitHub first.", error: true);
            return null;
        }
        LabRequestOutcome? outcome = null;
        var ok = await _main.RunAsync($"Sending {target.Name} to your config repo", async () =>
            outcome = await Task.Run(() => _service.FileOrQueueAsync(conn, session, _main.Outbox.Store, kind, target, verifyRun)));
        if (!ok || outcome is null) return null;
        new LabStateStore(session.Paths.DataDir).Update(st => st.Requested[target.Key] = DateTime.UtcNow);
        if (outcome.WasQueued) _main.Outbox.Service.NotifyQueued();
        else _ = RefreshIssuesAsync(conn);
        return outcome;
    }

    private async Task RefreshIssuesAsync(GameLabService.Connection conn)
    {
        try { Issues = await Task.Run(() => _service.ListIssuesAsync(conn)); }
        catch (Exception ex) { Log.Warn($"Game lab: refreshing requests failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- automatic behaviour

    /// <summary>After a library scan with the lab ready: move this device's old requests.yaml entries into the lab, then request the rest.</summary>
    public async Task AfterScanAsync(IReadOnlyList<LibraryEntry> entries, IReadOnlyList<DetectedGame> detected)
    {
        await MigrateRequestsAsync(entries, detected);
        await AutoRequestAsync(entries.Where(e => e.Status == LibraryStatus.NeedsPresets && !e.OptedOut && e.Detected is not null).ToList());
    }

    /// <summary>
    /// Entries of config/requests.yaml queued by THIS device (or by an old version without a device) whose game is installed here are
    /// filed in the lab (or the offline outbox) as new-game requests and removed from the file; then the file is synced when auto-sync is
    /// on. Opted-out games are left alone; entries for games that have presets by now are just dropped.
    /// </summary>
    public async Task MigrateRequestsAsync(IReadOnlyList<LibraryEntry> entries, IReadOnlyList<DetectedGame> detected)
    {
        var session = _main.Session;
        if (session is null || !IsReady) return;
        try
        {
            var s = await SettingsStore.LoadAsync();
            var store = _main.Outbox.Store;
            var optedOut = entries.Where(e => e.OptedOut).Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tuned = entries.Where(e => e.Status != LibraryStatus.NeedsPresets && !e.OptedOut).Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (moved, queued, dropped) = await Task.Run(async () =>
            {
                var pairs = RequestsMigration.ToSendFromHereWithInstalls(LibraryService.LoadRequests(session.Store).Requests, session.Device.Name, detected)
                    .Where(p => !optedOut.Contains($"{p.Request.Store}:{p.Request.StoreId}")).ToList();
                if (pairs.Count == 0) return (0, 0, 0);
                var conn = _service.Connect(s, session.Paths, out _);
                var state = new LabStateStore(session.Paths.DataDir);
                int movedN = 0, queuedN = 0, droppedN = 0;
                foreach (var (req, install) in pairs)
                {
                    if (tuned.Contains($"{req.Store}:{req.StoreId}"))
                    {
                        if (RequestsMigration.RemoveFiled(session.Store, req)) droppedN++;
                        continue;
                    }
                    if (conn is null) continue; // signed out: they stay in the file for next time
                    try
                    {
                        var target = RequestsMigration.ToTarget(req, install);
                        var outcome = await _service.FileOrQueueAsync(conn, session, store, LabKind.New, target).ConfigureAwait(false);
                        state.Update(st => st.Requested[target.Key] = DateTime.UtcNow);
                        RequestsMigration.RemoveFiled(session.Store, req);
                        movedN++;
                        if (outcome.WasQueued) queuedN++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"game lab: moving the queued request for {req.Name} failed (kept in requests.yaml): {ex.Message}");
                    }
                }
                return (movedN, queuedN, droppedN);
            });
            if (queued > 0) _main.Outbox.Service.NotifyQueued();
            if (moved + dropped > 0)
            {
                Log.Info($"game lab: moved {moved} queued game(s) from requests.yaml ({queued} waiting offline), dropped {dropped}");
                _main.Library.RefreshQueueNote();
            }
            if (moved > 0)
            {
                _main.Notify(moved == 1 ? "Moved 1 queued game to the game lab." : $"Moved {moved} queued games to the game lab.");
                TriggerCycle(force: true);
            }
            if (moved + dropped > 0 && s.AutoSync)
                await _main.SyncFilesQuietlyAsync($"chore(requests): move {moved + dropped} game(s) to the game lab", session.Paths.RequestsFile);
        }
        catch (Exception ex)
        {
            Log.Error("Moving queued requests to the game lab failed", ex);
        }
    }

    /// <summary>New library games that need presets: lab requests when the lab is set up, else the local requests.yaml queue.</summary>
    public async Task AutoRequestAsync(IReadOnlyList<LibraryEntry> needs)
    {
        var session = _main.Session;
        if (session is null || needs.Count == 0) return;
        try
        {
            var s = await SettingsStore.LoadAsync();
            var store = _main.Outbox.Store;
            var queuedOffline = 0;
            var result = await Task.Run(async () =>
            {
                var conn = RoutesToLab ? _service.Connect(s, session.Paths, out _) : null;
                return await LabRouter.RouteAsync(needs, conn is not null, s.GameLabAutoRequest,
                    async (e, ct) =>
                    {
                        var outcome = await _service.FileOrQueueAsync(conn!, session, store, LabRouter.KindFor(e), LabTarget.From(e), ct: ct).ConfigureAwait(false);
                        if (outcome.Filed is { } filed) return filed;
                        Interlocked.Increment(ref queuedOffline);
                        // Waiting in the outbox: counts as handled (remembered, not queued in requests.yaml), not as "filed".
                        return new FiledRequest(0, "", true, 0, "Saved to send when back online.");
                    },
                    e => GameLabService.QueueLocally(session, e), new LabStateStore(session.Paths.DataDir));
            });
            if (queuedOffline > 0)
            {
                _main.Outbox.Service.NotifyQueued();
                _main.Notify(queuedOffline == 1
                    ? "Saved - it will be sent to the game lab when you're back online."
                    : $"Saved {queuedOffline} requests - they will be sent to the game lab when you're back online.");
            }
            if (result.Filed > 0)
                _main.Notify(result.Filed == 1 ? "Asked the game lab for presets for 1 new game." : $"Asked the game lab for presets for {result.Filed} new games.");
            if (result.QueuedLocally > 0 && s.AutoSync)
                await _main.SyncFilesQuietlyAsync($"chore(requests): queue {result.QueuedLocally} game(s) for presets", session.Paths.RequestsFile);
            if (result.Filed > 0) TriggerCycle(force: true);
        }
        catch (Exception ex)
        {
            Log.Error("Automatic game-lab requests failed", ex);
        }
    }

    /// <summary>
    /// App start and after each sync: answer follow-ups, start the queue, move the board, pull merged results. Off the UI thread,
    /// at most every five minutes, only when the lab is set up.
    /// </summary>
    public void TriggerCycle(bool force = false)
    {
        var session = _main.Session;
        if (!IsReady || session is null || !_throttle.TryStart(force)) return;
        var detected = _main.Library.Detected;
        _ = Task.Run(() => CycleAsync(session, detected));
    }

    private async Task CycleAsync(AppSession session, IReadOnlyList<DetectedGame> detected)
    {
        if (!await _cycleGate.WaitAsync(0)) return;
        try
        {
            var s = SettingsStore.Load();
            var conn = _service.Connect(s, session.Paths, out _);
            if (conn is null) return;
            var result = await _service.RunCycleAsync(conn, session, detected);
            await Application.Current.Dispatcher.InvokeAsync(() => OnCycleDone(result)).Task.Unwrap();
        }
        catch (Exception ex)
        {
            Log.Error("Game lab pass failed", ex);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task OnCycleDone(LabCycleResult r)
    {
        if (r.Issues.Count > 0 || r.Errors.Count == 0) Issues = r.Issues;
        if (r.Errors.Any(e => e.Contains("Sign in to GitHub again", StringComparison.OrdinalIgnoreCase)))
        {
            IsReady = false;
            _ = SaveAsync(x => x.GameLabReady = false);
            _main.Notify("GitHub no longer accepts the saved sign-in. Sign in again to keep the game lab running.", error: true);
            return;
        }
        if (r.Answered.Count > 0) _main.Notify("Sent the extra details Claude asked for.");
        if (r.NewPresets.Count > 0)
        {
            for (var i = 0; i < 30 && _main.IsBusy; i++) await Task.Delay(1000);
            await _main.ReloadAllAsync();
            var names = string.Join(", ", r.NewPresets.Select(p => p.Name).Distinct());
            var allNew = r.NewPresets.All(p => p.Kind == LabKind.New);
            _main.Notify(allNew ? $"New presets for {names}." : $"Updated presets for {names}.");
        }
    }
}
