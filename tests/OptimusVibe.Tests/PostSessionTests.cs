using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.Tests;

/// <summary>
/// The post-session card (<see cref="PostSession"/>): which sessions are new (<see cref="PostSession.Detect"/>, each offered once), and which card a
/// finished session brings up (<see cref="PostSession.For"/>: needs mapping > recurring > updated > changed > first held), plus the library
/// tile's chip (<see cref="GameHealth.TileChip"/>) and the state the card keeps (seen marks, snoozes).
/// </summary>
public sealed class PostSessionTests
{
    private const string Preset = "recommended:performance";
    private const string Name = "Hades";
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static HealthInputs In(params RunResult[] recent) => new()
    {
        GameId = "g", Device = "desktop", HasConfig = true, HasPresets = true, LabReady = true, CanLaunch = true, CurrentPreset = Preset,
        Recent = recent,
    };

    private static RunResult Run(int day, int drifted = 0, CaptureAnswer? capture = null, int? sec = 1800, string preset = Preset, string? build = null,
        bool verify = false)
    {
        var r = new RunResult
        {
            Utc = T0.AddDays(day), Game = "g", Device = "desktop", Preset = preset, Mode = verify ? RunResult.VerifyMode : RunResult.NormalMode,
            AppliedKeys = 14, ProcessSeen = true, DurationSec = verify ? null : sec, Build = build, UserChanged = verify ? false : null,
            Capture = verify ? null : capture ?? (drifted > 0 ? CaptureAnswer.DontSave : CaptureAnswer.NotAsked),
        };
        for (var i = 0; i < drifted; i++) r.Drift.Add(new RunDrift { FileId = "gfx", Key = $"Quality{i}", Applied = "High", Now = "Low" });
        return r;
    }

    /// <summary>Overridden on its own: most of the preset drifted in a short session the player chose not to keep.</summary>
    private static RunResult Overridden(int day) => Run(day, drifted: 10, sec: 120);

    private static LabIssue Issue(LabStatus status) =>
        new(12, "Request presets: Hades", "https://github.com/owner/config/issues/12", true, [], status, null, null, null);

    private static PostSessionCard? Card(HealthInputs i, RunResult run, IReadOnlySet<PostSessionKind>? snoozed = null) =>
        PostSession.For(i, GameHealth.Evaluate(i), run, Name, snoozed);

    // ---------------------------------------------------------------- which file is a session signal

