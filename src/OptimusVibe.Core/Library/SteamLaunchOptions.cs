using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Library;

public sealed record LaunchOptionChange(int AppId, string? GameId, string? Before, string? After);

/// <summary>
/// Reads/writes per-game Steam launch options in
/// &lt;Steam&gt;\userdata\&lt;account&gt;\config\localconfig.vdf (UserLocalConfigStore/Software/Valve/Steam/apps/&lt;appid&gt;/LaunchOptions).
/// Steam overwrites that file on exit, so writes are refused while Steam is running.
/// The account is the most recent Steam login (loginusers.vdf MostRecent); the read/plan/apply methods have an
/// overload taking the userdata account folder (&lt;Steam&gt;\userdata\&lt;accountId&gt;) directly.
/// </summary>
public static class SteamLaunchOptions
{
    /// <summary>Marker that identifies our wrapper inside a launch option.</summary>
    public const string Marker = "OptimusVibe.exe\" run ";

    /// <summary>Wrapper exe names written by releases before the rename to Optimus-Vibe; still recognised so Plan() rewrites them.</summary>
    public const string LegacyMarker = "GameProfilesApp.exe\" run ";

    public const string CommandToken = "%command%";

    /// <summary>Backups kept in the backup folder (oldest deleted first).</summary>
    public const int BackupsKept = 10;

