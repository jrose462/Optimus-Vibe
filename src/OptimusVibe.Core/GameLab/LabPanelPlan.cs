namespace OptimusVibe.GameLab;

/// <summary>Which of the game page's lab actions are on offer for a game right now (its command could run there; the panel shows it once).</summary>
public sealed record PanelOffers
{
    public bool Request { get; init; }
    public bool Refresh { get; init; }
    public bool SyncCheck { get; init; }
    public bool Test { get; init; }
    public bool Map { get; init; }
    public bool ManageAgain { get; init; }

    /// <summary>"Flag as set in-game": the evidence suggests it (<see cref="GameHealth.FlagReason"/>); its command can't run otherwise.</summary>
    public bool Flag { get; init; }

    /// <summary>"Check presets on my next launch" can be turned on or off (presets, installed here, not flagged).</summary>
    public bool VerifyNext { get; init; }

    /// <summary>The game page's Play can be started from the panel (the host gave it a Play command).</summary>
    public bool Play { get; init; }

    /// <summary>"Keep my changes as a manual save": the game's settings can be saved (a session is configured here).</summary>
    public bool Keep { get; init; }

    public bool Offers(HealthAction action) => action switch
    {
        HealthAction.RequestPresets => Request,
        HealthAction.RefreshPresets => Refresh,
        HealthAction.SyncCheck => SyncCheck,
        HealthAction.TestPreset => Test,
        HealthAction.MapGame => Map,
        HealthAction.ManageAgain => ManageAgain,
        HealthAction.FlagSetInGame => Flag,
        HealthAction.VerifyNextLaunch => VerifyNext,
        _ => false,
    };

    public bool Offers(LabStepAction action) => action switch
    {
        LabStepAction.Play => Play,
        LabStepAction.KeepAsManualSave => Keep,
        _ => ToHealth(action) is var h && h != HealthAction.None && Offers(h),
    };

    /// <summary>The health action a step action runs; none for Play and "Keep my changes", which the manual tools do not list.</summary>
    public static HealthAction ToHealth(LabStepAction action) => action switch
    {
        LabStepAction.RequestPresets => HealthAction.RequestPresets,
        LabStepAction.MapThisGame => HealthAction.MapGame,
        LabStepAction.RefreshPresets => HealthAction.RefreshPresets,
        LabStepAction.RecheckSync => HealthAction.SyncCheck,
        LabStepAction.FlagSetInGame => HealthAction.FlagSetInGame,
        LabStepAction.ManageAgain => HealthAction.ManageAgain,
        _ => HealthAction.None,
    };
}

/// <summary>Where one lab action sits on the game page.</summary>
public enum ActionPlace
{
    /// <summary>Not on offer for this game, or its own card is open (a confirmation, the test result's own button).</summary>
    Hidden,

    /// <summary>A button of the current step of the checklist.</summary>
    Step,

    /// <summary>A row under "Manual tools", with its caption.</summary>
    ManualTools,

    /// <summary>A button of the separate post-session card (the panel's own button steps aside while the card is open).</summary>
    PostCard,

    /// <summary>A button of the open Test preset result card ("Flag as set in-game" after an overridden test).</summary>
    ResultCard,
}

/// <summary>What B (Esc) closes next on the game page's lab panel: the top layer only.</summary>
public enum CancelStep
{
    None,

    /// <summary>A confirmation card (a request, the test, flag, forget-copy, or "Keep my changes" asking on the fix step).</summary>
    CloseConfirmation,

    /// <summary>The Test preset result card underneath (not while the test is still running).</summary>
    CloseResult,

    /// <summary>"Not now" on the fix step or the post-session card, only while focus is inside it.</summary>
    DismissPostCard,

    /// <summary>The open "Manual tools" list collapses (focus back on its button).</summary>
    CollapseTools,
}

/// <summary>The buttons the current step shows once the panel dropped what is not on offer or already on screen elsewhere.</summary>
public sealed record StepButtons(LabStepAction Primary, IReadOnlyList<LabStepAction> Secondary)
{
    public static readonly StepButtons None = new(LabStepAction.None, []);
    public IEnumerable<LabStepAction> All => Primary == LabStepAction.None ? Secondary : Secondary.Prepend(Primary);
}

/// <summary>
/// The lab panel's placement rules, kept apart from WPF so they can be tested: the checklist's current step shows its buttons (those on
/// offer), every other action on offer sits exactly once under "Manual tools" (or on the separate post-session card while that offers it),
/// and B closes the top layer first. <c>LabGameViewModel</c> reads every visibility from here.
/// </summary>
public static class LabPanelPlan
{
    /// <summary>Every action the panel can place under "Manual tools", in the order it lists them.</summary>
    public static readonly IReadOnlyList<HealthAction> Actions =
    [
        HealthAction.RequestPresets, HealthAction.RefreshPresets, HealthAction.SyncCheck, HealthAction.TestPreset, HealthAction.VerifyNextLaunch,
        HealthAction.MapGame, HealthAction.ManageAgain, HealthAction.FlagSetInGame,
    ];

