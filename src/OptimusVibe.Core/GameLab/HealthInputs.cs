using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.GameLab;

/// <summary>
/// Everything one game's health on one device is judged from, gathered once (game page, post-session card, library tile). Plain data plus a
/// few derived facts; nothing here reads files except <see cref="FromRuns"/>. Run history, verdicts and builds are per device and local; the
/// flag and the presets come from the synced config.
/// </summary>
public sealed record HealthInputs
{
    public required string GameId { get; init; }
    public required string Device { get; init; }

    /// <summary>The game has a config file.</summary>
    public bool HasConfig { get; init; }

    /// <summary>The config has presets for this device.</summary>
    public bool HasPresets { get; init; }

    /// <summary>The effective "set in-game" flag (<see cref="GameFlags.Effective(GameDef, LibraryFile?)"/>), null when managed normally.</summary>
    public FlagDef? Flag { get; init; }

    /// <summary>The flag comes from the game file (<c>flagged:</c>, the lab's call) rather than the user's library.yaml.</summary>
    public bool FlagFromConfig { get; init; }

    /// <summary>The last run of any kind, and the last Test preset run.</summary>
    public RunResult? Last { get; init; }
    public RunResult? LastVerify { get; init; }

    /// <summary>The last normal runs, oldest first (<see cref="RunResultEntry.Recent"/>).</summary>
    public IReadOnlyList<RunResult> Recent { get; init; } = [];

    /// <summary>Consecutive overridden Test preset results (<see cref="RunResultEntry.OverriddenStreak"/>).</summary>
    public int OverriddenStreak { get; init; }

    /// <summary>Consecutive passively overridden normal runs (<see cref="RunResultEntry.PassiveOverriddenStreak"/>).</summary>
    public int PassiveOverriddenStreak { get; init; }

    /// <summary>The stored mapping run for this game, if any.</summary>
    public ProbeRecord? Mapping { get; init; }

    /// <summary>The newest lab request for the game (open ones first), and whether the lab is set up at all.</summary>
    public LabIssue? LabRequest { get; init; }
    public bool LabReady { get; init; }

    /// <summary>The local cloud-sync check for this game on this device.</summary>
    public CloudSyncReport? CloudSync { get; init; }

    /// <summary>When the lab's presets arrived on this device and against which build (<see cref="LabState.PresetsMade"/>).</summary>
    public PresetsMark? Presets { get; init; }

    /// <summary>The installed build now (<see cref="GameBuild"/>), null when unknown.</summary>
    public string? CurrentBuild { get; init; }

    /// <summary>This device keeps an autoSnapshot copy of the game's cloud-synced settings file.</summary>
    public bool HasAutoSnapshotCopy { get; init; }

    // ---------------------------------------------------------------- derived

    /// <summary>The passive verdict of every normal run in <see cref="Recent"/>, oldest first.</summary>
    public IReadOnlyList<PassiveVerdict> PassiveVerdicts => RunResults.PassiveVerdicts(Recent);

    /// <summary>The passive verdict of the newest normal run (Unknown when there is none).</summary>
    public PassiveVerdict LastPassive => PassiveVerdicts is { Count: > 0 } v ? v[^1] : PassiveVerdict.Unknown;

    /// <summary>Normal runs in a row, newest back, where the game kept every value.</summary>
    public int PassiveHeldStreak => RunResults.PassiveHeldStreak(Recent);

    /// <summary>The last Test preset run was held.</summary>
    public bool VerifyHeld => LastVerify is { } v && RunResults.Verdict(v) == RunVerdict.Held;

    /// <summary>
    /// The newest build the presets are known to work on: the build they arrived with, a held Test preset run's build, or a passively
    /// held normal run's build, whichever is newest and known. Null when none of them recorded a build.
    /// </summary>
    public string? BaselineBuild
    {
        get
        {
            var known = new List<(DateTime Utc, string Build)>();
            if (Presets is { BuildId: { Length: > 0 } made }) known.Add((Presets.MadeUtc, made));
            if (VerifyHeld && LastVerify!.Build is { Length: > 0 } verified) known.Add((LastVerify.Utc, verified));
            var verdicts = PassiveVerdicts;
            for (var i = 0; i < Recent.Count; i++)
                if (verdicts[i] == PassiveVerdict.Held && Recent[i].Build is { Length: > 0 } held) known.Add((Recent[i].Utc, held));
            return known.Count == 0 ? null : known.MaxBy(k => k.Utc).Build;
        }
    }

    /// <summary>The installed build differs from <see cref="BaselineBuild"/> (both known and of the same kind).</summary>
    public bool GameUpdated => GameBuild.Changed(BaselineBuild, CurrentBuild);

    /// <summary><see cref="GameUpdated"/> by a real store build id, not just an exe fingerprint (which only ever suggests).</summary>
    public bool GameUpdatedByStore => GameUpdated && GameBuild.IsStoreBuild(CurrentBuild);

    /// <summary>Either streak is long enough to suggest flagging the game "set in-game".</summary>
    public bool SuggestsFlagFromRuns => GameFlags.SuggestsFromVerify(OverriddenStreak) || GameFlags.SuggestsFromPassive(PassiveOverriddenStreak);

    /// <summary>Inputs for a game with its run history (last run, last verify run, the ring and both streaks) read from <c>run-results.json</c>.</summary>
    public static HealthInputs FromRuns(string dataDir, string gameId, string device)
    {
        var e = RunResults.Entry(dataDir, gameId, device);
        return new HealthInputs
        {
            GameId = gameId, Device = device,
            Last = e?.Last, LastVerify = e?.LastVerify, Recent = e?.Recent ?? [],
            OverriddenStreak = e?.OverriddenStreak ?? 0, PassiveOverriddenStreak = e?.PassiveOverriddenStreak ?? 0,
        };
    }
}