    [Theory]
    [InlineData(@"C:\data\run-results.json", true)]
    [InlineData(@"C:\data\RUN-RESULTS.JSON", true)]
    [InlineData(@"C:\data\state-hades-1234.json", false)]
    [InlineData(@"C:\data\state.json", false)]
    [InlineData(@"C:\data\run-results.json.4321.0123abcd.tmp", false)]
    [InlineData(@"C:\data\gamelab-state.json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyRunResultsJson_SignalsASession(string? path, bool signal)
    {
        Assert.Equal(signal, PostSession.IsSessionSignal(path));
    }

    // ---------------------------------------------------------------- detection

    private static RunResultEntry Entry(params RunResult[] recent) => new() { Recent = [.. recent], Last = recent.LastOrDefault() };

    [Fact]
    public void NewestFinished_WaitsForTheCaptureAnswer_AndIgnoresTests()
    {
        var pending = Run(1, drifted: 3);
        pending.Capture = null;
        Assert.Null(PostSession.NewestFinished(Entry(Run(0), pending)));
        Assert.Null(PostSession.NewestFinished(new RunResultEntry { LastVerify = Run(2, verify: true), Last = Run(2, verify: true) }));
        Assert.Null(PostSession.NewestFinished(null));
        var done = Run(1);
        Assert.Same(done, PostSession.NewestFinished(Entry(Run(0), done)));
    }

    [Fact]
    public void Detect_BeforeTheFirstLook_OnlyMarksSessionsSeen()
    {
        var entries = new Dictionary<string, RunResultEntry> { ["g"] = Entry(Run(0)), ["h"] = Entry(Run(1)) };
        var d = PostSession.Detect(entries, new Dictionary<string, DateTime>(), baselined: false);
        Assert.Empty(d.Fresh);
        Assert.Equal(T0, d.Seen["g"]);
        Assert.Equal(T0.AddDays(1), d.Seen["h"]);
        Assert.True(d.Changed);
    }

    [Fact]
    public void Detect_OffersEachNewSessionOnce()
    {
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { ["g"] = T0, ["old"] = T0.AddDays(3) };
        var newer = Run(2);
        var entries = new Dictionary<string, RunResultEntry>
        {
            ["g"] = Entry(Run(0), newer),        // newer than its mark
            ["old"] = Entry(Run(3)),             // already seen
            ["first"] = Entry(Run(1)),           // never marked: its first session counts
        };
        var d = PostSession.Detect(entries, seen, baselined: true);
        Assert.Equal(2, d.Fresh.Count);
        Assert.Contains(newer, d.Fresh);
        Assert.Equal(T0.AddDays(2), d.Seen["g"]);
        Assert.False(d.Seen.ContainsKey("old"));

        // the next look with the marks stored finds nothing
        var again = PostSession.Detect(entries, d.Seen.Concat(seen.Where(s => !d.Seen.ContainsKey(s.Key))).ToDictionary(), baselined: true);
        Assert.Empty(again.Fresh);
        Assert.False(again.Changed);
    }

    [Fact]
    public void Detect_APendingSession_IsNeitherOfferedNorMarked()
    {
        var pending = Run(2, drifted: 3);
        pending.Capture = null; // the capture prompt is still open
        var d = PostSession.Detect(new Dictionary<string, RunResultEntry> { ["g"] = Entry(Run(0), pending) }, new Dictionary<string, DateTime> { ["g"] = T0 }, true);
        Assert.Empty(d.Fresh);
        Assert.False(d.Changed);
    }

    // ---------------------------------------------------------------- held

    [Fact]
    public void Held_TheFirstTime_SaysTheGameKeptEverything_WithoutTakingFocus()
    {
        var run = Run(0);
        var card = Card(In(run), run);
        Assert.NotNull(card);
        Assert.Equal(PostSessionKind.Held, card!.Kind);
        Assert.Equal("Hades kept all 14 settings.", card.Text);
        Assert.Equal(PostSessionAction.None, card.Primary);
        Assert.False(card.TakesFocus);
        Assert.False(card.CanSnooze);
    }

    [Fact]
    public void Held_Again_ShowsNothing()
    {
        var run = Run(1);
        Assert.Null(Card(In(Run(0), run), run));
    }

    [Fact]
    public void Held_AfterNewPresets_OrANewBuild_IsTheFirstTimeAgain()
    {
        var run = Run(5, build: "steam:2");
        // presets arrived after the earlier held launch
        Assert.Equal(PostSessionKind.Held,
            Card(In(Run(0, build: "steam:2"), run) with { Presets = new PresetsMark { MadeUtc = T0.AddDays(3) } }, run)?.Kind);
        // the earlier held launch was on another build
        Assert.Equal(PostSessionKind.Held, Card(In(Run(0, build: "steam:1"), run), run)?.Kind);
        // a held test of this build counts as known to work
        Assert.Null(Card(In(run) with { LastVerify = Run(1, build: "steam:2", verify: true) }, run));
    }

    [Fact]
    public void Held_TooShortToTell_ShowsNothing()
    {
        var run = Run(0, sec: 20);
        Assert.Null(Card(In(run), run));
    }

    // ---------------------------------------------------------------- changed

    [Fact]
    public void Changed_NotAsked_OffersKeepThese_AndSeeWhich()
    {
        var run = Run(0, drifted: 10, capture: CaptureAnswer.Off);
        var card = Card(In(run), run)!;
        Assert.Equal(PostSessionKind.Changed, card.Kind);
        Assert.StartsWith("Last session the game changed 10 of 14 settings (Quality0, Quality1 and 8 more)", card.Text);
        Assert.Equal(PostSessionAction.KeepThese, card.Primary);
        Assert.Equal(PostSessionAction.SeeWhich, card.Secondary);
        Assert.True(card.TakesFocus);
        Assert.True(card.CanSnooze);
    }

    [Fact]
    public void Changed_AfterTheyAnsweredDontSave_OnlyShowsWhich()
    {
        var run = Run(0, drifted: 10, capture: CaptureAnswer.DontSave); // long session, no recurrence: not overridden, but most of it drifted
        var card = Card(In(run), run)!;
        Assert.Equal(PostSessionKind.Changed, card.Kind);
        Assert.Equal(PostSessionAction.SeeWhich, card.Primary);
        Assert.Equal(PostSessionAction.None, card.Secondary);
    }

    [Fact]
    public void Changed_KeptAsASave_OrAFewTweaks_ShowNothing()
    {
        var saved = Run(0, drifted: 10, capture: CaptureAnswer.Saved);
        Assert.Null(Card(In(saved), saved));
        var tweak = Run(0, drifted: 2, capture: CaptureAnswer.Off); // 2 of 14, no pattern: the player's own, most likely
        Assert.Null(Card(In(tweak), tweak));
    }

    // ---------------------------------------------------------------- recurring

    [Fact]
    public void Recurring_SuggestsTheFlag_AndTheLab()
    {
        var run = Overridden(1);
        var i = In(Overridden(0), run);
        var card = Card(i, run)!;
        Assert.Equal(PostSessionKind.Recurring, card.Kind);
        Assert.Contains("after your last 2 sessions", card.Text);
        Assert.Equal(PostSessionAction.FlagSetInGame, card.Primary);
        Assert.Equal(PostSessionAction.AskLab, card.Secondary);
        Assert.Equal(HealthAction.FlagSetInGame, PostSession.AsHealthAction(card.Primary));
    }

    [Fact]
    public void Recurring_WithAnOpenRequest_DoesNotOfferTheLabAgain()
    {
        var run = Overridden(1);
        var card = Card(In(Overridden(0), run) with { LabRequest = Issue(LabStatus.Researching) }, run)!;
        Assert.Equal(PostSessionKind.Recurring, card.Kind);
        Assert.Equal(PostSessionAction.None, card.Secondary);
    }

    [Fact]
    public void OneOverriddenSession_IsAChange_NotYetRecurring()
    {
        var run = Overridden(1);
        Assert.Equal(PostSessionKind.Changed, Card(In(Run(0), run), run)?.Kind);
    }

    // ---------------------------------------------------------------- updated

    [Fact]
    public void Updated_AsksTheLabToReCheck()
    {
        var run = Run(1, sec: 30); // too short to move the baseline
        var i = In(run) with { Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:100" }, CurrentBuild = "steam:200" };
        var card = Card(i, run)!;
        Assert.Equal(PostSessionKind.Updated, card.Kind);
        Assert.Equal("Hades updated since these presets were made.", card.Text);
        Assert.Equal(PostSessionAction.AskLab, card.Primary);
        Assert.Equal("Ask the game lab to re-check", PostSession.ActionText(card.Primary));
    }

    [Fact]
    public void Updated_WithoutTheLab_ShowsNoUpdateCard()
    {
        var run = Run(1, sec: 30);
        var i = In(run) with { Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:100" }, CurrentBuild = "steam:200", LabReady = false };
        Assert.Null(Card(i, run));
    }

    // ---------------------------------------------------------------- needs mapping

    [Fact]
    public void LabNeedsEvidence_AndNothingMapped_AsksToMapIt()
    {
        var run = Run(0);
        var card = Card(In(run) with { HasPresets = false, LabRequest = Issue(LabStatus.NeedsEvidence) }, run)!;
        Assert.Equal(PostSessionKind.NeedsMapping, card.Kind);
        Assert.Equal("The lab needs to know where Hades keeps its settings.", card.Text);
        Assert.Equal(PostSessionAction.MapGame, card.Primary);
        Assert.Equal("Map it", PostSession.ActionText(card.Primary));
    }

    [Fact]
    public void LabNeedsEvidence_ButMappedAlready_OrNotLaunchable_ShowsNothing()
    {
        var run = Run(0);
        var i = In(run) with { HasPresets = false, LabRequest = Issue(LabStatus.NeedsEvidence) };
        Assert.Null(Card(i with { Mapping = new ProbeRecord { Utc = T0 } }, run));
        Assert.Null(Card(i with { CanLaunch = false }, run));
    }

    // ---------------------------------------------------------------- never

    [Fact]
    public void AFlaggedGame_GetsNoCard()
    {
        var run = Overridden(1);
        var i = In(Overridden(0), run) with { Flag = GameFlags.Create("binary settings", today: T0), HasUserFlag = true };
        Assert.Null(Card(i, run));
    }

    [Fact]
    public void ARunOfAnotherPreset_GetsNoCard()
    {
        var run = Run(0, drifted: 10, capture: CaptureAnswer.Off, preset: "recommended:quality");
        Assert.Null(Card(In(run), run));
    }

    [Fact]
    public void ASnoozedKind_StaysAway_OthersStillShow()
    {
        var run = Run(0, drifted: 10, capture: CaptureAnswer.Off);
        Assert.Null(Card(In(run), run, new HashSet<PostSessionKind> { PostSessionKind.Changed }));
        Assert.NotNull(Card(In(run), run, new HashSet<PostSessionKind> { PostSessionKind.Recurring }));
    }

    [Fact]
    public void TheCardOrder_NeedsMappingFirst()
    {
        // overridden twice and the lab asks for evidence: mapping helps the lab most
        var run = Overridden(1);
        var card = Card(In(Overridden(0), run) with { LabRequest = Issue(LabStatus.NeedsEvidence) }, run)!;
        Assert.Equal(PostSessionKind.NeedsMapping, card.Kind);
    }

    // ---------------------------------------------------------------- library tile chip

    [Fact]
    public void TileChip_OnlyForAttentionStates()
    {
        GameHealth H(HealthState s) => new() { State = s, Headline = "" };
        Assert.Equal("Game updated", H(HealthState.GameUpdated).TileChip);
        Assert.Equal("Changed by game", H(HealthState.GameChangedSettings).TileChip);
        Assert.Equal("Changed by game", H(HealthState.CantBeSetFromOutside).TileChip);
        foreach (var s in new[] { HealthState.Flagged, HealthState.NoPresets, HealthState.SyncUnsure, HealthState.Working, HealthState.NotChecked })
            Assert.Null(H(s).TileChip);
    }

    [Fact]
    public void TileChip_FromRealInputs()
    {
        var changed = Run(0, drifted: 10, capture: CaptureAnswer.Off);
        Assert.Equal(GameHealth.ChangedByGameChip, GameHealth.Evaluate(In(changed)).TileChip);
        var updated = In(Run(0, sec: 30)) with { Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2" };
        Assert.Equal(GameHealth.GameUpdatedChip, GameHealth.Evaluate(updated).TileChip);
        Assert.Null(GameHealth.Evaluate(In(Run(0), Run(1), Run(2))).TileChip); // working
    }

    // ---------------------------------------------------------------- stored state

    [Fact]
    public void LabState_KeepsSeenMarks_AndSnoozes_WhichExpire()
    {
        using var tmp = new TempDir();
        var store = new LabStateStore(tmp.Path);
        store.Update(s =>
        {
            s.SessionsSeen["Hades"] = T0;
            s.SessionsBaselined = true;
            s.Snoozed[PostSession.SnoozeKey("hades", PostSessionKind.Changed)] = T0.AddDays(PostSession.SnoozeDays);
        });
        var back = store.Load();
        Assert.True(back.SessionsBaselined);
        Assert.Equal(T0, back.SessionsSeen["hades"]); // ids match case-insensitively
        Assert.Equal([PostSessionKind.Changed], back.SnoozedFor("HADES", T0.AddDays(1)));
        Assert.Empty(back.SnoozedFor("hades", T0.AddDays(PostSession.SnoozeDays + 1)));
    }

    [Fact]
    public void LabState_AnOlderFile_LoadsWithoutTheNewFields()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "gamelab-state.json"), """{ "requested": {}, "applied": [3], "baselined": true }""");
        var s = new LabStateStore(tmp.Path).Load();
        Assert.False(s.SessionsBaselined);
        Assert.Empty(s.SessionsSeen);
        Assert.Empty(s.SnoozedFor("g", T0));
        Assert.Equal([3], s.Applied);
    }

    [Fact]
    public void HealthInputs_FromContext_ReadsWhatTheTileNeeds()
    {
        using var f = new EngineFixture();
        var ctx = f.Load("desktop");
        var run = Run(0);
        run.Game = EngineFixture.GameId;
        run.Preset = ctx.Preset!.ToString();
        RunResults.Record(f.Paths.DataDir, run);
        new LabStateStore(f.Paths.DataDir).MarkPresets(EngineFixture.GameId, T0, "steam:7");

        var i = HealthInputs.FromContext(ctx);
        Assert.True(i.HasConfig);
        Assert.True(i.HasPresets);
        Assert.Null(i.Flag);
        Assert.Equal(ctx.Preset.ToString(), i.CurrentPreset);
        Assert.Single(i.Recent);
        Assert.Equal(3, i.ConfiguredFiles.Count);
        Assert.Equal("steam:7", i.Presets?.BuildId);

        // a device without presets
        var laptop = HealthInputs.FromContext(f.Load("laptop"));
        Assert.False(laptop.HasPresets);
        Assert.Null(laptop.CurrentPreset);
        Assert.Equal(HealthState.NoPresets, GameHealth.Evaluate(laptop).State);
    }

    [Fact]
    public void RunResults_Entries_ReadsEveryGameOfOneDevice()
    {
        using var tmp = new TempDir();
        RunResults.Record(tmp.Path, Run(0));
        var other = Run(1);
        other.Game = "h";
        RunResults.Record(tmp.Path, other);
        var ally = Run(2);
        ally.Device = "ally-x";
        RunResults.Record(tmp.Path, ally);

        var desktop = RunResults.Entries(tmp.Path, "desktop");
        Assert.Equal(["g", "h"], desktop.Keys.Order());
        Assert.Single(desktop["g"].Recent);
        Assert.Equal(["g"], RunResults.Entries(tmp.Path, "ally-x").Keys);
        Assert.Empty(RunResults.Entries(Path.Combine(tmp.Path, "none"), "desktop"));
    }
}
