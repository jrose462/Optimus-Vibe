namespace OptimusVibe.Launch;

/// <summary>Reads launcher logs to learn early that a launcher took the request but never started the game.</summary>
public static class LauncherLogs
{
    /// <summary>How long Battle.net gets to log LaunchBinary after the wrapper asked it to start a game.</summary>
    public static readonly TimeSpan BattleNetGrace = TimeSpan.FromSeconds(20);

    public const string BattleNetFailure = "Battle.net selected the game but did not launch it - press Play in Battle.net";

    public static string BattleNetLogDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net", "Logs");

    /// <summary>Length of every battle.net-*.log now, so only lines written after the launch request are looked at.</summary>
    public static Dictionary<string, long> Mark(string dir)
    {
        var marks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "battle.net-*.log"))
                    marks[f] = new FileInfo(f).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* no log: no early verdict */ }
        return marks;
    }

    /// <summary>
    /// Probe for <see cref="WatchdogInputs.EarlyFailure"/>: after the grace period, null when any Battle.net log gained a
    /// "LaunchBinary" line since <paramref name="marks"/>, the failure text when it did not. No log files at all = no verdict.
    /// </summary>
    public static string? BattleNetProbe(string dir, IReadOnlyDictionary<string, long> marks, TimeSpan elapsed, TimeSpan? grace = null)
    {
        if (elapsed < (grace ?? BattleNetGrace)) return null;
        try
        {
            if (!Directory.Exists(dir)) return null;
            var any = false;
            foreach (var f in Directory.EnumerateFiles(dir, "battle.net-*.log"))
            {
                any = true;
                var from = marks.TryGetValue(f, out var m) ? m : 0;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < from) from = 0; // rotated
                fs.Seek(from, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                if (sr.ReadToEnd().Contains("LaunchBinary", StringComparison.Ordinal)) return null;
            }
            return any ? BattleNetFailure : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // cannot read: never fail a launch on a guess
        }
    }
}