    /// <summary>
    /// Only the consent question and the "asked the lab" note still show as a post-session card; what every other card said is the checklist's
    /// job (the fix step shows the result, its buttons are the card's: Keep these, See which, Not now, Don't ask again).
    /// </summary>
    public static bool CardStaysSeparate(PostSessionCard? card) => card?.Kind is PostSessionKind.Consent or PostSessionKind.AskedLab;

    /// <summary>"Not now" and "Don't ask again" show on the fix step while a session card of the game is pending that can be snoozed.</summary>
    public static bool FixStepOwnsCard(LabStep? current, PostSessionCard? card) =>
        current is { IsFix: true } && card is { CanSnooze: true } && !CardStaysSeparate(card);

    /// <summary>
    /// The current step's buttons on offer. "Flag as set in-game" steps aside while its confirm card or a test result (which has its own) is
    /// open, and any action the separate post-session card offers shows there instead.
    /// </summary>
    public static StepButtons ForStep(LabStep? step, PanelOffers offers, PostSessionCard? card = null, bool flagConfirming = false, bool resultOpen = false)
    {
        if (step is null) return StepButtons.None;
        bool Shown(LabStepAction a) => a != LabStepAction.None && offers.Offers(a) &&
                                       !(a == LabStepAction.FlagSetInGame && (flagConfirming || resultOpen)) &&
                                       !(CardStaysSeparate(card) && card!.Offers(PanelOffers.ToHealth(a)));
        return new StepButtons(Shown(step.Primary) ? step.Primary : LabStepAction.None, step.Secondary.Where(Shown).ToList());
    }

    /// <summary>The button of a journey without steps (set-in-game: "Manage settings again" once the lab is done looking; all set while the lab still needs a mapping run: "Map this game"), when it is on offer.</summary>
    public static LabStepAction ForJourneyAction(LabJourney journey, PanelOffers offers) =>
        journey.Kind != LabJourneyKind.Steps && offers.Offers(journey.Action) ? journey.Action : LabStepAction.None;

    /// <summary>
    /// The action's row under "Manual tools" shows: it is on offer, not a button of the current step, and not offered by the separate
    /// post-session card (it comes back when the card closes). "Flag as set in-game" also steps aside while its confirm card or a test result is open.
    /// </summary>
    public static bool InTools(HealthAction action, PanelOffers offers, LabJourney journey, PostSessionCard? card = null, bool flagConfirming = false,
        bool resultOpen = false)
    {
        if (!offers.Offers(action) || (CardStaysSeparate(card) && card!.Offers(action))) return false;
        if (action == HealthAction.FlagSetInGame && (flagConfirming || resultOpen)) return false;
        var onStep = ForStep(journey.Current, offers, card, flagConfirming, resultOpen).All.Any(a => PanelOffers.ToHealth(a) == action);
        var onSetInGame = ForJourneyAction(journey, offers) is var set && set != LabStepAction.None && PanelOffers.ToHealth(set) == action;
        return !onStep && !onSetInGame;
    }

    /// <summary>Where an action sits right now (<see cref="ActionPlace"/>); each action on offer is in exactly one place.</summary>
    /// <param name="resultOverridden">The open test result found the game changed settings (its card then shows "Flag as set in-game").</param>
    public static ActionPlace Place(HealthAction action, LabJourney journey, PanelOffers offers, PostSessionCard? card = null,
        bool flagConfirming = false, bool resultOpen = false, bool resultOverridden = false)
    {
        if (!offers.Offers(action)) return ActionPlace.Hidden;
        if (CardStaysSeparate(card) && card!.Offers(action)) return ActionPlace.PostCard;
        if (ForStep(journey.Current, offers, card, flagConfirming, resultOpen).All.Any(a => PanelOffers.ToHealth(a) == action) ||
            (ForJourneyAction(journey, offers) is var set && set != LabStepAction.None && PanelOffers.ToHealth(set) == action))
            return ActionPlace.Step;
        if (InTools(action, offers, journey, card, flagConfirming, resultOpen)) return ActionPlace.ManualTools;
        if (action == HealthAction.FlagSetInGame && resultOpen && resultOverridden && !flagConfirming) return ActionPlace.ResultCard;
        return ActionPlace.Hidden;
    }

    /// <summary>B: the top layer closes first (a confirmation, then the test result, then the focused fix step's card, then Manual tools).</summary>
    public static CancelStep NextCancel(bool confirmationOpen, bool resultOpen, bool testing, bool postCardFocused, bool toolsOpen)
    {
        if (confirmationOpen) return CancelStep.CloseConfirmation;
        if (resultOpen && !testing) return CancelStep.CloseResult;
        if (postCardFocused) return CancelStep.DismissPostCard;
        return toolsOpen ? CancelStep.CollapseTools : CancelStep.None;
    }
}
