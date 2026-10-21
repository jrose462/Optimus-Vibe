using System.Windows.Input;
using OptimusVibe.App.Services;
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
public sealed record LabGameContext(string Name, string? Key, string? GameId, LabTarget? Target, bool HasConfig, bool NeedsPresets,
    string? PresetLabel = null);

/// <summary>
/// The game-lab actions on one game's page: Request / Refresh presets, Re-check settings sync, the confirmation with the evidence
/// preview (counts and tokenized paths, no contents), and the request's status chip with a link to its issue. Also Test preset: launch
/// the game once with nothing changed, then say whether it kept the preset's values (and offer to send that to the lab).
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
    private RunResult? _lastRun;
    private bool _lastRunLoaded;
    private string? _applyStatus;
    private bool _applyStatusLoaded;
    private bool _autoLoaded;
    private string? _autoLine;
    private bool _autoHasCopy;
    private bool _forgetConfirming;
    private bool _flagLoaded;
    private FlagDef? _flag;
    private FlagDef? _userFlag;
    private int _streak;
    private bool _flagConfirming;
    private string? _flagReason;
    private string? _flagEvidence;
    private int _researchFinishedFor;
#if DEBUG
    private bool _demo;
    private RunResult? _demoHint;
    private string? _demoStatus;
    private string? _demoAuto;
    private bool _demoAutoCopy;
    private string? _demoSuggest;
    private bool _demoResearchDone;
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
        lab.IssuesChanged += Refresh;
        lab.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(GameLabViewModel.IsReady)) Refresh(); };
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

    public void Refresh()
    {
        _lastRunLoaded = false;
        _applyStatusLoaded = false;
        _autoLoaded = false;
        _flagLoaded = false;
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

    /// <summary>The request card offers to map the game first when nothing is known about where its settings live. It never blocks the request.</summary>
    public bool ShowMapFirst => _confirming is LabKind.New or LabKind.Refresh && _verifyText is null && CanMap && !HasMapping;

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
            _main.MinimizeForGame, _main.RestoreAfterGame);
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
            _lastRunLoaded = false;
            _flagLoaded = false; // the new result may complete a streak
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

        ClearTransient();
        _result = null;
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
            case "held": _result = Sample(0); break;
            case "overridden": _result = Sample(10); break;
            case "sent": _result = Sample(10); _resultSent = true; break;
            case "unknown": _result = Sample(0, seen: false); break;
            case "hint": _demoHint = Sample(10, verify: false); break;
            case "status": _demoStatus = "Applied by file edit - verified held on 2026-10-20."; break;
            case "suggest": _demoSuggest = GameFlags.VerifySuggestionReason(2); break;
            case "suggest-map": _demoSuggest = GameFlags.MappingSuggestionReason(["{LocalAppData}/Demo/Saved/system.save"]); break;
            case "suggest-result": _result = Sample(10); _demoSuggest = GameFlags.VerifySuggestionReason(2); break;
            case "flag-confirm":
                _demoSuggest = GameFlags.VerifySuggestionReason(2);
                _flagReason = _demoSuggest;
                _flagConfirming = true;
                break;
            case "flagged":
                _userFlag = _flag = GameFlags.Create(GameFlags.VerifySuggestionReason(2), issue: 12);
                _demoStatus = GameFlags.StatusLine(_flag);
                break;
            case "flagged-done":
                _userFlag = _flag = GameFlags.Create(GameFlags.VerifySuggestionReason(2), issue: 12);
                _demoStatus = GameFlags.StatusLine(_flag);
                _demoResearchDone = true;
                break;
            case "flagged-config":
                _flag = new FlagDef { Reason = "the game rewrites its binary settings store on every boot", Since = "2026-10-20", Issue = 12 };
                _demoStatus = GameFlags.StatusLine(_flag);
                break;
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

    // ---------------------------------------------------------------- passive hint

    private RunResult? LastRun
    {
        get
        {
            if (_lastRunLoaded) return _lastRun;
            _lastRunLoaded = true;
            _lastRun = null;
            try
            {
                if (_main.Session is { } s && Ctx.GameId is { } id) _lastRun = RunResults.Get(s.Paths.DataDir, id, s.Device.Name);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the last run result: {ex.Message}");
            }
            return _lastRun;
        }
    }

    /// <summary>After a normal launch in which the game changed most of the preset: a quiet line on the game page. No popup.</summary>
    public string? HintText
    {
        get
        {
#if DEBUG
            if (_demo) return _demoHint is { } demo ? RunResults.HintLine(demo) : null;
#endif
            return CanTest && !IsFlagged && !HasResult && !_testing && RunResults.NeedsHint(LastRun) ? RunResults.HintLine(LastRun!) : null;
        }
    }

    public bool HasHint => HintText is not null;

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

    public bool HasApplyStatus => ApplyStatusText is not null;

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
        _streak = 0;
        try
        {
            if (_main.Session is { } s && Ctx.GameId is { } id && Ctx.HasConfig)
            {
                var game = s.Store.LoadGame(id);
                var lib = LibraryService.LoadLibrary(s.Store);
                _userFlag = GameFlags.FromLibrary(lib, id);
                _flag = GameFlags.Effective(game, lib);
                _streak = RunResults.OverriddenStreak(s.Paths.DataDir, id, s.Device.Name);
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

    public string ConfigOnlyNote => "The game lab set this flag, so only it can lift it. It does that when its research finds a way to set this game's settings.";

    /// <summary>Test preset stays visible but is off for a flagged game, and says why.</summary>
    public string? TestDisabledText => IsFlagged ? "Test preset is off: this game's settings are left to the game, so there is nothing to test." : null;

    public bool HasTestDisabledText => TestDisabledText is not null;

    /// <summary>
    /// Why the flag is worth suggesting (never applied on its own): two overridden Test preset results in a row, or a mapping run whose
    /// changes were all in binary files. Null when the game is flagged already or nothing suggests it.
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
            if (GameFlags.SuggestsFromVerify(_streak)) return GameFlags.VerifySuggestionReason(_streak);
            if (_main.Session is { } s && Ctx.Target is { } t && GameLabService.StoredMapping(s, t) is { SuggestSetInGame: true } m)
                return m.SuggestReason ?? "the game keeps its settings only in a binary file Optimus-Vibe can't edit";
            return null;
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

    public string ResearchFinishedText => "Research finished - try managing settings again?";

    private async Task CheckResearchAsync(int issue)
    {
        if (await _lab.IsIssueOpenAsync(issue) != false || _researchFinishedFor == issue) return;
        _researchFinishedFor = issue;
        OnPropertyChanged(nameof(ShowResearchFinished));
        OnPropertyChanged(nameof(ResearchFinishedText));
    }

    private void BeginFlag()
    {
        var reason = SuggestionReason;
        if (reason is null) return;
        ClearTransient();
        _flagReason = reason;
        _flagEvidence = EvidenceFor(reason);
        _flagConfirming = true;
        OnPropertyChanged(nameof(IsFlagConfirming));
        OnPropertyChanged(nameof(FlagTitle));
        OnPropertyChanged(nameof(FlagWhy));
        OnPropertyChanged(nameof(ShowSuggest));
        OnPropertyChanged(nameof(ShowResultSuggest));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The short evidence the flag keeps: the last overridden settings, or the mapping run's reason.</summary>
    private string EvidenceFor(string reason)
    {
        try
        {
            if (_main.Session is { } s && Ctx.GameId is { } id && GameFlags.SuggestsFromVerify(_streak) && LastOverriddenTest(s, id) is { } last)
                return $"Test preset: the game changed {last.Drift.Count} of {last.Applied} setting(s) in the last test ({RunResults.Names(last, 3)})";
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not describe the last test: {ex.Message}");
        }
        return reason;
    }

    private async Task ConfirmFlagAsync()
    {
        var ctx = Ctx;
        var reason = _flagReason;
        if (_main.Session is not { } s || ctx.GameId is not { } id || reason is null) return;
        var evidence = _flagEvidence;
        var target = ctx.Target;
        var verifyText = LastOverriddenTest(s, id) is { } last ? RunResults.EvidenceText(last) : null;
        ClearTransient();

        // 1. the flag first: it must hold even when nothing can be filed
        var flag = GameFlags.Create(reason, evidence: evidence);
        if (!await _main.RunAsync($"Flagging {ctx.Name} as set in-game", () => Task.Run(() => LibraryService.SetInGame(s.Store, id, flag)))) return;
        Log.Info($"{id}: flagged as set in-game ({reason})");

        // 2. the research task; a failure leaves the flag in place and says so
        FiledRequest? filed = null;
        string? problem = target is null ? "the game isn't installed here" : null;
        if (target is not null) (filed, problem) = await _lab.FileResearchLaterAsync(target, reason, verifyText);
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

    private static RunResult? LastOverriddenTest(AppSession s, string gameId)
    {
        try
        {
            var last = RunResults.Get(s.Paths.DataDir, gameId, s.Device.Name, verifyOnly: true);
            return last is not null && RunResults.Verdict(last) == RunVerdict.Overridden ? last : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the last test: {ex.Message}");
            return null;
        }
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
            : $"Optimus-Vibe manages {ctx.Name}'s settings again. Test the preset to check the game keeps it.");
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
    /// underneath closes on the next press. True when something was closed.
    /// </summary>
    public bool CancelTransient()
    {
        if (ClearTransient()) return true;
        var any = false;
        if (_result is not null && !_testing)
        {
            _result = null;
            OnPropertyChanged(string.Empty);
            any = true;
        }
        return any;
    }

    /// <summary>Closes confirmation cards but keeps a test result on screen (another panel on the game page is opening).</summary>
    public bool CloseCards() => ClearTransient();

    private bool ClearTransient()
    {
        var any = IsConfirming || _testConfirming || _forgetConfirming || _flagConfirming;
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
        }
        Preview = null;
        PreviewError = null;
        IsLoadingPreview = false;
        OnPropertyChanged(nameof(IsConfirming));
        OnPropertyChanged(nameof(ShowMapFirst));
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
