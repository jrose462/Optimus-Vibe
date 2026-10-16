using System.Text.Json;
using OptimusVibe.Logging;

namespace OptimusVibe.Library.Scanners;

/// <summary>
/// Epic Games Launcher: C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item (JSON). Skips incomplete installs,
/// Unreal Engine / plugin entries (AppCategories engines/plugins, or no LaunchExecutable) and DLC (MainGameAppName ≠ AppName).
/// StoreId = AppName. Launch: com.epicgames.launcher://apps/&lt;ns&gt;%3A&lt;item&gt;%3A&lt;AppName&gt;?action=launch&amp;silent=true.
/// </summary>
public sealed class EpicScanner : IGameScanner
{
    private readonly string _manifestsDir;

    public EpicScanner() : this(DefaultManifestsDir) { }

    internal EpicScanner(string manifestsDir) => _manifestsDir = manifestsDir;

    public static string DefaultManifestsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public string Store => "epic";

    public IEnumerable<DetectedGame> Scan()
    {
        var result = new List<DetectedGame>();
        if (!Directory.Exists(_manifestsDir)) return result;
        foreach (var file in ScanUtil.SafeFiles(_manifestsDir, "*.item"))
        {
            try
            {
                var game = Read(file);
                if (game is not null) result.Add(game);
            }
            catch (Exception ex)
            {
                Log.Warn($"Epic: skipped {file}: {ex.Message}");
            }
        }
        return result;
    }

    private DetectedGame? Read(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        if (root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;

        var categories = root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
            ? cats.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!.ToLowerInvariant()).ToList()
            : [];
        if (categories.Contains("engines") || categories.Contains("plugins") || categories.Contains("addons") && !categories.Contains("games"))
            return null;

        var appName = Str("AppName");
        var launchExe = Str("LaunchExecutable");
        if (string.IsNullOrEmpty(appName) || string.IsNullOrWhiteSpace(launchExe)) return null;
        var main = Str("MainGameAppName");
        if (!string.IsNullOrEmpty(main) && !main.Equals(appName, StringComparison.OrdinalIgnoreCase)) return null; // DLC

        var installDir = ScanUtil.NormalizeDir(Str("InstallLocation"));
        if (installDir is not null && !Directory.Exists(installDir))
        {
            Log.Warn($"Epic: {Str("DisplayName") ?? appName} points at a missing folder {installDir}; skipped");
            return null;
        }

        var ns = Str("CatalogNamespace");
        var item = Str("CatalogItemId");
        var uri = !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(item)
            ? $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(item)}%3A{Uri.EscapeDataString(appName)}?action=launch&silent=true"
            : $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true";

        var exeName = Path.GetFileName(launchExe.Replace('/', '\\'));
        return new DetectedGame
        {
            Store = Store,
            StoreId = appName,
            Name = Str("DisplayName") ?? appName,
            InstallDir = installDir,
            LaunchUri = uri,
            Exes = ScanUtil.FindExes(installDir, [exeName]),
        };
    }
}
