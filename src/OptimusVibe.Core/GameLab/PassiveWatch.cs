using System.Text.Json.Serialization;
using OptimusVibe.Logging;

namespace OptimusVibe.GameLab;

/// <summary>What one normal launch changed in the game's watched folders and registry keys (a passive pass A).</summary>
public sealed class PassiveRun
{
    public DateTime Utc { get; set; }
    public ProbeDiff Diff { get; set; } = new([], [], []);

    /// <summary>The slower of the two snapshots, in milliseconds (the handheld cost of watching).</summary>
    public int SnapshotMs { get; set; }
}

/// <summary>A game's passive pass A bookkeeping in <c>probe-results.json</c>: the last <see cref="PassiveWatch.MaxRuns"/> diffs and the slow strikes.</summary>
public sealed class PassiveEntry
{
    /// <summary>Oldest first.</summary>
    public List<PassiveRun> Runs { get; set; } = [];

    /// <summary>Sessions whose snapshots were too slow or hit the bounds; at <see cref="PassiveWatch.MaxStrikes"/> the game is no longer watched.</summary>
    public int Strikes { get; set; }

    [JsonIgnore] public bool Disabled => Strikes >= PassiveWatch.MaxStrikes;
}

/// <summary>
/// What the run itself wrote before the "before" snapshot (the preset's keys, whole-file saves and registry values), left out of a passive
/// session's diff. A real pass A launches the game with nothing written; a normal launch writes the preset first, so a game that resets or
/// normalises a preset key on every launch would otherwise show that key as "rewritten every session" (state, never restore) and hide it
/// from pass B. File paths are tokenized like the snapshot's.
/// </summary>
public sealed class PassiveExclusions
{
    private readonly Dictionary<string, HashSet<string>?> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>?> _registry = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => _files.Count == 0 && _registry.Count == 0;

    /// <summary>Leaves out <paramref name="keys"/> of a file (null = the whole file: written whole, or its keys can't be matched).</summary>
    public PassiveExclusions File(string token, IEnumerable<string>? keys)
    {
        Add(_files, token, keys);
        return this;
    }

    /// <summary>Leaves out <paramref name="names"/> under a registry key (null = every value under it).</summary>
    public PassiveExclusions Registry(string key, IEnumerable<string>? names)
    {
        Add(_registry, NormalizeKey(key), names?.SelectMany(n => n is "(default)" or "" ? new[] { "", "(default)" } : [n]));
        return this;
    }

    private static void Add(Dictionary<string, HashSet<string>?> map, string id, IEnumerable<string>? keys)
    {
        if (keys is null) { map[id] = null; return; }
        if (map.TryGetValue(id, out var have) && have is null) return; // already the whole thing
        have ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        have.UnionWith(keys);
        map[id] = have;
    }

    /// <summary>HKEY_CURRENT_USER\... and HKCU\... are the same key.</summary>
    public static string NormalizeKey(string key)
    {
        var k = key.Replace('/', '\\').TrimEnd('\\');
        return k.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ? "HKCU" + k["HKEY_CURRENT_USER".Length..] : k;
    }

    /// <summary>
    /// The diff without what the run wrote: a whole excluded file drops out; a text file loses the excluded keys and drops out when none are
    /// left (or when its changes are plain lines or unreadable, which can't be told apart from the written keys); registry values by key and name.
    /// </summary>
    public ProbeDiff Apply(ProbeDiff diff)
    {
        if (IsEmpty) return diff;
        var files = new List<FileChange>();
        foreach (var f in diff.Files)
        {
            if (!_files.TryGetValue(f.Path, out var keys)) { files.Add(f); continue; }
            if (keys is null || !f.IsText || f.Keys.Count == 0 || f.Keys.Any(k => k.Key == "(line)")) continue;
            var left = f.Keys.Where(k => !keys.Contains(k.Key)).ToList();
            if (left.Count == 0) continue;
            files.Add(f with { Keys = left });
        }
        var registry = new List<RegistryChange>();
        foreach (var r in diff.Registry)
        {
            if (!_registry.TryGetValue(NormalizeKey(r.Key), out var names)) { registry.Add(r); continue; }
            if (names is not null && !names.Contains(r.Name)) registry.Add(r);
        }
        return diff with { Files = files, Registry = registry };
    }
}

