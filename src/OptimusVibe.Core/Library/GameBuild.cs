using OptimusVibe.Config;
using OptimusVibe.Library.Scanners;
using OptimusVibe.Logging;

namespace OptimusVibe.Library;

/// <summary>
/// Which build of a game is installed, as one comparable string: <c>steam:&lt;buildid&gt;</c> from the Steam app manifest (a real build id), else
/// <c>exe:&lt;name&gt;:&lt;size&gt;:&lt;modified&gt;</c> of the main exe (a fingerprint: a launcher touching the exe can change it, so it only ever
/// suggests). Used to tell "the game updated since its presets were made". Cheap (a manifest read or a few directory listings); never throws.
/// </summary>
public static class GameBuild
{
    public const string SteamPrefix = "steam:";
    public const string ExePrefix = "exe:";

    /// <summary>The build of a detected install: the store's build id when it has one, else the exe fingerprint of its install folder.</summary>
    public static string? Of(DetectedGame game) =>
        game.BuildId is { Length: > 0 } id ? SteamPrefix + id : ExeFingerprint(game.InstallDir);

    /// <summary>
    /// The build of a configured game, as the wrapper sees it at run time: its Steam app's build id, else the exe fingerprint of
    /// <paramref name="installDir"/>. Matches <see cref="Of"/> for the same install.
    /// </summary>
    public static string? ForGame(GameDef game, string? installDir, SteamLocator? steam = null)
    {
        try
        {
            if (game.SteamAppId is { } appId && (steam ?? new SteamLocator()).BuildId(appId) is { } id) return SteamPrefix + id;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the Steam build of {game.Id}: {ex.Message}");
        }
        return ExeFingerprint(installDir);
    }

    /// <summary>
    /// <c>exe:&lt;name&gt;:&lt;size&gt;:&lt;modified UTC&gt;</c> of the first exe <see cref="ScanUtil.FindExes"/> would list for the folder (the biggest
    /// non-launcher one); null when there is none.
    /// </summary>
    public static string? ExeFingerprint(string? installDir)
    {
        try
        {
            var exe = ScanUtil.ExeFiles(installDir).FirstOrDefault();
            if (exe is null) return null;
            exe.Refresh();
            return $"{ExePrefix}{exe.Name.ToLowerInvariant()}:{exe.Length}:{exe.LastWriteTimeUtc:yyyyMMddHHmmss}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not fingerprint the exe in {installDir}: {ex.Message}");
            return null;
        }
    }

    /// <summary>True for a real build id from the store (<see cref="SteamPrefix"/>), false for an exe fingerprint or nothing.</summary>
    public static bool IsStoreBuild(string? build) => build?.StartsWith(SteamPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// True when both builds are known, of the same kind, and differ. Unknown on either side, or a store build against a fingerprint
    /// (the store started or stopped reporting one), is not a change.
    /// </summary>
    public static bool Changed(string? before, string? now)
    {
        if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(now)) return false;
        if (IsStoreBuild(before) != IsStoreBuild(now)) return false;
        return !string.Equals(before, now, StringComparison.OrdinalIgnoreCase);
    }
}
