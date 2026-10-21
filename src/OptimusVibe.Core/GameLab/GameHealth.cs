using System.Globalization;
using OptimusVibe.CloudSync;
using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.GameLab;

/// <summary>
/// One game's health on one device, in the order <see cref="GameHealth.Evaluate"/> checks them: the first that matches wins.
/// </summary>
public enum HealthState
{
    /// <summary>The game is flagged "set in-game" (game file or library.yaml): its settings are left to the game, there is no run history.</summary>
    Flagged,

    /// <summary>No presets for this device (no config, or a config without this device). An open lab request is shown here too.</summary>
    NoPresets,

    /// <summary>Not flagged, but the evidence says the game ignores the settings Optimus-Vibe writes: the flag is suggested (never applied).</summary>
    CantBeSetFromOutside,

    /// <summary>The installed build differs from the newest build the presets are known to work on.</summary>
    GameUpdated,

    /// <summary>The last launch or the last test found preset settings the game changed.</summary>
    GameChangedSettings,

    /// <summary>A launcher's cloud sync may be changing the settings (a contradiction, changes while Optimus-Vibe wasn't running, a sync rewrite).</summary>
    SyncUnsure,

    /// <summary>The preset held: a held Test preset, or <see cref="GameHealth.WorkingAfter"/> passively held launches in a row.</summary>
    Working,

    /// <summary>Has presets, no clear evidence on this device yet. It resolves itself as the user plays.</summary>
    NotChecked,
}

/// <summary>The one action a health state suggests (at most one). Every one of them is an existing command of the game page.</summary>
public enum HealthAction
{
    None,
    RequestPresets,
    RefreshPresets,
    TestPreset,
    MapGame,
    FlagSetInGame,
    ManageAgain,
}

/// <summary>
/// The per-game, per-device health shown on the game page: one status line (<see cref="Headline"/>), one dim "why" line and at most one
/// suggested action. Derived only from data already held (<see cref="HealthInputs"/>); pure, no I/O.
/// </summary>
public sealed record GameHealth
{
    /// <summary>Passively held launches in a row (or one held Test preset) that make a game "Working".</summary>
    public const int WorkingAfter = 3;

    public required HealthState State { get; init; }
    public required string Headline { get; init; }

    /// <summary>One dim line saying why; null when there is nothing to add.</summary>
    public string? Why { get; init; }

    public HealthAction Suggested { get; init; }

    /// <summary>Why the "set in-game" flag is worth suggesting (<see cref="FlagSuggestion"/>), whatever the state; null when it isn't.</summary>
    public string? FlagReason { get; init; }

    /// <summary>A lab request for the game is open (a secondary chip unless presets are missing).</summary>
    public bool LabWorking { get; init; }

    /// <summary>States worth a chip on the library tile.</summary>
    public bool NeedsAttention => State is HealthState.GameUpdated or HealthState.GameChangedSettings or HealthState.CantBeSetFromOutside;

    /// <summary>
    /// The library tile's attention chip: "Game updated" or "Changed by game" (a game the evidence says may need to be set in-game is one the
    /// game keeps changing); null for every other state. A flagged game needs none: its tile already says "Set in-game".
    /// </summary>
    public string? TileChip => State switch
    {
        HealthState.GameUpdated => GameUpdatedChip,
        HealthState.GameChangedSettings or HealthState.CantBeSetFromOutside => ChangedByGameChip,
        _ => null,
    };

    public const string GameUpdatedChip = "Game updated";
    public const string ChangedByGameChip = "Changed by game";

    public const string ResearchFinishedLine = "Research finished - try managing settings again?";
    public const string ConfigFlagLine = "The game lab set this flag, so only it can lift it. It does that when its research finds a way to set this game's settings.";

    /// <summary>The "Not checked" line when nothing counts yet (also right after "Manage settings again": older runs stopped counting).</summary>
    public const string ChecksAfterPlayLine = "Optimus-Vibe checks it by itself after you play.";

    /// <summary>The caption under "Manage settings again": says the same as the health shows afterwards (<see cref="ChecksAfterPlayLine"/>).</summary>
    public const string ManageAgainCaption = "Optimus-Vibe writes this game's settings again and checks them by itself after you play (or test the preset now).";

