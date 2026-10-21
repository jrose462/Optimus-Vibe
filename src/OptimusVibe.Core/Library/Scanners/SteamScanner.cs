using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Library.Scanners;

/// <summary>
/// Steam: every library from libraryfolders.vdf → steamapps\appmanifest_*.acf. Tools and redistributables are
/// skipped, as are apps whose StateFlags lack the "fully installed" bit (4). Launch: steam://rungameid/&lt;appid&gt;.
/// Art: &lt;Steam&gt;\appcache\librarycache, old flat layout (&lt;appid&gt;_library_600x900.jpg) and the newer per-app
/// folders (&lt;appid&gt;\library_600x900.jpg or &lt;appid&gt;\&lt;hash&gt;\library_600x900.jpg).
/// </summary>
public sealed class SteamScanner : IGameScanner
{
    private const int StateFullyInstalled = 4;

    /// <summary>Steamworks Common Redistributables, SteamVR, Steam Linux Runtimes, Proton builds.</summary>
    private static readonly HashSet<int> SkipAppIds =
        [228980, 250820, 1070560, 1391110, 1628350, 1493710, 1826330, 1161040];

    private static readonly string[] SkipNameHints =
        ["redistributable", "steamworks common", "proton ", "proton-", "steam linux runtime", "steamvr", "dedicated server", "proton easyanticheat", "proton battleye"];

    private readonly SteamLocator _locator;
    private string? _steamRoot; // SteamRoot in on-disk casing

    public SteamScanner() : this(new SteamLocator()) { }

    internal SteamScanner(SteamLocator locator) => _locator = locator;

    public string Store => "steam";

    public IEnumerable<DetectedGame> Scan()
    {
        var result = new List<DetectedGame>();
        if (_locator.SteamRoot is null || !Directory.Exists(_locator.SteamRoot)) return result;

        List<string> libraries;
        try
        {
            libraries = _locator.Libraries().Select(ScanUtil.ProperCase).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam: could not read libraryfolders.vdf: {ex.Message}");
            libraries = [ScanUtil.ProperCase(_locator.SteamRoot)];
        }

        foreach (var lib in libraries)
        {
            var steamapps = Path.Combine(lib, "steamapps");
            foreach (var manifest in ScanUtil.SafeFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    var game = ReadManifest(manifest, steamapps);
                    if (game is not null) result.Add(game);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Steam: skipped {manifest}: {ex.Message}");
                }
            }
        }
        return result;
    }

    private DetectedGame? ReadManifest(string manifest, string steamapps)
    {
        var app = Vdf.Load(manifest).Child("AppState");
        if (app is null) return null;
        if (!int.TryParse(app.Str("appid"), out var appId)) return null;
        var name = app.Str("name")?.Trim();
        if (string.IsNullOrEmpty(name)) name = app.Str("installdir") ?? appId.ToString();

        if (SkipAppIds.Contains(appId)) return null;
        var lower = name.ToLowerInvariant();
        if (SkipNameHints.Any(lower.Contains)) return null;
        if (int.TryParse(app.Str("StateFlags"), out var flags) && (flags & StateFullyInstalled) == 0) return null;

        var installDirName = app.Str("installdir");
        var installDir = string.IsNullOrEmpty(installDirName) ? null : Path.Combine(steamapps, "common", installDirName);
        var buildId = app.Str("buildid")?.Trim();
        DateTime? updated = long.TryParse(app.Str("LastUpdated"), out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : null;

        return new DetectedGame
        {
            Store = Store,
            StoreId = appId.ToString(),
            Name = name,
            InstallDir = installDir,
            LaunchUri = $"steam://rungameid/{appId}",
            ArtPath = FindArt(appId),
            Exes = ScanUtil.FindExes(installDir),
            BuildId = string.IsNullOrEmpty(buildId) || buildId == "0" ? null : buildId,
            BuildUpdatedUtc = updated,
        };
    }

    /// <summary>Portrait cover first (library_600x900), then library_capsule, then the landscape header.</summary>
    internal string? FindArt(int appId)
    {
        if (_locator.SteamRoot is null) return null;
        var cache = Path.Combine(_steamRoot ??= ScanUtil.ProperCase(_locator.SteamRoot), "appcache", "librarycache");
        if (!Directory.Exists(cache)) return null;

        string[] preference = ["library_600x900_2x", "library_600x900", "library_capsule", "header", "library_header"];
        var appDir = Path.Combine(cache, appId.ToString());
        var inFolder = Directory.Exists(appDir)
            ? ScanUtil.SafeFiles(appDir, "*.*", SearchOption.AllDirectories).Where(IsImage).ToList()
            : [];
        var flat = ScanUtil.SafeFiles(cache, appId + "_*.*").Where(IsImage).ToList();

        foreach (var want in preference)
        {
            // Per-app folder (new layout): <appid>\<want>.jpg or <appid>\<hash>\<want>.jpg
            var hit = inFolder.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(want, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
            // Flat (old layout): <appid>_<want>.jpg
            hit = flat.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals($"{appId}_{want}", StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    private static bool IsImage(string f) =>
        f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
}
