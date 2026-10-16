using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Handlers;
using OptimusVibe.Logging;

namespace OptimusVibe.CloudSync;

/// <summary>
/// Gathers the evidence for a game on this device (Steam remotecache.vdf, the EA app log, the observation store) and
/// combines it into a <see cref="CloudSyncReport"/>. Read-only; never throws for missing launchers or files.
/// </summary>
public sealed class CloudSyncChecker
{
    /// <summary>Launchers whose cloud sync Optimus-Vibe cannot inspect.</summary>
    private static readonly Dictionary<string, string> Uninspectable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["epic"] = "Epic Games Launcher",
        ["gog"] = "GOG Galaxy",
        ["battlenet"] = "Battle.net",
        ["xbox"] = "Xbox app",
    };

    private readonly ConfigStore _store;
    private readonly DeviceSelection _device;
    private readonly SteamLocator _steam;
    private readonly PathResolver _resolver;
    private readonly string _eaLogsDir;

    public CloudSyncChecker(ConfigStore store, DeviceSelection device, SteamLocator? steam = null,
        PathResolver? resolver = null, string? eaLogsDir = null)
    {
        _store = store;
        _device = device;
        _steam = steam ?? new SteamLocator();
        _resolver = resolver ?? new PathResolver(_steam);
        _eaLogsDir = eaLogsDir ?? EaCloudLog.DefaultLogsDir;
    }

    public CloudSyncReport Check(GameDef game)
    {
        var launchers = new List<LauncherEvidence>();
        try
        {
            var files = ConfiguredFiles(game);
            bool steamOnly = game.Store.Equals("steam", StringComparison.OrdinalIgnoreCase)
                             && !game.StoreIds.Any(kv => !string.IsNullOrEmpty(kv.Value) && !kv.Key.Equals("steam", StringComparison.OrdinalIgnoreCase));

            if (game.SteamAppId is { } appId) launchers.Add(CheckSteam(appId, files, steamOnly));
            if (UsesStore(game, "ea")) launchers.Add(CheckEa(files));
            foreach (var (key, name) in Uninspectable)
                if (UsesStore(game, key))
                    launchers.Add(LauncherEvidence.Unknown($"{name}: its cloud sync can't be inspected, so unknown."));
        }
        catch (Exception ex)
        {
            Log.Warn($"Cloud check for {game.Id} failed: {ex.Message}");
            launchers.Add(LauncherEvidence.Unknown($"Launcher check failed: {ex.Message}"));
        }

        var observed = new ObservationSummary();
        try { observed = SyncObservationStore.For(_store.Paths).Summary(game.Id, _device.Name); }
        catch (Exception ex) { Log.Warn($"Cloud check for {game.Id}: observations unreadable: {ex.Message}"); }

        return CloudSyncVerdict.Combine(game.Id, _device.Name, game.CloudSyncsSettings, launchers, observed);
    }

    private static bool UsesStore(GameDef game, string store) =>
        game.Store.Equals(store, StringComparison.OrdinalIgnoreCase)
        || game.StoreIds.Any(kv => kv.Key.Equals(store, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(kv.Value));

    private List<SyncFile> ConfiguredFiles(GameDef game)
    {
        var files = new List<SyncFile>();
        foreach (var def in game.Files)
        {
            try
            {
                var handler = SettingsHandler.For(def.Handler);
                string path;
                try { path = (handler.IsFile ? _resolver.ResolveFile(def.Path) : null) ?? def.Path; }
                catch (PathResolveException) { path = def.Path; } // game not installed here: match on the configured path
                files.Add(new SyncFile(def.Id, path, handler.IsFile));
            }
            catch (ConfigException) { /* unknown handler: the validator reports it */ }
        }
        return files;
    }

    private LauncherEvidence CheckSteam(int appId, List<SyncFile> files, bool steamOnly)
    {
        try
        {
            if (_steam.SteamRoot is null) return LauncherEvidence.Unknown("Steam is not installed on this machine.");
            var entries = SteamCloud.Read(_steam, appId);
            return SteamCloud.Evaluate(entries, files, _resolver, appId, steamOnly);
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam Cloud check for {appId} failed: {ex.Message}");
            return LauncherEvidence.Unknown($"Steam Cloud data could not be read: {ex.Message}");
        }
    }

    // The EA log is several MB: parse it once per (folder, size, mtime) however many checkers ask.
    private static readonly object EaLock = new();
    private static string? _eaKey;
    private static Dictionary<string, DateTime?> _eaCache = new();

    private LauncherEvidence CheckEa(List<SyncFile> files)
    {
        try
        {
            var logs = EaCloudLog.LogFiles(_eaLogsDir);
            var key = string.Join("|", logs.Select(l => { var i = new FileInfo(l); return $"{l}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }));
            Dictionary<string, DateTime?> synced;
            lock (EaLock)
            {
                if (_eaKey != key)
                {
                    _eaCache = EaCloudLog.ReadSyncedFiles(logs);
                    _eaKey = key;
                }
                synced = _eaCache;
            }
            return EaCloudLog.Evaluate(synced, files, logs.Count > 0);
        }
        catch (Exception ex)
        {
            Log.Warn($"EA app log check failed: {ex.Message}");
            return LauncherEvidence.Unknown($"EA app log could not be read: {ex.Message}");
        }
    }
}
