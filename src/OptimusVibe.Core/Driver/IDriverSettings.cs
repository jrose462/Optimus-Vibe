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
    /// <paramref name="beforeWrite"/> receives the token before the first driver write so the caller can persist it first (a kill between apply and
    /// save must stay recoverable); a null return after it was called means nothing changed after all.
    /// </summary>
    string? Apply(GameDef game, IReadOnlyDictionary<string, string> settings, Action<string>? beforeWrite = null);

    /// <summary>Undoes a token from <see cref="Apply"/>. True when everything is back (or the token is obsolete / unreadable and can never be restored);
    /// false when something could not be restored: the caller must keep the token and retry later.</summary>
    bool Restore(string token);
}

public static class DriverSettings
{
    /// <summary>Test seam (tests using it share the serial RunnerMutex collection): replaces the real vendor backends.</summary>
    internal static volatile Func<string, IDriverSettings>? Override;

    public static IDriverSettings For(string vendor) => Override?.Invoke(vendor) ?? (vendor ?? "").Trim().ToLowerInvariant() switch
    {
        "nvidia" => new NvidiaDrsManager(),
        "amd" => new AmdAdlxManager(),
        _ => new NoDriverSettings(),
    };
}
