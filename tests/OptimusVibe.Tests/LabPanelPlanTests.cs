using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.Tests;

/// <summary>
/// The game page's lab panel rules (<see cref="LabPanelPlan"/>, which <c>LabGameViewModel</c> reads every visibility from): for every health
/// state and every post-session card kind, at most one suggested action, and every action on offer on screen exactly once (suggested, under
/// More, or on the post-session card); B closes the top layer first.
/// </summary>
public sealed class LabPanelPlanTests
{
    private const string Preset = "recommended:performance";
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static HealthInputs In() => new()
    {
        GameId = "g", Device = "desktop", HasConfig = true, HasPresets = true, LabReady = true, CanLaunch = true, CurrentPreset = Preset,
    };

    private static RunResult Run(int day, int drifted = 0, bool verify = false, CaptureAnswer? capture = null, int? sec = 1800)
    {
        var r = new RunResult
        {
            Utc = T0.AddDays(day), Game = "g", Device = "desktop", Preset = Preset, Mode = verify ? RunResult.VerifyMode : RunResult.NormalMode,
            AppliedKeys = 10, ProcessSeen = true, DurationSec = verify ? null : sec, UserChanged = verify ? false : null,
            Capture = verify ? null : capture ?? (drifted > 0 ? CaptureAnswer.DontSave : CaptureAnswer.NotAsked),
        };
        for (var i = 0; i < drifted; i++) r.Drift.Add(new RunDrift { FileId = "gfx", Key = $"Q{i}", Applied = "High", Now = "Low" });
        return r;
    }

    private static LabIssue Issue(LabStatus status) => new(12, "Game", "https://github.com/o/c/issues/12", true, [], status, null, null, null);

