using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.Tests;

/// <summary>
/// The game page lab panel rules (<see cref="LabPanelPlan"/>, which <c>LabGameViewModel</c> reads every visibility from): for every health
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
            VerifyNext = i.HasPresets && i.CanLaunch && !flagged, Play = true, Keep = true,
        };
    }

    private static bool OnStep(HealthAction action, LabJourney journey, PanelOffers offers, PostSessionCard? card) =>
        LabPanelPlan.ForStep(journey.Current, offers, card).All.Any(a => PanelOffers.ToHealth(a) == action) ||
        (LabPanelPlan.ForJourneyAction(journey, offers) is var set && set != LabStepAction.None && PanelOffers.ToHealth(set) == action);

    private static void AssertEachOnce(string name, PanelOffers offers, LabJourney journey, PostSessionCard? card)
    {
        foreach (var action in LabPanelPlan.Actions)
        {
            var place = LabPanelPlan.Place(action, journey, offers, card);
            var places = 0;
            if (OnStep(action, journey, offers, card)) places++;
            if (LabPanelPlan.InTools(action, offers, journey, card)) places++;
            if (LabPanelPlan.CardStaysSeparate(card) && card!.Offers(action)) places++;
            Assert.True(places == (offers.Offers(action) ? 1 : 0), $"{name}: {action} is on screen {places} times ({place})");
            Assert.Equal(offers.Offers(action), place != ActionPlace.Hidden);
        }
    }

    [Theory]
    [MemberData(nameof(States))]
    public void EveryState_ShowsEveryActionOnce(string name, HealthInputs inputs)
    {
        var journey = LabSteps.Build(inputs);
        var offers = OffersFor(inputs, journey.Health);
        AssertEachOnce(name, offers, journey, null);

        // the current step only shows what is on offer
        var buttons = LabPanelPlan.ForStep(journey.Current, offers);
        foreach (var a in buttons.All) Assert.True(offers.Offers(a), $"{name}: {a} is shown but not on offer");
        Assert.DoesNotContain(LabStepAction.None, buttons.Secondary);
    }

    [Theory]
    [MemberData(nameof(States))]
    public void EverySeparateCard_KeepsEachActionOnce(string name, HealthInputs inputs)
    {
        var journey = LabSteps.Build(inputs);
        var offers = OffersFor(inputs, journey.Health);
        var run = Run(5, drifted: 4);
        foreach (var (kind, primary, secondary) in new[]
                 {
                     (PostSessionKind.Consent, PostSessionAction.TurnOnAutoFindings, PostSessionAction.AskLab),
                     (PostSessionKind.Consent, PostSessionAction.TurnOnAutoFindings, PostSessionAction.None),
                     (PostSessionKind.AskedLab, PostSessionAction.OpenIssue, PostSessionAction.None),
                     (PostSessionKind.AskedLab, PostSessionAction.None, PostSessionAction.None),
                 })
        {
            var card = new PostSessionCard { Kind = kind, Run = run, Text = "", Primary = primary, Secondary = secondary };
            Assert.True(LabPanelPlan.CardStaysSeparate(card));
            // a card only offers what the panel could run (its buttons run the panel's commands)
            var cardOffers = new[] { primary, secondary }.Select(PostSession.AsHealthAction).Where(a => a != HealthAction.None);
            if (cardOffers.Any(a => !offers.Offers(a))) continue;
            AssertEachOnce($"{name} + {kind}/{primary}/{secondary}", offers, journey, card);
        }
    }

    [Fact]
    public void OnlyTheConsentQuestionAndTheAskedNote_StayAsACard()
    {
        foreach (var kind in Enum.GetValues<PostSessionKind>())
        {
            var card = new PostSessionCard { Kind = kind, Run = Run(0), Text = "" };
            Assert.Equal(kind is PostSessionKind.Consent or PostSessionKind.AskedLab, LabPanelPlan.CardStaysSeparate(card));
        }
        Assert.False(LabPanelPlan.CardStaysSeparate(null));
    }

    [Fact]
    public void RealCards_FromPostSession_NoControlIsLost()
    {
        var recurring = Run(1, drifted: 10, sec: 120);
        var updatedRun = Run(1, sec: 30);
        var heldRun = Run(1);
        var changedRun = Run(1, drifted: 10, capture: CaptureAnswer.NotAsked);
        var cases = new (string Name, HealthInputs Inputs)[]
        {
            ("recurring", In() with { Recent = [Run(0, drifted: 10, sec: 120), recurring] }),
            ("updated", In() with { Recent = [updatedRun], Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2" }),
            ("needs mapping", In() with { Recent = [heldRun], LabRequest = Issue(LabStatus.NeedsEvidence) }),
            ("changed", In() with { Recent = [changedRun] }),
        };
        foreach (var (name, inputs) in cases)
        {
            var journey = LabSteps.Build(inputs);
            var offers = OffersFor(inputs, journey.Health);
            var card = PostSession.For(inputs, journey.Health, inputs.Recent[^1], "Hades");
            Assert.NotNull(card);
            Assert.False(LabPanelPlan.CardStaysSeparate(card), name);
            AssertEachOnce(name, offers, journey, card);

            // everything the card offered is a button of the checklist now (or the line under "All set")
            var shown = LabPanelPlan.ForStep(journey.Current, offers).All.ToList();
            if (LabPanelPlan.ForJourneyAction(journey, offers) is var footer && footer != LabStepAction.None) shown.Add(footer);
            foreach (var action in new[] { card!.Primary, card.Secondary })
            {
                switch (action)
                {
                    case PostSessionAction.KeepThese: Assert.Contains(LabStepAction.KeepAsManualSave, shown); break;
                    case PostSessionAction.SeeWhich: Assert.True(journey.Current is { IsFix: true }, name); break;
                    case PostSessionAction.FlagSetInGame: Assert.Contains(LabStepAction.FlagSetInGame, shown); break;
                    case PostSessionAction.AskLab: Assert.Contains(LabStepAction.RefreshPresets, shown); break;
                    case PostSessionAction.MapGame: Assert.Contains(LabStepAction.MapThisGame, shown); break;
                }
            }
            // "Not now" / "Don't ask again" live on the fix step
            Assert.Equal(card.Kind == PostSessionKind.Changed, LabPanelPlan.FixStepOwnsCard(journey.Current, card));
        }
    }

    [Fact]
    public void Flag_StepsAside_WhileItsConfirmCardOrATestResultIsOpen()
    {
        var inputs = In() with { OverriddenStreak = 2 };
        var journey = LabSteps.Build(inputs); // the confirm step's main button is "Flag as set in-game"
        var offers = OffersFor(inputs, journey.Health);
        Assert.Equal(LabStepAction.FlagSetInGame, LabPanelPlan.ForStep(journey.Current, offers).Primary);
        Assert.Equal(ActionPlace.Step, LabPanelPlan.Place(HealthAction.FlagSetInGame, journey, offers));

        Assert.Equal(LabStepAction.None, LabPanelPlan.ForStep(journey.Current, offers, flagConfirming: true).Primary);
        Assert.Equal(LabStepAction.None, LabPanelPlan.ForStep(journey.Current, offers, resultOpen: true).Primary);
        Assert.False(LabPanelPlan.InTools(HealthAction.FlagSetInGame, offers, journey, flagConfirming: true));
        Assert.False(LabPanelPlan.InTools(HealthAction.FlagSetInGame, offers, journey, resultOpen: true));
        Assert.Equal(ActionPlace.ResultCard,
            LabPanelPlan.Place(HealthAction.FlagSetInGame, journey, offers, resultOpen: true, resultOverridden: true));
        Assert.Equal(ActionPlace.Hidden, LabPanelPlan.Place(HealthAction.FlagSetInGame, journey, offers, flagConfirming: true));
        // other buttons don't step aside for a result card
        Assert.Contains(LabStepAction.RefreshPresets, LabPanelPlan.ForStep(journey.Current, offers, resultOpen: true).Secondary);
    }

    [Fact]
    public void Place_NamesTheOnePlace()
    {
        var inputs = In() with { Recent = [Run(0, drifted: 4), Run(1, drifted: 4)] }; // the fix step: Map this game (main), Keep my changes
        var journey = LabSteps.Build(inputs);
        var offers = new PanelOffers { Refresh = true, Test = true, Map = true, SyncCheck = true, Keep = true, Play = true };
        Assert.Equal(ActionPlace.Step, LabPanelPlan.Place(HealthAction.MapGame, journey, offers));
        Assert.Equal(ActionPlace.ManualTools, LabPanelPlan.Place(HealthAction.TestPreset, journey, offers));
        Assert.Equal(ActionPlace.ManualTools, LabPanelPlan.Place(HealthAction.RefreshPresets, journey, offers));
        Assert.Equal(ActionPlace.Hidden, LabPanelPlan.Place(HealthAction.RequestPresets, journey, offers));
        var buttons = LabPanelPlan.ForStep(journey.Current, offers);
        Assert.Equal(LabStepAction.MapThisGame, buttons.Primary);
        Assert.Equal([LabStepAction.KeepAsManualSave], buttons.Secondary);

        // a button that is not on offer is dropped, not shown dead
        var none = LabPanelPlan.ForStep(journey.Current, new PanelOffers { Refresh = true });
        Assert.Equal(LabStepAction.None, none.Primary);
        Assert.Empty(none.Secondary);
    }

    [Fact]
    public void SetInGame_ManageAgainIsTheJourneysButton_NotAlsoUnderManualTools()
    {
        var flag = GameFlags.Create("the game rewrites its settings", issue: 7, today: T0);
        var inputs = In() with { Flag = flag, HasUserFlag = true, ResearchFinished = true };
        var journey = LabSteps.Build(inputs);
        var offers = OffersFor(inputs, journey.Health);
        Assert.Equal(LabStepAction.ManageAgain, LabPanelPlan.ForJourneyAction(journey, offers));
        Assert.Equal(ActionPlace.Step, LabPanelPlan.Place(HealthAction.ManageAgain, journey, offers));
        Assert.False(LabPanelPlan.InTools(HealthAction.ManageAgain, offers, journey));

        // lab not done: the same action stays a manual tool
        var open = In() with { Flag = flag, HasUserFlag = true };
        var openJourney = LabSteps.Build(open);
        Assert.Equal(LabStepAction.None, LabPanelPlan.ForJourneyAction(openJourney, OffersFor(open, openJourney.Health)));
        Assert.Equal(ActionPlace.ManualTools, LabPanelPlan.Place(HealthAction.ManageAgain, openJourney, OffersFor(open, openJourney.Health)));
    }

    [Fact]
    public void FixStepOwnsTheSnoozeButtons_OnlyForASnoozableAbsorbedCard()
    {
        var fix = LabSteps.Build(In() with { Recent = [Run(0, drifted: 4), Run(1, drifted: 4)] }).Current;
        var play = LabSteps.Build(In()).Current;
        var changed = new PostSessionCard { Kind = PostSessionKind.Changed, Run = Run(0), Text = "" };
        Assert.True(LabPanelPlan.FixStepOwnsCard(fix, changed));
        Assert.False(LabPanelPlan.FixStepOwnsCard(play, changed));
        Assert.False(LabPanelPlan.FixStepOwnsCard(fix, null));
        Assert.False(LabPanelPlan.FixStepOwnsCard(fix, new PostSessionCard { Kind = PostSessionKind.Held, Run = Run(0), Text = "" })); // nothing to snooze
        Assert.False(LabPanelPlan.FixStepOwnsCard(fix, new PostSessionCard { Kind = PostSessionKind.Consent, Run = Run(0), Text = "" }));
    }

    [Theory]
    // confirmation first, then the result (not while testing), then the focused fix step, then Manual tools
    [InlineData(true, true, false, true, true, CancelStep.CloseConfirmation)]
    [InlineData(false, true, false, true, true, CancelStep.CloseResult)]
    [InlineData(false, true, true, true, true, CancelStep.DismissPostCard)]
    [InlineData(false, false, false, true, true, CancelStep.DismissPostCard)]
    [InlineData(false, false, false, false, true, CancelStep.CollapseTools)]
    [InlineData(false, false, false, false, false, CancelStep.None)]
    public void B_ClosesTheTopLayerFirst(bool confirming, bool result, bool testing, bool postFocused, bool toolsOpen, CancelStep expected)
    {
        Assert.Equal(expected, LabPanelPlan.NextCancel(confirming, result, testing, postFocused, toolsOpen));
    }

    [Fact]
    public void EveryPlacedAction_HasButtonText()
    {
        foreach (var a in LabPanelPlan.Actions) Assert.NotEmpty(GameHealth.ActionText(a));
    }

    [Fact]
    public void EveryStepAction_IsOnOfferOnlyWhenItsCommandIs()
    {
        var offers = new PanelOffers { Request = true, Play = true };
        Assert.True(offers.Offers(LabStepAction.RequestPresets));
        Assert.True(offers.Offers(LabStepAction.Play));
        Assert.False(offers.Offers(LabStepAction.KeepAsManualSave));
        Assert.False(offers.Offers(LabStepAction.MapThisGame));
        Assert.False(offers.Offers(LabStepAction.None));
        Assert.Equal(HealthAction.SyncCheck, PanelOffers.ToHealth(LabStepAction.RecheckSync));
        Assert.Equal(HealthAction.None, PanelOffers.ToHealth(LabStepAction.Play));
    }
}