/// <summary>The passive pass A of a game: what it rewrote in at least <see cref="PassiveWatch.MinSessions"/> of <paramref name="Launches"/> normal launches.</summary>
public sealed record PassivePassA(ProbeDiff Diff, int Launches, DateTime LastUtc);

/// <summary>
/// Pass A of "Map this game" folded into normal launches: the runner fingerprints the game's settings folders and registry keys (the same
/// bounded, read-only <see cref="ProbeSession"/> snapshot) after the preset, display and driver are applied and again once the game has
/// exited, and keeps the diff. A single session may hold the player's own edits, so only files and keys that changed in at least
/// <see cref="MinSessions"/> of the last <see cref="MaxRuns"/> sessions count as "rewritten every session" (<see cref="Intersect"/>).
/// Stays on this PC until a request is filed. A game whose snapshots take longer than <see cref="SlowSnapshot"/> (or hit the bounds) in
/// <see cref="MaxStrikes"/> sessions is no longer watched. Never throws.
/// </summary>
public sealed class PassiveWatch
{
    public const int MaxRuns = 3;
    public const int MinSessions = 2;
    public const int MaxStrikes = 2;
    public static readonly TimeSpan SlowSnapshot = TimeSpan.FromSeconds(5);

    private readonly string _dataDir;
    private readonly string _key;
    private readonly Func<ProbeSnapshot> _snapshot;
    private readonly ProbeLimits _limits;
    private readonly TimeProvider _time;
    private readonly ProbeSnapshot _before;
    private readonly TimeSpan _beforeTook;
    private readonly PassiveExclusions? _exclude;

    private PassiveWatch(string dataDir, string key, Func<ProbeSnapshot> snapshot, ProbeLimits limits, TimeProvider time, ProbeSnapshot before, TimeSpan took,
        PassiveExclusions? exclude)
    {
        _exclude = exclude;
        _dataDir = dataDir;
        _key = key;
        _snapshot = snapshot;
        _limits = limits;
        _time = time;
        _before = before;
        _beforeTook = took;
    }

