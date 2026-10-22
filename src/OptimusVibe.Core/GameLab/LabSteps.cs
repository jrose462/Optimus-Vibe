using System.Globalization;
using OptimusVibe.Launch;

namespace OptimusVibe.GameLab;

/// <summary>What the game lab panel shows for a game: a checklist of steps, "All set", or nothing to do because the game is set in-game.</summary>
public enum LabJourneyKind
{
    /// <summary>A checklist, "Step X of N" (<see cref="LabJourney.Steps"/>, <see cref="LabJourney.CurrentIndex"/>).</summary>
    Steps,

    /// <summary>Every step is done: the preset works here and Optimus-Vibe keeps watching for game updates.</summary>
    AllSet,

    /// <summary>The game is flagged "set in-game": there are no steps, the flag's status line says it (and "Manage settings again" may be offered).</summary>
    SetInGame,
}

/// <summary>The steps of the guided checklist, in order. A step that does not apply to a game is left out (<see cref="LabJourney.Count"/> is dynamic).</summary>
public enum LabStepId
{
    /// <summary>The game has presets for this device.</summary>
    GetPresets,

    /// <summary>A launch with the current preset was recorded (after a game update: one more).</summary>
    PlayOnce,

    /// <summary>The newest launch showed that the game kept the preset's settings (or the fix for the ones it changed).</summary>
    ConfirmKept,

    /// <summary>Only while it is unsure whether a launcher's cloud sync changes the settings between devices.</summary>
    CheckSync,
}

public enum StepState { Done, Current, Upcoming }

/// <summary>The button a step offers. Each is one of the game page's existing commands; <see cref="LabPanelPlan"/> says which are on offer.</summary>
public enum LabStepAction
{
    None,
    RequestPresets,

    /// <summary>The game page's Play button (a normal launch, which Optimus-Vibe checks afterwards by itself).</summary>
    Play,

    /// <summary>"Map this game": the lab learns where the game keeps its settings.</summary>
    MapThisGame,

    /// <summary>Ask the game lab to re-check the game (sends the findings, requests a fix).</summary>
    RefreshPresets,
    RecheckSync,

    /// <summary>Keep what the game left as a manual save.</summary>
    KeepAsManualSave,
    FlagSetInGame,
    ManageAgain,
}

/// <summary>One line of the checklist.</summary>
public sealed record LabStep
{
    public required LabStepId Id { get; init; }
    public required string Title { get; init; }

    /// <summary>One line saying what to do (the current step) or what comes (an upcoming one).</summary>
    public required string Detail { get; init; }
    public required StepState State { get; init; }

    /// <summary>One line for a done step that still names it: "Played once with the preset · Oct 20". Empty for the others.</summary>
    public string DoneSummary { get; init; } = "";

    /// <summary>The main button of the current step (the A button); <see cref="LabStepAction.None"/> for every other step.</summary>
    public LabStepAction Primary { get; init; }

    /// <summary>The quieter choices of the current step.</summary>
    public IReadOnlyList<LabStepAction> Secondary { get; init; } = [];

    /// <summary>The step is the fix for settings the game changed ("Fix: the game changed 4 of 14 settings"); it lists them on demand.</summary>
    public bool IsFix { get; init; }

    /// <summary>Every action this step shows, main button first.</summary>
    public IEnumerable<LabStepAction> Actions => Primary == LabStepAction.None ? Secondary : Secondary.Prepend(Primary);
}

/// <summary>The guided checklist of one game on one device (<see cref="LabSteps.Build"/>).</summary>
public sealed record LabJourney
{
    public required LabJourneyKind Kind { get; init; }
    public IReadOnlyList<LabStep> Steps { get; init; } = [];

    /// <summary>"All set" or the flag's status line; empty while there are steps.</summary>
    public string Headline { get; init; } = "";

    /// <summary>The line under <see cref="Headline"/>; empty when there is nothing to add.</summary>
    public string Detail { get; init; } = "";

    /// <summary>For <see cref="LabJourneyKind.SetInGame"/>: "Manage settings again" when the game lab is done looking; for <see cref="LabJourneyKind.AllSet"/>: "Map this game" while the lab still waits for a mapping run; else none.</summary>
    public LabStepAction Action { get; init; }

