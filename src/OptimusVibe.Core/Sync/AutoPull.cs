using System.Globalization;
using System.Security.Cryptography;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Sync;

public enum PullStatus { Pulled, Skipped, TimedOut, Failed }

/// <param name="ChangedGames">Ids of game files that are new or different after the pull.</param>
public sealed record PullOutcome(PullStatus Status, string Message, IReadOnlyList<string> ChangedGames);

/// <summary>
/// Best-effort "get new presets" pull: skipped when the last successful pull is recent, stops waiting after a timeout, reports which
/// game files changed. Used by the app (startup, every 15 minutes, window re-activated) and, with a short timeout, before a game starts.
/// Never throws.
/// </summary>
public static class AutoPull
{
    public static string StampFile(string dataDir) => Path.Combine(dataDir, "last-pull.txt");

    /// <summary>When the config repo was last pulled successfully on this machine (UTC), or null.</summary>
    public static DateTime? LastSuccess(string dataDir)
    {
        try
        {
            var f = StampFile(dataDir);
            return File.Exists(f) && DateTime.TryParse(File.ReadAllText(f).Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var t) ? t : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void RecordSuccess(string dataDir, DateTime utcNow)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(StampFile(dataDir), utcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Could not record the pull time: {ex.Message}"); }
    }

    /// <param name="minAge">Skip when the last successful pull is newer than this (ignored with <paramref name="force"/>).</param>
    /// <param name="timeout">Stop waiting after this long. The pull itself is not cancelled; it records its own success when it ends.</param>
    public static PullOutcome Run(ISyncProvider provider, AppPaths paths, TimeSpan minAge, TimeSpan timeout, Func<DateTime>? now = null, bool force = false)
    {
        now ??= () => DateTime.UtcNow;
        try
        {
            if (!force && LastSuccess(paths.DataDir) is { } last && now() - last < minAge)
                return new PullOutcome(PullStatus.Skipped, "pulled recently", []);
            if (!provider.IsReady(out var why)) return new PullOutcome(PullStatus.Skipped, why, []);

            var task = Task.Run(() =>
            {
                var before = Snapshot(paths.GamesDir);
                var r = provider.Pull();
                if (!r.Ok) return new PullOutcome(PullStatus.Failed, r.Message, []);
                RecordSuccess(paths.DataDir, now());
                var after = Snapshot(paths.GamesDir);
                var changed = after.Where(kv => !before.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key).Order().ToList();
                return new PullOutcome(PullStatus.Pulled, r.Message, changed);
            });
            if (!task.Wait(timeout)) return new PullOutcome(PullStatus.TimedOut, $"no answer within {timeout.TotalSeconds:0.#} s", []);
            return task.Result;
        }
        catch (Exception ex)
        {
            Log.Warn($"Pull failed: {ex.Message}");
            return new PullOutcome(PullStatus.Failed, ex.Message, []);
        }
    }

    private static Dictionary<string, string> Snapshot(string gamesDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(gamesDir)) return map;
            foreach (var f in Directory.EnumerateFiles(gamesDir, "*.yaml"))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (id.StartsWith('_')) continue; // _template
                try { map[id] = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(f))); }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return map;
    }
}
