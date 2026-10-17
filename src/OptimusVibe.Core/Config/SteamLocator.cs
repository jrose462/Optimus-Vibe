using Microsoft.Win32;

namespace OptimusVibe.Config;

/// <summary>Finds Steam install dirs and userdata folders for {SteamLibrary:appid} / {SteamUserData:appid} tokens.</summary>
public sealed class SteamLocator
{
    private const long SteamId64Base = 76561197960265728;
    private readonly string? _steamRoot;

    public SteamLocator(string? steamRoot = null)
    {
        _steamRoot = steamRoot ?? FindSteamRoot();
    }

    public string? SteamRoot => _steamRoot;

    public static string? FindSteamRoot()
    {
        // OPTIMUSVIBE_STEAM_ROOT points the app and CLI at a scratch copy of the Steam folder (testing).
        if (Environment.GetEnvironmentVariable("OPTIMUSVIBE_STEAM_ROOT") is { Length: > 0 } over) return Path.GetFullPath(over);
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                   ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        return path is null ? null : Path.GetFullPath(path);
    }

    public IEnumerable<string> Libraries()
    {
        if (_steamRoot is null) yield break;
        yield return _steamRoot;
        var vdfPath = Path.Combine(_steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;
        var root = Vdf.Load(vdfPath).Child("libraryfolders");
        if (root is null) yield break;
        foreach (var lib in root.Values.OfType<Dictionary<string, object>>())
        {
            var p = lib.Str("path");
            if (p is not null && !string.Equals(Path.GetFullPath(p), _steamRoot, StringComparison.OrdinalIgnoreCase))
                yield return Path.GetFullPath(p);
        }
    }

    /// <summary>steamapps/common/&lt;installdir&gt; for an installed app, or null.</summary>
    public string? InstallDir(int appId)
    {
        foreach (var lib in Libraries())
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            var dir = Vdf.Load(manifest).Child("AppState")?.Str("installdir");
            if (dir is not null) return Path.Combine(lib, "steamapps", "common", dir);
        }
        return null;
    }

    /// <summary>userdata/&lt;accountid&gt;/&lt;appid&gt; for the most recent Steam login (Steam Cloud files live under here).</summary>
    public string? UserDataDir(int appId)
    {
        if (_steamRoot is null) return null;
        var userdata = Path.Combine(_steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return null;

        var account = MostRecentAccountId();
        if (account is not null)
        {
            var candidate = Path.Combine(userdata, account, appId.ToString());
            if (Directory.Exists(candidate)) return candidate;
        }

        return Directory.EnumerateDirectories(userdata)
            .Select(d => Path.Combine(d, appId.ToString()))
            .Where(Directory.Exists)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Account id (userdata folder name) of the Steam login marked MostRecent in loginusers.vdf, or null.</summary>
    public string? MostRecentAccountId()
    {
        if (_steamRoot is null) return null;
        var path = Path.Combine(_steamRoot, "config", "loginusers.vdf");
        if (!File.Exists(path)) return null;
        var users = Vdf.Load(path).Child("users");
        if (users is null) return null;
        foreach (var (id64, value) in users)
        {
            if (value is Dictionary<string, object> u && u.Str("MostRecent") == "1" && long.TryParse(id64, out var id))
                return (id - SteamId64Base).ToString();
        }
        return null;
    }
}
