using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;
using OptimusVibe.Outbox;

namespace OptimusVibe.Tests;

/// <summary>
/// <see cref="AutoFindings"/>: what the evidence warrants sending to the game lab by itself (<see cref="AutoFindings.Detect"/>), and that it
/// is sent only with consent, within the limits (14 days per game, once ever for sync, a per-start cap, never while a request is open or
/// waiting in the outbox) and never twice. Plus the consent card on the post-session card.
/// </summary>
public sealed class AutoFindingsTests : IDisposable
{
    private const string Preset = "recommended:performance";
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private readonly TempDir _data = new();

    public void Dispose() => _data.Dispose();

    private static HealthInputs In(string id = "g") => new()
    {
        GameId = id, Device = "desktop", HasConfig = true, HasPresets = true, LabReady = true, CanLaunch = true, CurrentPreset = Preset,
    };

    private static RunResult Run(double day, int drifted = 0, bool verify = false, int sec = 1800, string? build = null, CaptureAnswer? capture = null)
    {
        var r = new RunResult
        {
            Utc = T0.AddDays(day), Game = "g", Device = "desktop", Preset = Preset, Mode = verify ? RunResult.VerifyMode : RunResult.NormalMode,
            AppliedKeys = 10, ProcessSeen = true, DurationSec = verify ? null : sec, UserChanged = verify ? false : null, Build = build,
            Capture = verify ? null : capture ?? (drifted > 0 ? CaptureAnswer.DontSave : CaptureAnswer.NotAsked),
        };
        for (var i = 0; i < drifted; i++) r.Drift.Add(new RunDrift { FileId = "gfx", Key = $"Quality{i}", Applied = "High", Now = "Low" });
        return r;
    }

    /// <summary>A launch the passive check rates overridden: most of the preset drifted in a short, unsaved session.</summary>
    private static RunResult Reset(double day) => Run(day, drifted: 6, sec: 120);

    /// <summary>Held on Steam build 1 (a clean launch), now build 2 is installed.</summary>
    private static HealthInputs Updated(string id = "g") => In(id) with
    {
        Presets = new PresetsMark { MadeUtc = T0.AddDays(-10), BuildId = "steam:1" }, Recent = [Run(-1, build: "steam:1")], CurrentBuild = "steam:2",
    };

    private static CloudSyncReport ChangedWhileClosed(int clean) => new()
    {
        GameId = "g", Device = "desktop", HasExternalChanges = true, LastExternalChangeUtc = T0.AddDays(-20), CleanLaunches = clean,
    };

    // ---------------------------------------------------------------- Detect

    [Fact]
    public void GameUpdated_AfterThePresetHeld_AsksForARefresh()
    {
        var f = AutoFindings.Detect(Updated());
        Assert.Equal(new AutoFinding(LabKind.Refresh, AutoFindingReason.GameUpdated), f);
    }

    [Fact]
    public void GameUpdated_NeverKnownToWork_IsNoFinding()
    {
        // the presets arrived for build 1 but were never seen holding: the update is only suggested on the page
        var i = In() with { Presets = new PresetsMark { MadeUtc = T0.AddDays(-10), BuildId = "steam:1" }, CurrentBuild = "steam:2" };
        Assert.True(i.GameUpdated);
        Assert.Null(AutoFindings.Detect(i));
    }

    [Fact]
    public void AnExeFingerprintChange_NeverFilesByItself()
    {
        var i = In() with { Recent = [Run(-1, build: "exe:game.exe:1:20260101")], CurrentBuild = "exe:game.exe:2:20260201" };
        Assert.True(i.GameUpdated);
        Assert.Null(AutoFindings.Detect(i));
    }

    [Fact]
    public void TwoOverriddenTestsInARow_AskForARefresh_UntilTheGameHoldsAgain()
    {
        var i = In() with { OverriddenStreak = 2, LastVerify = Run(0, drifted: 3, verify: true) };
        Assert.Equal(AutoFindingReason.TestsOverridden, AutoFindings.Detect(i)?.Reason);

        var heldSince = i with { Recent = [Run(1)] };
        Assert.Null(AutoFindings.Detect(heldSince));
    }

