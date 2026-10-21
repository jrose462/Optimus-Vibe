using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.GameLab;

/// <summary>Which post-session card a finished session brings up (at most one, in the order <see cref="PostSession.For"/> checks them).</summary>
public enum PostSessionKind
{
    /// <summary>The lab asked for evidence and nothing is mapped yet: one launch where the player changes a few graphics options.</summary>
    NeedsMapping,

    /// <summary>The game passively overrode the preset in a row: it may not accept settings from outside.</summary>
    Recurring,

    /// <summary>The game updated since its presets were made.</summary>
    Updated,

    /// <summary>The game changed some of the preset's settings during the session.</summary>
    Changed,

    /// <summary>The game kept the whole preset, the first time since the presets arrived or the game updated.</summary>
    Held,
}

/// <summary>A button on the post-session card. Each runs an existing game-page action (its usual confirm card opens).</summary>
public enum PostSessionAction
{
    None,

    /// <summary>
    /// Keep what the game left as a manual save (the test result's "Make a manual save"). It writes, so it asks on the card first
    /// (<see cref="PostSession.Confirms"/>), and a card that offers it never takes focus by itself.
    /// </summary>
    KeepThese,

    /// <summary>List the settings the game changed, in the card.</summary>
    SeeWhich,

    FlagSetInGame,

    /// <summary>Refresh presets: the game lab researches the game again.</summary>
    AskLab,

    MapGame,
}

/// <summary>The inline card on the game page after a session: one line, one dim line, at most two actions, and "Not now" (B).</summary>
public sealed record PostSessionCard
{
    public required PostSessionKind Kind { get; init; }
    public required RunResult Run { get; init; }
    public required string Text { get; init; }
    public string? Why { get; init; }
    public PostSessionAction Primary { get; init; }
    public PostSessionAction Secondary { get; init; }

    /// <summary>"Not now" snoozes this kind of card for the game (<see cref="PostSession.SnoozeDays"/>); the one-off "kept all" card just closes.</summary>
    public bool CanSnooze => Kind != PostSessionKind.Held;

    /// <summary>
    /// The card asks for something, so it takes focus when the player comes back to the game's page. The "kept all" card never does, and neither
    /// does one whose first button would write the player's settings ("Keep these"): a reflex A press on the way back from the game must not
    /// land on it.
    /// </summary>
    public bool TakesFocus => Kind != PostSessionKind.Held && Primary is not (PostSessionAction.None or PostSessionAction.KeepThese);

    /// <summary>
    /// The card has a button for this health action (<see cref="PostSession.AsHealthAction"/>), so the panel's own button for it (the suggested
    /// one, or its row under "More") steps aside while the card is open: each action is on screen once.
    /// </summary>
    public bool Offers(HealthAction action) =>
        action != HealthAction.None && (PostSession.AsHealthAction(Primary) == action || PostSession.AsHealthAction(Secondary) == action);
}

/// <summary>
/// The post-session card: which session is new (<see cref="Detect"/>, from <c>run-results.json</c>, which the headless wrapper writes when the
/// game exits) and what to say about it (<see cref="For"/>, from the game's health). Pure; the app watches the file and shows the card.
/// </summary>
public static class PostSession
{
    /// <summary>How long "Not now" keeps a kind of card away for a game.</summary>
    public const int SnoozeDays = 3;

    /// <summary>The only file that signals a finished session (the data dir also sees <c>state-*.json</c> and <c>.tmp</c> files during runs).</summary>
    public const string RunResultsFileName = "run-results.json";

    public static string SnoozeKey(string gameId, PostSessionKind kind) => $"{gameId}|{kind}";

    /// <summary>
    /// The card actions that ask on the card itself before they run (the others open their own command's confirm card): "Keep these" writes a
    /// manual save of what the game's files hold now and makes it active.
    /// </summary>
    public static bool Confirms(PostSessionAction action) => action == PostSessionAction.KeepThese;

    /// <summary>The dim line while "Keep these" asks (the files may have changed since the session the card is about).</summary>
    public const string KeepConfirmText = "This saves the game's settings as they are now as a manual save and makes it the active setting.";

    /// <summary>The button that confirms "Keep these".</summary>
    public const string KeepConfirmAction = "Save and use";

    /// <summary>
    /// The sessions of <paramref name="fresh"/> not offered yet in this app run, recording them in <paramref name="offered"/> (game id, run time).
    /// The seen marks in gamelab-state.json normally keep a session from coming back; this keeps it away too while that file cannot be written.
    /// </summary>
    public static IReadOnlyList<RunResult> NotOfferedYet(IEnumerable<RunResult> fresh, IDictionary<string, DateTime> offered)
    {
        var result = new List<RunResult>();
        foreach (var run in fresh)
        {
            if (offered.TryGetValue(run.Game, out var last) && run.Utc <= last) continue;
            offered[run.Game] = run.Utc;
            result.Add(run);
        }
        return result;
    }

