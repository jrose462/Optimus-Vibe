using GameProfiles.Config;
using GameProfiles.Logging;

namespace GameProfiles.Driver;

/// <summary>
/// AMD (ROG Xbox Ally X): ADLX exposes only GLOBAL 3D settings (Chill, FRTC, Anti-Lag, RSR, AFMF, Boost,
/// Image Sharpening), so the intended design is apply-before-launch / restore-after-exit with a JSON restore token.
/// Not implemented yet: see docs/amd-adlx.md for the researched native call path (amdadlx64.dll + vtables).
/// </summary>
public sealed class AmdAdlxManager : IDriverSettings
{
    internal const string NotImplementedReason = "AMD ADLX support not implemented yet (see docs/amd-adlx.md)";

    public string Vendor => "amd";

    public bool IsAvailable(out string reason)
    {
        reason = NotImplementedReason;
        return false;
    }

    public IReadOnlyList<DriverChange> Plan(GameDef game, IReadOnlyDictionary<string, string> settings)
    {
        var changes = new List<DriverChange>();
        // Current values cannot be read until ADLX is wired up, so every requested setting is reported with Current = null.
        foreach (var (key, value) in settings)
            changes.Add(new DriverChange(key, null, value ?? ""));
        return changes;
    }

    public string? Apply(GameDef game, IReadOnlyDictionary<string, string> settings)
    {
        if (settings.Count > 0)
            Log.Warn($"AMD settings for {game.Name} not applied: {NotImplementedReason}. Requested: " +
                     string.Join(", ", settings.Select(kv => $"{kv.Key}={kv.Value}")));
        return null;
    }

    public void Restore(string token)
    {
        if (!string.IsNullOrWhiteSpace(token))
            Log.Warn($"AMD restore token ignored: {NotImplementedReason}.");
    }
}