    [Fact]
    public void PassiveResets_CountOnlyOnSeparateDays()
    {
        Assert.Null(AutoFindings.Detect(In() with { Recent = [Reset(0), Reset(0.1)] }));          // same day: could be one evening of tweaking
        Assert.Equal(AutoFindingReason.KeepsResetting, AutoFindings.Detect(In() with { Recent = [Reset(0), Reset(1)] })?.Reason);
    }

    [Fact]
    public void PassiveResets_AHeldLaunchInBetweenEndsTheCount()
    {
        Assert.Null(AutoFindings.Detect(In() with { Recent = [Reset(0), Run(1), Reset(2)] }));
    }

    [Fact]
    public void PassiveResets_FromBeforeTheNewPresetsDontCount()
    {
        var i = In() with { Recent = [Reset(0), Reset(1)], Presets = new PresetsMark { MadeUtc = T0.AddDays(0.5) } };
        Assert.Null(AutoFindings.Detect(i));
    }

    [Fact]
    public void SyncStillUnsure_AfterFiveCleanLaunches_AsksForASyncCheck()
    {
        Assert.Null(AutoFindings.Detect(In() with { CloudSync = ChangedWhileClosed(clean: 4) }));
        var f = AutoFindings.Detect(In() with { CloudSync = ChangedWhileClosed(clean: CloudSyncVerdict.CleanLaunchesForNotSynced) });
        Assert.Equal(new AutoFinding(LabKind.SyncCheck, AutoFindingReason.SyncUnsure), f);
    }

    [Fact]
    public void ASettledSyncAnswer_IsNoFinding()
    {
        var sure = ChangedWhileClosed(clean: 12) with { Detected = SyncVerdict.Synced, Confidence = SyncConfidence.High };
        Assert.Null(AutoFindings.Detect(In() with { CloudSync = sure }));
    }

    [Fact]
    public void FlaggedGames_AndGamesWithoutPresets_HaveNoFinding()
    {
        var flag = GameFlags.Create("the game rewrites its settings", issue: 7, today: T0);
        Assert.Null(AutoFindings.Detect(Updated() with { Flag = flag }));
        Assert.Null(AutoFindings.Detect(Updated() with { HasPresets = false }));
        Assert.Null(AutoFindings.Detect(Updated() with { HasConfig = false }));
    }

    // ---------------------------------------------------------------- RunAsync: consent, limits, no duplicates

    private static AutoFindingCandidate Candidate(string id = "g", HealthInputs? inputs = null, bool optedOut = false) =>
        new($"steam:{id}", id, $"Game {id}", inputs ?? Updated(id), optedOut);

    private sealed class Filer
    {
        public List<(string GameId, LabKind Kind)> Calls { get; } = [];
        public bool Offline { get; init; }
        public bool Fail { get; init; }

        public Task<LabRequestOutcome> File(AutoFindingCandidate c, AutoFinding f, CancellationToken ct)
        {
            Calls.Add((c.GameId, f.Kind));
            if (Fail) throw new InvalidOperationException("boom");
            if (Offline)
                return Task.FromResult(new LabRequestOutcome(null, new OutboxItem("x", OutboxKind.LabRequest, T0, 0, null, null, "{}")));
            return Task.FromResult(new LabRequestOutcome(new FiledRequest(40 + Calls.Count, $"https://github.com/o/r/issues/{40 + Calls.Count}", false, 0, "ok"), null));
        }
    }

    private static readonly IReadOnlyList<LabIssue> NoRequests = [];

    private Task<AutoFindingsResult> Run(IReadOnlyList<AutoFindingCandidate> candidates, Filer filer, DateTime now, bool consent = true,
        AutoFindingBudget? budget = null, IReadOnlySet<string>? queued = null, bool labReady = true, IReadOnlyList<LabIssue>? issues = null,
        bool readFailed = false, bool? repoPrivate = true) =>
        AutoFindings.RunAsync(candidates, consent, labReady, budget ?? new AutoFindingBudget(AutoFindings.MaxPerStart), filer.File,
            new LabStateStore(_data.Path), readFailed ? null : issues ?? NoRequests, repoPrivate, queued, () => now);

