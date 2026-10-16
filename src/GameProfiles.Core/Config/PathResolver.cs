using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameProfiles.Config;

/// <summary>Expands {Token} / {Token:arg} / %ENV% in configured paths and resolves * wildcards to the newest match.</summary>
public sealed partial class PathResolver
{
    private readonly SteamLocator _steam;
    private readonly Dictionary<string, string> _overrides;

    public PathResolver(SteamLocator? steam = null, Dictionary<string, string>? overrides = null)
    {
        _steam = steam ?? new SteamLocator();
        _overrides = overrides ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public static readonly string[] KnownTokens =
    [
        "Documents", "LocalAppData", "AppData", "LocalLow", "SavedGames", "UserProfile",
        "ProgramData", "ProgramFiles", "ProgramFilesX86", "SteamRoot", "SteamLibrary", "SteamUserData",
    ];

    [GeneratedRegex(@"\{(?<name>[A-Za-z0-9]+)(?::(?<arg>[^}]+))?\}")]
    private static partial Regex TokenRegex();

    /// <summary>Expands tokens only. Throws if a token cannot be resolved (e.g. game not installed).</summary>
    public string Expand(string path)
    {
        var expanded = TokenRegex().Replace(path, m => ResolveToken(m.Groups["name"].Value,
            m.Groups["arg"].Success ? m.Groups["arg"].Value : null));
        return Environment.ExpandEnvironmentVariables(expanded);
    }

    /// <summary>Expands tokens and wildcards for a filesystem path. Returns null if a wildcard matches nothing.</summary>
    public string? ResolveFile(string path)
    {
        var expanded = Expand(path).Replace('/', '\\');
        if (!expanded.Contains('*') && !expanded.Contains('?')) return Path.GetFullPath(expanded);
        return ExpandWildcards(expanded);
    }

    public static IEnumerable<string> TokensIn(string path) =>
        TokenRegex().Matches(path).Select(m => m.Groups["name"].Value);

    private string ResolveToken(string name, string? arg)
    {
        if (_overrides.TryGetValue(name, out var o)) return o;
        return name.ToLowerInvariant() switch
        {
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "localappdata" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "appdata" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "userprofile" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "programdata" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "programfiles" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "programfilesx86" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "locallow" => KnownFolder(new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16")),
            "savedgames" => KnownFolder(new Guid("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4")),
            "steamroot" => _steam.SteamRoot ?? throw new PathResolveException("Steam is not installed"),
            "steamlibrary" => _steam.InstallDir(ParseAppId(name, arg))
                              ?? throw new PathResolveException($"Steam app {arg} is not installed"),
            "steamuserdata" => _steam.UserDataDir(ParseAppId(name, arg))
                               ?? throw new PathResolveException($"No Steam userdata folder for app {arg}"),
            _ => throw new PathResolveException($"Unknown path token {{{name}}}"),
        };
    }

    private static int ParseAppId(string token, string? arg) =>
        int.TryParse(arg, out var id) ? id : throw new PathResolveException($"{{{token}}} needs an app id, e.g. {{{token}:1091500}}");

    private static string? ExpandWildcards(string path)
    {
        var parts = path.Split('\\');
        IEnumerable<string> current = [parts[0] + "\\"];
        for (int i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;
            bool last = i == parts.Length - 1;
            current = current.SelectMany(dir =>
            {
                if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
                if (!part.Contains('*') && !part.Contains('?')) return [Path.Combine(dir, part)];
                return last
                    ? Directory.EnumerateFileSystemEntries(dir, part)
                    : Directory.EnumerateDirectories(dir, part);
            }).ToList();
        }
        return current
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .OrderByDescending(p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : Directory.GetLastWriteTimeUtc(p))
            .FirstOrDefault();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static string KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0)
            throw new PathResolveException($"Known folder {id} not available");
        try { return Marshal.PtrToStringUni(ptr)!; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }
}

public sealed class PathResolveException(string message) : Exception(message);