    /// <summary>The toast after "Manage settings again", in line with <see cref="ChecksAfterPlayLine"/> (nothing is suggested until a run counts).</summary>
    public static string ManagedAgainLine(string name) =>
        $"Optimus-Vibe manages {name}'s settings again. It checks by itself that the game keeps them after you play, or test the preset now.";

    /// <summary>The button text of a suggested action.</summary>
    public static string ActionText(HealthAction action) => action switch
    {
        HealthAction.RequestPresets => "Request presets",
        HealthAction.RefreshPresets => "Ask the game lab to re-check",
        HealthAction.TestPreset => "Test preset",
        HealthAction.MapGame => "Map this game",
        HealthAction.FlagSetInGame => "Flag as set in-game",
        HealthAction.ManageAgain => "Manage settings again",
        _ => "",
    };

    /// <summary>The health of a game on a device: the first state that matches, in the order of <see cref="HealthState"/>.</summary>
    public static GameHealth Evaluate(HealthInputs i)
    {
        var open = i.LabRequest is { IsOpen: true } issue ? issue : null;
        var flagReason = FlagSuggestion(i);

        // 1. flagged: no run history exists for it, so nothing below applies
        if (i.Flag is { } flag)
        {
            var manage = i.HasUserFlag && i.ResearchFinished;
            return new GameHealth
            {
                State = HealthState.Flagged, Headline = GameFlags.StatusLine(flag), LabWorking = open is not null,
                Why = !i.HasUserFlag ? ConfigFlagLine : manage ? ResearchFinishedLine : null,
                Suggested = manage ? HealthAction.ManageAgain : HealthAction.None,
            };
        }

        // 2. no presets for this device
        if (!i.HasPresets)
        {
            if (open is not null)
            {
                var needsEvidence = open.Status == LabStatus.NeedsEvidence;
                var map = needsEvidence && i.Mapping is null && i.CanLaunch;
                return new GameHealth
                {
                    State = HealthState.NoPresets, LabWorking = true, FlagReason = flagReason,
                    Headline = $"Game lab is working on it (#{open.Number}, {LabStatusIndex.Chip(open).ToLowerInvariant()})",
                    Why = map ? $"The lab needs to know where this game keeps its settings: {i.MapEffort}."
                        : needsEvidence ? "The lab asked for more files from this PC. Optimus-Vibe sends them by itself."
                        : "The presets arrive by themselves when it's done.",
                    Suggested = map ? HealthAction.MapGame : HealthAction.None,
                };
            }
            return new GameHealth
            {
                State = HealthState.NoPresets, Headline = "No presets for this device yet", FlagReason = flagReason,
                // without the lab the panel already says why and links to Settings
                Why = !i.LabReady ? null
                    : i.HasConfig ? "The game has presets for your other devices. The game lab can add this one."
                    : "Claude researches the game's settings and writes presets for your devices.",
                Suggested = i.LabReady ? HealthAction.RequestPresets : HealthAction.None,
            };
        }

        var labWorking = open is not null;

        // 3. the evidence says the game ignores the settings (suggested only, never flagged automatically)
        if (flagReason is not null)
            return new GameHealth
            {
                State = HealthState.CantBeSetFromOutside, Headline = "This game may need to be set in-game", FlagReason = flagReason, LabWorking = labWorking,
                Why = $"It seems to ignore Optimus-Vibe's settings: {flagReason}.", Suggested = HealthAction.FlagSetInGame,
            };

        // 4. the game updated since the presets last worked
        if (i.GameUpdated)
            return new GameHealth
            {
                State = HealthState.GameUpdated, Headline = "Game updated since these presets were made", LabWorking = labWorking,
                Why = i.GameUpdatedByStore
                    ? "A new build is installed. The game may have moved or renamed some settings."
                    : "The game's files changed. It may have moved or renamed some settings.",
                Suggested = i.LabReady && i.HasConfig && !labWorking ? HealthAction.RefreshPresets : i.CanLaunch ? HealthAction.TestPreset : HealthAction.None,
            };

        var runs = Runs.Of(i);

        // 5. the game changed some of the preset's settings
        if (runs.Changed is { } changed)
        {
            var fromTest = changed.IsVerify;
            return new GameHealth
            {
                State = HealthState.GameChangedSettings, LabWorking = labWorking,
                Headline = $"The game changed some settings ({changed.Drift.Count} of {changed.Applied})",
                Why = $"{(fromTest ? "Last test" : "Last launch")}, {Day(changed.Utc)}: {RunResults.Names(changed, 3)}",
                Suggested = fromTest
                    ? (i.LabReady && i.HasConfig && !labWorking ? HealthAction.RefreshPresets : HealthAction.None)
                    : (i.CanLaunch ? HealthAction.TestPreset : HealthAction.None),
            };
        }

        // 6. cloud sync may be changing them
        // Only the contradiction that says the settings do travel counts ("probably per machine" is no doubt about this game), and changes
        // made while Optimus-Vibe wasn't running stop counting once the preset held on a later launch or test, and like every other evidence
        // once they are older than the fresh start (EvidenceSinceUtc: the game was flagged, or its settings managed again since).
        var sync = i.CloudSync;
        var syncContradiction = sync is { Contradiction: { } contradiction, Effective: true } ? contradiction : null;
        var syncExternal = sync is { HasExternalChanges: true, Confidence: <= SyncConfidence.Low } &&
                           !(sync.LastExternalChangeUtc is { } changedUtc &&
                             ((runs.NewestHeldUtc is { } heldUtc && heldUtc > changedUtc) || (i.EvidenceSinceUtc is { } since && since > changedUtc)));
        if (syncContradiction is not null || syncExternal || runs.LastPassive == PassiveVerdict.SyncSuspect)
            return new GameHealth
            {
                State = HealthState.SyncUnsure, Headline = "Settings may be changed by cloud sync", LabWorking = labWorking,
                Why = syncContradiction is not null ? Sentence(syncContradiction)
                    : runs.LastPassive == PassiveVerdict.SyncSuspect ? "A launcher's cloud sync rewrote the settings during your last launch."
                    : "The settings changed while Optimus-Vibe wasn't running.",
            };

        // 7. working
        if (runs.HeldStreak >= WorkingAfter || runs.VerifyHeld)
        {
            var verified = runs.VerifyHeld ? $"Verified on this device, {Day(runs.Verify!.Utc)}." : null;
            return new GameHealth
            {
                State = HealthState.Working, LabWorking = labWorking,
                Headline = runs.HeldStreak >= WorkingAfter
                    ? $"Working. Presets held on your last {runs.HeldStreak} launches."
                    : "Working. The game kept the preset in your last test.",
                Why = verified ?? $"Last checked {Day(runs.LastNormal!.Utc)}.",
            };
        }

        // 8. not checked yet: it resolves itself as the user plays
        var ambiguous = runs.HeldStreak == 0 && runs.Counted >= WorkingAfter && i.CanLaunch;
        return new GameHealth
        {
            State = HealthState.NotChecked, Headline = "Not checked on this device yet", LabWorking = labWorking,
            Why = runs.HeldStreak > 0
                ? $"The preset held on your last {(runs.HeldStreak == 1 ? "launch" : $"{runs.HeldStreak} launches")}. {WorkingAfter - runs.HeldStreak} more to be sure."
                : ambiguous ? "Your last launches didn't show clearly whether the game keeps the preset."
                : ChecksAfterPlayLine,
            Suggested = ambiguous ? HealthAction.TestPreset : HealthAction.None,
        };
    }

