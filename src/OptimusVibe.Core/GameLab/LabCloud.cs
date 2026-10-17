using System.Text;
using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Logging;

namespace OptimusVibe.GameLab;

/// <summary>Builds the "Cloud check" evidence text: the app's own sync verdict plus what Steam Cloud tracks for the game.</summary>
public static class LabCloud
{
    /// <summary>The report as plain lines (what `optimus-vibe cloud-check` prints).</summary>
    public static string Describe(CloudSyncReport r)
    {
        var sb = new StringBuilder();
        sb.Append("Game: ").Append(r.GameId).Append("   Device: ").Append(r.Device).Append('\n');
        sb.Append("Verdict: ").Append(r.Headline);
        if (r.Confidence != SyncConfidence.None) sb.Append("  [").Append(r.Detected).Append(", ").Append(r.ConfidenceText).Append(']');
        sb.Append('\n');
        sb.Append("Declared in config: ").Append(r.Declared switch { true => "settings sync (cloudSyncsSettings: true)", false => "per machine (cloudSyncsSettings: false)", _ => "unknown" }).Append('\n');
        if (r.Evidence.Count > 0)
        {
            sb.Append("Evidence:\n");
            foreach (var line in r.Evidence) sb.Append("  - ").Append(line).Append('\n');
        }
        if (r.Contradiction is { } c) sb.Append("Contradiction: ").Append(c).Append('\n');
        sb.Append("External changes seen: ").Append(r.HasExternalChanges ? "yes" : "no").Append("; clean launches observed: ").Append(r.CleanLaunches).Append('\n');
        return sb.ToString();
    }

    /// <summary>Files Steam Cloud tracks for the app, one per line as "root/name (size)".</summary>
    public static string DescribeSteamCloud(IReadOnlyList<SteamCloudEntry>? entries)
    {
        if (entries is null) return "Steam Cloud: no remotecache.vdf for this game on this PC.";
        if (entries.Count == 0) return "Steam Cloud: tracks no files.";
        var sb = new StringBuilder($"Steam Cloud tracks {entries.Count} file(s):\n");
        foreach (var e in entries.OrderBy(e => e.Root).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(100))
            sb.Append("  ").Append(RootName(e.Root)).Append('/').Append(e.Name).Append(" (").Append(e.Size).Append(" B)\n");
        if (entries.Count > 100) sb.Append($"  ... {entries.Count - 100} more\n");
        return sb.ToString();
    }

    private static string RootName(int root) => root switch
    {
        1 => "{InstallDir}", 2 => "{Documents}", 3 => "{LocalAppData}", 4 => "{AppData}", 9 => "{SavedGames}", 10 => "{ProgramData}", 12 => "{LocalLow}",
        _ => $"root{root}",
    };

    /// <summary>
    /// The cloud-check text for a target using the app's checker. A game without a config gets a stub GameDef (id, store ids) so the
    /// launcher evidence is still gathered. Never throws; returns what it could.
    /// </summary>
    public static Func<LabTarget, string?> ForChecker(ConfigStore store, DeviceSelection device, SteamLocator? steam = null)
    {
        steam ??= new SteamLocator();
        var checker = new CloudSyncChecker(store, device, steam);
        return t =>
        {
            var sb = new StringBuilder();
            try
            {
                var def = t.Game ?? new GameDef
                {
                    Id = t.GameId ?? Config.ConfigStore.Slugify(t.Name),
                    Name = t.Name,
                    Store = t.Store,
                    SteamAppId = t.Store.Equals("steam", StringComparison.OrdinalIgnoreCase) && int.TryParse(t.StoreId, out var id) ? id : null,
                    StoreIds = !t.Store.Equals("steam", StringComparison.OrdinalIgnoreCase) && t.StoreId.Length > 0
                        ? new Dictionary<string, string> { [t.Store] = t.StoreId } : new(),
                };
                sb.Append(Describe(checker.Check(def)));
                if (def.SteamAppId is { } appId && steam.SteamRoot is not null)
                    sb.Append('\n').Append(DescribeSteamCloud(SteamCloud.Read(steam, appId)));
            }
            catch (Exception ex)
            {
                Log.Warn($"game lab: cloud check for {t.Name} failed: {ex.Message}");
                sb.Append("Cloud check failed: ").Append(ex.Message).Append('\n');
            }
            return sb.ToString();
        };
    }

    /// <summary>The text of games\&lt;id&gt;.yaml for "Current config" (null when the game has no config file).</summary>
    public static Func<LabTarget, string?> CurrentConfigFrom(AppPaths paths) => t =>
    {
        try
        {
            if (string.IsNullOrEmpty(t.GameId)) return null;
            var file = paths.GameFile(t.GameId);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    };
}
