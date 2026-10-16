using GameProfiles.Logging;

namespace GameProfiles.Library.Scanners;

/// <summary>
/// GOG (Galaxy and offline installers): HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\&lt;id&gt; (gameName, path, exe,
/// launchParam, workingDir, dependsOn). DLC entries (dependsOn set) are skipped. StoreId = GOG game id.
/// Launch: the game exe directly (LaunchExe = exe, LaunchArgs = launchParam). GOG games are DRM-free, so this works
/// with or without Galaxy and lets the wrapper wait on the real process. (goggalaxy://openGameView/&lt;id&gt; only opens
/// the store page; "GalaxyClient.exe /command=runGame /gameId=&lt;id&gt;" would also work but adds Galaxy's startup and
/// an extra process hop, so it is not used.)
/// </summary>
public sealed class GogScanner : IGameScanner
{
    internal const string GamesKey = @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games";
    private readonly IRegistryReader _registry;

    public GogScanner() : this(WindowsRegistryReader.Instance) { }

    internal GogScanner(IRegistryReader registry) => _registry = registry;

    public string Store => "gog";

    public IEnumerable<DetectedGame> Scan()
    {
        var result = new List<DetectedGame>();
        foreach (var id in _registry.SubKeys(GamesKey))
        {
            try
            {
                var game = Read(id);
                if (game is not null) result.Add(game);
            }
            catch (Exception ex)
            {
                Log.Warn($"GOG: skipped game {id}: {ex.Message}");
            }
        }
        return result;
    }

    private DetectedGame? Read(string id)
    {
        var key = $@"{GamesKey}\{id}";
        string? V(string name) => _registry.Value(key, name) is { Length: > 0 } s ? s : null;

        if (V("dependsOn") is not null) return null; // DLC
        var gameId = V("gameID") ?? id;
        var path = ScanUtil.NormalizeDir(V("path"));
        if (path is not null && !Directory.Exists(path))
        {
            Log.Warn($"GOG: {V("gameName") ?? gameId} points at a missing folder {path}; skipped");
            return null;
        }

        var exe = V("exe") ?? ScanUtil.SplitArgs(V("launchCommand")).FirstOrDefault();
        if (exe is not null && !Path.IsPathRooted(exe) && path is not null) exe = Path.Combine(path, exe);
        var args = ScanUtil.SplitArgs(V("launchParam"));
        var exeName = exe is null ? null : Path.GetFileName(exe);

        return new DetectedGame
        {
            Store = Store,
            StoreId = gameId,
            Name = V("gameName") ?? V("GAMENAME") ?? (path is null ? gameId : Path.GetFileName(path)),
            InstallDir = path,
            LaunchExe = exe,
            LaunchArgs = args,
            Exes = ScanUtil.FindExes(path, exeName is null ? null : [exeName]),
        };
    }
}
