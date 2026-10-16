using System.Globalization;

namespace OptimusVibe.CloudSync;

/// <summary>
/// Reads the EA app's own log for its cloud saves. EADesktop.log lines look like
/// <c>[2026-02-06T03:21:03.327Z] PID: 3164 TID: 38260 INFO (eax::services::cloudsync::retrieveLocalState) [CloudSync] New file
/// added to the LocalState filesMap. File path [C:\Users\me\Saved Games\Respawn\JediSurvivor\GameUserSettings.sav], file size
/// [8360], LocalState totalSize [...]</c>: one line per file the EA app uploads for a game.
/// </summary>
public static class EaCloudLog
{
    private const string Marker = "[CloudSync]";
    private const string PathStart = "File path [";
    private const string PathEnd = "], file size";

    /// <summary>%LOCALAPPDATA%\Electronic Arts\EA Desktop\Logs</summary>
    public static string DefaultLogsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Electronic Arts", "EA Desktop", "Logs");

    /// <summary>EADesktop*.log (current) and EADesktop*.bak (rotated), newest first; the Verbose copies are skipped.</summary>
    public static IReadOnlyList<string> LogFiles(string logsDir)
    {
        if (!Directory.Exists(logsDir)) return [];
        return Directory.EnumerateFiles(logsDir, "EADesktop*")
            .Where(f => (f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                        && !Path.GetFileName(f).Contains("Verbose", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>Normalized file path → the last time the EA app logged it as part of a cloud sync (UTC, or null if the line has no timestamp).</summary>
    public static Dictionary<string, DateTime?> ReadSyncedFiles(IEnumerable<string> logFiles)
    {
        var result = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        foreach (var log in logFiles)
        {
            try
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                Parse(reader, result);
            }
            catch (IOException) { /* locked or rotated away: skip it */ }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>Stream through a log; only lines containing "CloudSync" are looked at.</summary>
    public static void Parse(TextReader reader, Dictionary<string, DateTime?> into)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.Contains(Marker, StringComparison.Ordinal)) continue;
            int s = line.IndexOf(PathStart, StringComparison.Ordinal);
            if (s < 0) continue;
            s += PathStart.Length;
            int e = line.IndexOf(PathEnd, s, StringComparison.Ordinal);
            if (e <= s) continue;
            var path = SteamCloud.Norm(line[s..e]);
            var time = Timestamp(line);
            if (!into.TryGetValue(path, out var seen) || (time is not null && (seen is null || time > seen))) into[path] = time ?? seen;
        }
    }

    private static DateTime? Timestamp(string line)
    {
        // "[2026-02-06T03:21:03.327Z]", possibly after a short line-number prefix ("   322" and a tab)
        var start = line.IndexOf('[');
        if (start < 0 || start > 16) return null;
        var end = line.IndexOf(']', start);
        if (end < 0) return null;
        return DateTime.TryParse(line.AsSpan(start + 1, end - start - 1), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    }

    /// <summary>
    /// Synced when the EA app logs a configured file; NotSynced (medium) when it logs other files in the same folder but
    /// not this one; Unknown when the log has nothing for these files.
    /// </summary>
    public static LauncherEvidence Evaluate(IReadOnlyDictionary<string, DateTime?> synced, IReadOnlyList<SyncFile> files, bool logsFound)
    {
        if (!logsFound) return LauncherEvidence.Unknown("EA app logs not found on this machine.");
        var fileFiles = files.Where(f => f.IsFile).ToList();
        var hits = fileFiles.Where(f => synced.ContainsKey(SteamCloud.Norm(f.Path))).ToList();
        if (hits.Count > 0)
        {
            var misses = fileFiles.Except(hits).ToList();
            var last = hits.Select(f => synced[SteamCloud.Norm(f.Path)]).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty().Max();
            var when = last == default ? "" : $" (last seen {last.ToLocalTime():MMM d})";
            return new LauncherEvidence
            {
                Verdict = SyncVerdict.Synced,
                Confidence = SyncConfidence.High,
                Lines = [$"EA app cloud-syncs {Names(hits)}{when}" + (misses.Count > 0 ? $" but not {Names(misses)}." : ".")],
            };
        }

        var folders = synced.Keys.Select(k => Path.GetDirectoryName(k) ?? "").ToHashSet(StringComparer.Ordinal);
        var sibling = fileFiles.Where(f => folders.Contains(Path.GetDirectoryName(SteamCloud.Norm(f.Path)) ?? "\0")).ToList();
        if (sibling.Count > 0)
        {
            return new LauncherEvidence
            {
                Verdict = SyncVerdict.NotSynced,
                Confidence = SyncConfidence.Medium,
                Lines = [$"EA app cloud-syncs other files in that folder, but not {Names(sibling)}."],
            };
        }
        return LauncherEvidence.Unknown(synced.Count == 0
            ? "EA app log has no cloud-sync entries yet."
            : "EA app log does not mention this game's settings files.");
    }

    private static string Names(IEnumerable<SyncFile> files) =>
        string.Join(", ", files.Select(f => Path.GetFileName(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase));
}