    /// <summary>
    /// Why flagging the game "set in-game" is worth suggesting (never applied on its own): two overridden Test preset results in a row, two
    /// passively overridden launches, or a mapping run whose changes were all in binary files Optimus-Vibe can't edit. A mapping run doesn't
    /// count when every such file is one the game's config already writes (an Unreal <c>.sav</c> through the <c>gvas</c> handler).
    /// The same rules as the rest of the health apply: only evidence about the current preset counts, and newer evidence wins. The verify
    /// streak and the mapping run stop counting once the preset held on a later launch or test, or once new presets arrived; the passive
    /// streak is counted from the current preset's own launches (<see cref="PassiveOverridden"/>), not from the device-wide stored counter.
    /// Null when the game is flagged already, has no config, or nothing suggests it.
    /// </summary>
    public static string? FlagSuggestion(HealthInputs i) => FlagEvidence(i)?.Reason;

    /// <summary>
    /// <see cref="FlagSuggestion"/> with where it comes from: the rule that fired and the run behind it, so flagging the game keeps the right
    /// evidence (<see cref="FlagFinding.Summary"/> in the flag, <see cref="FlagFinding.RunText"/> in the research task). Evidence from before
    /// <see cref="HealthInputs.EvidenceSinceUtc"/> (the game was flagged, or its settings managed again since) never counts. Null when nothing
    /// suggests the flag.
    /// </summary>
    public static FlagFinding? FlagEvidence(HealthInputs i)
    {
        if (i.Flag is not null || !i.HasConfig) return null;
        var heldUtc = Runs.NewestHeld(i);
        var presetsUtc = i.Presets?.MadeUtc;
        bool Stale(DateTime utc) => (heldUtc is { } h && h > utc) || (presetsUtc is { } p && p > utc) || (i.EvidenceSinceUtc is { } s && s > utc);

        if (VerifyStreakStands(i) && GameFlags.SuggestsFromVerify(i.OverriddenStreak))
        {
            var test = i.LastVerify is { } v && Runs.CountsFor(i, v) && RunResults.Verdict(v) == RunVerdict.Overridden ? v : null;
            return new FlagFinding(FlagSource.Verify, GameFlags.VerifySuggestionReason(i.OverriddenStreak), test);
        }
        var passive = PassiveOverridden(i);
        if (GameFlags.SuggestsFromPassive(passive))
            return new FlagFinding(FlagSource.Passive, GameFlags.PassiveSuggestionReason(passive), NewestPassiveOverridden(i));
        if (i.Mapping is { SuggestSetInGame: true } m && !AllConfigured(m.OpaqueFiles, i.ConfiguredFiles) && !Stale(m.Utc))
            return new FlagFinding(FlagSource.Mapping, m.SuggestReason ?? "the game keeps its settings only in a binary file Optimus-Vibe can't edit", null);
        return null;
    }

