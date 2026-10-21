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
}

/// <summary>Where one lab action sits on the game page.</summary>
public enum ActionPlace
{
    /// <summary>Not on offer for this game, or its own card is open (a confirmation, the test result's own button).</summary>
    Hidden,

    /// <summary>The one suggested button under the status line.</summary>
    Suggested,

    /// <summary>A row under "More", with its caption.</summary>
    More,

    /// <summary>A button of the post-session card (the panel's own button steps aside while the card is open).</summary>
    PostCard,

    /// <summary>A button of the open Test preset result card ("Flag as set in-game" after an overridden test).</summary>
    ResultCard,
}

/// <summary>What B (Esc) closes next on the game page's lab panel: the top layer only.</summary>
public enum CancelStep
{
    None,

    /// <summary>A confirmation card (a request, the test, flag, forget-copy, or "Keep these" asking on the post-session card).</summary>
    CloseConfirmation,

    /// <summary>The Test preset result card underneath (not while the test is still running).</summary>
    CloseResult,

    /// <summary>The post-session card ("Not now"), only while focus is inside it.</summary>
    DismissPostCard,

    /// <summary>The open "More" list collapses (focus back on More).</summary>
    CollapseMore,
}

/// <summary>
/// The lab panel's placement rules, kept apart from WPF so they can be tested: at most one suggested action (the health's, when its command is
/// on offer), every other action on offer exactly once (under More, or on the post-session card while that card offers it), and B closing the
/// top layer first. <c>LabGameViewModel</c> reads every visibility from here.
/// </summary>
public static class LabPanelPlan
{
    /// <summary>Every action the panel can place, in the order "More" lists them.</summary>
    public static readonly IReadOnlyList<HealthAction> Actions =
    [
        HealthAction.RequestPresets, HealthAction.RefreshPresets, HealthAction.SyncCheck, HealthAction.TestPreset, HealthAction.VerifyNextLaunch,
        HealthAction.MapGame, HealthAction.ManageAgain, HealthAction.FlagSetInGame,
    ];

    /// <summary>The health's suggestion when its command is on offer here, otherwise none.</summary>
    public static HealthAction Suggested(HealthAction suggested, PanelOffers offers) => offers.Offers(suggested) ? suggested : HealthAction.None;

    /// <summary>
    /// The suggested button shows: there is one, the post-session card doesn't offer the same, and for "Flag as set in-game" neither its confirm
    /// card nor a test result (which has its own Flag button) is open.
    /// </summary>
    public static bool ShowSuggested(HealthAction suggested, PostSessionCard? card, bool flagConfirming, bool resultOpen) =>
        suggested != HealthAction.None && !(suggested == HealthAction.FlagSetInGame && (flagConfirming || resultOpen)) && card?.Offers(suggested) != true;

    /// <summary>
    /// The action's row under "More" shows: it is on offer, not the suggested one, and not offered by the post-session card (it comes back when
    /// the card closes). "Flag as set in-game" also steps aside while its confirm card or a test result is open.
    /// </summary>
    public static bool InMore(HealthAction action, PanelOffers offers, HealthAction suggested, PostSessionCard? card, bool flagConfirming = false,
        bool resultOpen = false)
    {
        if (!offers.Offers(action) || suggested == action || card?.Offers(action) == true) return false;
        return !(action == HealthAction.FlagSetInGame && (flagConfirming || resultOpen));
    }

    /// <summary>Where an action sits right now (<see cref="ActionPlace"/>); each action on offer is in exactly one place.</summary>
    /// <param name="resultOverridden">The open test result found the game changed settings (its card then shows "Flag as set in-game").</param>
    public static ActionPlace Place(HealthAction action, PanelOffers offers, HealthAction healthSuggested, PostSessionCard? card,
        bool flagConfirming = false, bool resultOpen = false, bool resultOverridden = false)
    {
        if (!offers.Offers(action)) return ActionPlace.Hidden;
        var suggested = Suggested(healthSuggested, offers);
        if (card?.Offers(action) == true) return ActionPlace.PostCard;
        if (action == suggested && ShowSuggested(suggested, card, flagConfirming, resultOpen)) return ActionPlace.Suggested;
        if (InMore(action, offers, suggested, card, flagConfirming, resultOpen)) return ActionPlace.More;
        if (action == HealthAction.FlagSetInGame && resultOpen && resultOverridden && !flagConfirming) return ActionPlace.ResultCard;
        return ActionPlace.Hidden;
    }

    /// <summary>B: the top layer closes first (a confirmation, then the test result, then the focused post-session card, then More).</summary>
    public static CancelStep NextCancel(bool confirmationOpen, bool resultOpen, bool testing, bool postCardFocused, bool moreOpen)
    {
        if (confirmationOpen) return CancelStep.CloseConfirmation;
        if (resultOpen && !testing) return CancelStep.CloseResult;
        if (postCardFocused) return CancelStep.DismissPostCard;
        return moreOpen ? CancelStep.CollapseMore : CancelStep.None;
    }
}
