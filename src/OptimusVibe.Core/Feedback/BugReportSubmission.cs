using System.Text;
using System.Text.Json;
using OptimusVibe.Config;
using OptimusVibe.Logging;
using OptimusVibe.Sync;
using OptimusVibe.Updates;

namespace OptimusVibe.Feedback;

/// <summary>
/// The built-in bug-report token, baked in at build time (BUG_REPORT_TOKEN env var / -p:BugReportToken, see OptimusVibe.Core.csproj).
/// It is XOR-obfuscated with a per-build random key in <see cref="BakedSecrets"/>. That only keeps it out of a plain
/// strings/grep of the exe; it is NOT security. The token is therefore a fine-grained token owned by a bot account with
/// Issues read/write on the app repo and nothing else. The value is never logged or displayed.
/// </summary>
internal static class BugReportToken
{
    public static bool IsAvailable => BakedSecrets.BugReportData.Length > 0 && BakedSecrets.BugReportKey.Length > 0;

    /// <summary>The decoded token, or null in dev builds. Callers must not log it.</summary>
    public static string? Get()
    {
        var key = BakedSecrets.BugReportKey;
        var data = BakedSecrets.BugReportData;
        if (data.Length == 0 || key.Length == 0) return null;
        var plain = new byte[data.Length];
        for (var i = 0; i < data.Length; i++) plain[i] = (byte)(data[i] ^ key[i % key.Length]);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>Client-side rate limit for bug reports: at most <see cref="MaxPerDay"/> per rolling 24 hours per machine (a courtesy, not enforcement).</summary>
public static class BugReportLimiter
{
    public const int MaxPerDay = 5;
    private const string FileName = "bugreports.json";

    /// <summary>Throws <see cref="GitHubException"/> when today's quota is used up.</summary>
    public static void EnsureAllowed(string? dataDir = null, DateTimeOffset? now = null)
    {
        var recent = Recent(dataDir, now ?? DateTimeOffset.UtcNow);
        if (recent.Count >= MaxPerDay)
            throw new GitHubException(
                $"You've already sent {MaxPerDay} reports in the last 24 hours. Please wait a bit, or add to one of your open issues at {AppRepo.IssuesUrl}.");
    }

    public static void Record(string? dataDir = null, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var list = Recent(dataDir, t);
        list.Add(t);
        try
        {
            dataDir ??= AppPaths.DefaultDataDir;
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(list.Select(x => x.ToUnixTimeSeconds())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"bug report rate-limit file: {ex.Message}");
        }
    }

    private static List<DateTimeOffset> Recent(string? dataDir, DateTimeOffset now)
    {
        try
        {
            var file = Path.Combine(dataDir ?? AppPaths.DefaultDataDir, FileName);
            if (!File.Exists(file)) return [];
            var stamps = JsonSerializer.Deserialize<List<long>>(File.ReadAllText(file)) ?? [];
            return stamps.Select(DateTimeOffset.FromUnixTimeSeconds).Where(t => now - t < TimeSpan.FromHours(24) && t <= now.AddMinutes(5)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
