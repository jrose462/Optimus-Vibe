using System.Text;
using System.Text.Json;
using OptimusVibe.Logging;

namespace OptimusVibe.GameLab;

/// <summary>A finished mapping run kept on this PC until it is sent.</summary>
public sealed class ProbeRecord
{
    public DateTime Utc { get; set; }

    /// <summary>The "### Mapping run" body, already tokenized and sanitized.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>Short lines of what the game changed by itself (pass A).</summary>
    public List<string> StateLines { get; set; } = [];

    /// <summary>Short lines of where settings changed (pass B).</summary>
    public List<string> SettingsLines { get; set; } = [];

    /// <summary>Binary files changed only in pass B.</summary>
    public List<string> OpaqueFiles { get; set; } = [];

    /// <summary>Pass B changed only binary files: the game keeps its settings in a store Optimus-Vibe cannot write, so "Flag as set in-game" is suggested.</summary>
    public bool SuggestSetInGame { get; set; }

    /// <summary>The one-line why of <see cref="SuggestSetInGame"/>.</summary>
    public string? SuggestReason { get; set; }
}

/// <summary>
/// <c>probe-results.json</c> in the per-machine data dir: the last mapping run per game (key like <c>steam:1234</c>), so a later request
/// carries it as evidence and the lab dialog knows whether the game was mapped. Written atomically under a cross-process lock (the wrapper and the app both write it); never throws; not synced.
/// </summary>
public static class ProbeStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Model
    {
        public Dictionary<string, ProbeRecord> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Passive pass A per game (<see cref="PassiveWatch"/>); absent in files written before it existed.</summary>
        public Dictionary<string, PassiveEntry> Passive { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static string FileFor(string dataDir) => Path.Combine(dataDir, "probe-results.json");

    public static ProbeRecord FromEvidence(ProbeEvidence e, DateTime? utc = null) => new()
    {
        Utc = utc ?? DateTime.UtcNow,
        Evidence = e.Render(),
        StateLines = e.StateLines.ToList(),
        SettingsLines = e.SettingsLines.ToList(),
        OpaqueFiles = e.OpaqueFiles.ToList(),
        SuggestSetInGame = e.SuggestsSetInGame,
        SuggestReason = e.SuggestionReason,
    };

    public static void Record(string dataDir, string gameKey, ProbeRecord record) => Update(dataDir, m => m.Games[gameKey] = record);

    public static ProbeRecord? Get(string dataDir, string gameKey)
    {
        lock (Gate) return Load(dataDir)?.Games.GetValueOrDefault(gameKey);
    }

    /// <summary>The passive pass A bookkeeping of a game (its last normal-launch diffs and slow strikes); null when nothing was watched yet.</summary>
    public static PassiveEntry? GetPassive(string dataDir, string gameKey)
    {
        lock (Gate) return Load(dataDir)?.Passive.GetValueOrDefault(gameKey);
    }

    /// <summary>Changes a game's passive entry (created when missing) under the store's lock and saves it. Never throws.</summary>
    public static void UpdatePassive(string dataDir, string gameKey, Action<PassiveEntry> change) => Update(dataDir, m =>
    {
        if (!m.Passive.TryGetValue(gameKey, out var entry)) m.Passive[gameKey] = entry = new PassiveEntry();
        change(entry);
    });

    /// <summary>
    /// What the game rewrote by itself in at least <see cref="PassiveWatch.MinSessions"/> of its last normal launches on this PC (the passive
    /// pass A), with the number of launches it was read from; null when there is not enough yet or it is older than <paramref name="maxAge"/>.
    /// </summary>
    public static PassivePassA? PassiveA(string dataDir, string gameKey, TimeSpan? maxAge = null, DateTime? now = null)
    {
        var entry = GetPassive(dataDir, gameKey);
        if (entry is null) return null;
        var runs = entry.Runs.Where(r => maxAge is not { } age || (now ?? DateTime.UtcNow) - r.Utc <= age).ToList();
        return PassiveWatch.Intersect(runs.Select(r => r.Diff).ToList()) is { } diff
            ? new PassivePassA(diff, runs.Count, runs.Max(r => r.Utc))
            : null;
    }

    /// <summary>The cross-process mutex around read-modify-write: the wrapper (after each session) and the app (Map this game) both write the file.</summary>
    private const string FileMutexName = @"Local\OptimusVibe.probe-results";

    private static void Update(string dataDir, Action<Model> change)
    {
        lock (Gate)
        {
            using var fileLock = FileLock.Acquire();
            try
            {
                if (Load(dataDir) is not { } m)
                {
                    // never replace what is there with an empty model because it could not be read just now (mid-replace, a scanner)
                    Log.Warn($"Not saving {FileFor(dataDir)}: it could not be read just now");
                    return;
                }
                change(m);
                Directory.CreateDirectory(dataDir);
                var tmp = $"{FileFor(dataDir)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(tmp, JsonSerializer.Serialize(m, Opts), new UTF8Encoding(false));
                    File.Move(tmp, FileFor(dataDir), overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not save {FileFor(dataDir)}: {ex.Message}");
            }
        }
    }

    /// <summary>The stored model; an empty one when there is no file or it is not valid JSON; null when it exists but could not be read now.</summary>
    private static Model? Load(string dataDir)
    {
        var file = FileFor(dataDir);
        try
        {
            if (!File.Exists(file)) return new Model();
            var m = JsonSerializer.Deserialize<Model>(File.ReadAllText(file), Opts);
            if (m is null) return new Model();
            return new Model
            {
                Games = new Dictionary<string, ProbeRecord>(m.Games ?? [], StringComparer.OrdinalIgnoreCase),
                Passive = new Dictionary<string, PassiveEntry>(m.Passive ?? [], StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (JsonException ex)
        {
            Log.Warn($"Ignoring unreadable {file}: {ex.Message}");
            return new Model();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read {file}: {ex.Message}");
            return null;
        }
    }

    private sealed class FileLock : IDisposable
    {
        private Mutex? _mutex;

        public static FileLock Acquire()
        {
            var l = new FileLock();
            try
            {
                var mutex = new Mutex(initiallyOwned: false, FileMutexName);
                bool owned;
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { owned = true; } // the holder died without releasing: it is ours now
                if (owned) l._mutex = mutex;
                else
                {
                    mutex.Dispose();
                    Log.Warn("probe-results.json: another process held it too long, going on without the lock");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                Log.Warn($"probe-results.json: no cross-process lock ({ex.Message})");
            }
            return l;
        }

        public void Dispose()
        {
            if (_mutex is null) return;
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned by this thread */ }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
