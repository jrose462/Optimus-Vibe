using System.Globalization;
using System.Text;
using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Logging;

namespace OptimusVibe.Library;

/// <summary>Joins detected installs with game configs; reads/writes config/library.yaml and config/requests.yaml.</summary>
public static class LibraryService
{
    private const string LibraryHeader =
        "# Optimus-Vibe library choices, shared by every device (git-synced). Written by Optimus-Vibe.\n" +
        "# optOut: detected games (\"store:id\", e.g. steam:1285190) that Optimus-Vibe leaves alone —\n" +
        "# no settings are applied and they launch plainly (the Steam launch option is removed).\n" +
        "# steamLaunchOptionsSkip: Steam games (\"steam:appid\") whose Steam launch option Optimus-Vibe never touches.\n";

    private const string RequestsHeader =
        "# Games detected on a device that have no Optimus-Vibe presets yet (or none for that device).\n" +
        "# Claude processes this queue with the game-settings-recommender skill: for each entry it researches the\n" +
        "# game's settings, writes config/games/<id>.yaml with per-device presets (store ids from here), validates,\n" +
        "# and removes the entry. Optimus-Vibe adds entries (`optimus-vibe library --queue` / the app) and prunes\n" +
        "# ones whose game has since been configured for the requesting device or opted out.\n";

    /// <summary>
    /// One entry per detected game plus configured-but-not-installed games. Matching: SteamAppId, then
    /// GameDef.StoreIds[store], then a normalized name match. Status per <see cref="LibraryStatus"/>; a configured
    /// game without presets for <paramref name="device"/> counts as NeedsPresets. With a <paramref name="cloudSync"/> checker
    /// each configured game also gets its <see cref="LibraryEntry.CloudSync"/> report (reads launcher files; skip on hot paths).
    /// </summary>
    public static IReadOnlyList<LibraryEntry> Build(ConfigStore store, DeviceSelection device, IReadOnlyList<DetectedGame> detected,
        CloudSyncChecker? cloudSync = null)
    {
        var games = LoadGames(store);
        var lib = LoadLibrary(store);
        var optOut = OptOutSet(lib);
        var skips = lib.SteamLaunchOptionsSkip.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<LibraryEntry>();

        foreach (var d in detected)
        {
            var game = MatchGame(d, games);
            if (game is not null) matched.Add(game.Id);
            var opted = optOut.Contains(d.Key);
            var status = opted ? LibraryStatus.OptedOut
                : game is not null && ProfileResolver.PresetNames(game, device.Name).Count > 0 ? LibraryStatus.Managed
                : LibraryStatus.NeedsPresets;
            entries.Add(new LibraryEntry
            {
                Key = d.Key,
                Name = game is not null && !string.IsNullOrWhiteSpace(game.Name) ? game.Name : d.Name,
                Detected = d,
                Game = game,
                Status = status,
                OptedOut = opted,
                SteamLaunchOptionSkipped = skips.Contains(d.Key),
                CloudSync = CheckSync(cloudSync, game),
            });
        }

        foreach (var game in games.Where(g => !matched.Contains(g.Id)))
        {
            var key = ConfiguredKey(game);
            entries.Add(new LibraryEntry
            {
                Key = key,
                Name = string.IsNullOrWhiteSpace(game.Name) ? game.Id : game.Name,
                Game = game,
                Status = LibraryStatus.NotInstalled,
                OptedOut = optOut.Contains(key),
                SteamLaunchOptionSkipped = skips.Contains(key),
                CloudSync = CheckSync(cloudSync, game),
            });
        }

        return entries
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The library key for a configured game that was not detected: steam:&lt;SteamAppId&gt;, else &lt;store&gt;:&lt;StoreIds[store]&gt;
    /// for its own store, else its first store id, else config:&lt;game id&gt;.
    /// </summary>
    public static string ConfiguredKey(GameDef game)
    {
        if (game.SteamAppId is { } appId) return $"steam:{appId}";
        var own = game.StoreIds.FirstOrDefault(kv => kv.Key.Equals(game.Store, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(own.Value)) return $"{own.Key.ToLowerInvariant()}:{own.Value}";
        var first = game.StoreIds.FirstOrDefault(kv => !string.IsNullOrEmpty(kv.Value));
        if (first.Key is not null) return $"{first.Key.ToLowerInvariant()}:{first.Value}";
        return $"config:{game.Id}";
    }

    public static LibraryFile LoadLibrary(ConfigStore store)
    {
        var file = store.Paths.LibraryFile;
        if (!File.Exists(file)) return new LibraryFile();
        try
        {
            var lib = Yaml.Load<LibraryFile>(file);
            lib.OptOut ??= new();
            lib.SteamLaunchOptionsSkip ??= new();
            return lib;
        }
        catch (Exception ex)
        {
            throw new ConfigException($"{file}: {ex.Message}");
        }
    }

    /// <summary>Add/remove a key in config/library.yaml optOut.</summary>
    public static void SetOptOut(ConfigStore store, string key, bool optOut)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ConfigException("Empty library key (expected store:id, e.g. steam:1285190)");
        var lib = LoadLibrary(store);
        var had = lib.OptOut.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        lib.OptOut.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (optOut) lib.OptOut.Add(key);
        if (had == optOut && File.Exists(store.Paths.LibraryFile)) return; // nothing changed
        lib.OptOut = lib.OptOut.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        Write(store.Paths.LibraryFile, LibraryHeader, lib);
    }

    /// <summary>Keys ("steam:appid") in config/library.yaml steamLaunchOptionsSkip (case-insensitive).</summary>
    public static HashSet<string> SteamLaunchOptionSkips(ConfigStore store) =>
        LoadLibrary(store).SteamLaunchOptionsSkip.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Add/remove a key in config/library.yaml steamLaunchOptionsSkip. Returns true when the file changed.</summary>
    public static bool SetSteamLaunchOptionSkip(ConfigStore store, string key, bool skip)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ConfigException("Empty library key (expected store:id, e.g. steam:1285190)");
        var lib = LoadLibrary(store);
        var had = lib.SteamLaunchOptionsSkip.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (had == skip) return false; // nothing changed
        lib.SteamLaunchOptionsSkip.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (skip) lib.SteamLaunchOptionsSkip.Add(key);
        lib.SteamLaunchOptionsSkip = lib.SteamLaunchOptionsSkip.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        Write(store.Paths.LibraryFile, LibraryHeader, lib);
        return true;
    }

