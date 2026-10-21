using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.CloudSync;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Config;
using OptimusVibe.Launch;
using OptimusVibe.Library;
using OptimusVibe.Logging;

namespace OptimusVibe.App.ViewModels;

/// <summary>What the per-game panel needs to know about its game right now.</summary>
/// <param name="Target">Null when the game can't be described (nothing installed and no config).</param>
/// <param name="HasConfig">The game has a config file here.</param>
/// <param name="NeedsPresets">No presets for this device (no config, or a config without this device).</param>
/// <param name="PresetLabel">The preset that would be applied now ("Performance", "Manual: Steady 40"); null when unknown.</param>
/// <param name="CloudSync">The local cloud-sync check of the game on this device (from its snapshot); null when not known.</param>
/// <param name="Game">
/// The game's resolved context from the page's snapshot (Big Picture included, as the wrapper resolves it), so the health judges the runs of
/// the preset the page shows; null when there is no snapshot.
/// </param>
public sealed record LabGameContext(string Name, string? Key, string? GameId, LabTarget? Target, bool HasConfig, bool NeedsPresets,
    string? PresetLabel = null, CloudSyncReport? CloudSync = null, GameContext? Game = null);

/// <summary>
/// The game-lab actions on one game's page: Request / Refresh presets, Re-check settings sync, the confirmation with the evidence
/// preview (counts and tokenized paths, no contents), and the request's status chip with a link to its issue. Also Test preset: launch
/// the game once with nothing changed, then say whether it kept the preset's values (and offer to send that to the lab).
/// The panel leads with the game's health on this device (<see cref="GameHealth"/>): one status line, one "why" line and at most one
/// suggested action; every other action sits under "More" with a one-line caption.
/// </summary>
public sealed class LabGameViewModel : ObservableObject
{
    private readonly GameLabViewModel _lab;
    private readonly MainViewModel _main;
    private readonly Func<LabGameContext> _context;
    private LabKind? _confirming;
    private bool _loadingPreview;
    private EvidencePreview? _preview;
    private string? _previewError;
    private int _generation;
    private bool _testConfirming;
    private bool _testing;
    private bool _resultSent;
    private RunResult? _result;
    private string? _verifyText;
    private string? _applyStatus;
    private bool _applyStatusLoaded;
    private bool _autoLoaded;
    private string? _autoLine;
    private bool _autoHasCopy;
    private bool _forgetConfirming;
    private bool _flagLoaded;
    private FlagDef? _flag;
    private FlagDef? _userFlag;
    private bool _flagConfirming;
    private string? _flagReason;
    private string? _flagEvidence;
    private string? _flagVerifyRun;
    private string? _flagLaunchRun;
    private int _researchFinishedFor;
    private bool _healthLoaded;
    private GameHealth? _health;
    private HealthInputs? _inputs;
    private bool _moreOpen;
    private bool _postLoaded;
    private PostSessionCard? _post;
    private DateTime? _postDetailsFor;
    private DateTime? _keepConfirmFor;
    private bool _verifyNextLoaded;
    private bool _verifyNext;
    private DateTime? _verifyNextDoneAt;
    private bool _changedOpen;
    private bool _mutedLoaded;
    private bool _muted;
#if DEBUG
    private bool _demoVerifyNext;
    private bool _demoMuted;
    private PostSessionCard? _demoPost;
    private bool _demo;
    private RunResult? _demoHint;
    private string? _demoStatus;
    private string? _demoAuto;
    private bool _demoAutoCopy;
    private string? _demoSuggest;
    private bool _demoResearchDone;
    private HealthInputs? _demoInputs;
#endif

    public LabGameViewModel(GameLabViewModel lab, MainViewModel main, Func<LabGameContext> context)
    {
        _lab = lab;
        _main = main;
        _context = context;
        bool Idle() => !main.IsBusy;
        RequestCommand = new AsyncRelayCommand(() => BeginAsync(Ctx.HasConfig ? LabKind.Refresh : LabKind.New), () => Idle() && CanAct && CanRequestPresets);
        RefreshPresetsCommand = new AsyncRelayCommand(() => BeginAsync(LabKind.Refresh), () => Idle() && CanAct && CanRefreshPresets);
        SyncCheckCommand = new AsyncRelayCommand(() => BeginAsync(LabKind.SyncCheck), () => Idle() && CanAct && CanSyncCheck);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => Idle() && Preview is not null && Ctx.Target is not null);
        CancelCommand = new RelayCommand(() => ClearTransient());
        OpenIssueCommand = new RelayCommand(() => { if (Issue is { } i) _lab.OpenUrl(i.Url); }, () => Issue is not null);
        OpenSettingsCommand = lab.OpenSettingsCommand;
        TestCommand = new RelayCommand(BeginTest, () => Idle() && CanTest && !IsFlagged && !_testing);
        StartTestCommand = new AsyncRelayCommand(StartTestAsync, () => Idle() && CanTest && !IsFlagged && !_testing);
        SendResultCommand = new AsyncRelayCommand(() => BeginAsync(LabKind.Refresh, _result), () => Idle() && CanSendResult);
        SaveResultCommand = new AsyncRelayCommand(SaveResultAsync, () => Idle() && _result is not null && Ctx.GameId is not null);
        DismissResultCommand = new RelayCommand(DismissResult);
        MapCommand = new RelayCommand(BeginMap, () => Idle() && CanMap && !_testing);
        MapFirstCommand = new RelayCommand(BeginMap, () => Idle() && CanMap);
        ForgetAutoCommand = new RelayCommand(BeginForget, () => Idle() && HasAutoCopy && !_forgetConfirming);
        ConfirmForgetCommand = new RelayCommand(ConfirmForget, () => Idle() && _forgetConfirming);
        FlagCommand = new RelayCommand(BeginFlag, () => Idle() && SuggestionReason is not null && !_flagConfirming);
        ConfirmFlagCommand = new AsyncRelayCommand(ConfirmFlagAsync, () => Idle() && _flagConfirming);
        ManageAgainCommand = new AsyncRelayCommand(ManageAgainAsync, () => Idle() && CanManageAgain);
        ToggleMoreCommand = new RelayCommand(() => IsMoreOpen = !IsMoreOpen);
        PostPrimaryCommand = new AsyncRelayCommand(() => RunPostAsync(PostCard?.Primary ?? PostSessionAction.None), () => Idle() && CanRunPost(PostCard?.Primary));
        PostSecondaryCommand = new AsyncRelayCommand(() => RunPostAsync(PostCard?.Secondary ?? PostSessionAction.None), () => Idle() && CanRunPost(PostCard?.Secondary));
        PostDismissCommand = new RelayCommand(DismissPost, () => HasPostSession);
        PostMuteCommand = new RelayCommand(MutePost, () => ShowPostMute);
        UnmuteCommand = new RelayCommand(Unmute, () => IsMuted);
        VerifyNextCommand = new RelayCommand(ToggleVerifyNext, () => Idle() && CanVerifyNext);
        ToggleChangedCommand = new RelayCommand(ToggleChanged);
        lab.IssuesChanged += Refresh;
        lab.RunsChanged += Refresh;
        lab.SessionsChanged += id =>
        {
            if (!string.Equals(id, Ctx.GameId, StringComparison.OrdinalIgnoreCase)) return;
            Refresh();
            CommandManager.InvalidateRequerySuggested();
        };
        lab.SessionFocusRequested += id =>
        {
            if (string.Equals(id, Ctx.GameId, StringComparison.OrdinalIgnoreCase)) PostSessionFocusRequested?.Invoke();
        };
        lab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameLabViewModel.IsReady) or nameof(GameLabViewModel.AsksAutoFindingsConsent)) Refresh();
        };
    }

    private LabGameContext Ctx => _context();

    public ICommand RequestCommand { get; }
    public ICommand RefreshPresetsCommand { get; }
    public ICommand SyncCheckCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenIssueCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand StartTestCommand { get; }
    public ICommand SendResultCommand { get; }
    public ICommand SaveResultCommand { get; }
    public ICommand DismissResultCommand { get; }
    public ICommand MapCommand { get; }
    public ICommand MapFirstCommand { get; }
    public ICommand ForgetAutoCommand { get; }
    public ICommand ConfirmForgetCommand { get; }
    public ICommand FlagCommand { get; }
    public ICommand ConfirmFlagCommand { get; }
    public ICommand ManageAgainCommand { get; }
    public ICommand ToggleMoreCommand { get; }
    public ICommand PostPrimaryCommand { get; }
    public ICommand PostSecondaryCommand { get; }
    public ICommand PostDismissCommand { get; }
    public ICommand PostMuteCommand { get; }
    public ICommand UnmuteCommand { get; }
    public ICommand VerifyNextCommand { get; }
    public ICommand ToggleChangedCommand { get; }

    public void Refresh()
    {
        _applyStatusLoaded = false;
        _autoLoaded = false;
        _flagLoaded = false;
        _healthLoaded = false;
        _postLoaded = false;
        _mutedLoaded = false;
        _verifyNextLoaded = false;
        // a check on the next launch was on: if that launch is done, its result shows in the result card
        if (_verifyNext)
        {
            LoadVerifyNext();
            ShowVerifyNextResult();
        }
        OnPropertyChanged(string.Empty);
    }

    // ---------------------------------------------------------------- what is offered

    public bool LabReady => _lab.IsReady;
    public bool CanAct => LabReady && Ctx.Target is not null;
    public bool CanRequestPresets => Ctx.NeedsPresets || !Ctx.HasConfig;
    public bool CanRefreshPresets => Ctx.HasConfig && !Ctx.NeedsPresets;
    public bool CanSyncCheck => Ctx.HasConfig;
    public bool ShowRequest => CanRequestPresets;
    public bool ShowRefresh => CanRefreshPresets;
    public bool ShowSyncCheck => CanSyncCheck;

    public string RequestText => Ctx.HasConfig ? "Request presets for this device" : "Request presets";
    public string DisabledReason => LabReady
        ? (Ctx.Target is null ? "This game isn't installed here, so there is nothing to collect." : "")
        : "The game lab isn't set up yet. Set it up in Settings and Claude can research this game for you.";
    public bool IsDisabled => DisabledReason.Length > 0;
    public bool NeedsSetup => !LabReady;

    // ---------------------------------------------------------------- map this game

    /// <summary>The game is installed here and can be launched: two real launches can show where its settings live. Does not need the game lab.</summary>
    public bool CanMap =>