    /// <summary>
    /// Takes the "before" snapshot. Null (nothing watched) when the game is no longer watched on this PC, the snapshot hit the bounds
    /// (a strike), or anything failed. <paramref name="exclude"/>: what the run itself wrote, left out of the kept diff.
    /// </summary>
    public static PassiveWatch? Begin(string dataDir, string gameKey, Func<ProbeSnapshot> snapshot, ProbeLimits? limits = null, TimeProvider? time = null,
        PassiveExclusions? exclude = null)
    {
        try
        {
            time ??= TimeProvider.System;
            if (ProbeStore.GetPassive(dataDir, gameKey) is { Disabled: true })
            {
                Log.Info($"session watch: {gameKey} is not watched on this PC (its snapshots were too slow or too big)");
                return null;
            }
            var (before, took) = Timed(snapshot, time);
            if (before.Truncated)
            {
                Strike(dataDir, gameKey, $"the snapshot hit its bounds ({before.Files.Count} file(s))");
                return null;
            }
            return new PassiveWatch(dataDir, gameKey, snapshot, limits ?? new ProbeLimits(), time, before, took, exclude);
        }
        catch (Exception ex)
        {
            Log.Warn($"session watch: could not start for {gameKey}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// After the game exited: takes the "after" snapshot and keeps the diff (the newest <see cref="MaxRuns"/>). <paramref name="skipWhy"/> set =
    /// the session can't tell what the game rewrites (game never seen, cancelled, files written by the runner while it ran): nothing is kept,
    /// but a slow "before" snapshot still counts as a strike. Returns the kept run, or null.
    /// </summary>
    public PassiveRun? End(string? skipWhy = null)
    {
        try
        {
            if (skipWhy is not null)
            {
                Log.Info($"session watch: {_key}: {skipWhy}, not kept");
                if (_beforeTook > SlowSnapshot) Strike(_dataDir, _key, $"the snapshot took {_beforeTook.TotalSeconds:0.#} s");
                return null;
            }
            var (after, took) = Timed(_snapshot, _time);
            var slowest = took > _beforeTook ? took : _beforeTook;
            if (after.Truncated)
            {
                Strike(_dataDir, _key, $"the snapshot hit its bounds ({after.Files.Count} file(s))");
                return null;
            }
            var run = new PassiveRun
            {
                Utc = _time.GetUtcNow().UtcDateTime,
                Diff = _exclude is null ? ProbeSession.Diff(_before, after, _limits) : _exclude.Apply(ProbeSession.Diff(_before, after, _limits)),
                SnapshotMs = (int)slowest.TotalMilliseconds,
            };
            var slow = slowest > SlowSnapshot;
            ProbeStore.UpdatePassive(_dataDir, _key, e =>
            {
                e.Runs.Add(run);
                if (e.Runs.Count > MaxRuns) e.Runs.RemoveRange(0, e.Runs.Count - MaxRuns);
                if (slow) e.Strikes++;
            });
            Log.Info($"session watch: {_key}: the game changed {run.Diff.Files.Count} file(s) and {run.Diff.Registry.Count} registry value(s); " +
                     $"snapshots {slowest.TotalSeconds:0.#} s{(slow ? " (slow)" : "")}");
            return run;
        }
        catch (Exception ex)
        {
            Log.Warn($"session watch: could not finish for {_key}: {ex.Message}");
            return null;
        }
    }

    private static (ProbeSnapshot Snap, TimeSpan Took) Timed(Func<ProbeSnapshot> snapshot, TimeProvider time)
    {
        var start = time.GetTimestamp();
        var snap = snapshot();
        return (snap, time.GetElapsedTime(start));
    }

    private static void Strike(string dataDir, string key, string why)
    {
        var strikes = 0;
        ProbeStore.UpdatePassive(dataDir, key, e => strikes = ++e.Strikes);
        Log.Info($"session watch: {key}: {why} (strike {strikes} of {MaxStrikes}{(strikes >= MaxStrikes ? ", no longer watched" : "")})");
    }

    // ------------------------------------------------------------ intersection

    /// <summary>
    /// What changed in at least <paramref name="min"/> of the sessions (oldest first): files by path (for text files with key changes, only the keys
    /// that recur; a text file none of whose keys recur is a one-off edit and drops out, unless its changes are plain lines, which carry their value in their
    /// identity: then the file itself recurs), and registry values by key and name. The newest
    /// session's values are kept. Null when there are fewer than <paramref name="min"/> sessions; an empty diff means the game rewrites nothing.
    /// </summary>
    public static ProbeDiff? Intersect(IReadOnlyList<ProbeDiff> sessions, int min = MinSessions)
    {
        if (sessions.Count < Math.Max(1, min)) return null;
        var files = new List<FileChange>();
        var byPath = sessions.SelectMany((d, i) => d.Files.Select(f => (Index: i, File: f)))
            .GroupBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byPath)
        {
            var seen = group.GroupBy(x => x.Index).Select(g => g.Last().File).ToList(); // one per session, oldest first
            if (seen.Count < min) continue;
            var latest = seen[^1];
            if (!seen.Any(f => f.Keys.Count > 0))
            {
                files.Add(latest);
                continue;
            }
            var recurring = seen.SelectMany(f => f.Keys.Select(KeyId).Distinct(StringComparer.Ordinal))
                .GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() >= min).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            if (recurring.Count == 0)
            {
                // a plain line carries its value in its identity, so a line rewritten with a new value every session (a timestamp, a counter)
                // never recurs as such: a file of plain-line changes that changed in enough sessions is rewritten every session
                if (seen.All(f => f.Keys.All(k => k.Key == "(line)"))) files.Add(latest);
                continue;
            }
            var keys = new List<KeyChange>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Enumerable.Reverse(seen))
                foreach (var k in f.Keys)
                    if (recurring.Contains(KeyId(k)) && taken.Add(KeyId(k))) keys.Add(k);
            files.Add(latest with { Keys = keys.OrderBy(k => k.Key, StringComparer.Ordinal).ToList(), KeysOmitted = 0 });
        }

        var registry = sessions.SelectMany((d, i) => d.Registry.Select(r => (Index: i, Change: r)))
            .GroupBy(x => (x.Change.Key.ToLowerInvariant(), x.Change.Name.ToLowerInvariant()))
            .Where(g => g.Select(x => x.Index).Distinct().Count() >= min)
            .Select(g => g.Last().Change)
            .OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var latestDiff = sessions[^1];
        var watched = sessions.SelectMany(d => d.Watched ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ProbeDiff(files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), registry, latestDiff.Notes.ToList(), watched);
    }

    /// <summary>A key's identity across sessions: its name, or for an unrecognised text file ("(line)") the line itself.</summary>
    private static string KeyId(KeyChange k) => k.Key == "(line)" ? $"(line)\u0001{k.Before}\u0001{k.After}" : k.Key;
}