    /// <summary>A lab request for steam:<paramref name="id"/>, as the lab's issue list reads it.</summary>
    private static LabIssue Request(int number, LabKind kind, bool open, LabStatus status, string id = "g") =>
        new(number, $"[game-lab] Game {id}", $"https://github.com/o/r/issues/{number}", open, [], status,
            new GameLabContract.RequestInfo(kind, id, $"Game {id}", "steam", id, "laptop", "1.0"), null, null);

    [Fact]
    public async Task ConsentOff_SendsNothing()
    {
        var filer = new Filer();
        var r = await Run([Candidate()], filer, T0, consent: false);
        Assert.Empty(filer.Calls);
        Assert.Empty(r.Filed);
        Assert.Empty(new LabStateStore(_data.Path).Load().AutoRefresh);
    }

    [Fact]
    public async Task LabNotReady_SendsNothing()
    {
        var filer = new Filer();
        await Run([Candidate()], filer, T0, labReady: false);
        Assert.Empty(filer.Calls);
    }

    [Fact]
    public async Task AFinding_IsSentOnce_AndRemembered()
    {
        var filer = new Filer();
        var r = await Run([Candidate()], filer, T0);

        var sent = Assert.Single(r.Filed);
        Assert.Equal(41, sent.Issue);
        Assert.False(sent.Queued);
        Assert.Equal(("g", LabKind.Refresh), Assert.Single(filer.Calls));
        var state = new LabStateStore(_data.Path).Load();
        Assert.Equal(T0, state.AutoRefresh["g"]);
        Assert.Equal(T0, state.Requested["steam:g"]);
    }

    [Fact]
    public async Task Refresh_AtMostOncePerGameEvery14Days()
    {
        var filer = new Filer();
        await Run([Candidate()], filer, T0);
        var again = await Run([Candidate()], filer, T0.AddDays(8));          // past the 7-day request memory, inside the 14 days
        Assert.Single(filer.Calls);
        Assert.Contains("2 weeks", again.Skipped["g"]);

        await Run([Candidate()], filer, T0.AddDays(15));
        Assert.Equal(2, filer.Calls.Count);
    }

    [Fact]
    public async Task AnyRecentRequest_ForTheGame_BlocksAFinding()
    {
        new LabStateStore(_data.Path).Update(s => s.Requested["steam:g"] = T0.AddDays(-2)); // the user asked by hand two days ago
        var filer = new Filer();
        var r = await Run([Candidate()], filer, T0);
        Assert.Empty(filer.Calls);
        Assert.Equal("requested recently", r.Skipped["g"]);
    }

    [Fact]
    public async Task SyncCheck_OncePerGameEver()
    {
        var filer = new Filer();
        var unsure = Candidate(inputs: In() with { CloudSync = ChangedWhileClosed(clean: 6) });
        await Run([unsure], filer, T0);
        await Run([unsure], filer, T0.AddDays(200));
        Assert.Equal(("g", LabKind.SyncCheck), Assert.Single(filer.Calls));
    }