    /// <summary>The run behind the fix step (what the game changed), for "See which"; null otherwise.</summary>
    public RunResult? ChangedRun { get; init; }

    /// <summary>The health the journey was built from (its flag suggestion, lab request and chip stay available to the panel).</summary>
    public required GameHealth Health { get; init; }

    /// <summary>Steps in the checklist (N of "Step X of N").</summary>
    public int Count => Steps.Count;

    /// <summary>Index of the current step in <see cref="Steps"/>; -1 when none is current (all set, set in-game).</summary>
    public int CurrentIndex
    {
        get
        {
            for (var k = 0; k < Steps.Count; k++)
                if (Steps[k].State == StepState.Current) return k;
            return -1;
        }
    }

    public LabStep? Current => CurrentIndex >= 0 ? Steps[CurrentIndex] : null;

    /// <summary>"Step 2 of 3", "All set" or "Set in-game": the panel header's right-hand text.</summary>
    public string ProgressText => Kind switch
    {
        LabJourneyKind.Steps => $"Step {CurrentIndex + 1} of {Count}",
        LabJourneyKind.AllSet => "All set",
        _ => "Set in-game",
    };
}

/// <summary>
/// The guided "Step X of N" checklist of the game lab panel: what to do now, in order, instead of one status line. A pure function of the
/// same <see cref="HealthInputs"/> as <see cref="GameHealth.Evaluate"/>, whose result it reuses (flag, missing presets, game updates,
/// changed settings and cloud sync are its rules, not repeated here); only the run history is read again to tell "played" from "kept".
/// </summary>
public static class LabSteps
{
    public const string AllSetHeadline = "All set";
    public const string AllSetDetail = "Working. Optimus-Vibe keeps watching for game updates.";

    public const string GetPresetsTitle = "Get presets";
    public const string PlayOnceTitle = "Play once with the preset";
    public const string PlayAfterUpdateTitle = "Play once after the update";
    public const string ConfirmKeptTitle = "Confirm the game kept them";
    public const string CheckSyncTitle = "Check settings sync between devices";

    public const string PlayDetail = "Play normally and quit when you like. Optimus-Vibe checks afterwards whether the game kept the settings.";
    public const string ConfirmDetail = "Optimus-Vibe checks the game's settings after you quit.";
    public const string UnclearDetail = "Your last launch didn't show clearly whether the game kept the preset. Play a little longer, then quit.";
    public const string UpdatedDetail = "The game updated and may have moved some settings. Play once and Optimus-Vibe checks the preset again.";
    public const string SyncDetail = "Find out whether this game's settings follow you between devices.";

    /// <summary>A step before it is known which one is current: <see cref="Finish"/> makes the first one not done the current step.</summary>
    private sealed record Draft(LabStepId Id, string Title, string UpcomingDetail, bool Done = false, string DoneSummary = "", string? Detail = null,
        LabStepAction Primary = LabStepAction.None, IReadOnlyList<LabStepAction>? Secondary = null, bool IsFix = false);