    /// <summary>One set of inputs per health state (and the lab-request variants), named for the failure message.</summary>
    public static IEnumerable<object[]> States()
    {
        var flag = GameFlags.Create("the game rewrites its settings", issue: 7, today: T0);
        yield return ["flagged by the user", In() with { Flag = flag, HasUserFlag = true }];
        yield return ["flagged, research done", In() with { Flag = flag, HasUserFlag = true, ResearchFinished = true }];
        yield return ["flagged by the config", In() with { Flag = flag, FlagFromConfig = true }];
        yield return ["no presets", In() with { HasConfig = false, HasPresets = false }];
        yield return ["no presets, lab needs evidence", In() with { HasPresets = false, LabRequest = Issue(LabStatus.NeedsEvidence) }];
        yield return ["can't be set from outside", In() with { OverriddenStreak = 2 }];
        yield return ["game updated", In() with { Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2" }];
        yield return ["game changed settings (launch)", In() with { Recent = [Run(0, drifted: 4), Run(1, drifted: 4)] }];
        yield return ["game changed settings (test)", In() with { LastVerify = Run(0, drifted: 3, verify: true) }];
        yield return ["sync unsure", In() with
        {
            CloudSync = new CloudSyncReport { GameId = "g", Device = "desktop", HasExternalChanges = true, Confidence = SyncConfidence.Low },
        }];
        yield return ["working", In() with { Recent = [Run(0), Run(1), Run(2)] }];
        yield return ["not checked", In() with { Recent = [Run(0)] }];
        yield return ["not checked, unclear", In() with { Recent = [Run(0, sec: 20), Run(1, sec: 20), Run(2, sec: 20)] }];
        yield return ["not checked, check on next launch on", In() with { Recent = [Run(0, sec: 20), Run(1, sec: 20), Run(2, sec: 20)], VerifyNextLaunch = true }];
        yield return ["working, lab needs evidence", In() with { Recent = [Run(0), Run(1), Run(2)], LabRequest = Issue(LabStatus.NeedsEvidence) }];
    }

    /// <summary>What the panel offers for these inputs, as <c>LabGameViewModel</c> works it out.</summary>
    private static PanelOffers OffersFor(HealthInputs i, GameHealth h)
    {
        var flagged = i.Flag is not null;
        return new PanelOffers
        {
            Request = !i.HasPresets || !i.HasConfig, Refresh = i.HasConfig && i.HasPresets, SyncCheck = i.HasConfig,
            Test = i.HasPresets && i.CanLaunch && !flagged, Map = i.CanLaunch, ManageAgain = i.HasUserFlag, Flag = h.FlagReason is not null,
            VerifyNext = i.HasPresets && i.CanLaunch && !flagged,
        };
    }

    private static void AssertEachOnce(string name, PanelOffers offers, GameHealth h, PostSessionCard? card)
    {
        var suggested = LabPanelPlan.Suggested(h.Suggested, offers);
        var showSuggested = LabPanelPlan.ShowSuggested(suggested, card, false, false);
        foreach (var action in LabPanelPlan.Actions)
        {
            var places = 0;
            if (showSuggested && suggested == action) places++;
            if (LabPanelPlan.InMore(action, offers, suggested, card)) places++;
            if (card?.Offers(action) == true) places++;
            Assert.True(places == (offers.Offers(action) ? 1 : 0), $"{name}: {action} is on screen {places} times");
        }
    }

    [Theory]
    [MemberData(nameof(States))]
    public void EveryState_SuggestsAtMostOne_AndShowsEveryActionOnce(string name, HealthInputs inputs)
    {
        var h = GameHealth.Evaluate(inputs);
        var offers = OffersFor(inputs, h);
        var suggested = LabPanelPlan.Suggested(h.Suggested, offers);
        // the suggestion is the health's own when its command is on offer, otherwise none
        Assert.True(suggested == HealthAction.None || suggested == h.Suggested, name);
        if (h.Suggested != HealthAction.None) Assert.True(offers.Offers(h.Suggested), $"{name}: suggests {h.Suggested}, which the panel doesn't offer");
        AssertEachOnce(name, offers, h, null);
    }

    [Theory]
    [MemberData(nameof(States))]
    public void EveryCardKind_KeepsEachActionOnce(string name, HealthInputs inputs)
    {
        var h = GameHealth.Evaluate(inputs);
        var offers = OffersFor(inputs, h);
        var run = Run(5, drifted: 4);
        foreach (var (primary, secondary) in new[]
                 {
                     (PostSessionAction.MapGame, PostSessionAction.None),
                     (PostSessionAction.TurnOnAutoFindings, PostSessionAction.AskLab),
                     (PostSessionAction.FlagSetInGame, PostSessionAction.AskLab),
                     (PostSessionAction.AskLab, PostSessionAction.None),
                     (PostSessionAction.KeepThese, PostSessionAction.SeeWhich),
                     (PostSessionAction.OpenIssue, PostSessionAction.None),
                     (PostSessionAction.None, PostSessionAction.None),
                 })
        {
            var card = new PostSessionCard { Kind = PostSessionKind.Changed, Run = run, Text = "", Primary = primary, Secondary = secondary };
            // a card only offers what the panel could run (its buttons run the panel's commands)
            var cardOffers = new[] { primary, secondary }.Select(PostSession.AsHealthAction).Where(a => a != HealthAction.None);
            if (cardOffers.Any(a => !offers.Offers(a))) continue;
            AssertEachOnce($"{name} + {primary}/{secondary}", offers, h, card);
        }
    }

    [Fact]
    public void RealCards_FromPostSession_KeepEachActionOnce()
    {
        var recurring = Run(1, drifted: 10, sec: 120);
        var cases = new (string Name, HealthInputs Inputs, RunResult Run)[]
        {
            ("recurring", In() with { Recent = [Run(0, drifted: 10, sec: 120), recurring] }, recurring),
            ("updated", In() with { Recent = [Run(1, sec: 30)], Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2" }, Run(1, sec: 30)),
            ("needs mapping", In() with { Recent = [Run(1)], LabRequest = Issue(LabStatus.NeedsEvidence) }, Run(1)),
            ("changed", In() with { Recent = [Run(1, drifted: 10, capture: CaptureAnswer.NotAsked)] }, Run(1, drifted: 10, capture: CaptureAnswer.NotAsked)),
        };
        foreach (var (name, inputs, run) in cases)
        {
            var h = GameHealth.Evaluate(inputs);
            var card = PostSession.For(inputs, h, inputs.Recent.Count > 0 ? inputs.Recent[^1] : run, "Hades");
            Assert.NotNull(card);
            AssertEachOnce(name, OffersFor(inputs, h), h, card);
        }
    }

    [Fact]
    public void Flag_StepsAside_WhileItsConfirmCardOrATestResultIsOpen()
    {
        var offers = new PanelOffers { Flag = true, Test = true, Refresh = true };
        Assert.False(LabPanelPlan.ShowSuggested(HealthAction.FlagSetInGame, null, flagConfirming: true, resultOpen: false));
        Assert.False(LabPanelPlan.ShowSuggested(HealthAction.FlagSetInGame, null, flagConfirming: false, resultOpen: true));
        Assert.False(LabPanelPlan.InMore(HealthAction.FlagSetInGame, offers, HealthAction.RefreshPresets, null, flagConfirming: true));
        Assert.Equal(ActionPlace.ResultCard,
            LabPanelPlan.Place(HealthAction.FlagSetInGame, offers, HealthAction.FlagSetInGame, null, resultOpen: true, resultOverridden: true));
        Assert.Equal(ActionPlace.Hidden,
            LabPanelPlan.Place(HealthAction.FlagSetInGame, offers, HealthAction.FlagSetInGame, null, flagConfirming: true));
        // other suggestions don't step aside for a result card
        Assert.True(LabPanelPlan.ShowSuggested(HealthAction.RefreshPresets, null, false, resultOpen: true));
    }

    [Fact]
    public void Place_NamesTheOnePlace()
    {
        var offers = new PanelOffers { Refresh = true, Test = true, Map = true, SyncCheck = true };
        Assert.Equal(ActionPlace.Suggested, LabPanelPlan.Place(HealthAction.RefreshPresets, offers, HealthAction.RefreshPresets, null));
        Assert.Equal(ActionPlace.More, LabPanelPlan.Place(HealthAction.TestPreset, offers, HealthAction.RefreshPresets, null));
        Assert.Equal(ActionPlace.Hidden, LabPanelPlan.Place(HealthAction.RequestPresets, offers, HealthAction.RefreshPresets, null));
        var card = new PostSessionCard { Kind = PostSessionKind.Updated, Run = Run(0), Text = "", Primary = PostSessionAction.AskLab };
        Assert.Equal(ActionPlace.PostCard, LabPanelPlan.Place(HealthAction.RefreshPresets, offers, HealthAction.RefreshPresets, card));
        // a suggestion that isn't on offer is none
        Assert.Equal(HealthAction.None, LabPanelPlan.Suggested(HealthAction.ManageAgain, offers));
    }

    [Theory]
    // confirmation first, then the result (not while testing), then the focused post card, then More
    [InlineData(true, true, false, true, true, CancelStep.CloseConfirmation)]
    [InlineData(false, true, false, true, true, CancelStep.CloseResult)]
    [InlineData(false, true, true, true, true, CancelStep.DismissPostCard)]
    [InlineData(false, false, false, true, true, CancelStep.DismissPostCard)]
    [InlineData(false, false, false, false, true, CancelStep.CollapseMore)]
    [InlineData(false, false, false, false, false, CancelStep.None)]
    public void B_ClosesTheTopLayerFirst(bool confirming, bool result, bool testing, bool postFocused, bool moreOpen, CancelStep expected)
    {
        Assert.Equal(expected, LabPanelPlan.NextCancel(confirming, result, testing, postFocused, moreOpen));
    }

    [Fact]
    public void EveryPlacedAction_HasButtonText()
    {
        foreach (var a in LabPanelPlan.Actions) Assert.NotEmpty(GameHealth.ActionText(a));
    }
}
