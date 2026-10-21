using System.Text;
using System.Text.Json;
using OptimusVibe.Logging;

namespace OptimusVibe.Launch;

/// <summary>
/// <c>probe-active.json</c> in the per-machine data dir: the games that are being mapped right now, each with an expiry
/// (<see cref="Lifetime"/> from when it was set). A run of such a game, including one Steam starts through the launch option, skips
/// applying anything so the mapping sees the game's own behaviour. Written atomically; never throws; a stale or corrupt file counts as "not mapping".
/// </summary>
public static class ProbeMarker
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Model
    {
        /// <summary>game id → expiry (UTC).</summary>
        public Dictionary<string, DateTime> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static string FileFor(string dataDir) => Path.Combine(dataDir, "probe-active.json");

    /// <summary>Marks the game as being mapped until <paramref name="now"/> + <see cref="Lifetime"/> (default: now = the clock).</summary>
    public static void Set(string dataDir, string gameId, DateTime? now = null)
    {
        lock (Gate)
        {
            var m = Load(dataDir, now);
            m.Games[gameId] = (now ?? DateTime.UtcNow) + Lifetime;
            Save(dataDir, m);
        }
    }

    public static void Clear(string dataDir, string gameId)
    {
        lock (Gate)
        {
            var m = Load(dataDir, DateTime.MinValue); // no pruning: the entry goes either way
            if (m.Games.Remove(gameId)) Save(dataDir, m);
        }
    }

    /// <summary>True while the game's marker has not expired.</summary>
    public static bool IsActive(string dataDir, string gameId, DateTime? now = null)
    {
        lock (Gate)
        {
            var t = now ?? DateTime.UtcNow;
            return Load(dataDir, t).Games.TryGetValue(gameId, out var expires) && expires > t;
        }
    }

    private static Model Load(string dataDir, DateTime? now)
    {
        var m = new Model();
        try
        {
            var file = FileFor(dataDir);
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<Model>(File.ReadAllText(file), Opts);
                var t = now ?? DateTime.UtcNow;
                if (loaded?.Games is { } games)
                    foreach (var (g, expires) in games)
                        if (expires.ToUniversalTime() > t) m.Games[g] = expires.ToUniversalTime();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ignoring unreadable {FileFor(dataDir)}: {ex.Message}");
        }
        return m;
    }

    private static void Save(string dataDir, Model m)
    {
        try
        {
            var file = FileFor(dataDir);
            if (m.Games.Count == 0)
            {
                if (File.Exists(file)) File.Delete(file);
                return;
            }
            Directory.CreateDirectory(dataDir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, Opts), new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not save {FileFor(dataDir)}: {ex.Message}");
        }
    }
}