    /// <summary>A file-system event on <paramref name="path"/> may mean a session finished: only <c>run-results.json</c> itself counts.</summary>
    public static bool IsSessionSignal(string? path) =>
        !string.IsNullOrEmpty(path) && string.Equals(Path.GetFileName(path), RunResultsFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The newest normal run of an entry once it is final (the capture prompt answered); null while the newest is still pending or there is
    /// none. Test preset runs never count: their own result card covers them.
    /// </summary>
    public static RunResult? NewestFinished(RunResultEntry? entry) =>
        entry?.Recent is { Count: > 0 } recent && recent[^1] is { IsVerify: false, Capture: not null } run ? run : null;

    /// <summary>What <see cref="Detect"/> found: the sessions to offer a card for, and the "seen" marks to store.</summary>
    public sealed record Detection(IReadOnlyList<RunResult> Fresh, IReadOnlyDictionary<string, DateTime> Seen)
    {
        public bool Changed => Seen.Count > 0;
    }

    /// <summary>
    /// The sessions finished since the last look: per game, the newest final normal run when it is newer than <paramref name="seen"/>. Before
    /// the first look (<paramref name="baselined"/> false) every session is only marked seen, so old sessions never bring up a card. A game
    /// with no mark yet (first played since) does count.
    /// </summary>
    public static Detection Detect(IReadOnlyDictionary<string, RunResultEntry> entries, IReadOnlyDictionary<string, DateTime> seen, bool baselined)
    {
        var fresh = new List<RunResult>();
        var marks = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gameId, entry) in entries)
        {
            if (NewestFinished(entry) is not { } run) continue;
            if (seen.TryGetValue(gameId, out var last) && run.Utc <= last) continue;
            marks[gameId] = run.Utc;
            if (baselined) fresh.Add(run);
        }
        return new Detection(fresh.OrderBy(r => r.Utc).ToList(), marks);
    }

    /// <summary>
    /// The card for a finished session (<paramref name="run"/>) of a game, or null when there is nothing worth saying (a clean run after the
    /// first, a run of another preset, a flagged game, a card kind snoozed). In order:
    /// <list type="number">
    /// <item><see cref="PostSessionKind.NeedsMapping"/>: an open lab request needs evidence, nothing is mapped and the game can be launched;</item>
    /// <item><see cref="PostSessionKind.Recurring"/>: this session was passively overridden and so were the ones before it, enough to suggest the
    /// "set in-game" flag (<see cref="GameFlags.SuggestsFromPassive"/>);</item>
    /// <item><see cref="PostSessionKind.Updated"/>: the health says the game updated since its presets, and the lab can be asked;</item>
    /// <item><see cref="PostSessionKind.Changed"/>: the game changed some of the preset's settings this session (as the health counts it);</item>
    /// <item><see cref="PostSessionKind.Held"/>: the game kept everything, the first time since the presets arrived or the build changed.</item>
    /// </list>
    /// Nothing here files, flags, saves or launches: every action is the player's press, with its usual confirm card ("Keep these" asks on the
    /// card itself, <see cref="Confirms"/>).
    /// </summary>
    public static PostSessionCard? For(HealthInputs i, GameHealth health, RunResult run, string name, IReadOnlySet<PostSessionKind>? snoozed = null)
    {
        if (i.Flag is not null || health.State == HealthState.Flagged || run.IsVerify) return null;
        bool Off(PostSessionKind k) => snoozed?.Contains(k) == true;
        var open = i.LabRequest is { IsOpen: true } issue ? issue : null;

        // 1. the lab is stuck without evidence
        if (open is { Status: LabStatus.NeedsEvidence } && i.Mapping is null && i.CanLaunch && !Off(PostSessionKind.NeedsMapping))
            return new PostSessionCard
            {
                Kind = PostSessionKind.NeedsMapping, Run = run, Text = $"The lab needs to know where {name} keeps its settings.",
                Why = "Map it: one launch where you change a few graphics options, then quit.", Primary = PostSessionAction.MapGame,
            };

        if (!i.HasPresets || !GameHealth.CountsForCurrentPreset(i, run)) return null;
        var canAsk = i.LabReady && i.HasConfig && open is null;
        var verdict = VerdictOf(i, run);

        // 2. the game keeps resetting the preset
        var streak = GameHealth.PassiveOverridden(i);
        if (verdict == PassiveVerdict.Overridden && GameFlags.SuggestsFromPassive(streak) && health.FlagReason is not null && !Off(PostSessionKind.Recurring))
            return new PostSessionCard
            {
                Kind = PostSessionKind.Recurring, Run = run,
                Text = $"{name} reset the preset's settings after your last {streak} sessions, so it may not accept them from outside.",
                Why = $"Last session: {RunResults.Names(run, 3)}",
                Primary = PostSessionAction.FlagSetInGame, Secondary = canAsk ? PostSessionAction.AskLab : PostSessionAction.None,
            };

        // 3. the game updated since the presets were made
        if (health.State == HealthState.GameUpdated && canAsk && !Off(PostSessionKind.Updated))
            return new PostSessionCard
            {
                Kind = PostSessionKind.Updated, Run = run, Text = $"{name} updated since these presets were made.", Why = health.Why,
                Primary = PostSessionAction.AskLab,
            };

        // 4. the game changed some settings this session
        if (GameHealth.ChangedByGame(run, verdict) &&
            health.State is HealthState.GameChangedSettings or HealthState.CantBeSetFromOutside or HealthState.GameUpdated &&
            !Off(PostSessionKind.Changed))
        {
            // offer to keep them only when the wrapper never asked (--no-prompt). The player already answered at the capture prompt (don't save)
            // or turned that question off for this game (don't ask again / off): don't ask again here, just show which
            var keep = run.Capture == CaptureAnswer.NotAsked && i.HasConfig;
            return new PostSessionCard
            {
                Kind = PostSessionKind.Changed, Run = run,
                Text = $"Last session the game changed {run.Drift.Count} of {run.Applied} settings ({RunResults.Names(run, 2).TrimEnd('.')}).",
                Why = keep ? "Keep them as a manual save if you set them yourself." : null,
                Primary = keep ? PostSessionAction.KeepThese : PostSessionAction.SeeWhich,
                Secondary = keep ? PostSessionAction.SeeWhich : PostSessionAction.None,
            };
        }

        // 5. the first clean session of these presets (or of this build)
        if (verdict == PassiveVerdict.Held && FirstHeld(i, run) && !Off(PostSessionKind.Held))
            return new PostSessionCard
            {
                Kind = PostSessionKind.Held, Run = run,
                Text = run.Applied == 1 ? $"{name} kept the preset's setting." : $"{name} kept all {run.Applied} settings.",
                Why = "Optimus-Vibe keeps checking after each session.",
            };
        return null;
    }

    /// <summary>The button text of a card action.</summary>
    public static string ActionText(PostSessionAction action) => action switch
    {
        PostSessionAction.KeepThese => "Keep these",
        PostSessionAction.SeeWhich => "See which",
        PostSessionAction.FlagSetInGame => "Flag as set in-game",
        PostSessionAction.AskLab => "Ask the game lab to re-check",
        PostSessionAction.MapGame => "Map it",
        _ => "",
    };

    /// <summary>The health action a card action runs (so the panel's suggested button steps aside while the card offers the same).</summary>
    public static HealthAction AsHealthAction(PostSessionAction action) => action switch
    {
        PostSessionAction.FlagSetInGame => HealthAction.FlagSetInGame,
        PostSessionAction.AskLab => HealthAction.RefreshPresets,
        PostSessionAction.MapGame => HealthAction.MapGame,
        _ => HealthAction.None,
    };

    /// <summary>The passive verdict of <paramref name="run"/> as the ring judges it (with the run before it), or on its own when it isn't in the ring.</summary>
    private static PassiveVerdict VerdictOf(HealthInputs i, RunResult run)
    {
        var k = IndexOf(i.Recent, run);
        return k >= 0 ? i.PassiveVerdicts[k] : RunResults.Passive(run, null);
    }

    private static int IndexOf(IReadOnlyList<RunResult> recent, RunResult run)
    {
        for (var k = recent.Count - 1; k >= 0; k--)
            if (ReferenceEquals(recent[k], run) || (recent[k].Utc == run.Utc && !recent[k].IsVerify)) return k;
        return -1;
    }

    /// <summary>
    /// No earlier held launch or held Test preset of the current preset, on the same build, since the presets arrived: this is the first time it
    /// is known to work.
    /// </summary>
    private static bool FirstHeld(HealthInputs i, RunResult run)
    {
        var since = i.Presets?.MadeUtc ?? DateTime.MinValue;
        bool Earlier(RunResult r) => r.Utc < run.Utc && r.Utc >= since && GameHealth.CountsForCurrentPreset(i, r) &&
                                     string.Equals(r.Build, run.Build, StringComparison.Ordinal);
        var verdicts = i.PassiveVerdicts;
        for (var k = 0; k < i.Recent.Count; k++)
            if (verdicts[k] == PassiveVerdict.Held && Earlier(i.Recent[k])) return false;
        return !(i.LastVerify is { } v && RunResults.Verdict(v) == RunVerdict.Held && Earlier(v));
    }
}
