using System.Text.Json;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Engine;

/// <summary>
/// Which preset each game uses on this machine ("Pick in app, remembered"). Stored locally in
/// %LOCALAPPDATA%\OptimusVibe\selections.json, keyed by device and game, so it is never fought over in git.
/// </summary>
public sealed class SelectionStore(string file)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string Key(string device, string gameId) => $"{device}/{gameId}";

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignoring unreadable {file}: {ex.Message}");
            return new();
        }
    }

    public PresetRef? Get(string device, string gameId) =>
        Load().TryGetValue(Key(device, gameId), out var v) ? PresetRef.Parse(v) : null;

    public void Set(string device, string gameId, PresetRef? preset)
    {
        var all = Load();
        if (preset is null) all.Remove(Key(device, gameId));
        else all[Key(device, gameId)] = preset.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(all, JsonOpts));
    }
}
