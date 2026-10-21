using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
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
#if DEBUG
    private bool _demo;
    private RunResult? _demoHint;
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
        TestCommand = new RelayCommand(BeginTest, () => Idle() && CanTest && !_testing);
        StartTestCommand = new AsyncRelayCommand(StartTestAsync, () => Idle() && CanTest && !_testing);
        SendResultCommand = new AsyncRelayCommand(() => BeginAsync(LabKind.Refresh, _result), () => Idle() && CanSendResult);
        SaveResultCommand = new AsyncRelayCommand(SaveResultAsync, () => Idle() && _result is not null && Ctx.GameId is not null);
        DismissResultCommand = new RelayCommand(DismissResult);
        MapCommand = new RelayCommand(BeginMap, () => Idle() && CanMap && !_testing);
        MapFirstCommand = new RelayCommand(BeginMap, () => Idle() && CanMap);
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

    public void Refresh()
    {
        _lastRunLoaded = false;
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
        _resultSent = false;
        switch (kind)
        {
            case "confirm": BeginTest(); break;
            case "held": _result = Sample(0); break;
            case "overridden": _result = Sample(10); break;
            case "sent": _result = Sample(10); _resultSent = true; break;
            case "unknown": _result = Sample(0, seen: false); break;
            case "hint": _demoHint = Sample(10, verify: false); break;
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
            return CanTest && !HasResult && !_testing && RunResults.NeedsHint(LastRun) ? RunResults.HintLine(LastRun!) : null;
        }
    }

    public bool HasHint => HintText is not null;

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
        var any = IsConfirming || _testConfirming;
        _generation++;
        _confirming = null;
        _verifyText = null;
        if (_testConfirming)
        {
            _testConfirming = false;
            OnPropertyChanged(nameof(IsTestConfirming));
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