#if DEBUG
        _demo ||
#endif
        (_main.Session is not null && Ctx.Target is { InstallDir.Length: > 0 } t && GameLauncher.CanLaunch(t.Game, t.Detected));

    public bool ShowMap => CanMap;

    /// <summary>This PC already has a recent mapping run for the game (it is attached to requests).</summary>
    public bool HasMapping =>
#if DEBUG
        !_demo &&
#endif
        _main.Session is { } s && Ctx.Target is { } t && GameLabService.StoredMapping(s, t) is not null;

    /// <summary>Normal launches on this PC already showed what the game rewrites by itself (passive pass A): "Map this game" needs one launch.</summary>
    public bool HasPassiveA =>
#if DEBUG
        !_demo &&
#endif
        _main.Session is { } s && Ctx.Target is { } t && GameLabService.PassiveMapping(s, t) is not null;

    /// <summary>The request card offers to map the game first when nothing is known about where its settings live. It never blocks the request.</summary>
    public bool ShowMapFirst => _confirming is LabKind.New or LabKind.Refresh && _verifyText is null && CanMap && !HasMapping;

    /// <summary>The tooltip of "Map this game first": one launch when normal launches already showed what the game rewrites, else two.</summary>
    public string MapFirstTip => (HasPassiveA ? "One launch shows" : "Two launches show") +
        " Claude where this game really keeps its settings. You can still send the request without it.";

    private void BeginMap()
    {
        var ctx = Ctx;
        if (ctx.Target is not { } target || _main.Session is not { } session) return;
        ClearTransient();
        var kind = ctx.HasConfig ? LabKind.Refresh : LabKind.New;
        var options = new EvidenceOptions { CurrentConfig = LabCloud.CurrentConfigFrom(session.Paths) };

        async Task<string?> Send()
        {
            var outcome = await _lab.FileRequestAsync(kind, target);
            if (outcome is null) return null;
            OnPropertyChanged(string.Empty);
            if (outcome.Filed is not { } filed) return "Saved - it will be sent to the game lab when you're back online.";
            return filed.Reused ? filed.Message + " The mapping wasn't added to it." : $"Sent to the game lab. {filed.Message}";
        }

        async Task<string?> Keep()
        {
            var saved = await SaveGameSettingsAsync($"Mapped settings {DateTime.Now:MMM d HH:mm}");
            return saved is null ? null
                : $"Saved “{saved.Save.Name}”: {saved.KeysSaved} setting(s)" + (saved.Snapshots.Count > 0 ? $", {saved.Snapshots.Count} whole file(s)" : "") + " - now active.";
        }

        var flow = new MapGameFlow(target, session.Paths.DataDir, options, CanAct, Send, ctx.HasConfig && ctx.GameId is not null ? Keep : null,
            _main.MinimizeForGame, _main.RestoreAfterGame, GameLabService.PassiveMapping(session, target));
        MapGameHost.Open(new MapGameModel(ctx.Name, flow));
    }

    // ---------------------------------------------------------------- test preset

    /// <summary>The game is installed here, launchable, and has presets for this device (it does not need the game lab to be set up).</summary>
    public bool CanTest =>
#if DEBUG
        _demo ||
#endif
        (Ctx.HasConfig && !Ctx.NeedsPresets && _main.Session is not null && Ctx.Target?.Game is { } g && GameLauncher.CanLaunch(g, Ctx.Target.Detected));

    public bool ShowTest => CanTest;

    public string PresetName =>
#if DEBUG
        _demo ? "Performance" :
