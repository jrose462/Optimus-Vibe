using System.Globalization;
using OptimusVibe.Config;

namespace OptimusVibe.CloudSync;

/// <summary>One file Steam Cloud tracks for an app (an entry in userdata\&lt;account&gt;\&lt;appid&gt;\remotecache.vdf).</summary>
/// <param name="Name">Path relative to the root, with forward slashes as Steam writes it.</param>
/// <param name="Root">ERemoteStorageFileRoot: 0 Steam cloud dir, 1 install dir, 2 Documents, 3 LocalAppData, 4 LocalLow, 8 Saved Games, 9 ProgramData, 11 Roaming AppData.</param>
public sealed record SteamCloudEntry(string Name, int Root, long Size, DateTime? Time);

/// <summary>A configured file as seen by the sync check: its id, its resolved path (or the raw configured one) and handler kind.</summary>
public sealed record SyncFile(string Id, string Path, bool IsFile);

/// <summary>Reads Steam Cloud's remotecache.vdf and decides which configured settings files it syncs.</summary>
public static class SteamCloud
{
    public static IReadOnlyList<SteamCloudEntry> Parse(string vdfText)
    {
        var root = Vdf.Parse(vdfText);
        // The file has one top-level object named after the app id.
        var app = root.Values.OfType<Dictionary<string, object>>().FirstOrDefault();
        if (app is null) return [];
        var entries = new List<SteamCloudEntry>();
        foreach (var (name, value) in app)
        {
            if (value is not Dictionary<string, object> e) continue; // ChangeNumber, OSType...
            int.TryParse(e.Str("root"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rootId);
            long.TryParse(e.Str("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            DateTime? time = long.TryParse(e.Str("time") ?? e.Str("remotetime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0
                ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime
                : null;
            entries.Add(new SteamCloudEntry(name, rootId, size, time));
        }
        return entries;
    }

    /// <summary>The entries Steam Cloud tracks for an app on this machine; null when there is no userdata/remotecache for it.</summary>
    public static IReadOnlyList<SteamCloudEntry>? Read(SteamLocator steam, int appId)
    {
        var dir = steam.UserDataDir(appId);
        if (dir is null) return null;
        var file = System.IO.Path.Combine(dir, "remotecache.vdf");
        return File.Exists(file) ? Parse(File.ReadAllText(file)) : null;
    }

    /// <summary>
    /// Whether Steam Cloud syncs a file: the entry's root folder + name equals the file's path (roots Documents,
    /// LocalAppData, LocalLow, Saved Games, ProgramData, Roaming and the install dir are resolved), otherwise the entry
    /// name is matched as a case-insensitive path suffix of the file ("/" and "\" are equivalent).
    /// </summary>
    public static bool Syncs(IReadOnlyList<SteamCloudEntry> entries, SyncFile file, PathResolver resolver, int appId)
    {
        var path = Norm(file.Path);
        foreach (var e in entries)
        {
            var name = Norm(e.Name);
            if (name.Length == 0) continue;
            var rootToken = e.Root switch
            {
                1 => $"{{SteamLibrary:{appId}}}",
                2 => "{Documents}",
                3 => "{LocalAppData}",
                4 => "{LocalLow}",
                8 => "{SavedGames}",
                9 => "{ProgramData}",
                11 => "{AppData}",
                _ => null,
            };
            if (rootToken is not null)
            {
                try
                {
                    var full = Norm(System.IO.Path.Combine(resolver.Expand(rootToken), name));
                    if (full == path) return true;
                    continue; // the root is known: a different folder is a different file
                }
                catch (PathResolveException)
                {
                    // root not resolvable here: fall through to the suffix match
                }
            }
            if (path == name || path.EndsWith("\\" + name, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Lower-case, backslashes, no trailing separator.</summary>
    internal static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    /// <summary>
    /// Evidence from remotecache.vdf. Synced when any configured settings file is tracked; NotSynced when Steam Cloud
    /// tracks other files (typically saves) but none of them; Unknown when Steam has no cloud data for the app.
    /// </summary>
    public static LauncherEvidence Evaluate(IReadOnlyList<SteamCloudEntry>? entries, IReadOnlyList<SyncFile> files,
        PathResolver resolver, int appId, bool steamIsOnlyLauncher)
    {
        if (entries is null || entries.Count == 0)
        {
            // No cloud data. For a Steam-only game that means Steam syncs nothing; with other launchers in play, no answer.
            return steamIsOnlyLauncher
                ? new LauncherEvidence
                {
                    Verdict = SyncVerdict.NotSynced,
                    Confidence = SyncConfidence.Low,
                    Lines = ["No Steam Cloud data for this game."],
                }
                : LauncherEvidence.Unknown("No Steam Cloud data for this game.");
        }

        var fileFiles = files.Where(f => f.IsFile).ToList();
        if (fileFiles.Count == 0)
            return LauncherEvidence.Unknown("Steam Cloud tracks files, but this game's settings are not in files it can match.");

        var synced = fileFiles.Where(f => Syncs(entries, f, resolver, appId)).ToList();
        var unsynced = fileFiles.Except(synced).ToList();
        if (synced.Count > 0)
        {
            var line = $"Steam Cloud syncs {Names(synced)}" +
                       (unsynced.Count > 0 ? $" but not {Names(unsynced)}" : "") + ".";
            return new LauncherEvidence { Verdict = SyncVerdict.Synced, Confidence = SyncConfidence.High, Lines = [line] };
        }
        return new LauncherEvidence
        {
            Verdict = SyncVerdict.NotSynced,
            Confidence = SyncConfidence.High,
            Lines = [$"Steam Cloud syncs {Describe(entries)} but not {Names(unsynced)}."],
        };
    }

    private static string Names(IEnumerable<SyncFile> files) =>
        string.Join(", ", files.Select(f => System.IO.Path.GetFileName(f.Path.TrimEnd('\\', '/'))).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>"4 .sav files", "3 .sav and 1 .ini files".</summary>
    internal static string Describe(IReadOnlyList<SteamCloudEntry> entries)
    {
        var groups = entries
            .GroupBy(e => System.IO.Path.GetExtension(e.Name).ToLowerInvariant() is { Length: > 0 } x ? x : "(no extension)")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();
        var text = groups.Count <= 1 ? groups.FirstOrDefault() ?? "0" : string.Join(", ", groups.Take(groups.Count - 1)) + " and " + groups[^1];
        return $"{text} file{(entries.Count == 1 ? "" : "s")}";
    }
}
