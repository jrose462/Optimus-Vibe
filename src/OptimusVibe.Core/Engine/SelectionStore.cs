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

    private static string NoBigPictureKey(string device, string gameId) => Key(device, gameId) + "#no-big-picture";

    /// <summary>"Never use Big Picture for this game" (per device, kept beside the remembered preset).</summary>
    public bool GetNeverBigPicture(string device, string gameId) =>
        Load().ContainsKey(NoBigPictureKey(device, gameId));

    public void SetNeverBigPicture(string device, string gameId, bool never)
    {
        var all = Load();
        if (never) all[NoBigPictureKey(device, gameId)] = "1";
        else all.Remove(NoBigPictureKey(device, gameId));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(all, JsonOpts));
    }
}
