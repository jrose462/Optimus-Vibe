using OptimusVibe.Config;

namespace OptimusVibe.Driver;

/// <summary>One driver setting the tool would set. <c>Current == Target</c> means it is already in place.</summary>
public sealed record DriverChange(string Setting, string? Current, string Target);

/// <summary>GPU-vendor driver settings (NVIDIA DRS per-exe profile, AMD ADLX global 3D settings, or none).</summary>
public interface IDriverSettings
{
    /// <summary>"nvidia" | "amd" | "none"</summary>
    string Vendor { get; }

    bool IsAvailable(out string reason);

    /// <summary>
    /// Reports every requested setting with its current and target value (read-only).
    /// Unknown keys or invalid values are reported via Log.Warn and skipped, never thrown.
    /// </summary>
    IReadOnlyList<DriverChange> Plan(GameDef game, IReadOnlyDictionary<string, string> settings);

    /// <summary>
    /// Applies the settings. Returns a restore token if the change is temporary and must be undone
    /// after the game exits, or null if there is nothing to undo.
    /// </summary>
    string? Apply(GameDef game, IReadOnlyDictionary<string, string> settings);

    void Restore(string token);
}

public static class DriverSettings
{
    public static IDriverSettings For(string vendor) => (vendor ?? "").Trim().ToLowerInvariant() switch
    {
        "nvidia" => new NvidiaDrsManager(),
        "amd" => new AmdAdlxManager(),
        _ => new NoDriverSettings(),
    };
}