    public static RequestsFile LoadRequests(ConfigStore store)
    {
        var file = store.Paths.RequestsFile;
        if (!File.Exists(file)) return new RequestsFile();
        try
        {
            var req = Yaml.Load<RequestsFile>(file);
            req.Requests ??= new();
            return req;
        }
        catch (Exception ex)
        {
            throw new ConfigException($"{file}: {ex.Message}");
        }
    }

    /// <summary>
    /// Add NeedsPresets games (not opted out) to config/requests.yaml if not already queued (by Key).
    /// Returns the number added. Requests for games that now have a config (with presets for the requesting device)
    /// or that were opted out are pruned. The file is only written when something changed.
    /// </summary>
    public static int QueueRequests(ConfigStore store, IEnumerable<LibraryEntry> entries, string device)
    {
        var file = LoadRequests(store);
        var games = LoadGames(store);
        var optOut = OptOutSet(LoadLibrary(store));

        int before = file.Requests.Count;
        file.Requests.RemoveAll(r =>
        {
            var key = $"{r.Store}:{r.StoreId}";
            if (optOut.Contains(key)) return true;
            var probe = new DetectedGame { Store = r.Store, StoreId = r.StoreId, Name = r.Name };
            var game = MatchGame(probe, games);
            return game is not null && ProfileResolver.PresetNames(game, r.Device ?? device).Count > 0;
        });
        bool changed = file.Requests.Count != before;

        var queued = file.Requests.Select(r => $"{r.Store}:{r.StoreId}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int added = 0;
        foreach (var e in entries)
        {
            if (e.Status != LibraryStatus.NeedsPresets || e.OptedOut || e.Detected is null) continue;
            if (optOut.Contains(e.Key) || !queued.Add(e.Key)) continue;
            var d = e.Detected;
            file.Requests.Add(new GameRequest
            {
                Store = d.Store,
                StoreId = d.StoreId,
                Name = d.Name,
                InstallDir = d.InstallDir,
                Exes = d.Exes.ToList(),
                Requested = today,
                Device = device,
            });
            added++;
        }

        if (added > 0 || changed || (!File.Exists(store.Paths.RequestsFile) && file.Requests.Count > 0))
            Write(store.Paths.RequestsFile, RequestsHeader, file);
        return added;
    }

    /// <summary>Find the detected install for a configured game (for launching), or null.</summary>
    public static DetectedGame? FindInstall(GameDef game, IReadOnlyList<DetectedGame> detected) =>
        detected
            .Select(d => (d, score: MatchStrength(d, game)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.d.Store.Equals(game.Store, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.d)
            .FirstOrDefault();

    /// <summary>
    /// The configured game a detected install belongs to: Steam app id first, then GameDef.StoreIds[store], then a
    /// normalized name match (only against games that have no id for that store). Null when nothing matches.
    /// </summary>
    public static GameDef? MatchGame(DetectedGame detected, IEnumerable<GameDef> games) =>
        games
            .Select(g => (g, score: MatchStrength(detected, g)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.g)
            .FirstOrDefault();

    /// <summary>3 = Steam app id, 2 = store id, 1 = normalized name, 0 = no match.</summary>
    private static int MatchStrength(DetectedGame d, GameDef g)
    {
        var isSteam = d.Store.Equals("steam", StringComparison.OrdinalIgnoreCase);
        if (isSteam && g.SteamAppId is { } appId && d.StoreId == appId.ToString(CultureInfo.InvariantCulture)) return 3;

        var storeId = g.StoreIds.FirstOrDefault(kv => kv.Key.Equals(d.Store, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrEmpty(storeId) && storeId.Trim().Equals(d.StoreId, StringComparison.OrdinalIgnoreCase)) return 2;

        // A game that names its id in this store is a different app when the ids differ (e.g. DOOM vs DOOM (1993)).
        bool hasIdForStore = !string.IsNullOrEmpty(storeId) || (isSteam && g.SteamAppId is not null);
        if (!hasIdForStore)
        {
            var a = NormalizeName(d.Name);
            if (a.Length > 0 && (a == NormalizeName(g.Name) || a == NormalizeName(g.Id))) return 1;
        }
        return 0;
    }

    /// <summary>
    /// Name key for matching: lowercase, letters and digits only (drops ™®©, colons, apostrophes, dashes, spaces and
    /// other punctuation). Roman numerals are kept as written ("Diablo IV" ≠ "Diablo 4").
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- helpers

    private static CloudSyncReport? CheckSync(CloudSyncChecker? checker, GameDef? game)
    {
        if (checker is null || game is null) return null;
        try { return checker.Check(game); }
        catch (Exception ex)
        {
            Log.Warn($"Library: cloud check for {game.Id} failed: {ex.Message}");
            return null;
        }
    }

    private static List<GameDef> LoadGames(ConfigStore store)
    {
        var games = new List<GameDef>();
        foreach (var id in store.GameIds())
        {
            try
            {
                games.Add(store.LoadGame(id));
            }
            catch (Exception ex)
            {
                Log.Warn($"Library: skipped game config {id}: {ex.Message}");
            }
        }
        return games;
    }

    private static HashSet<string> OptOutSet(LibraryFile lib) =>
        lib.OptOut.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rewrite a YAML file, keeping its existing leading comment block (or using <paramref name="header"/> for a new file).</summary>
    private static void Write(string path, string header, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var lines = File.ReadAllLines(path).TakeWhile(l => l.TrimStart().StartsWith('#') || l.Trim().Length == 0).ToList();
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) header = string.Join("\n", lines) + "\n";
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, header + Yaml.Serialize(value), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