#endif
        Ctx.PresetLabel is { Length: > 0 } l ? l : "your active preset";

    public bool IsTestConfirming => _testConfirming;
    public string TestTitle => $"Test {PresetName} on {Ctx.Name}?";
    public string TestLead => $"Launches with {PresetName}. Change nothing, play about a minute, quit.";
    public string TestNote => "Afterwards Optimus-Vibe checks whether the game kept the preset's settings and tells you which ones it changed.";
    public string StartTestText => "Start test";

    public bool IsTesting { get => _testing; private set { if (Set(ref _testing, value)) CommandManager.InvalidateRequerySuggested(); } }

    private void BeginTest()
    {
        ClearTransient();
        _testConfirming = true;
        OnPropertyChanged(nameof(IsTestConfirming));
        OnPropertyChanged(nameof(TestTitle));
        OnPropertyChanged(nameof(TestLead));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task StartTestAsync()
    {
        var ctx = Ctx;
        if (ctx.Target?.Game is not { } game) return;
        _testConfirming = false;
        OnPropertyChanged(nameof(IsTestConfirming));
        IsTesting = true;
        try
        {
            var result = await _main.TestPresetAsync(game, ctx.Target.Detected, ctx.Name);
            if (result is null)
            {
                _main.Notify("The test didn't finish, so there is no result.", error: true);
                return;
            }
            _resultSent = false;
            _result = result;
            _applyStatusLoaded = false;
            _flagLoaded = false; // the new result may complete a streak
            _healthLoaded = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Test preset failed: {ex.Message}");
            _main.Notify($"Couldn't test the preset: {ex.Message}", error: true);
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(string.Empty);
        }
    }

    public RunResult? Result => _result;
    public bool HasResult => _result is not null;
    private RunVerdict ResultVerdict => _result is null ? RunVerdict.Unknown : RunResults.Verdict(_result);
    public bool ResultHeld => HasResult && ResultVerdict == RunVerdict.Held;
    public bool ResultOverridden => HasResult && ResultVerdict == RunVerdict.Overridden;

    public string ResultHeadline => ResultVerdict switch
    {
        RunVerdict.Held when HasResult => "Held",
        RunVerdict.Overridden => "Overridden",
        _ => "No clear answer",
    };

    public string ResultText => _result is null ? "" : RunResults.Describe(_result);

    public const int MaxResultLines = 8;

    public IReadOnlyList<string> ResultLines => _result is null || !ResultOverridden
        ? []
        : _result.Drift.Take(MaxResultLines)
            .Select(d => d.IsWholeFile ? $"{d.FileId} (whole file)" : $"{d.Key}: {d.Applied ?? "(not set)"} → {d.Now ?? "(missing)"}").ToList();

    public string ResultMore => _result is { } r && ResultOverridden && r.Drift.Count > MaxResultLines ? $"…and {r.Drift.Count - MaxResultLines} more" : "";
    public bool HasResultMore => ResultMore.Length > 0;
    public bool HasResultLines => ResultLines.Count > 0;

    public bool CanSendResult => ResultOverridden && !_resultSent && CanAct;
    public bool ShowSendResult => ResultOverridden && !_resultSent;
    public bool ShowResultSent => ResultOverridden && _resultSent;
    public bool ShowSaveResult => ResultOverridden;
    public string ResultCloseText => ResultOverridden ? "Close" : "Done";

    private void DismissResult()
    {
        _result = null;
        OnPropertyChanged(string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Keeps the settings the game left as a manual save (it becomes the active setting, like any other manual save).</summary>
    private async Task SaveResultAsync()
    {
        var gameId = Ctx.GameId;
        var saved = await SaveGameSettingsAsync($"Game's own {DateTime.Now:MMM d HH:mm}");
        if (saved is null || gameId is null) return;
        _main.Notify($"Saved “{saved.Save.Name}”: {saved.KeysSaved} setting(s)" +
                     (saved.Snapshots.Count > 0 ? $", {saved.Snapshots.Count} whole file(s)" : "") + " - now active.", saved.Sync is { Ok: false });
    }

    /// <summary>Saves whatever the game's files hold right now as a manual save (it becomes the active setting). Null when it could not be done.</summary>
    private async Task<SaveResult?> SaveGameSettingsAsync(string name)
    {
        var session = _main.Session;
        var gameId = Ctx.GameId;
        if (session is null || gameId is null) return null;
        SaveResult? saved = null;
        var ok = await _main.RunAsync("Saving the game's own settings (and syncing git)", () => Task.Run(() =>
        {
            var gctx = GameContext.Load(session.Store, gameId, session.Device);
            saved = ManualSaves.SaveCurrent(gctx, name);
        }));
        if (!ok || saved is null) return null;
        await _main.ReloadGameAsync(gameId);
        return saved;
    }

#if DEBUG
    private static HealthInputs DemoInputs() => new()
    {
        GameId = "demo", Device = "desktop", HasConfig = true, HasPresets = true, LabReady = true, CanLaunch = true, CurrentPreset = "recommended:performance",
    };

    /// <summary>Debug scenes only: show a card with sample data (confirm | held | overridden | sent | unknown | hint).</summary>
    public void ShowDemo(string kind)
    {
        _demo = true;
        RunResult Sample(int drifted, bool verify = true, bool seen = true)
        {
            string[] keys = ["Shadows", "TextureQuality", "AntiAliasing", "ViewDistance", "MotionBlur", "AmbientOcclusion", "VSync", "FrameRateLimit", "Upscaler", "Bloom"];
            var r = new RunResult
            {
                Utc = DateTime.UtcNow, Game = "demo", Device = "desktop", Preset = "recommended:performance", PresetLabel = "Performance",
                Mode = verify ? RunResult.VerifyMode : RunResult.NormalMode, AppliedKeys = 14, SnapshotFiles = 0, ProcessSeen = seen,
                UserChanged = verify ? false : null,
            };
            for (var i = 0; i < drifted; i++)
                r.Drift.Add(new RunDrift { FileId = "graphics", Key = keys[i % keys.Length] + (i >= keys.Length ? i.ToString() : ""), Applied = "Low", Now = "High" });
            return r;
        }

        HealthInputs Inputs() => new()
        {
            GameId = "demo", Device = "desktop", HasConfig = true, HasPresets = true, LabReady = true, CanLaunch = true,
            CurrentPreset = "recommended:performance",
        };
        RunResult Played(int daysAgo, int drifted = 0)
        {
            var r = Sample(drifted, verify: false);
            r.Utc = DateTime.UtcNow.AddDays(-daysAgo);
            r.DurationSec = 1800;
            r.Capture = drifted > 0 ? CaptureAnswer.DontSave : CaptureAnswer.NotAsked;
            return r;
        }

        void Post(HealthInputs inputs, RunResult run, bool askConsent = false)
        {
            _demoInputs = inputs;
            _demoPost = PostSession.For(inputs, GameHealth.Evaluate(inputs), run, Ctx.Name, askConsent: askConsent);
        }
        RunResult Session(int drifted, CaptureAnswer capture, int sec = 1800)
        {
            var r = Played(0, drifted);
            r.Capture = capture;
            r.DurationSec = sec;
            return r;
        }

        ClearTransient();
        _result = null;
        _demoInputs = null;
        _demoVerifyNext = false;
        _demoMuted = false;
        _changedOpen = false;
        _demoPost = null;
        _postDetailsFor = null;
        _moreOpen = false;
        _healthLoaded = false;
        _demoHint = null;
        _demoStatus = null;
        _demoAuto = null;
        _demoAutoCopy = false;
        _flag = null;
        _userFlag = null;
        _demoSuggest = null;
        _demoResearchDone = false;
        _flagConfirming = false;
        _resultSent = false;
        switch (kind)
        {
            case "confirm": BeginTest(); break;
            case "held": _result = Sample(0); _demoInputs = Inputs() with { LastVerify = _result }; break;
            case "overridden": _result = Sample(10); _demoInputs = Inputs() with { LastVerify = _result }; break;
            case "sent": _result = Sample(10); _resultSent = true; _demoInputs = Inputs() with { LastVerify = _result }; break;
            case "unknown": _result = Sample(0, seen: false); break;
            case "hint": _demoHint = Sample(10, verify: false); _demoInputs = Inputs() with { Recent = [_demoHint] }; break;
            case "status": _demoStatus = "Applied by file edit - verified held on 2026-10-20."; break;
            case "suggest":
                _demoSuggest = GameFlags.VerifySuggestionReason(2);
                _demoInputs = Inputs() with { OverriddenStreak = 2 };
                break;
            case "suggest-map":
                _demoSuggest = GameFlags.MappingSuggestionReason(["{LocalAppData}/Demo/Saved/system.save"]);
                _demoInputs = Inputs() with
                {
                    Mapping = new ProbeRecord { SuggestSetInGame = true, SuggestReason = _demoSuggest, OpaqueFiles = ["{LocalAppData}/Demo/Saved/system.save"] },
                };
                break;
            case "suggest-result":
                _result = Sample(10);
                _demoSuggest = GameFlags.VerifySuggestionReason(2);
                _demoInputs = Inputs() with { OverriddenStreak = 2, LastVerify = _result };
                break;
            case "flag-confirm":
                _demoSuggest = GameFlags.VerifySuggestionReason(2);
                _demoInputs = Inputs() with { OverriddenStreak = 2 };
                _flagReason = _demoSuggest;
                _flagConfirming = true;
                break;
            case "flagged":
                _userFlag = _flag = GameFlags.Create(GameFlags.VerifySuggestionReason(2), issue: 12);
                _demoStatus = GameFlags.StatusLine(_flag);
                _demoInputs = Inputs() with { Flag = _flag, HasUserFlag = true };
                break;
            case "flagged-done":
                _userFlag = _flag = GameFlags.Create(GameFlags.VerifySuggestionReason(2), issue: 12);
                _demoStatus = GameFlags.StatusLine(_flag);
                _demoResearchDone = true;
                _demoInputs = Inputs() with { Flag = _flag, HasUserFlag = true, ResearchFinished = true };
                break;
            case "flagged-config":
                _flag = new FlagDef { Reason = "the game rewrites its binary settings store on every boot", Since = "2026-10-20", Issue = 12 };
                _demoStatus = GameFlags.StatusLine(_flag);
                _demoInputs = Inputs() with { Flag = _flag, FlagFromConfig = true };
                break;
            // one scene per health state (the "suggest" scenes above cover CantBeSetFromOutside)
            case "health-working": _demoInputs = Inputs() with { Recent = [Played(5), Played(3), Played(1)] }; break;
            case "health-unchecked": _demoInputs = Inputs() with { Recent = [Played(1)] }; break;
            case "health-ambiguous":
            {
                // three short launches that can't tell: the next launch can check it ("Check presets on my next launch")
                var a = Played(3); a.DurationSec = 20;
                var b = Played(2); b.DurationSec = 20;
                var c = Played(1); c.DurationSec = 20;
                _demoInputs = Inputs() with { Recent = [a, b, c] };
                break;
            }
            case "health-verifynext":
            {
                var a = Played(3); a.DurationSec = 20;
                var b = Played(2); b.DurationSec = 20;
                var c = Played(1); c.DurationSec = 20;
                _demoVerifyNext = true;
                _demoInputs = Inputs() with { Recent = [a, b, c], VerifyNextLaunch = true };
                break;
            }
            case "health-changed": _demoInputs = Inputs() with { Recent = [Played(2, drifted: 4), Played(1, drifted: 4)] }; break;
            case "health-changed-which":
                _demoInputs = Inputs() with { Recent = [Played(2, drifted: 4), Played(1, drifted: 4)] };
                _changedOpen = true;
                break;
            case "health-evidence":
                // presets, and the lab asked for evidence nothing on this PC has sent: Map this game is the suggestion
                _demoInputs = Inputs() with
                {
                    Recent = [Played(1)],
                    LabRequest = new LabIssue(14, "Demo", "https://github.com/", true, [], LabStatus.NeedsEvidence, null, null, null),
                };
                break;
            case "health-muted": _demoMuted = true; _moreOpen = true; break;
            case "health-updated":
                _demoInputs = Inputs() with
                {
                    Presets = new PresetsMark { MadeUtc = DateTime.UtcNow.AddDays(-30), BuildId = "steam:100" }, CurrentBuild = "steam:200",
                };
                break;
            case "health-sync":
                _demoInputs = Inputs() with
                {
                    CloudSync = new CloudSyncReport { GameId = "demo", Device = "desktop", HasExternalChanges = true, Confidence = SyncConfidence.Low },
                };
                break;
            case "post-asked":
            {
                var run = Session(0, CaptureAnswer.NotAsked);
                var inputs = Inputs() with
                {
                    Recent = [run], Presets = new PresetsMark { MadeUtc = DateTime.UtcNow.AddDays(-30), BuildId = "steam:100" }, CurrentBuild = "steam:200",
                };
                // the request just sent counts as open for the page (as JustAsked makes it), the card is judged before it
                _demoInputs = inputs with { LabRequest = new LabIssue(14, "Demo", "https://github.com/", true, [], LabStatus.Queued, null, null, null) };
                _demoPost = PostSession.For(inputs, GameHealth.Evaluate(inputs), run, Ctx.Name,
                    asked: new AutoFindingFiled("demo", Ctx.Name, new AutoFinding(LabKind.Refresh, AutoFindingReason.GameUpdated), 14,
                        "https://github.com/", false, false));
                break;
            }
            case "health-nopresets": _demoInputs = Inputs() with { HasPresets = false }; break;
            case "health-labworking":
                _demoInputs = Inputs() with
                {
                    HasPresets = false,
                    LabRequest = new LabIssue(12, "Demo", "https://github.com/", true, [], LabStatus.NeedsEvidence, null, null, null),
                };
                break;
            case "health-more": _moreOpen = true; break;
            // the post-session card, one scene per kind
            case "post-held":
            {
                var run = Session(0, CaptureAnswer.NotAsked);
                Post(Inputs() with { Recent = [run] }, run);
                break;
            }
            case "post-changed":
            {
                var run = Session(10, CaptureAnswer.NotAsked); // the wrapper never asked (--no-prompt), so the card offers "Keep these"
                Post(Inputs() with { Recent = [run] }, run);
                break;
            }
            case "post-recurring":
            {
                var before = Session(10, CaptureAnswer.DontSave, sec: 120);
                before.Utc = DateTime.UtcNow.AddDays(-1);
                var run = Session(10, CaptureAnswer.DontSave, sec: 120);
                Post(Inputs() with { Recent = [before, run] }, run);
                _demoSuggest = GameFlags.PassiveSuggestionReason(2); // as Health.FlagReason gives it, so "Flag as set in-game" is enabled
                break;
            }
            case "post-updated":
            {
                var run = Session(0, CaptureAnswer.NotAsked);
                Post(Inputs() with
                {
                    Recent = [run], Presets = new PresetsMark { MadeUtc = DateTime.UtcNow.AddDays(-30), BuildId = "steam:100" }, CurrentBuild = "steam:200",
                }, run);
                break;
            }
            case "post-consent":
            {
                // the game updated after the preset held on the old build: the first finding asks once whether to send such findings by itself
                var held = Played(3);
                held.Build = "steam:100";
                var run = Session(3, CaptureAnswer.DontSave); // a held session on the new build would settle it
                run.Build = "steam:200";
                Post(Inputs() with
                {
                    Recent = [held, run], Presets = new PresetsMark { MadeUtc = DateTime.UtcNow.AddDays(-30), BuildId = "steam:100" }, CurrentBuild = "steam:200",
                }, run, askConsent: true);
                break;
            }
            case "post-map":
            {
                var run = Session(0, CaptureAnswer.NotAsked);
                Post(Inputs() with
                {
                    HasPresets = false, Recent = [run],
                    LabRequest = new LabIssue(12, "Demo", "https://github.com/", true, [], LabStatus.NeedsEvidence, null, null, null),
                }, run);
                break;
            }
            case "auto": _demoAuto = "Settings file kept per device: Performance copy from Oct 20, 2026"; _demoAutoCopy = true; break;
            case "auto-first": _demoAuto = "First launch with this preset: set it up in-game; Optimus-Vibe keeps it for next time."; break;
            case "auto-forget": _demoAuto = "Settings file kept per device: Performance copy from Oct 20, 2026"; _demoAutoCopy = true; _forgetConfirming = true; break;
            case "map-first":
                _confirming = LabKind.New;
                Preview = new EvidencePreview(3, 2, 1,
                    ["Install: {SteamLibrary:1234}", "File: {LocalAppData}\\Demo\\Saved\\Config\\GameUserSettings.ini", "File: {Documents}\\Demo\\settings.json",
                     "Directory: {LocalAppData}\\Demo", "Registry: HKCU\\Software\\Demo\\Graphics"], []);
                break;
        }
        OnPropertyChanged(string.Empty);
    }
#endif

    // ---------------------------------------------------------------- health: status line, why line, one suggested action

    /// <summary>
    /// The game's health on this device (<see cref="GameHealth.Evaluate"/>), read once per refresh from what is already stored: the run
    /// history, the flag, the stored mapping run, the lab request, the cloud-sync check and the build the presets were made against.
    /// </summary>
    public GameHealth Health
    {
        get
        {
#if DEBUG
            if (_demo)
                return GameHealth.Evaluate(_inputs = _demoInputs ?? new HealthInputs
                {
                    GameId = "demo", Device = "desktop", HasConfig = true, HasPresets = true, LabReady = true, CanLaunch = true,
                });
#endif
            if (_healthLoaded && _health is not null) return _health;
            _healthLoaded = true;
            _inputs = LoadHealthInputs();
            _health = GameHealth.Evaluate(_inputs);
            return _health;
        }
    }

    private HealthInputs LoadHealthInputs()
    {
        LoadFlag();
        var ctx = Ctx;
        var session = _main.Session;
        var inputs = new HealthInputs { GameId = ctx.GameId ?? ctx.Key ?? ctx.Name, Device = session?.Device.Name ?? "" };
        if (session is not null && ctx.GameId is { } id)
            inputs = Read("run history", () => HealthInputs.FromRuns(session.Paths.DataDir, id, session.Device.Name), inputs);

        // what is already known never depends on a file read below: one failed read only drops its own input
        inputs = inputs with
        {
            HasConfig = ctx.HasConfig, HasPresets = ctx.HasConfig && !ctx.NeedsPresets,
            Flag = _flag, FlagFromConfig = _flag is not null && _userFlag is null, HasUserFlag = _userFlag is not null,
            ResearchFinished = _flag?.Issue is { } n && n == _researchFinishedFor,
            LabRequest = Issue ?? JustAsked(), LabReady = LabReady, CloudSync = ctx.CloudSync, CanLaunch = CanMap, VerifyNextLaunch = VerifyNextArmed,
        };
        if (session is null) return inputs;

        if (ctx.GameId is { } gameId && ctx.HasConfig && !IsFlagged)
        {
            // the preset the page shows: the snapshot's context (Big Picture resolved as the wrapper does), else the same load
            var game = ctx.Game ?? Read<GameContext?>("game context",
                () => GameContext.Load(session.Store, gameId, session.Device, bigPicture: new BigPictureOptions()), null);
            if (game is not null)
                inputs = inputs with
                {
                    CurrentPreset = game.Profile.IsEmpty ? null : game.Preset?.ToString(),
                    ConfiguredFiles = game.Game.Files.Select(f => f.Path).Where(p => p.Length > 0).ToList(),
                };
        }
        if (ctx.Target is { } t)
            inputs = inputs with
            {
                Mapping = Read("mapping run", () => GameLabService.StoredMapping(session, t), null),
                HasPassiveA = Read("passive mapping", () => GameLabService.PassiveMapping(session, t) is not null, false),
                CurrentBuild = t.Detected is { } detected ? Read("build", () => GameBuild.Of(detected), null) : null,
            };
        if (ctx.GameId is { } markId)
            inputs = inputs with { Presets = Read("presets mark", () => new LabStateStore(session.Paths.DataDir).Load().PresetsFor(markId), null) };
        return inputs;

        static T Read<T>(string what, Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the game's health ({what}): {ex.Message}");
                return fallback;
            }
        }
    }

    /// <summary>
    /// The request the app just sent by itself after this game's session, until the lab's request list shows it: the health counts it as
    /// open, so nothing offers to ask the lab again meanwhile. Null when none (or it only waits in the outbox).
    /// </summary>
    private LabIssue? JustAsked() =>
        _lab.AskedFor(Ctx.GameId) is { Queued: false, Issue: { } n, Url: { } url } a
            ? new LabIssue(n, a.Name, url, true, [], LabStatus.Queued, null, null, null)
            : null;

    /// <summary>The status line: "Working. Presets held on your last 3 launches.", "Not checked on this device yet"...</summary>
    public string HealthText => Health.Headline;

    /// <summary>The one dim line under it saying why; empty when there is nothing to add.</summary>
    public string HealthWhy => Health.Why ?? "";

    public bool HasHealthWhy => HealthWhy.Length > 0;

    /// <summary>Which lab actions are on offer for this game right now (<see cref="LabPanelPlan"/> places each once).</summary>
    public PanelOffers Offers => new()
    {
        Request = ShowRequest, Refresh = ShowRefresh, SyncCheck = ShowSyncCheck, Test = ShowTest && !IsFlagged, Map = ShowMap,
        ManageAgain = ShowManageAgain, Flag = SuggestionReason is not null, VerifyNext = CanVerifyNext,
    };

    /// <summary>The state's suggested action, when its command is on offer here; otherwise none.</summary>
    public HealthAction Suggested
    {
        get
        {
#if DEBUG
            if (_demo) return Health.Suggested;
#endif
            return LabPanelPlan.Suggested(Health.Suggested, Offers);
        }
    }

    /// <summary>The existing command behind the suggested action (its confirm card opens as from "More").</summary>
    public ICommand? SuggestedCommand => Suggested switch
    {
        HealthAction.RequestPresets => RequestCommand,
        HealthAction.RefreshPresets => RefreshPresetsCommand,
        HealthAction.TestPreset => TestCommand,
        HealthAction.MapGame => MapCommand,
        HealthAction.FlagSetInGame => FlagCommand,
        HealthAction.ManageAgain => ManageAgainCommand,
        HealthAction.SyncCheck => SyncCheckCommand,
        HealthAction.VerifyNextLaunch => VerifyNextCommand,
        _ => null,
    };

    public string SuggestedText => Suggested == HealthAction.RequestPresets ? RequestText : GameHealth.ActionText(Suggested);

    /// <summary>
    /// The suggested button; "Flag as set in-game" steps aside while its confirm card or a test result (which has its own) is open, and any
    /// suggestion steps aside while the post-session card offers the same action (<see cref="LabPanelPlan.ShowSuggested"/>).
    /// </summary>
    public bool ShowSuggested => LabPanelPlan.ShowSuggested(Suggested, PostCard, _flagConfirming, HasResult);

    // ---------------------------------------------------------------- "Also check presets on my next launch" (a manual opt-in per game)

    /// <summary>The game has presets here, is installed and not flagged: its next launch can check the preset instead of an extra test launch.</summary>
    public bool CanVerifyNext =>
#if DEBUG
        _demo ? !IsFlagged && Health.State is not HealthState.NoPresets :
#endif
        Ctx.GameId is not null && Ctx.HasConfig && !Ctx.NeedsPresets && !IsFlagged && CanTest;

    /// <summary>The next launch of this game checks the preset (stored per device in gamelab-state.json; the wrapper clears it after that launch).</summary>
    public bool VerifyNextArmed
    {
        get
        {
#if DEBUG
            if (_demo) return _demoVerifyNext;
#endif
            LoadVerifyNext();
            return _verifyNext;
        }
    }

    private void LoadVerifyNext()
    {
        if (_verifyNextLoaded) return;
        _verifyNextLoaded = true;
        var was = _verifyNext;
        _verifyNext = false;
        try
        {
            if (_main.Session is { } s && Ctx.GameId is { } id) _verifyNext = new LabStateStore(s.Paths.DataDir).VerifiesNext(id);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read whether the next launch checks the preset: {ex.Message}");
        }
        if (was && !_verifyNext) _verifyNextDoneAt ??= DateTime.UtcNow; // the wrapper finished that launch: its result shows in the result card
    }

    public string VerifyNextText => VerifyNextArmed ? "Don't check on my next launch" : GameHealth.ActionText(HealthAction.VerifyNextLaunch);

    public string VerifyNextCaption => VerifyNextArmed
        ? "Your next launch checks the preset. Turn it off to play normally (you're asked about changed settings again)."
        : "Your own next launch checks the preset, no extra launch: change nothing in-game that time. It doesn't ask to keep changes afterwards.";

    /// <summary>The dim line while it is on, unless the health's why line says it already.</summary>
    public bool ShowVerifyNextLine => VerifyNextArmed && CanVerifyNext && HealthWhy != GameHealth.VerifyNextLine;

    public string VerifyNextLine => GameHealth.VerifyNextLine;

    private void ToggleVerifyNext()
    {
        var on = !VerifyNextArmed;
#if DEBUG
        if (_demo)
        {
            _demoVerifyNext = on;
            _demoInputs = (_demoInputs ?? DemoInputs()) with { VerifyNextLaunch = on };
            OnPropertyChanged(string.Empty);
            return;
        }
#endif
        if (_main.Session is not { } s || Ctx.GameId is not { } id) return;
        if (!new LabStateStore(s.Paths.DataDir).SetVerifyNext(id, on))
        {
            _main.Notify("Couldn't save that. Try again in a moment.", error: true);
            return;
        }
        _verifyNextLoaded = false;
        _verifyNext = on;
        _verifyNextDoneAt = null;
        _healthLoaded = false;
        _main.Notify(on
            ? $"Your next launch of {Ctx.Name} checks the preset. Change nothing in-game that time."
            : $"Your next launch of {Ctx.Name} is a normal one again.");
        OnPropertyChanged(string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// The check on the next launch finished while this page was alive: its result shows in the test result card, as after Test preset.
    /// </summary>
    private void ShowVerifyNextResult()
    {
        if (_verifyNextDoneAt is not { } since || _main.Session is not { } s || Ctx.GameId is not { } id || _result is not null || _testing) return;
        try
        {
            if (RunResults.Get(s.Paths.DataDir, id, s.Device.Name, verifyOnly: true) is { } verify && verify.Utc >= since.AddHours(-12))
            {
                _verifyNextDoneAt = null;
                _resultSent = false;
                _result = verify;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the check of the last launch: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- "See which": every setting behind "The game changed some settings"

    /// <summary>The run behind <see cref="HealthState.GameChangedSettings"/>, unless the post-session card lists the same run.</summary>
    private RunResult? ChangedRun => Health.ChangedRun is { } r && !(PostCard is { Kind: PostSessionKind.Changed } c && c.Run.Utc == r.Utc) ? r : null;

    public bool ShowChangedWhich => ChangedRun is not null;
    public string ChangedWhichText => _changedOpen ? "Hide the list" : "See which";
    public bool ShowChangedLines => ChangedRun is not null && _changedOpen;

    public IReadOnlyList<string> ChangedLines => ChangedRun is { } r && _changedOpen ? DriftLines(r) : [];
    public string ChangedMore => ChangedRun is { } r && _changedOpen && r.Drift.Count > MaxResultLines ? $"…and {r.Drift.Count - MaxResultLines} more" : "";
    public bool HasChangedMore => ChangedMore.Length > 0;

    private static IReadOnlyList<string> DriftLines(RunResult r) =>
        r.Drift.Take(MaxResultLines).Select(d => d.IsWholeFile ? $"{d.FileId} (whole file)" : $"{d.Key}: {d.Applied ?? "(not set)"} → {d.Now ?? "(missing)"}").ToList();

    private void ToggleChanged()
    {
        _changedOpen = !_changedOpen;
        OnPropertyChanged(nameof(ChangedWhichText));
        OnPropertyChanged(nameof(ShowChangedLines));
        OnPropertyChanged(nameof(ChangedLines));
        OnPropertyChanged(nameof(ChangedMore));
        OnPropertyChanged(nameof(HasChangedMore));
    }

    // ---------------------------------------------------------------- post-session card: inline, once, B = Not now

    /// <summary>
    /// The card about the game's last session (<see cref="PostSession.For"/>), while it is pending (<see cref="GameLabViewModel.SessionFor"/>):
    /// what the game kept or changed, or what would help next. Null when there is nothing to say or it was dismissed.
    /// </summary>
    public PostSessionCard? PostCard
    {
        get
        {
#if DEBUG
            if (_demo) return _demoPost;
#endif
            if (_postLoaded) return _post;
            _postLoaded = true;
            _post = null;
            if (Ctx.GameId is not { } id || _lab.SessionFor(id) is not { } run) return null;
            try
            {
                var health = Health;
                if (_inputs is { } inputs)
                    _post = PostSession.For(inputs, health, run, Ctx.Name, _lab.SnoozedFor(id), _lab.AsksAutoFindingsConsent, _lab.AskedFor(id));
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not work out the card for the last session: {ex.Message}");
            }
            return _post;
        }
    }

    public bool HasPostSession => PostCard is not null;
    public string PostSessionText => PostCard?.Text ?? "";
    public string PostSessionWhy => IsKeepConfirming ? PostSession.KeepConfirmText : PostCard?.Why ?? "";

    /// <summary>"Keep these" was pressed once: the card asks before it saves (A = Save and use, B = Cancel).</summary>
    public bool IsKeepConfirming => PostCard is { } c && _keepConfirmFor == c.Run.Utc;

    /// <summary>
    /// Keyboard or gamepad focus is inside the card (set by the panel). Only then does B close it: a card below the fold or on a page the player
    /// only passes through never swallows B (or snoozes itself unseen).
    /// </summary>
    public bool PostCardHasFocus { get; set; }
    public bool HasPostSessionWhy => PostSessionWhy.Length > 0;

    /// <summary>"See which" was pressed: the card lists the settings the game changed.</summary>
    public bool ShowPostLines => PostCard is { } c && _postDetailsFor == c.Run.Utc && c.Run.Drift.Count > 0;

    public IReadOnlyList<string> PostLines => PostCard is { } c && ShowPostLines
        ? c.Run.Drift.Take(MaxResultLines).Select(d => d.IsWholeFile ? $"{d.FileId} (whole file)" : $"{d.Key}: {d.Applied ?? "(not set)"} → {d.Now ?? "(missing)"}").ToList()
        : [];

    public string PostLinesMore => PostCard is { } c && ShowPostLines && c.Run.Drift.Count > MaxResultLines ? $"…and {c.Run.Drift.Count - MaxResultLines} more" : "";
    public bool HasPostLinesMore => PostLinesMore.Length > 0;

    // "Open issue" steps aside while the panel's own "Open issue" (beside the request chip) is on screen: each action once
    private bool ShowPostAction(PostSessionAction? action) =>
        action is { } a && a != PostSessionAction.None && !(a == PostSessionAction.SeeWhich && ShowPostLines) &&
        !(a == PostSessionAction.OpenIssue && HasChip);

    public bool ShowPostPrimary => ShowPostAction(PostCard?.Primary);
    public string PostPrimaryText => IsKeepConfirming ? PostSession.KeepConfirmAction : PostSession.ActionText(PostCard?.Primary ?? PostSessionAction.None);
    public bool ShowPostSecondary => ShowPostAction(PostCard?.Secondary);
    public string PostSecondaryText => PostSession.ActionText(PostCard?.Secondary ?? PostSessionAction.None);

    /// <summary>B: "Not now" keeps this kind of card away for a few days; the one-off "kept all" card just closes.</summary>
    public string PostDismissText => IsKeepConfirming ? "Cancel" : PostCard is { CanSnooze: false } ? "OK" : "Not now";

    /// <summary>"Don't ask again": no more post-session cards for this game on this device (More turns them back on).</summary>
    public bool ShowPostMute => PostCard is { CanSnooze: true } && !IsKeepConfirming;

    /// <summary>The player chose "Don't ask again" on a post-session card of this game (<see cref="LabState.Muted"/>).</summary>
    public bool IsMuted
    {
        get
        {
#if DEBUG
            if (_demo) return _demoMuted;
#endif
            if (_mutedLoaded) return _muted;
            _mutedLoaded = true;
            _muted = false;
            try
            {
                if (_main.Session is { } s && Ctx.GameId is { } id) _muted = new LabStateStore(s.Paths.DataDir).Load().IsMuted(id);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read whether this game's cards are muted: {ex.Message}");
            }
            return _muted;
        }
    }

    public bool ShowUnmuteInMore => IsMuted;
    public string UnmuteCaption => "You chose “Don't ask again” on a card after a session. This shows those cards for this game again.";

    private void MutePost()
    {
        var card = PostCard;
        if (card is not { CanSnooze: true }) return;
#if DEBUG
        if (_demo)
        {
            _demoMuted = true;
            ClosePost(snooze: false);
            OnPropertyChanged(string.Empty);
            return;
        }
#endif
        if (Ctx.GameId is not { } id) return;
        if (PostSession.KeepsSession(card.Kind)) _lab.AnswerConsent(id, snooze: false); // it asked once either way
        _lab.MuteSessions(id, true);
        _mutedLoaded = false;
        _main.Notify($"No more cards after sessions of {Ctx.Name}. More on its page turns them back on.");
        OnPropertyChanged(string.Empty);
    }

    private void Unmute()
    {
#if DEBUG
        if (_demo)
        {
            _demoMuted = false;
            OnPropertyChanged(string.Empty);
            return;
        }
#endif
        if (Ctx.GameId is not { } id) return;
        _lab.MuteSessions(id, false);
        _mutedLoaded = false;
        OnPropertyChanged(string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// The card should take focus now: it asks for something, it arrived after a session the player just came back from, and no panel has taken
    /// it yet. The panel takes it only while it is on screen in the active window (the game's page is open).
    /// </summary>
    public bool PostSessionWantsFocus =>
        Ctx.GameId is { } id && _lab.WantsSessionFocus(id) && PostCard is { TakesFocus: true };

    /// <summary>The moment to try: the session just arrived, or the window just came to the front (never on an ordinary refresh).</summary>
    public event Action? PostSessionFocusRequested;

    public void PostSessionFocusTaken()
    {
        if (Ctx.GameId is { } id) _lab.SessionFocusTaken(id);
    }

    private bool CanRunPost(PostSessionAction? action) => action switch
    {
        PostSessionAction.KeepThese => _main.Session is not null && Ctx.GameId is not null,
        PostSessionAction.SeeWhich => true,
        PostSessionAction.FlagSetInGame => FlagCommand.CanExecute(null),
        PostSessionAction.AskLab => RefreshPresetsCommand.CanExecute(null),
        PostSessionAction.MapGame => MapCommand.CanExecute(null),
        PostSessionAction.TurnOnAutoFindings => CanAct && Ctx.GameId is not null,
        PostSessionAction.OpenIssue =>
#if DEBUG
            _demo ||
#endif
            (Ctx.GameId is { } id && _lab.AskedFor(id)?.Url is not null),
        _ => false,
    };

    /// <summary>
    /// A card button: "See which" opens the list in place; "Keep these" first asks on the card (<see cref="PostSession.Confirms"/>) and saves on
    /// the second press; every other one closes the card and runs the existing action (its confirm card opens).
    /// </summary>
    private async Task RunPostAsync(PostSessionAction action)
    {
        if (PostCard is not { } card) return;
        if (action == PostSessionAction.SeeWhich)
        {
            _postDetailsFor = card.Run.Utc;
            RaisePost();
            return;
        }
        if (PostSession.Confirms(action) && !IsKeepConfirming)
        {
            ClearTransient(); // one card asks at a time
            _keepConfirmFor = card.Run.Utc;
            RaisePost();
            return;
        }
        var asked = Ctx.GameId is { } askedId ? _lab.AskedFor(askedId) : null;
        ClosePost(snooze: false);
        switch (action)
        {
            case PostSessionAction.OpenIssue:
                if (asked?.Url is { } url) _lab.OpenUrl(url);
                break;
            case PostSessionAction.KeepThese: await SaveResultAsync(); break;
            case PostSessionAction.FlagSetInGame: Run(FlagCommand); break;
            case PostSessionAction.AskLab: Run(RefreshPresetsCommand); break;
            case PostSessionAction.MapGame: Run(MapCommand); break;
            case PostSessionAction.TurnOnAutoFindings:
#if DEBUG
                if (_demo) break; // a debug scene never turns on a real setting or files a real request
#endif
                if (Ctx.GameId is { } gameId) await _lab.TurnOnAutoFindingsAsync(gameId, Ctx.Name);
                break;
        }

        static void Run(ICommand command)
        {
            if (command.CanExecute(null)) command.Execute(null);
        }
    }

    /// <summary>B / "Not now": while "Keep these" asks, only that question closes (the card stays); otherwise the card closes and snoozes.</summary>
    private void DismissPost()
    {
        if (CancelKeepConfirm()) return;
        ClosePost(snooze: true);
    }

    private bool CancelKeepConfirm()
    {
        if (_keepConfirmFor is null) return false;
        var was = IsKeepConfirming;
        _keepConfirmFor = null;
        RaisePost();
        return was;
    }

    private void ClosePost(bool snooze)
    {
        var card = PostCard;
        _postDetailsFor = null;
        _keepConfirmFor = null;
#if DEBUG
        if (_demo)
        {
            _demoPost = null;
            RaisePost();
            return;
        }
#endif
        if (card is null || Ctx.GameId is not { } id) return;
        // the consent card is answered by any button (it asks once) and leaves the session pending: the session's own card shows next
        if (PostSession.KeepsSession(card.Kind)) _lab.AnswerConsent(id, snooze && card.CanSnooze);
        else _lab.DismissSession(id, snooze && card.CanSnooze ? card.Kind : null); // its panels refresh through SessionsChanged
    }

    private void RaisePost()
    {
        foreach (var n in new[]
                 {
                     nameof(PostCard), nameof(HasPostSession), nameof(PostSessionText), nameof(PostSessionWhy), nameof(HasPostSessionWhy), nameof(ShowPostLines),
                     nameof(PostLines), nameof(PostLinesMore), nameof(HasPostLinesMore), nameof(ShowPostPrimary), nameof(PostPrimaryText), nameof(ShowPostSecondary),
                     nameof(PostSecondaryText), nameof(PostDismissText), nameof(IsKeepConfirming), nameof(ShowSuggested), nameof(ShowPostMute),
                     nameof(ShowRequestInMore), nameof(ShowRefreshInMore), nameof(ShowTestInMore), nameof(ShowMapInMore), nameof(ShowManageAgainInMore),
                     nameof(ShowFlagInMore), nameof(ShowSyncCheckInMore), nameof(ShowVerifyNextInMore), nameof(HasMore), nameof(ShowChangedWhich),
                     nameof(ShowChangedLines), nameof(ChangedLines),
                 })
            OnPropertyChanged(n);
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------------------------------------------------------------- More: every other action, each once, with a caption

    public bool IsMoreOpen
    {
        get => _moreOpen;
        set
        {
            if (!Set(ref _moreOpen, value)) return;
            OnPropertyChanged(nameof(MoreText));
        }
    }

    public string MoreText => _moreOpen ? "Less" : "More";

    // each action once: not under More while the suggested button or the post-session card offers it (it comes back when the card closes)
    private bool InMore(HealthAction action) =>
        LabPanelPlan.InMore(action, Offers, Suggested, PostCard, _flagConfirming, HasResult);

    public bool ShowRequestInMore => InMore(HealthAction.RequestPresets);
    public bool ShowRefreshInMore => InMore(HealthAction.RefreshPresets);
    public bool ShowSyncCheckInMore => InMore(HealthAction.SyncCheck);
    public bool ShowTestInMore => InMore(HealthAction.TestPreset);
    public bool ShowVerifyNextInMore => InMore(HealthAction.VerifyNextLaunch);
    public bool ShowMapInMore => InMore(HealthAction.MapGame);
    public bool ShowManageAgainInMore => InMore(HealthAction.ManageAgain);

    /// <summary>"Flag as set in-game" while it is suggested by the evidence but not the state's own suggestion (the state shows a stronger one).</summary>
    public bool ShowFlagInMore => InMore(HealthAction.FlagSetInGame);

    public bool HasMore => ShowRequestInMore || ShowRefreshInMore || ShowSyncCheckInMore || ShowTestInMore || ShowVerifyNextInMore || ShowMapInMore ||
                           ShowManageAgainInMore || ShowFlagInMore || ShowUnmuteInMore;

    public string RequestCaption => "Claude researches the game's settings and writes presets for this device. Takes a few minutes.";
    public string RefreshCaption => "Asks the game lab to research this game again. Use after a big update or if the presets look wrong.";
    public string SyncCheckCaption => "Asks the game lab whether this game's settings follow you between devices. Your presets don't change.";

    /// <summary>Test preset's caption, or why it is off for a flagged game.</summary>
    public string TestCaption => TestDisabledText ??
        "Plays the game once with the preset and tells you which settings it changed. Use after a patch, or when settings keep resetting.";

    public string MapCaption => HasPassiveA
        ? "One launch where you change a few graphics options; your normal launches already showed what the game rewrites by itself."
        : "Quick launches that show where this game stores its settings. Use when the lab says it needs evidence.";
    public string ManageAgainCaption => GameHealth.ManageAgainCaption;
    public string FlagCaption => "Optimus-Vibe stops writing this game's settings. It still sets the display, graphics-driver settings and launch options.";

    /// <summary>
    /// One honest line about how the active preset reaches the game and whether it was verified on this device ("Applied by file edit -
    /// verified held on 2026-10-20", "In-game steps only: ..."). Null when the game has no config or preset yet.
    /// </summary>
    public string? ApplyStatusText
    {
        get
        {
#if DEBUG
            if (_demo) return _demoStatus;
#endif
            if (_applyStatusLoaded) return _applyStatus;
            _applyStatusLoaded = true;
            _applyStatus = null;
            try
            {
                if (_main.Session is { } s && Ctx.GameId is { } id && Ctx.HasConfig)
                {
                    var game = GameContext.Load(s.Store, id, s.Device);
                    if (game.InGame is { } inGame) _applyStatus = LaunchPolicy.Describe(game.Game, game.Profile, null, inGame);
                    else if (!game.Profile.IsEmpty)
                    {
                        var verify = RunResults.Get(s.Paths.DataDir, id, s.Device.Name, verifyOnly: true);
                        if (verify is not null && verify.Preset != game.Preset?.ToString()) verify = null; // another preset was tested
                        _applyStatus = LaunchPolicy.Describe(game.Game, game.Profile, verify);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not describe how the preset is applied: {ex.Message}");
            }
            return _applyStatus;
        }
    }

    /// <summary>For a flagged game the status line already says it (<see cref="GameFlags.StatusLine"/>); the two are never shown together.</summary>
    public bool HasApplyStatus => ApplyStatusText is not null && !IsFlagged;

    // ---------------------------------------------------------------- set in-game (settings off, rest on)

    private void LoadFlag()
    {
#if DEBUG
        if (_demo) return; // the demo scenes set the flag state themselves
#endif
        if (_flagLoaded) return;
        _flagLoaded = true;
        _flag = null;
        _userFlag = null;
        try
        {
            if (_main.Session is { } s && Ctx.GameId is { } id && Ctx.HasConfig)
            {
                var game = s.Store.LoadGame(id);
                var lib = LibraryService.LoadLibrary(s.Store);
                _userFlag = GameFlags.FromLibrary(lib, id);
                _flag = GameFlags.Effective(game, lib);
                if (_flag is not null) RunResults.MarkFlagged(s.Paths.DataDir, id, s.Device.Name); // seen flagged here: its old evidence stops counting
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the set-in-game flag: {ex.Message}");
        }
        if (_flag?.Issue is { } issue) _ = CheckResearchAsync(issue);
    }

    /// <summary>The game's settings are left to the game ("set in-game"): its game file or the user's library.yaml flags it.</summary>
    public bool IsFlagged
    {
        get
        {
            LoadFlag();
            return _flag is not null;
        }
    }

    /// <summary>The flag in the user's own library.yaml can be lifted here; one that only comes from the game's config is the game lab's to lift.</summary>
    public bool CanManageAgain
    {
        get
        {
            LoadFlag();
            return _userFlag is not null;
        }
    }

    public bool ShowManageAgain => CanManageAgain;
    public bool ShowConfigOnlyNote => IsFlagged && !CanManageAgain;

    public string ConfigOnlyNote => GameHealth.ConfigFlagLine;

    /// <summary>Test preset stays visible but is off for a flagged game, and says why.</summary>
    public string? TestDisabledText => IsFlagged ? "Test preset is off: this game's settings are left to the game, so there is nothing to test." : null;

    public bool HasTestDisabledText => TestDisabledText is not null;

    /// <summary>
    /// Why the flag is worth suggesting (never applied on its own): two overridden Test preset results in a row, two passively overridden
    /// launches, or a mapping run whose changes were all in binary files Optimus-Vibe can't edit (<see cref="GameHealth.FlagSuggestion"/>).
    /// Null when the game is flagged already or nothing suggests it.
    /// </summary>
    public string? SuggestionReason
    {
        get
        {
            LoadFlag();
            if (_flag is not null || !Ctx.HasConfig) return null;
#if DEBUG
            if (_demo) return _demoSuggest;
#endif
            return Health.FlagReason;
        }
    }

    public string SuggestLine => SuggestionReason is { } r ? $"This game seems to ignore Optimus-Vibe's settings: {r}." : "";

    /// <summary>The quiet suggestion on the game page; while a Test preset result is open the card shows it instead.</summary>
    public bool ShowSuggest => SuggestionReason is not null && !_flagConfirming && !HasResult;

    public bool ShowResultSuggest => ResultOverridden && SuggestionReason is not null && !_flagConfirming;

    public bool IsFlagConfirming => _flagConfirming;
    public string FlagTitle => $"Flag {Ctx.Name} as set in-game?";

    public string FlagLead =>
        "Optimus-Vibe will stop writing this game's settings (display and graphics-driver settings still apply). A research task is filed so the game lab can look deeper later.";

    public string FlagWhy => _flagReason is { } r ? $"Why: {r}." : "";
    public string FlagConfirmText => "Flag it";

    /// <summary>"Research finished" when the flag's research task is closed (read once per page and ten minutes, no polling).</summary>
    public bool ShowResearchFinished =>
#if DEBUG
        _demo ? _demoResearchDone :
#endif
        IsFlagged && _flag?.Issue is { } n && n == _researchFinishedFor;

    public string ResearchFinishedText => GameHealth.ResearchFinishedLine;

    private async Task CheckResearchAsync(int issue)
    {
        await Task.Yield(); // called while the panel reads the flag: never notify from inside that read
        if (await _lab.IsIssueOpenAsync(issue) != false || _researchFinishedFor == issue) return;
        _researchFinishedFor = issue;
        _healthLoaded = false; // "Manage settings again" becomes the suggestion
        OnPropertyChanged(string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    private void BeginFlag()
    {
        var reason = SuggestionReason;
        if (reason is null) return;
        ClearTransient();
        _flagReason = reason;
        var finding = CurrentFlagFinding();
        _flagEvidence = finding?.Summary ?? reason;
        _flagVerifyRun = finding?.VerifyRunText;
        _flagLaunchRun = finding?.LaunchRunText;
        _flagConfirming = true;
        OnPropertyChanged(nameof(IsFlagConfirming));
        OnPropertyChanged(nameof(FlagTitle));
        OnPropertyChanged(nameof(FlagWhy));
        OnPropertyChanged(nameof(ShowSuggest));
        OnPropertyChanged(nameof(ShowResultSuggest));
        OnPropertyChanged(nameof(ShowSuggested));
        OnPropertyChanged(nameof(ShowFlagInMore));
        OnPropertyChanged(nameof(HasMore));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// The evidence behind the suggestion the user is confirming (<see cref="GameHealth.FlagEvidence"/>): which rule fired and its run (the last
    /// overridden test, or the newest launch the game reset), from the same inputs the health was judged on. Null when unknown.
    /// </summary>
    private FlagFinding? CurrentFlagFinding()
    {
        try
        {
            _ = Health; // the inputs the suggestion came from
            return _inputs is { } inputs ? GameHealth.FlagEvidence(inputs) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not describe why the flag is suggested: {ex.Message}");
            return null;
        }
    }

    private async Task ConfirmFlagAsync()
    {
        var ctx = Ctx;
        var reason = _flagReason;
        if (_main.Session is not { } s || ctx.GameId is not { } id || reason is null) return;
        var evidence = _flagEvidence;
        var verifyRun = _flagVerifyRun;
        var launchRun = _flagLaunchRun;
        var target = ctx.Target;
        ClearTransient();

        // 1. the flag first: it must hold even when nothing can be filed
        var flag = GameFlags.Create(reason, evidence: evidence);
        if (!await _main.RunAsync($"Flagging {ctx.Name} as set in-game", () => Task.Run(() =>
        {
            LibraryService.SetInGame(s.Store, id, flag);
            // what led to the flag must not suggest it again once it is lifted, here or on another device (never throws)
            RunResults.MarkFlagged(s.Paths.DataDir, id, s.Device.Name);
        }))) return;
        Log.Info($"{id}: flagged as set in-game ({reason})");

        // a flagged game gets no automatic findings: one still waiting in the outbox from before the flag is withdrawn (the research task asks instead)
        try
        {
            if (AutoFindings.WithdrawQueued(_main.Outbox.Store, id) > 0) _main.Outbox.Service.RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not withdraw {id}'s waiting automatic requests: {ex.Message}");
        }

        // 2. the research task; a failure leaves the flag in place and says so
        FiledRequest? filed = null;
        string? problem = target is null ? "the game isn't installed here" : null;
        if (target is not null) (filed, problem) = await _lab.FileResearchLaterAsync(target, reason, verifyRun, launchRun);
        if (filed is not null)
        {
            flag.Issue = filed.Number;
            await _main.RunAsync("Saving the research task number", () => Task.Run(() => LibraryService.SetInGame(s.Store, id, flag)));
        }

        await FinishFlagChangeAsync(s, id, $"chore(library): flag {id} as set in-game");
        if (filed is not null)
            _main.NotifyWithAction($"{ctx.Name} is set in-game now: Optimus-Vibe leaves its settings alone. Research task #{filed.Number} is filed.", "Open task", () => _lab.OpenUrl(filed.Url));
        else
            _main.Notify($"{ctx.Name} is set in-game now: Optimus-Vibe leaves its settings alone. The research task wasn't filed ({problem}).", error: true);
    }

    private async Task ManageAgainAsync()
    {
        var ctx = Ctx;
        if (_main.Session is not { } s || ctx.GameId is not { } id) return;
        var ok = await _main.RunAsync($"Managing {ctx.Name}'s settings again", () => Task.Run(() =>
        {
            LibraryService.SetInGame(s.Store, id, null);
            RunResults.ResetStreak(s.Paths.DataDir, id, s.Device.Name); // test it again before the flag is suggested again
            Log.Info($"{id}: manages its settings again");
        }));
        if (!ok) return;
        await FinishFlagChangeAsync(s, id, $"chore(library): manage {id} settings again");
        _main.Notify(IsFlagged
            ? $"{ctx.Name} is still flagged by its game config, so its settings stay with the game."
            : GameHealth.ManagedAgainLine(ctx.Name));
    }

    /// <summary>After the flag changed: re-read the game and the library (the tile says "Set in-game"), publish library.yaml when auto-sync is on.</summary>
    private async Task FinishFlagChangeAsync(AppSession s, string gameId, string commitMessage)
    {
        await _main.ReloadGameAsync(gameId);
        await _main.Library.ScanAsync(rescanLaunchers: false);
        var settings = await SettingsStore.LoadAsync();
        if (settings.AutoSync) await _main.SyncFilesQuietlyAsync(commitMessage, s.Paths.LibraryFile);
        Refresh();
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------------------------------------------------------------- auto snapshot (this device's kept copy of a cloud-synced file)

    private void LoadAuto()
    {
        if (_autoLoaded) return;
        _autoLoaded = true;
        _autoLine = null;
        _autoHasCopy = false;
        try
        {
            if (_main.Session is { } s && Ctx.GameId is { } id && Ctx.HasConfig)
            {
                var game = GameContext.Load(s.Store, id, s.Device);
                _autoLine = AutoSnapshots.StatusLine(game);
                _autoHasCopy = _autoLine is not null && AutoSnapshots.HasCopy(game);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read this device's kept settings file: {ex.Message}");
        }
    }

    /// <summary>"Settings file kept per device: Performance copy from Oct 20, 2026" or the first-launch hint; null when the game has no autoSnapshot file.</summary>
    public string? AutoSnapshotLine
    {
        get
        {
#if DEBUG
            if (_demo) return _demoAuto;
#endif
            LoadAuto();
            return _autoLine;
        }
    }

    public bool HasAutoSnapshot => AutoSnapshotLine is not null;

    public bool HasAutoCopy
    {
        get
        {
#if DEBUG
            if (_demo) return _demoAutoCopy;
#endif
            LoadAuto();
            return _autoHasCopy;
        }
    }

    public bool ShowForgetAuto => HasAutoCopy && !_forgetConfirming;
    public bool IsForgetConfirming => _forgetConfirming;

    private void BeginForget()
    {
        ClearTransient();
        _forgetConfirming = true;
        OnPropertyChanged(nameof(IsForgetConfirming));
        OnPropertyChanged(nameof(ShowForgetAuto));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ConfirmForget()
    {
        _forgetConfirming = false;
        try
        {
            if (_main.Session is { } s && Ctx.GameId is { } id)
            {
                var n = AutoSnapshots.ForgetActive(GameContext.Load(s.Store, id, s.Device));
                _main.Notify(n > 0 ? "Forgot this device's copy. The next launch starts from what the game has." : "There was no copy to forget.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not forget this device's copy: {ex.Message}");
            _main.Notify("Couldn't forget the copy.", error: true);
        }
        Refresh();
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------------------------------------------------------------- status chip

    public LabIssue? Issue => LabStatusIndex.For(_lab.Issues, Ctx.Key, Ctx.GameId);
    public bool HasChip => Issue is not null;
    public string ChipText => Issue is { } i ? LabStatusIndex.Chip(i) : "";
    public string ChipLine => Issue is { } i ? $"Game lab: {ChipText.ToLowerInvariant()} (#{i.Number})" : "";
    public bool ChipNeedsAttention => Issue is { } i && i.IsOpen && i.Status is LabStatus.NeedsEvidence or LabStatus.Failed;

    // ---------------------------------------------------------------- confirmation

    public bool IsConfirming => _confirming is not null;
    public bool IsLoadingPreview { get => _loadingPreview; private set => Set(ref _loadingPreview, value); }
    public EvidencePreview? Preview { get => _preview; private set { if (Set(ref _preview, value)) OnPropertyChanged(nameof(HasPreview)); } }
    public bool HasPreview => Preview is not null;
    public string? PreviewError { get => _previewError; private set { if (Set(ref _previewError, value)) OnPropertyChanged(nameof(HasPreviewError)); } }
    public bool HasPreviewError => PreviewError is not null;

    public string ConfirmTitle => _confirming switch
    {
        LabKind.Refresh when _verifyText is not null => $"Send the test result for {Ctx.Name}?",
        LabKind.SyncCheck => $"Re-check settings sync for {Ctx.Name}?",
        LabKind.Refresh => $"Refresh presets for {Ctx.Name}?",
        _ => $"Request presets for {Ctx.Name}?",
    };

    public string ConfirmLead => _confirming switch
    {
        LabKind.Refresh when _verifyText is not null =>
            "Claude researches the game again and sees which of the preset's settings the game changed on its own, so it can look for the file or key the game really obeys.",
        LabKind.SyncCheck => "Claude looks again at whether this game's settings travel between your devices. Your presets stay as they are.",
        LabKind.Refresh => "Claude researches the game again (after a patch, or with better evidence) and rewrites the presets.",
        _ => "Claude researches the game's settings and writes presets for your devices. This takes a few minutes and you'll get a notice when they arrive.",
    };

    public string SendText => "Send request";

    public string PrivacyNote => $"Evidence goes only to your private config repo{(_lab.RepoName is { } r ? $" ({r})" : "")}.";

    /// <summary>
    /// B / Esc: closes the top card only. A confirmation (request, test or "send the test result") goes first; the test result
    /// underneath closes on the next press, then the post-session card ("Not now", only while focus is in it), then an open "More" collapses.
    /// True when something was closed.
    /// </summary>
    public bool CancelTransient()
    {
        // only while focus is in the post-session card: one the player has not reached (below the fold, an old session) never takes B or snoozes unseen
        var confirming = IsConfirming || _testConfirming || _forgetConfirming || _flagConfirming || IsKeepConfirming;
        switch (LabPanelPlan.NextCancel(confirming, _result is not null, _testing, HasPostSession && PostCardHasFocus, _moreOpen))
        {
            case CancelStep.CloseConfirmation:
                ClearTransient();
                return true;
            case CancelStep.CloseResult:
                _result = null;
                OnPropertyChanged(string.Empty);
                return true;
            case CancelStep.DismissPostCard:
                DismissPost();
                return true;
            case CancelStep.CollapseMore:
                IsMoreOpen = false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Closes confirmation cards but keeps a test result on screen (another panel on the game page is opening).</summary>
    public bool CloseCards() => ClearTransient();

    /// <summary>The page is left (or opened afresh): fold "More" away so a hidden panel never keeps it open to swallow B.</summary>
    public void CollapseMore()
    {
        if (_moreOpen) IsMoreOpen = false;
    }

    private bool ClearTransient()
    {
        var any = IsConfirming || _testConfirming || _forgetConfirming || _flagConfirming;
        any |= CancelKeepConfirm(); // "Keep these" asking on the post-session card is a confirmation too
        _generation++;
        _confirming = null;
        _verifyText = null;
        if (_forgetConfirming)
        {
            _forgetConfirming = false;
            OnPropertyChanged(nameof(IsForgetConfirming));
        }
        if (_testConfirming)
        {
            _testConfirming = false;
            OnPropertyChanged(nameof(IsTestConfirming));
        }
        if (_flagConfirming)
        {
            _flagConfirming = false;
            OnPropertyChanged(nameof(IsFlagConfirming));
            OnPropertyChanged(nameof(ShowSuggest));
            OnPropertyChanged(nameof(ShowResultSuggest));
            OnPropertyChanged(nameof(ShowSuggested));
            OnPropertyChanged(nameof(ShowFlagInMore));
            OnPropertyChanged(nameof(HasMore));
        }
        Preview = null;
        PreviewError = null;
        IsLoadingPreview = false;
        OnPropertyChanged(nameof(IsConfirming));
        OnPropertyChanged(nameof(ShowMapFirst));
        OnPropertyChanged(nameof(MapFirstTip));
        OnPropertyChanged(nameof(ConfirmTitle));
        CommandManager.InvalidateRequerySuggested();
        return any;
    }

    private async Task BeginAsync(LabKind kind, RunResult? test = null)
    {
        var ctx = Ctx;
        if (ctx.Target is null) return;
        var verifyText = test is null ? null : RunResults.EvidenceText(test);
        ClearTransient();
        var gen = ++_generation;
        _confirming = kind;
        _verifyText = verifyText;
        Preview = null;
        PreviewError = null;
        IsLoadingPreview = true;
        OnPropertyChanged(nameof(IsConfirming));
        OnPropertyChanged(nameof(ShowMapFirst));
        OnPropertyChanged(nameof(MapFirstTip));
        OnPropertyChanged(nameof(ConfirmTitle));
        OnPropertyChanged(nameof(ConfirmLead));
        OnPropertyChanged(nameof(PrivacyNote));
        try
        {
            var preview = await _lab.PreviewAsync(ctx.Target, verifyText);
            if (gen != _generation) return;
            Preview = preview;
        }
        catch (Exception ex)
        {
            Log.Warn($"Evidence preview failed: {ex.Message}");
            if (gen == _generation) PreviewError = $"Couldn't look at this PC: {ex.Message}";
        }
        finally
        {
            if (gen == _generation) IsLoadingPreview = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ConfirmAsync()
    {
        var ctx = Ctx;
        if (_confirming is not { } kind || ctx.Target is null) return;
        var verifyText = _verifyText;
        var outcome = await _lab.FileRequestAsync(kind, ctx.Target, verifyText);
        if (outcome is null) return;
        ClearTransient();
        if (verifyText is not null && outcome.Filed is not { Reused: true }) _resultSent = true;
        OnPropertyChanged(string.Empty);
        if (outcome.Filed is not { } filed)
        {
            _main.Notify("Saved - it will be sent to the game lab when you're back online.");
            return;
        }
        if (filed.Reused) _main.NotifyWithAction(verifyText is null ? filed.Message : filed.Message + " The test result wasn't added to it.", "Open", () => _lab.OpenUrl(filed.Url));
        else _main.NotifyWithAction($"Sent {ctx.Name} to the game lab. {filed.Message}", "Open issue", () => _lab.OpenUrl(filed.Url));
    }
}