    /// <summary>The newest passively overridden normal run of the current preset (one <see cref="PassiveOverridden"/> counts), or null.</summary>
    private static RunResult? NewestPassiveOverridden(HealthInputs i)
    {
        var verdicts = i.PassiveVerdicts;
        for (var k = i.Recent.Count - 1; k >= 0; k--)
        {
            if (!Runs.CountsFor(i, i.Recent[k])) continue;
            if (verdicts[k] == PassiveVerdict.Held) return null;
            if (verdicts[k] == PassiveVerdict.Overridden) return i.Recent[k];
        }
        return null;
    }

    /// <summary>
    /// The stored overridden-test streak (<see cref="HealthInputs.OverriddenStreak"/>) still speaks for the current preset. It is device-wide, so
    /// it counts only through the last test, which must be of the current preset and still the newest word: no held launch or test since, and
    /// no new presets since.
    /// </summary>
    public static bool VerifyStreakStands(HealthInputs i)
    {
        if (i.LastVerify is not { } lastVerify) return true;
        var heldUtc = Runs.NewestHeld(i);
        var presetsUtc = i.Presets?.MadeUtc;
        var stale = (heldUtc is { } h && h > lastVerify.Utc) || (presetsUtc is { } p && p > lastVerify.Utc);
        return Runs.CountsFor(i, lastVerify) && !stale;
    }