    // Our wrapper as written by any install (OptimusVibe.exe / optimus-vibe.exe, or the legacy GameProfilesApp.exe / gameprofiles.exe, any folder, quoted or
    // not): <exe> run <game> [options...] -- %command%. The trailing "-- %command%" is optional so a hand-written
    // wrapper without it is still recognised (everything after it is then treated as ours).
    private static readonly Regex WrapperRegex = new(
        """(?:"(?<exe>[^"]*?(?:OptimusVibe|optimus-vibe|GameProfilesApp|gameprofiles)\.exe)"|(?<exe>[^\s"]*?(?:OptimusVibe|optimus-vibe|GameProfilesApp|gameprofiles)\.exe))\s+run\s+(?:"(?<game>[^"]+)"|(?<game>[^\s"]+))(?:(?<opts>.*?)\s--\s+(?<cmd>%command%)|.*$)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    // ------------------------------------------------------------------ Steam process

    public static bool IsSteamRunning()
    {
        var procs = Process.GetProcessesByName("steam");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    /// <summary>Ask Steam to exit (steam://exit) and wait up to <paramref name="timeout"/> for it to close.</summary>
    public static bool ExitSteam(TimeSpan timeout)
    {
        if (!IsSteamRunning()) return true;
        try
        {
            Process.Start(new ProcessStartInfo("steam://exit") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Could not ask Steam to exit", ex);
            return false;
        }
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsSteamRunning()) return true;
            Thread.Sleep(500);
        }
        return !IsSteamRunning();
    }

    // ------------------------------------------------------------------ locations

    /// <summary>
    /// &lt;Steam&gt;\userdata\&lt;accountId&gt; of the most recent login (falls back to the account whose localconfig.vdf
    /// was written last), or null when Steam / its user data is not found.
    /// </summary>
    public static string? AccountDir(string? steamRoot = null)
    {
        var locator = new SteamLocator(steamRoot);
        if (locator.SteamRoot is null) return null;
        var userdata = Path.Combine(locator.SteamRoot, "userdata");
        if (!Directory.Exists(userdata)) return null;

        string? account = null;
        try { account = locator.MostRecentAccountId(); }
        catch (Exception ex) { Log.Warn($"Could not read loginusers.vdf: {ex.Message}"); }
        if (account is not null && Directory.Exists(Path.Combine(userdata, account)))
            return Path.Combine(userdata, account);

        return Directory.EnumerateDirectories(userdata)
            .Where(d => File.Exists(LocalConfigPath(d)))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(LocalConfigPath(d)))
            .FirstOrDefault();
    }

    public static string LocalConfigPath(string accountDir) => Path.Combine(accountDir, "config", "localconfig.vdf");

    /// <summary>%LOCALAPPDATA%\OptimusVibe\backups\steam (honours OPTIMUSVIBE_DATA).</summary>
    public static string DefaultBackupDir => Path.Combine(AppPaths.DefaultDataDir, "backups", "steam");

    /// <summary>
    /// OptimusVibe.exe to put in launch options: this process when it is the app, otherwise the app next to this
    /// exe (the CLI ships beside it). May not exist — callers should check.
    /// </summary>
    public static string DefaultWrapperExe()
    {
        var self = Environment.ProcessPath;
        if (self is not null && Path.GetFileName(self).Equals("OptimusVibe.exe", StringComparison.OrdinalIgnoreCase))
            return self;
        return Path.Combine(AppContext.BaseDirectory, "OptimusVibe.exe");
    }

    // ------------------------------------------------------------------ read

    /// <summary>Current launch option for an app (most recent Steam account), or null.</summary>
    public static string? Get(int appId) => AccountDir() is { } dir ? Get(appId, dir) : null;

    /// <summary>Current launch option for an app in this userdata account folder, or null.</summary>
    public static string? Get(int appId, string accountDir)
    {
        var file = LocalConfigPath(accountDir);
        if (!File.Exists(file)) return null;
        return Apps(Vdf.Load(file), create: false)?.Child(Id(appId))?.Str("LaunchOptions");
    }

    /// <summary>Every app in this account folder with a non-empty launch option (app id → option).</summary>
    public static IReadOnlyDictionary<int, string> GetAll(string accountDir)
    {
        var result = new Dictionary<int, string>();
        var file = LocalConfigPath(accountDir);
        if (!File.Exists(file)) return result;
        var apps = Apps(Vdf.Load(file), create: false);
        if (apps is null) return result;
        foreach (var (key, value) in apps)
            if (value is Dictionary<string, object> app && app.Str("LaunchOptions") is { Length: > 0 } opt
                && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                result[id] = opt;
        return result;
    }

    // ------------------------------------------------------------------ compose / strip

    /// <summary>
    /// `"&lt;wrapperExe&gt;" run &lt;gameId&gt; -- %command%`, keeping the user's own options: plain args go after %command%;
    /// an existing %command% is replaced in place. Idempotent (re-composing replaces our old wrapper).
    /// </summary>
    public static string Compose(string? existing, string wrapperExe, string gameId)
    {
        var exe = wrapperExe.Trim().Trim('"');
        var game = gameId.Any(char.IsWhiteSpace) ? $"\"{gameId}\"" : gameId;
        var wrapper = $"\"{exe}\" run {game} -- {CommandToken}";

        var user = Strip(existing);
        if (string.IsNullOrWhiteSpace(user)) return wrapper;
        int i = user.IndexOf(CommandToken, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return user[..i] + wrapper + user[(i + CommandToken.Length)..];
        return $"{wrapper} {user}";
    }

    /// <summary>
    /// Remove our wrapper, leaving the user's own options (null if nothing remains). A leading %command% left behind
    /// is dropped ("%command% -dx12" → "-dx12", which Steam treats the same); one with something before it
    /// ("FOO=1 %command%") is kept. Options without our wrapper are returned unchanged (whitespace-trimmed).
    /// </summary>
    public static string? Strip(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing)) return null;
        if (!IsWrapped(existing)) return existing.Trim();
        var s = existing;
        for (int guard = 0; guard < 8; guard++) // wrapped more than once (older bugs / hand edits): strip all
        {
            var m = WrapperRegex.Match(s);
            if (!m.Success) break;
            var replacement = m.Groups["cmd"].Success ? CommandToken : "";
            s = s[..m.Index] + replacement + s[(m.Index + m.Length)..];
        }
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.StartsWith(CommandToken, StringComparison.OrdinalIgnoreCase))
            s = s[CommandToken.Length..].Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>True when the launch option runs our wrapper (any install path, app or CLI).</summary>
    public static bool IsWrapped(string? launchOptions) =>
        !string.IsNullOrEmpty(launchOptions) && WrapperRegex.IsMatch(launchOptions);

    /// <summary>The game id our wrapper is invoked with, or null when not wrapped.</summary>
    public static string? WrappedGameId(string? launchOptions)
    {
        if (string.IsNullOrEmpty(launchOptions)) return null;
        var m = WrapperRegex.Match(launchOptions);
        return m.Success ? m.Groups["game"].Value : null;
    }

    /// <summary>The wrapper exe path inside the launch option (as written, unquoted), or null when not wrapped.</summary>
    public static string? WrappedExe(string? launchOptions)
    {
        if (string.IsNullOrEmpty(launchOptions)) return null;
        var m = WrapperRegex.Match(launchOptions);
        return m.Success ? m.Groups["exe"].Value : null;
    }

    // ------------------------------------------------------------------ which games

    /// <summary>
    /// Games whose launch option should carry the wrapper on this device — configs with a steamAppId and presets for
    /// <paramref name="device"/> that are not opted out in config\library.yaml — and app ids that should not (opted out:
    /// "steam:&lt;appid&gt;" or the game id in optOut). Unreadable game configs are skipped (logged).
    /// </summary>
    public static (IReadOnlyList<(int AppId, string GameId)> Enable, IReadOnlyList<int> Disable) ManagedTargets(ConfigStore store, string device)
    {
        var optOut = LoadOptOuts(store.Paths);
        var enable = new List<(int, string)>();
        var disable = new List<int>();
        foreach (var id in store.GameIds())
        {
            GameDef game;
            try { game = store.LoadGame(id); }
            catch (Exception ex)
            {
                Log.Warn($"Skipping {id}: {ex.Message}");
                continue;
            }
            if (game.SteamAppId is not { } appId) continue;
            if (optOut.Contains($"steam:{appId}") || optOut.Contains(game.Id)) disable.Add(appId);
            else if (ProfileResolver.PresetNames(game, device).Count > 0) enable.Add((appId, game.Id));
        }
        foreach (var key in optOut)
            if (key.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key["steam:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
                && !disable.Contains(appId))
                disable.Add(appId);
        return (enable, disable);
    }

    /// <summary>optOut keys from config\library.yaml (case-insensitive); empty when the file is missing or unreadable.</summary>
    public static HashSet<string> LoadOptOuts(AppPaths paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(paths.LibraryFile))
                foreach (var k in Yaml.Load<LibraryFile>(paths.LibraryFile).OptOut)
                    if (!string.IsNullOrWhiteSpace(k)) set.Add(k.Trim());
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {paths.LibraryFile}: {ex.Message}");
        }
        return set;
    }

    // ------------------------------------------------------------------ plan / apply

    /// <summary>
    /// Changes needed so exactly the given apps carry the wrapper (and the given ones don't), for the most recent
    /// Steam account. Empty when Steam's user data is not found.
    /// </summary>
    public static IReadOnlyList<LaunchOptionChange> Plan(IEnumerable<(int AppId, string GameId)> enable, IEnumerable<int> disable, string wrapperExe)
    {
        var dir = AccountDir();
        if (dir is null)
        {
            Log.Warn("Steam user data not found — no launch options to change");
            return [];
        }
        return Plan(enable, disable, wrapperExe, dir);
    }

    /// <summary>Plan for one userdata account folder. An app in both lists is enabled.</summary>
    public static IReadOnlyList<LaunchOptionChange> Plan(IEnumerable<(int AppId, string GameId)> enable, IEnumerable<int> disable,
        string wrapperExe, string accountDir)
    {
        var file = LocalConfigPath(accountDir);
        var apps = File.Exists(file) ? Apps(Vdf.Load(file), create: false) : null;
        string? Current(int id) => apps?.Child(Id(id))?.Str("LaunchOptions");

        var changes = new List<LaunchOptionChange>();
        var enabled = new HashSet<int>();
        foreach (var (appId, gameId) in enable)
        {
            if (!enabled.Add(appId)) continue;
            var before = NullIfEmpty(Current(appId));
            var after = Compose(before, wrapperExe, gameId);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                changes.Add(new LaunchOptionChange(appId, gameId, before, after));
        }
        foreach (var appId in disable.Distinct())
        {
            if (enabled.Contains(appId)) continue;
            var before = Current(appId);
            if (!IsWrapped(before)) continue;
            changes.Add(new LaunchOptionChange(appId, WrappedGameId(before), before, Strip(before)));
        }
        return changes;
    }

    /// <summary>Write the changes (backing up localconfig.vdf first). Throws InvalidOperationException if Steam is running.</summary>
    public static void Apply(IReadOnlyList<LaunchOptionChange> changes)
    {
        if (changes.Count == 0) return;
        var dir = AccountDir() ?? throw new InvalidOperationException("Steam user data (Steam\\userdata\\<account>) was not found.");
        Apply(changes, dir);
    }

    /// <summary>
    /// Write the changes into this account's localconfig.vdf: refuses while Steam runs (InvalidOperationException;
    /// <paramref name="isSteamRunning"/> overrides the process check), backs the file up into
    /// <paramref name="backupDir"/> (default <see cref="DefaultBackupDir"/>; the last <see cref="BackupsKept"/> are kept)
    /// and replaces it atomically. A null/empty After clears the option. Returns the backup path (null if no changes).
    /// </summary>
    public static string? Apply(IReadOnlyList<LaunchOptionChange> changes, string accountDir,
        Func<bool>? isSteamRunning = null, string? backupDir = null)
    {
        if (changes.Count == 0) return null;
        if ((isSteamRunning ?? IsSteamRunning)())
            throw new InvalidOperationException(
                "Steam is running. Exit Steam first — it rewrites its settings when it closes and would undo the launch option change.");

        var file = LocalConfigPath(accountDir);
        if (!File.Exists(file))
            throw new InvalidOperationException($"Steam settings file not found: {file}. Sign in to Steam once, exit it, then try again.");

        var original = File.ReadAllText(file);
        var newLine = original.Contains("\r\n") ? "\r\n" : "\n";
        var root = Vdf.Parse(original);
        if (Vdf.Serialize(root, newLine) != original)
            Log.Warn($"{file} does not round-trip byte-for-byte; its formatting will be normalised (a backup is kept)");

        var apps = Apps(root, create: true)!;
        foreach (var c in changes)
            apps.GetOrAddChild(Id(c.AppId))["LaunchOptions"] = c.After ?? "";

        // what we write must parse back to what we mean to write
        var check = Apps(Vdf.Parse(Vdf.Serialize(root, newLine)), create: false);
        foreach (var c in changes)
            if (check?.Child(Id(c.AppId))?.Str("LaunchOptions") != (c.After ?? ""))
                throw new InvalidOperationException($"Launch option for {c.AppId} did not survive serialisation; nothing was written.");

        var backup = Backup(file, backupDir ?? DefaultBackupDir);
        Vdf.Save(file, root, newLine);
        foreach (var c in changes)
            Log.Info($"Steam launch option {c.AppId}{(c.GameId is null ? "" : $" ({c.GameId})")}: '{c.Before ?? ""}' -> '{c.After ?? ""}'");
        Log.Info($"Wrote {file} (backup: {backup})");
        return backup;
    }

    private static string Backup(string file, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var target = Path.Combine(backupDir, $"localconfig-{stamp}.vdf");
        for (int n = 1; File.Exists(target); n++) target = Path.Combine(backupDir, $"localconfig-{stamp}-{n}.vdf");
        File.Copy(file, target);

        foreach (var old in Directory.EnumerateFiles(backupDir, "localconfig-*.vdf")
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                     .Skip(BackupsKept))
        {
            try { File.Delete(old); }
            catch (Exception ex) { Log.Warn($"Could not delete old backup {old}: {ex.Message}"); }
        }
        return target;
    }

    /// <summary>UserLocalConfigStore/Software/Valve/Steam/apps (any key case; created when asked).</summary>
    private static Dictionary<string, object>? Apps(Dictionary<string, object> root, bool create)
    {
        if (!create)
            return root.Child("UserLocalConfigStore")?.Child("Software")?.Child("Valve")?.Child("Steam")?.Child("apps");
        return root.GetOrAddChild("UserLocalConfigStore").GetOrAddChild("Software").GetOrAddChild("Valve")
            .GetOrAddChild("Steam").GetOrAddChild("apps");
    }

    private static string Id(int appId) => appId.ToString(CultureInfo.InvariantCulture);

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
