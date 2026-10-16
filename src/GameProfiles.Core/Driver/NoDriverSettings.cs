using GameProfiles.Config;
using GameProfiles.Logging;

namespace GameProfiles.Driver;

/// <summary>Used when the device has no supported GPU driver API: every request is skipped with a warning.</summary>
public sealed class NoDriverSettings : IDriverSettings
{
    public string Vendor => "none";

    public bool IsAvailable(out string reason)
    {
        reason = "No supported GPU driver settings API for this device.";
        return false;
    }

    public IReadOnlyList<DriverChange> Plan(GameDef game, IReadOnlyDictionary<string, string> settings)
    {
        if (settings.Count > 0)
            Log.Warn($"Driver settings ignored for {game.Name}: no supported GPU driver API on this device ({string.Join(", ", settings.Keys)}).");
        return Array.Empty<DriverChange>();
    }

    public string? Apply(GameDef game, IReadOnlyDictionary<string, string> settings)
    {
        Plan(game, settings);
        return null;
    }

    public void Restore(string token)
    {
    }
}
