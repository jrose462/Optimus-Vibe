using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.GameLab;

/// <summary>
/// The user-profile and known folders the lab may look into, as token → absolute path ({Documents}, {LocalAppData}, ...), plus
/// per-game extras such as <c>{SteamLibrary:1234}</c> (the game's install dir). Converts absolute paths to tokenized ones and back;
/// injectable so tests can run on a fake profile tree.
/// </summary>
public sealed partial class LabFolders
{
    /// <summary>Tokens that may be used to start a collected path (UserProfile is only accepted when the rest is inside another root).</summary>
    public static readonly string[] ProfileTokens = ["Documents", "LocalAppData", "AppData", "LocalLow", "SavedGames", "ProgramData"];

    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _extras = new(StringComparer.OrdinalIgnoreCase); // "SteamLibrary:123" -> dir

    public LabFolders(IEnumerable<KeyValuePair<string, string>> tokens)
    {
        foreach (var (k, v) in tokens)
            if (!string.IsNullOrWhiteSpace(v)) _tokens[k] = Norm(v);
    }

    /// <summary>The real folders of the current user (via <see cref="PathResolver"/>); tokens that can't be resolved are left out.</summary>
    public static LabFolders FromSystem()
    {
        var resolver = new PathResolver();
        var map = new Dictionary<string, string>();
        foreach (var name in new[] { "Documents", "LocalAppData", "AppData", "LocalLow", "SavedGames", "UserProfile", "ProgramData", "ProgramFiles", "ProgramFilesX86" })
        {
            try { map[name] = resolver.Expand("{" + name + "}"); }
            catch (Exception ex) when (ex is PathResolveException or InvalidOperationException) { Log.Info($"game lab: folder {name} unavailable: {ex.Message}"); }
        }
        return new LabFolders(map);
    }

    public string? Get(string token) => _tokens.TryGetValue(token, out var v) ? v : null;

    public IReadOnlyDictionary<string, string> Tokens => _tokens;

    /// <summary>A copy that also knows an extra token such as "SteamLibrary:1234" or "InstallDir".</summary>
    public LabFolders WithExtra(string token, string? dir)
    {
        var copy = new LabFolders(_tokens);
        foreach (var kv in _extras) copy._extras[kv.Key] = kv.Value;
        if (!string.IsNullOrWhiteSpace(dir)) copy._extras[token] = Norm(dir);
        return copy;
    }

    public IReadOnlyDictionary<string, string> Extras => _extras;

    private static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');

    private IEnumerable<(string Token, string Path)> AllByLength() =>
        _extras.Select(kv => (kv.Key, kv.Value)).Concat(_tokens.Select(kv => (kv.Key, kv.Value)))
            .Where(t => t.Item2.Length > 3).OrderByDescending(t => t.Item2.Length);

    /// <summary>
    /// <c>C:\Users\me\AppData\Local\Foo\a.ini</c> → <c>{LocalAppData}\Foo\a.ini</c> (longest matching folder wins, case-insensitive,
    /// whole path segments only). Paths under no known folder are returned unchanged.
    /// </summary>
    public string Tokenize(string path)
    {
        var p = path.Replace('/', '\\');
        foreach (var (token, dir) in AllByLength())
        {
            if (p.Length < dir.Length || !p.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Length > dir.Length && p[dir.Length] != '\\') continue;
            return "{" + token + "}" + p[dir.Length..];
        }
        return path;
    }

    /// <summary>
    /// Replaces every known absolute folder inside free text (file contents, registry values) with its token, in single-backslash,
    /// double-backslash (JSON) and forward-slash spellings.
    /// </summary>
    public string TokenizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (token, dir) in AllByLength())
        {
            var parts = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
            var pattern = (dir.StartsWith(@"\\") ? @"\\\\" : "") + string.Join(@"[\\/]+", parts) + @"(?![\w.\-$])";
            var replacement = "{" + token + "}";
            text = Regex.Replace(text, pattern, _ => replacement, RegexOptions.IgnoreCase);
        }
        return text;
    }

    /// <summary>
    /// Expands <c>{Token}</c> / <c>{Token:arg}</c> at the start of a pattern. Null when the token is unknown or has no folder here
    /// (the caller treats that as "not allowed"). Environment variables are deliberately not expanded.
    /// </summary>
    public string? Expand(string tokenized)
    {
        var m = LeadingToken().Match(tokenized);
        if (!m.Success) return null;
        var name = m.Groups["name"].Value + (m.Groups["arg"].Success ? ":" + m.Groups["arg"].Value : "");
        var dir = _extras.TryGetValue(name, out var e) ? e : _tokens.TryGetValue(name, out var t) ? t : null;
        if (dir is null) return null;
        return dir + tokenized[m.Length..].Replace('/', '\\');
    }

    [GeneratedRegex(@"^\{(?<name>[A-Za-z0-9]+)(?::(?<arg>[^}]+))?\}")]
    private static partial Regex LeadingToken();

    /// <summary>Token name of the leading <c>{Token}</c> of a pattern, or null.</summary>
    internal static string? LeadingTokenName(string tokenized) =>
        LeadingToken().Match(tokenized) is { Success: true } m ? m.Groups["name"].Value : null;

    /// <summary>The absolute roots a collected path may live under (profile folders + per-game extras).</summary>
    internal IEnumerable<string> AllowedRoots()
    {
        foreach (var t in ProfileTokens)
            if (_tokens.TryGetValue(t, out var d)) yield return d;
        foreach (var d in _extras.Values) yield return d;
    }
}

/// <summary>Read-only registry access for the lab (so tests can use a fake).</summary>
public interface IRegistryView
{
    /// <summary>Names of the sub keys; empty when the key doesn't exist. Key like <c>HKCU\Software\Foo</c>.</summary>
    IReadOnlyList<string> SubKeyNames(string key);

    /// <summary>The values of the key as (name, formatted value); null when the key doesn't exist or can't be read.</summary>
    IReadOnlyList<(string Name, string Value)>? Values(string key);
}

/// <summary>The real registry (HKCU / HKLM, read-only, 64-bit view).</summary>
public sealed class SystemRegistryView : IRegistryView
{
    public IReadOnlyList<string> SubKeyNames(string key)
    {
        try
        {
            using var k = Open(key);
            return k?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return [];
        }
    }

    public IReadOnlyList<(string Name, string Value)>? Values(string key)
    {
        try
        {
            using var k = Open(key);
            if (k is null) return null;
            var list = new List<(string, string)>();
            foreach (var n in k.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var raw = k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (raw is null) continue;
                list.Add((n.Length == 0 ? "(Default)" : n, Format(raw, k.GetValueKind(n))));
            }
            return list;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static RegistryKey? Open(string path)
    {
        var parts = path.Split('\\', 2);
        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            _ => throw new ArgumentException("Registry path must start with HKCU or HKLM"),
        };
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return parts.Length < 2 ? null : baseKey.OpenSubKey(parts[1], writable: false);
    }

    internal static string Format(object raw, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((uint)(int)raw).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => unchecked((ulong)(long)raw).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => FormatBinary((byte[])raw),
        RegistryValueKind.MultiString => string.Join(" | ", (string[])raw),
        _ => raw.ToString() ?? "",
    };

    private static string FormatBinary(byte[] b)
    {
        const int max = 64;
        var hex = Convert.ToHexString(b.AsSpan(0, Math.Min(max, b.Length)));
        return b.Length > max ? $"hex:{hex}... ({b.Length} bytes)" : $"hex:{hex}";
    }
}
