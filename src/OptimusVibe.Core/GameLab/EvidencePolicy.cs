using System.Text.RegularExpressions;

namespace OptimusVibe.GameLab;

/// <summary>
/// What the workflow may ask the app to read (needs-evidence follow-ups). Everything not explicitly allowed is refused:
/// only paths under the user's profile / known game folders (or the game's own install dir) and HKCU/HKLM\Software, no
/// <c>..</c>, no system folders, no credential stores.
/// </summary>
public static partial class EvidencePolicy
{
    public const int MaxFilePatterns = 40;
    public const int MaxRegistryKeys = 20;
    public const int MaxListDirs = 20;
    public const int MaxMatchesPerPattern = 25;
    public const int MaxFilesTotal = 80;

    /// <summary>Tokens a requested path may start with. {UserProfile} is accepted only when the rest lands inside another allowed root.</summary>
    private static readonly HashSet<string> AllowedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Documents", "LocalAppData", "AppData", "LocalLow", "SavedGames", "UserProfile", "ProgramData", "SteamLibrary", "InstallDir",
    };

    /// <summary>Folder names that hold credentials, browser profiles or messenger data: refused wherever they appear in a path.</summary>
    private static readonly HashSet<string> DeniedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "credentials", "protect", "vault", "crypto", "systemcertificates", ".ssh", ".aws", ".azure", ".gnupg", ".kube",
        "google", "mozilla", "firefox", "opera software", "bravesoftware", "brave-browser", "microsoft edge",
        "discord", "1password", "keepass", "keepassxc", "bitwarden", "signal", "telegram desktop", "whatsapp", "slack",
        "cookies", "login data", "web data", "winlogon",
    };

    private static readonly Regex DeniedFile = DeniedFileRegex();

    [GeneratedRegex(@"(?i)^(ntuser\.dat.*|.*\.(kdbx|pfx|p12|pem|key|ppk)|id_(rsa|ed25519|ecdsa).*|loginusers\.vdf|ssfn\d*|secrets?(\..*)?|.*\.rdp)$")]
    private static partial Regex DeniedFileRegex();

    private static readonly HashSet<string> DeniedRegistrySegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "sam", "security", "cryptography", "credentials", "winlogon", "lsa", "policies", "password", "passwords", "authentication",
        "secrets", "protectedstorage", "microsoft", "currentversion\\run",
    };

    /// <summary>Why a path pattern is refused, or null when it is fine. <paramref name="absolute"/> is the expanded pattern (wildcards intact).</summary>
    public static string? CheckPath(string tokenized, LabFolders folders, out string absolute)
    {
        absolute = "";
        if (string.IsNullOrWhiteSpace(tokenized)) return "empty path";
        var p = tokenized.Trim();
        if (p.IndexOfAny(['\0', '|', '<', '>', '"']) >= 0) return "invalid characters";
        if (p.Contains("%", StringComparison.Ordinal)) return "environment variables are not allowed";
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)) return "network paths are not allowed";

        var token = LabFolders.LeadingTokenName(p);
        if (token is null) return "must start with a folder token such as {LocalAppData}";
        if (!AllowedTokens.Contains(token)) return $"folder token {{{token}}} is not allowed";

        var rest = p[(p.IndexOf('}') + 1)..];
        var segs = rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in segs)
        {
            if (s is ".." or ".") return "'..' is not allowed";
            if (s.Contains(':')) return "drive letters and alternate streams are not allowed";
            if (s.TrimEnd('.', ' ').Length != s.Length && s.TrimEnd('.', ' ').Length == 0) return "invalid path segment";
            if (DeniedSegments.Contains(s)) return $"'{s}' is a protected folder";
        }
        if (segs.Length > 0 && DeniedFile.IsMatch(segs[^1])) return "that file name is protected";

        var expanded = folders.Expand(p);
        if (expanded is null) return $"no folder is known for {{{token}}} on this PC";
        absolute = expanded;

        // Containment: after expansion the pattern must still sit under an allowed root.
        string full;
        try { full = Path.GetFullPath(expanded).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "invalid path"; }
        if (!folders.AllowedRoots().Any(root => IsUnder(full, root)))
            return "outside the user's profile and game folders";
        return null;
    }

    private static bool IsUnder(string full, string root)
    {
        root = root.TrimEnd('\\');
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why a registry key is refused, or null. <paramref name="normalized"/> uses HKCU / HKLM and backslashes.</summary>
    public static string? CheckRegistryKey(string key, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(key)) return "empty key";
        var k = key.Trim().Replace('/', '\\').TrimEnd('\\');
        if (k.IndexOfAny(['\0', '*', '?']) >= 0) return "invalid characters";
        k = Regex.Replace(k, @"^HKEY_CURRENT_USER\b", "HKCU", RegexOptions.IgnoreCase);
        k = Regex.Replace(k, @"^HKEY_LOCAL_MACHINE\b", "HKLM", RegexOptions.IgnoreCase);
        var segs = k.Split('\\');
        if (segs.Any(s => s.Length == 0)) return "invalid key";
        if (segs.Any(s => s is ".." or ".")) return "'..' is not allowed";
        if (!segs[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase) && !segs[0].Equals("HKLM", StringComparison.OrdinalIgnoreCase))
            return "only HKCU and HKLM are allowed";
        if (segs.Length < 3 || !segs[1].Equals("Software", StringComparison.OrdinalIgnoreCase))
            return segs.Length >= 2 && !segs[1].Equals("Software", StringComparison.OrdinalIgnoreCase)
                ? $"{segs[0]}\\{segs[1]} is not allowed (only {segs[0]}\\Software\\<vendor>...)"
                : "name a vendor key under Software, not the whole Software tree";
        foreach (var s in segs.Skip(2))
            if (DeniedRegistrySegments.Contains(s)) return $"'{s}' is a protected key";
        // WOW6432Node\<vendor> is fine; WOW6432Node alone is the whole tree.
        if (segs.Length == 3 && segs[2].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) return "name a vendor key under WOW6432Node";
        normalized = string.Join('\\', segs);
        normalized = segs[0].ToUpperInvariant() + normalized[segs[0].Length..];
        return null;
    }
}