    /// <summary>
    /// Passively overridden launches of the current preset, newest back (<see cref="RunResults.NextPassiveStreak"/>'s rules over the ring): an
    /// overridden one adds one, a held one ends the count, anything else (saved, unclear, sync, another preset) is skipped. Launches from before
    /// the presets arrived don't count.
    /// </summary>
    public static int PassiveOverridden(HealthInputs i)
    {
        var verdicts = i.PassiveVerdicts;
        var n = 0;
        for (var k = i.Recent.Count - 1; k >= 0; k--)
        {
            var r = i.Recent[k];
            if (i.Presets is { } made && r.Utc < made.MadeUtc) break;
            if (!Runs.CountsFor(i, r)) continue;
            if (verdicts[k] == PassiveVerdict.Held) break;
            if (verdicts[k] == PassiveVerdict.Overridden) n++;
        }
        return n;
    }

    /// <summary>
    /// A normal run the health counts as "the game changed settings" (<see cref="HealthState.GameChangedSettings"/>): its passive verdict says
    /// so, or most of the preset drifted and the player did not keep it as a save, unless it points at cloud sync.
    /// </summary>
    public static bool ChangedByGame(RunResult run, PassiveVerdict verdict) => Runs.NormalChanged(run, verdict);

    /// <summary>A run (or test) of the preset that applies now (<see cref="HealthInputs.CurrentPreset"/>); every run counts when that isn't known.</summary>
    public static bool CountsForCurrentPreset(HealthInputs i, RunResult run) => Runs.CountsFor(i, run);

    /// <summary>Every opaque file of a mapping run is (by file name) one of the game's configured files.</summary>
    public static bool AllConfigured(IReadOnlyList<string> opaqueFiles, IReadOnlyList<string> configuredFiles)
    {
        if (opaqueFiles.Count == 0 || configuredFiles.Count == 0) return false;
        var names = configuredFiles.Select(FileName).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return opaqueFiles.All(f => names.Contains(FileName(f)));
    }

    private static string FileName(string path)
    {
        var p = path.Replace('\\', '/').TrimEnd('/');
        var slash = p.LastIndexOf('/');
        return slash < 0 ? p : p[(slash + 1)..];
    }

    private static string Day(DateTime utc) =>
        (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()).ToString("MMM d", CultureInfo.InvariantCulture);

    private static string Sentence(string s) => s.TrimEnd().EndsWith('.') ? s.Trim() : s.Trim() + ".";

    /// <summary>What the run history says about the current preset, newest evidence first.</summary>
    private sealed class Runs
    {
        public RunResult? Verify { get; private init; }
        public RunResult? LastNormal { get; private init; }
        public PassiveVerdict LastPassive { get; private init; }
        public int HeldStreak { get; private init; }
        public int Counted { get; private init; }
        public bool VerifyHeld => Verify is { } v && RunResults.Verdict(v) == RunVerdict.Held;
        public RunResult? Changed { get; private init; }

        /// <summary>When the current preset last held: a held Test preset or a passively held launch, whichever is newer; null when never.</summary>
        public DateTime? NewestHeldUtc { get; private init; }

        /// <summary>A run (or test) of the preset that applies now (every run counts when that isn't known), not from before <see cref="HealthInputs.EvidenceSinceUtc"/>.</summary>
        public static bool CountsFor(HealthInputs i, RunResult r) =>
            (i.EvidenceSinceUtc is not { } since || r.Utc >= since) &&
            (i.CurrentPreset is null || string.Equals(r.Preset, i.CurrentPreset, StringComparison.OrdinalIgnoreCase));

        public static DateTime? NewestHeld(HealthInputs i)
        {
            DateTime? newest = i.LastVerify is { } v && CountsFor(i, v) && RunResults.Verdict(v) == RunVerdict.Held ? v.Utc : null;
            var verdicts = i.PassiveVerdicts;
            for (var k = 0; k < i.Recent.Count; k++)
                if (verdicts[k] == PassiveVerdict.Held && CountsFor(i, i.Recent[k]) && (newest is null || i.Recent[k].Utc > newest)) newest = i.Recent[k].Utc;
            return newest;
        }

