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
/// carries it as evidence and the lab dialog knows whether the game was mapped. Written atomically; never throws; not synced.
/// </summary>
public static class ProbeStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Model
    {
        public Dictionary<string, ProbeRecord> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    public static void Record(string dataDir, string gameKey, ProbeRecord record)
    {
        lock (Gate)
        {
            try
            {
                var m = Load(dataDir);
                m.Games[gameKey] = record;
                Directory.CreateDirectory(dataDir);
                var tmp = FileFor(dataDir) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(m, Opts), new UTF8Encoding(false));
                File.Move(tmp, FileFor(dataDir), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not save {FileFor(dataDir)}: {ex.Message}");
            }
        }
    }

    public static ProbeRecord? Get(string dataDir, string gameKey)
    {
        lock (Gate) return Load(dataDir).Games.GetValueOrDefault(gameKey);
    }

    private static Model Load(string dataDir)
    {
        try
        {
            var file = FileFor(dataDir);
            if (File.Exists(file))
            {
                var m = JsonSerializer.Deserialize<Model>(File.ReadAllText(file), Opts);
                if (m is not null) return new Model { Games = new Dictionary<string, ProbeRecord>(m.Games, StringComparer.OrdinalIgnoreCase) };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ignoring unreadable {FileFor(dataDir)}: {ex.Message}");
        }
        return new Model();
    }
}