    public static LabJourney Build(HealthInputs i)
    {
        var h = GameHealth.Evaluate(i);
        if (h.State == HealthState.Flagged)
            return new LabJourney
            {
                Kind = LabJourneyKind.SetInGame, Health = h, Headline = h.Headline, Detail = h.Why ?? "",
                Action = h.Suggested == HealthAction.ManageAgain ? LabStepAction.ManageAgain : LabStepAction.None,
            };

        var drafts = new List<Draft>();
        var canPlay = i.CanLaunch;
        var wantsMap = h.Suggested == HealthAction.MapGame;
        var mapSecondary = wantsMap && canPlay ? new[] { LabStepAction.MapThisGame } : [];
        RunResult? fixRun = null;

        // 1. presets for this device
        if (h.State == HealthState.NoPresets)
        {
            drafts.Add(new Draft(LabStepId.GetPresets, GetPresetsTitle, "Ask the game lab for presets for this device.",
                Detail: h.LabWorking ? h.Headline : h.Why ?? "Ask the game lab for presets for this device.",
                Primary: h.Suggested == HealthAction.RequestPresets ? LabStepAction.RequestPresets : LabStepAction.None,
                Secondary: wantsMap ? [LabStepAction.MapThisGame] : []));
            drafts.Add(new Draft(LabStepId.PlayOnce, PlayOnceTitle, PlayDetail));
            drafts.Add(new Draft(LabStepId.ConfirmKept, ConfirmKeptTitle, ConfirmDetail));
            return Finish(drafts, h, null, false);
        }
        drafts.Add(new Draft(LabStepId.GetPresets, GetPresetsTitle, "The game has presets for this device.", Done: true,
            DoneSummary: i.Presets is { } made ? $"Got presets for this device · {Day(made.MadeUtc)}" : "Got presets for this device"));

        // what the newest launch says: was the game played with this preset, did it keep the settings, did it change them?
        var evidence = Evidence.Of(i);
        var updated = h.State == HealthState.GameUpdated;
        var cantBeSet = h.State == HealthState.CantBeSetFromOutside;
        var playedSinceUpdate = updated && evidence.Run is { IsVerify: false, Build.Length: > 0 } run && string.Equals(run.Build, i.CurrentBuild, StringComparison.Ordinal);
        var played = cantBeSet || (evidence.Run is not null && (!updated || playedSinceUpdate));
        var held = played && !cantBeSet && (h.State == HealthState.Working || evidence.Held);

        if (h.State == HealthState.GameChangedSettings && h.ChangedRun is { } changed) fixRun = changed;
        else if (playedSinceUpdate && !evidence.Held && evidence.Normal is { } normal && GameHealth.ChangedByGame(normal, evidence.NormalVerdict)) fixRun = normal;

        // 2. play once with the preset (again after an update)
        drafts.Add(played
            ? new Draft(LabStepId.PlayOnce, PlayOnceTitle, PlayDetail, Done: true,
                DoneSummary: evidence.Run is { } last ? $"Played once with the preset · {Day(last.Utc)}" : "Played once with the preset")
            : new Draft(LabStepId.PlayOnce, updated ? PlayAfterUpdateTitle : PlayOnceTitle, updated ? UpdatedDetail : PlayDetail,
                Detail: updated ? UpdatedDetail : wantsMap ? h.Why ?? PlayDetail : PlayDetail,
                Primary: canPlay ? LabStepAction.Play : LabStepAction.None,
                Secondary: PlaySecondary(updated && h.Suggested == HealthAction.RefreshPresets, mapSecondary)));

        // 3. confirm the game kept them, or fix what it changed
        if (fixRun is not null) drafts.Add(FixDraft(i, h, fixRun));
        else if (cantBeSet)
            drafts.Add(new Draft(LabStepId.ConfirmKept, "This game may need to be set in-game", ConfirmDetail, Detail: h.Why ?? h.Headline,
                Primary: LabStepAction.FlagSetInGame,
                Secondary: i.LabReady && i.HasConfig && !h.LabWorking ? [LabStepAction.RefreshPresets] : []));
        else if (held)
            drafts.Add(new Draft(LabStepId.ConfirmKept, ConfirmKeptTitle, ConfirmDetail, Done: true,
                DoneSummary: $"The game kept the settings · {Day(GameHealth.NewestHeldUtc(i) ?? evidence.Run!.Utc)}"));
        else
            drafts.Add(new Draft(LabStepId.ConfirmKept, ConfirmKeptTitle, ConfirmDetail, Detail: played ? UnclearDetail : ConfirmDetail,
                Primary: played && canPlay ? LabStepAction.Play : LabStepAction.None, Secondary: played ? mapSecondary : []));

        // 4. settings sync: only while it is unsure whether a launcher's cloud sync changes them
        // "unknown" alone only becomes a step while the game lab can be asked: without it nothing could ever complete the step
        if (h.State == HealthState.SyncUnsure || (i.CloudSync is { Effective: null } && i.LabReady && i.HasConfig))
            drafts.Add(new Draft(LabStepId.CheckSync, CheckSyncTitle, SyncDetail,
                Detail: h.State == HealthState.SyncUnsure ? h.Why ?? SyncDetail : SyncDetail,
                Primary: i.LabReady && i.HasConfig && !h.LabWorking ? LabStepAction.RecheckSync : LabStepAction.None));

        return Finish(drafts, h, fixRun, wantsMap && canPlay);
    }

