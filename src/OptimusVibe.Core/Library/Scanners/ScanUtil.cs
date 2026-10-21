using System.Text;

namespace OptimusVibe.Library.Scanners;

/// <summary>Helpers shared by the launcher scanners.</summary>
internal static class ScanUtil
{
    public const int MaxExes = 5;

    /// <summary>Folders that hold engine/installer/anti-cheat helpers, never the game exe.</summary>
    private static readonly string[] SkipDirs =
    [
        "engine", "__installer", "_commonredist", "commonredist", "redist", "redistributables", "directx", "vcredist",
        "easyanticheat", "battleye", "dotnetcore", "support", "installer", "installers", "prereqs", "prerequisites",
        "blizzardbrowser", "blizzarderror", "crashreporter", "eac", "origin", "__overlay", "tools", "uninstall",
    ];

    /// <summary>File-name fragments of helper exes (crash reporters, setups, redistributables, anti-cheat installers...).</summary>
    private static readonly string[] HelperExeHints =
    [
        "crash", "setup", "redist", "dxsetup", "unins", "cleanup", "touchup", "installer", "dotnet", "runtime",
        "prereq", "easyanticheat", "eac_", "battleye", "helper", "repair", "dump", "cleaner", "reporter", "checker",
        "blizzarderror", "errorreport", "updater", "patcher", "vc_", "oalinst", "physx", "uploader", "benchmark", "activation", "register",
        "cefprocess", "browser", "notification", "uplay", "overlay", "dxwebsetup", "elevate",
    ];

    public static bool IsHelperExe(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return HelperExeHints.Any(n.Contains);
    }

    /// <summary>
    /// Likely game exe names in an install folder: the folder itself, its direct sub folders, and the Unreal/Unity-style
    /// Binaries\Win64|Win32 folders (top level and one level down). Helpers are dropped; launcher-ish exes go last,
    /// otherwise bigger first. Distinct by file name, at most <see cref="MaxExes"/>.
    /// </summary>
    public static IReadOnlyList<string> FindExes(string? installDir, IEnumerable<string>? preferred = null)
    {
        var result = new List<string>();
        foreach (var p in preferred ?? [])
            if (!string.IsNullOrWhiteSpace(p) && !result.Contains(p, StringComparer.OrdinalIgnoreCase)) result.Add(p);
        foreach (var f in ExeFiles(installDir))
        {
            if (result.Count >= MaxExes) break;
            if (!result.Contains(f.Name, StringComparer.OrdinalIgnoreCase)) result.Add(f.Name);
        }
        return result;
    }

    /// <summary>
    /// The exe files <see cref="FindExes"/> picks its names from, in the same order (launcher-ish last, otherwise bigger first). Empty when the
    /// folder is missing. Never throws.
    /// </summary>
    public static IReadOnlyList<FileInfo> ExeFiles(string? installDir)
    {
        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return [];

        var found = new List<FileInfo>();
        try
        {
            var dirs = new List<string> { installDir };
            foreach (var sub in SafeDirs(installDir))
            {
                if (IsSkipDir(sub)) continue;
                dirs.Add(sub);
                foreach (var plat in new[] { "Win64", "Win32", "x64" })
                    dirs.Add(Path.Combine(sub, "Binaries", plat));
            }
            foreach (var plat in new[] { "Win64", "Win32" }) dirs.Add(Path.Combine(installDir, "Binaries", plat));

            foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in SafeFiles(dir, "*.exe"))
                    if (!IsHelperExe(f)) found.Add(new FileInfo(f));
            }
        }
        catch (Exception ex)
        {
            Logging.Log.Warn($"Could not list exes in {installDir}: {ex.Message}");
        }

        return found
            .OrderBy(f => f.Name.Contains("launcher", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(f => SafeLength(f))
            .ToList();
    }

    private static bool IsSkipDir(string dir)
    {
        var name = Path.GetFileName(dir).ToLowerInvariant();
        return SkipDirs.Contains(name) || name.StartsWith("directx") || name.StartsWith("dotnet") || name.StartsWith("vcredist");
    }

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; } catch { return 0; }
    }

    public static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return []; }
    }

    public static IEnumerable<string> SafeFiles(string dir, string pattern, SearchOption option = SearchOption.TopDirectoryOnly)
    {
        try { return Directory.GetFiles(dir, pattern, option); } catch { return []; }
    }

    /// <summary>Full path with forward slashes fixed and no trailing separator; null for blank input.</summary>
    public static string? NormalizeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Split a command-line argument string (double quotes group, no escapes beyond \").</summary>
    public static IReadOnlyList<string> SplitArgs(string? args)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return result;
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '\\' && i + 1 < args.Length && args[i + 1] == '"') { sb.Append('"'); i++; any = true; }
            else if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) result.Add(sb.ToString());
                sb.Clear();
                any = false;
            }
            else { sb.Append(c); any = true; }
        }
        if (any) result.Add(sb.ToString());
        return result;
    }

    /// <summary>Fixed drive roots (C:\, D:\, ...) that are ready.</summary>
    public static IEnumerable<string> FixedDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var d in drives)
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    /// <summary>The on-disk casing of an existing path (Steam's registry path is often all lower case); input unchanged otherwise.</summary>
    public static string ProperCase(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full) && !File.Exists(full)) return path;
            var root = Path.GetPathRoot(full)!;
            var result = root.ToUpperInvariant();
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = new DirectoryInfo(result).EnumerateFileSystemInfos(part).FirstOrDefault();
                result = Path.Combine(result, match?.Name ?? part);
            }
            return result;
        }
        catch
        {
            return path;
        }
    }

    public static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>One "Programs and Features" entry (HKLM Uninstall, 64-bit and WOW6432Node views).</summary>
internal sealed record UninstallEntry(string KeyName, string? DisplayName, string? Publisher, string? InstallLocation,
    string? UninstallString, string? DisplayIcon)
{
    public static readonly string[] Roots =
    [
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public static IEnumerable<UninstallEntry> Read(IRegistryReader registry)
    {
        foreach (var root in Roots)
        foreach (var name in registry.SubKeys(root))
        {
            var key = $@"{root}\{name}";
            yield return new UninstallEntry(name,
                registry.Value(key, "DisplayName"), registry.Value(key, "Publisher"), registry.Value(key, "InstallLocation"),
                registry.Value(key, "UninstallString"), registry.Value(key, "DisplayIcon"));
        }
    }
}