        public static Runs Of(HealthInputs i)
        {
            bool Counts(RunResult r) => CountsFor(i, r);
            var verdicts = i.PassiveVerdicts;
            var verify = i.LastVerify is { } v && Counts(v) ? v : null;

            var last = -1;
            for (var k = i.Recent.Count - 1; k >= 0 && last < 0; k--)
                if (Counts(i.Recent[k])) last = k;
            var lastNormal = last >= 0 ? i.Recent[last] : null;
            var lastPassive = last >= 0 ? verdicts[last] : PassiveVerdict.Unknown;

            // held launches in a row, newest back; a run of another preset ends the streak
            var held = 0;
            for (var k = i.Recent.Count - 1; k >= 0 && Counts(i.Recent[k]) && verdicts[k] == PassiveVerdict.Held; k--) held++;

            // the newest evidence wins: a launch after the test speaks for itself, a held launch after an overridden test clears it
            RunResult? changed = null;
            if (lastNormal is not null && (verify is null || lastNormal.Utc > verify.Utc) && NormalChanged(lastNormal, lastPassive))
                changed = lastNormal;
            else if (verify is not null && RunResults.Verdict(verify) == RunVerdict.Overridden &&
                     !i.Recent.Where((r, k) => Counts(r) && r.Utc > verify.Utc && verdicts[k] == PassiveVerdict.Held).Any())
                changed = verify;

            return new Runs
            {
                Verify = verify, LastNormal = lastNormal, LastPassive = lastPassive, HeldStreak = held, Changed = changed,
                Counted = i.Recent.Count(Counts), NewestHeldUtc = NewestHeld(i),
            };
        }

        /// <summary>
        /// A normal run counts as "the game changed settings" when its passive verdict says so, or (the old "Test preset?" hint) when most
        /// of the preset drifted and the player did not keep it as a save, unless it points at cloud sync.
        /// </summary>
        public static bool NormalChanged(RunResult r, PassiveVerdict verdict) =>
            verdict == PassiveVerdict.Overridden ||
            (RunResults.NeedsHint(r) && r.Capture != CaptureAnswer.Saved && verdict is not (PassiveVerdict.SyncSuspect or PassiveVerdict.UserSaved or PassiveVerdict.Held));
    }
}

/// <summary>Which evidence suggests flagging a game "set in-game" (<see cref="GameHealth.FlagEvidence"/>).</summary>
public enum FlagSource
{
    /// <summary>Overridden Test preset results in a row (<see cref="GameFlags.SuggestsFromVerify"/>).</summary>
    Verify,

    /// <summary>Passively overridden normal launches (<see cref="GameFlags.SuggestsFromPassive"/>).</summary>
    Passive,

    /// <summary>A mapping run whose changes were all in binary files Optimus-Vibe can't edit (<see cref="ProbeRecord.SuggestSetInGame"/>).</summary>
    Mapping,
}

/// <summary>
/// Why the "set in-game" flag is suggested (<see cref="Reason"/>, the confirm card's "Why") and the run behind it, if any: the last overridden
/// test, or the newest passively overridden launch. A mapping run has none (its evidence is the stored mapping, which requests attach anyway).
/// </summary>
public sealed record FlagFinding(FlagSource Source, string Reason, RunResult? Run)
{
    /// <summary>The short evidence kept in the flag (<see cref="Config.FlagDef.Evidence"/>): what the run found, else the reason.</summary>
    public string Summary => Run is { } r && r.Drift.Count > 0
        ? Source == FlagSource.Verify
            ? $"Test preset: the game changed {r.Drift.Count} of {r.Applied} setting(s) in the last test ({RunResults.Names(r, 3)})"
            : $"Normal launches: the game changed {r.Drift.Count} of {r.Applied} setting(s) in the last launch ({RunResults.Names(r, 3)})"
        : Reason;

    /// <summary>The run's full evidence text for the research task (<see cref="RunResults.EvidenceText"/>), null without a run.</summary>
    public string? RunText => Run is { } r ? RunResults.EvidenceText(r) : null;

    /// <summary><see cref="RunText"/> of a Test preset (the research task's "Verify run" section), null when the run was not a test.</summary>
    public string? VerifyRunText => Source == FlagSource.Verify ? RunText : null;

    /// <summary><see cref="RunText"/> of a normal launch (the research task's "Normal launch" section, never "Verify run": the player may have changed settings).</summary>
    public string? LaunchRunText => Source == FlagSource.Passive ? RunText : null;
}