    /// <summary>The first step not done is the current one, the ones after it are upcoming (no buttons); none left means all set.</summary>
    private static LabJourney Finish(List<Draft> drafts, GameHealth h, RunResult? fixRun, bool mapOnAllSet)
    {
        var current = drafts.FindIndex(d => !d.Done);
        var steps = drafts.Select((d, k) =>
        {
            if (d.Done)
                return new LabStep { Id = d.Id, Title = d.Title, Detail = d.UpcomingDetail, State = StepState.Done, DoneSummary = d.DoneSummary };
            if (k != current)
                return new LabStep { Id = d.Id, Title = d.Title, Detail = d.UpcomingDetail, State = StepState.Upcoming };
            return new LabStep
            {
                Id = d.Id, Title = d.Title, Detail = d.Detail ?? d.UpcomingDetail, State = StepState.Current, Primary = d.Primary,
                Secondary = d.Secondary ?? [], IsFix = d.IsFix,
            };
        }).ToList();
        return current < 0
            ? new LabJourney
            {
                Kind = LabJourneyKind.AllSet, Steps = steps, Health = h, Headline = AllSetHeadline,
                // the lab still waits for a mapping run (the old post-session "Map it" card): say so next to the button
                Detail = mapOnAllSet ? h.Why ?? AllSetDetail : AllSetDetail, Action = mapOnAllSet ? LabStepAction.MapThisGame : LabStepAction.None,
            }
            : new LabJourney { Kind = LabJourneyKind.Steps, Steps = steps, Health = h, ChangedRun = fixRun };
    }

    /// <summary>
    /// The game changed some of the preset's settings: map the game (let the lab learn where its settings live) unless a mapping run exists,
    /// then ask the lab to re-check it (send the findings, request a fix). Keeping the changes as a manual save is always possible; flagging
    /// the game only when the evidence suggests it (<see cref="GameHealth.FlagSuggestion"/>).
    /// </summary>
    private static Draft FixDraft(HealthInputs i, GameHealth h, RunResult run)
    {
        var canRefresh = i.LabReady && i.HasConfig && !h.LabWorking;
        var primary = i.Mapping is null && i.CanLaunch && !h.LabWorking ? LabStepAction.MapThisGame
            : canRefresh ? LabStepAction.RefreshPresets
            : LabStepAction.None;
        var names = RunResults.Names(run, 3);
        List<LabStepAction> secondary = [LabStepAction.KeepAsManualSave];
        if (h.FlagReason is not null) secondary.Add(LabStepAction.FlagSetInGame);
        return new Draft(LabStepId.ConfirmKept, $"Fix: the game changed {run.Drift.Count} of {run.Applied} settings", ConfirmDetail,
            Detail: h.LabWorking ? $"{names} The game lab is looking into it." : names, Primary: primary, Secondary: secondary, IsFix: true);
    }

    private static LabStepAction[] PlaySecondary(bool refresh, LabStepAction[] map) => refresh ? [LabStepAction.RefreshPresets, .. map] : map;

    private static string Day(DateTime utc) =>
        (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()).ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>The newest launch or test of the current preset and whether it held.</summary>
    private sealed record Evidence(RunResult? Run, bool Held, RunResult? Normal, PassiveVerdict NormalVerdict)
    {
        public static Evidence Of(HealthInputs i)
        {
            var verdicts = i.PassiveVerdicts;
            RunResult? normal = null;
            var verdict = PassiveVerdict.Unknown;
            for (var k = i.Recent.Count - 1; k >= 0 && normal is null; k--)
                if (GameHealth.CountsForCurrentPreset(i, i.Recent[k])) (normal, verdict) = (i.Recent[k], verdicts[k]);
            var verify = i.LastVerify is { } v && GameHealth.CountsForCurrentPreset(i, v) ? v : null;

            if (verify is not null && (normal is null || verify.Utc >= normal.Utc))
                return new Evidence(verify, RunResults.Verdict(verify) == RunVerdict.Held, normal, verdict);
            return new Evidence(normal, normal is not null && verdict == PassiveVerdict.Held, normal, verdict);
        }
    }
}