    [Fact]
    public async Task AtMostTwoPerStart()
    {
        var filer = new Filer();
        var budget = new AutoFindingBudget(AutoFindings.MaxPerStart);
        var r = await Run([Candidate("a"), Candidate("b"), Candidate("c")], filer, T0, budget: budget);
        Assert.Equal(["a", "b"], filer.Calls.Select(c => c.GameId));
        Assert.Contains("per start", r.Skipped["c"]);

        await Run([Candidate("c")], filer, T0.AddMinutes(10), budget: budget); // a later pass of the same start
        Assert.Equal(2, filer.Calls.Count);
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public async Task AnOpenRequest_IsNeverDuplicated()
    {
        var filer = new Filer();
        var r = await Run([Candidate()], filer, T0, issues: [Request(12, LabKind.New, open: true, LabStatus.Researching)]);
        Assert.Empty(filer.Calls);
        Assert.Contains("#12", r.Skipped["g"]);
    }

    [Fact]
    public async Task AnOpenRequestOfAnotherKind_FromAnotherDevice_BlocksAFinding_EvenWhenTheAppHeldNoList()
    {
        // device A filed a Refresh 10 days ago and it still needs evidence; this device starts with an empty list (no lab check yet) and its
        // evidence says sync is unsure: the open request comes from the fresh read, so no SyncCheck is filed beside the Refresh
        var filer = new Filer();
        var unsure = Candidate(inputs: In() with { CloudSync = ChangedWhileClosed(clean: 6), LabRequest = null });
        var r = await Run([unsure], filer, T0, issues: [Request(31, LabKind.Refresh, open: true, LabStatus.NeedsEvidence)]);
        Assert.Empty(filer.Calls);
        Assert.Contains("#31", r.Skipped["g"]);
    }

    [Fact]
    public async Task AClosedRequest_InTheList_DoesNotBlock()
    {
        var filer = new Filer();
        await Run([Candidate()], filer, T0, issues: [Request(5, LabKind.New, open: false, LabStatus.Done)]);
        Assert.Single(filer.Calls);
    }

    [Fact]
    public async Task TheLabsRequestsCouldNotBeRead_NothingIsSent()
    {
        var filer = new Filer();
        var budget = new AutoFindingBudget(AutoFindings.MaxPerStart);
        var r = await Run([Candidate()], filer, T0, readFailed: true, budget: budget);
        Assert.Empty(filer.Calls);
        Assert.Equal(AutoFindings.RequestsUnknownReason, r.Skipped["g"]);
        Assert.Equal(AutoFindings.MaxPerStart, budget.Remaining);
        Assert.Empty(new LabStateStore(_data.Path).Load().AutoRefresh); // asked again next time
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ARepoNotKnownToBePrivate_GetsNothingAutomatically(bool? repoPrivate)
    {
        var filer = new Filer();
        var r = await Run([Candidate()], filer, T0, repoPrivate: repoPrivate);
        Assert.Empty(filer.Calls);
        Assert.Equal(AutoFindings.RepoBlockReason(repoPrivate), r.Skipped["g"]);
    }

    [Fact]
    public void SettingText_CallsTheRepoPrivate_OnlyWhenGitHubSaidSo()
    {
        Assert.Contains("private config repo (me/cfg)", AutoFindings.SettingText("me/cfg", repoPrivate: true));
        var pub = AutoFindings.SettingText("me/cfg", repoPrivate: false);
        Assert.Contains("is public", pub);
        Assert.DoesNotContain("private config repo", pub);
        Assert.DoesNotContain("goes only to your private", AutoFindings.SettingText("me/cfg"));
        Assert.Null(AutoFindings.RepoBlockReason(true));
    }

    [Fact]
    public void Consent_NeedsTheSessionCheckToo()
    {
        Assert.True(AutoFindings.ConsentFrom(new AppSettings { GameLabAutoFindings = true, GameLabWatchSessions = true }));
        Assert.False(AutoFindings.ConsentFrom(new AppSettings { GameLabAutoFindings = true, GameLabWatchSessions = false }));
        Assert.False(AutoFindings.ConsentFrom(new AppSettings { GameLabAutoFindings = false, GameLabWatchSessions = true }));
    }

    [Fact]
    public void WithdrawingConsent_RemovesOnlyTheAutomaticRequestsWaitingInTheOutbox()
    {
        var store = new OutboxStore(_data.Path);
        var info = new GameLabContract.RequestInfo(LabKind.Refresh, "g", "Game g", "steam", "g", "desktop", "1.0");
        var auto = store.Add(OutboxItem.ForLabRequest("o/r", "main", new PreparedLabRequest(info, ["e"]), automatic: true));
        var byHand = store.Add(OutboxItem.ForLabRequest("o/r", "main", new PreparedLabRequest(info with { GameId = "h", StoreId = "h" }, ["e"])));
        store.Add(OutboxItem.ForBugReport(new OptimusVibe.Feedback.BugReportDraft("t", "b", []), asSignedInUser: false));

        Assert.True(store.Get(auto.Id)!.LabRequest.Automatic);
        Assert.Equal(1, AutoFindings.WithdrawQueued(store));
        Assert.Null(store.Get(auto.Id));
        Assert.NotNull(store.Get(byHand.Id));
        Assert.Equal(2, store.Count);
        Assert.Equal(0, AutoFindings.WithdrawQueued(store));
    }

    [Fact]
    public void AQueuedRequestStoredBeforeTheAutomaticMark_ReadsAsByHand()
    {
        var item = new OutboxItem("x", OutboxKind.LabRequest, T0, 0, null, null,
            """{"repo":"o/r","branch":"main","kind":"refresh","name":"G","store":"steam","storeId":"1","gameId":"g","device":"d","appVersion":"1","evidenceParts":["e"]}""");
        Assert.False(item.LabRequest.Automatic);
    }

    [Fact]
    public void TurnedOnText_SaysWhyTheFindingAtHandWasNotSent()
    {
        var skipped = new AutoFindingsResult([], new Dictionary<string, string> { ["g"] = "requested recently" }, []);
        var text = AutoFindings.TurnedOnText("Hades", "g", skipped);
        Assert.Contains("by themselves", text);
        Assert.Contains("Nothing was sent for Hades now: requested recently.", text);

        var sent = new AutoFindingsResult([new AutoFindingFiled("g", "Hades", new AutoFinding(LabKind.Refresh, AutoFindingReason.GameUpdated), 4, "u", false, false)],
            new Dictionary<string, string>(), []);
        Assert.DoesNotContain("Nothing was sent", AutoFindings.TurnedOnText("Hades", "g", sent));
        Assert.Contains("Nothing was sent for Hades now.", AutoFindings.TurnedOnText("Hades", "g", AutoFindingsResult.None));
    }

    [Fact]
    public async Task ARequestWaitingInTheOutbox_IsNeverDuplicated()
    {
        var store = new OutboxStore(_data.Path);
        var info = new GameLabContract.RequestInfo(LabKind.New, "", "Game g", "steam", "g", "desktop", "1.0");
        store.Add(OutboxItem.ForLabRequest("o/r", "main", new PreparedLabRequest(info, [])));
        var queued = AutoFindings.QueuedKeys(store.List());
        Assert.Contains("steam:g", queued);

        var filer = new Filer();
        var r = await Run([Candidate()], filer, T0, queued: queued);
        Assert.Empty(filer.Calls);
        Assert.Contains("outbox", r.Skipped["g"]);
    }

    [Fact]
    public async Task Offline_TheFindingWaitsInTheOutbox_AndIsNotSentAgain()
    {
        var offline = new Filer { Offline = true };
        var r = await Run([Candidate()], offline, T0);
        Assert.True(Assert.Single(r.Filed).Queued);

        var online = new Filer();
        await Run([Candidate()], online, T0.AddHours(1));
        Assert.Empty(online.Calls);
    }

    [Fact]
    public async Task AFailedRequest_IsReported_AndNotRemembered()
    {
        var r = await Run([Candidate()], new Filer { Fail = true }, T0);
        Assert.Single(r.Errors);
        Assert.Empty(r.Filed);
        Assert.Empty(new LabStateStore(_data.Path).Load().AutoRefresh);
    }

    [Fact]
    public async Task FlaggedOrOptedOutGames_AreSkipped()
    {
        var filer = new Filer();
        var flag = GameFlags.Create("the game rewrites its settings", issue: 7, today: T0);
        await Run([Candidate("a", optedOut: true), Candidate("b", Updated("b") with { Flag = flag })], filer, T0);
        Assert.Empty(filer.Calls);
    }

    [Fact]
    public async Task NoFinding_NothingIsSent_AndTheBudgetIsKept()
    {
        var filer = new Filer();
        var budget = new AutoFindingBudget(AutoFindings.MaxPerStart);
        await Run([Candidate(inputs: In() with { Recent = [Run(0), Run(1), Run(2)] })], filer, T0, budget: budget);
        Assert.Empty(filer.Calls);
        Assert.Equal(AutoFindings.MaxPerStart, budget.Remaining);
    }

    [Fact]
    public void LabState_WithoutTheNewFields_StillLoads()
    {
        File.WriteAllText(Path.Combine(_data.Path, "gamelab-state.json"), """{ "requested": { "steam:1": "2026-10-01T00:00:00Z" }, "applied": [3] }""");
        var s = new LabStateStore(_data.Path).Load();
        Assert.Empty(s.AutoRefresh);
        Assert.Empty(s.AutoSyncCheck);
        Assert.Single(s.Requested);
        s.AutoRefresh["G"] = T0;
        Assert.True(s.AutoRefresh.ContainsKey("g")); // case-insensitive like the other maps
    }

    // ---------------------------------------------------------------- the consent card (post-session)

    private static PostSessionCard? Card(HealthInputs i, bool askConsent) =>
        PostSession.For(i, GameHealth.Evaluate(i), i.Recent[^1], "Hades", askConsent: askConsent);

    private static HealthInputs UpdatedAfterSession() =>
        Updated() with { Recent = [Run(-1, build: "steam:1"), Run(0, drifted: 3, build: "steam:2")] }; // a held run on the new build would settle it

    [Fact]
    public void ConsentCard_LeavesTheSessionPending_SoItsKeepTheseStillFollows()
    {
        // two overridden tests in a row (a finding), then a normal --no-prompt session where the game changed most settings
        var session = Run(1, drifted: 8, capture: CaptureAnswer.NotAsked);
        var i = In() with { OverriddenStreak = 2, LastVerify = Run(0, drifted: 3, verify: true), Recent = [session] };
        Assert.Equal(AutoFindingReason.TestsOverridden, AutoFindings.Detect(i)?.Reason);

        var consent = Card(i, askConsent: true);
        Assert.Equal(PostSessionKind.Consent, consent?.Kind);
        Assert.True(PostSession.KeepsSession(consent!.Kind)); // answering it does not dismiss the session

        var next = Card(i, askConsent: false);                 // answered: asked once, so the session's own card shows
        Assert.Equal(PostSessionKind.Changed, next?.Kind);
        Assert.Equal(PostSessionAction.KeepThese, next!.Primary);
        Assert.Equal(PostSessionAction.SeeWhich, next.Secondary);
        Assert.False(PostSession.KeepsSession(next.Kind));
    }

    [Fact]
    public void ConsentCard_AsksOnce_OnTheFirstFinding_WithoutTakingFocus()
    {
        var card = Card(UpdatedAfterSession(), askConsent: true);
        Assert.NotNull(card);
        Assert.Equal(PostSessionKind.Consent, card.Kind);
        Assert.Equal(PostSessionAction.TurnOnAutoFindings, card.Primary);
        Assert.Equal(PostSessionAction.AskLab, card.Secondary);       // or just ask this once
        Assert.Equal(AutoFindings.ConsentQuestion, card.Why);
        Assert.Equal("Turn on", PostSession.ActionText(card.Primary));
        Assert.True(card.CanSnooze);                                    // "Not now"
        Assert.False(card.TakesFocus);                                  // a reflex A press must not turn on network writes
    }

    [Fact]
    public void ConsentCard_NotAskedAgain_ShowsTheUsualCard()
    {
        var card = Card(UpdatedAfterSession(), askConsent: false);
        Assert.Equal(PostSessionKind.Updated, card?.Kind);
    }

    [Fact]
    public void ConsentCard_NeedsAFinding_AndALabThatCanBeAsked()
    {
        var clean = In() with { Recent = [Run(0)] };
        Assert.NotEqual(PostSessionKind.Consent, Card(clean, askConsent: true)?.Kind);

        var open = new LabIssue(12, "t", "u", true, [], LabStatus.Researching, null, null, null);
        Assert.NotEqual(PostSessionKind.Consent, Card(UpdatedAfterSession() with { LabRequest = open }, askConsent: true)?.Kind);
        Assert.NotEqual(PostSessionKind.Consent, Card(UpdatedAfterSession() with { LabReady = false }, askConsent: true)?.Kind);
    }

    [Fact]
    public void ConsentCard_ForASyncFinding_OffersNoRefresh()
    {
        var i = In() with { CloudSync = ChangedWhileClosed(clean: 6), Recent = [Run(0)] };
        var card = Card(i, askConsent: true);
        Assert.Equal(PostSessionKind.Consent, card?.Kind);
        Assert.Equal(PostSessionAction.None, card!.Secondary);
    }
}
