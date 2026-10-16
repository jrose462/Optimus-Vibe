using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Logging;
using OptimusVibe.Sync;
using OptimusVibe.Updates;

namespace OptimusVibe.Feedback;

/// <summary>Everything a bug report carries besides the user's description. All optional; build with <see cref="Collect"/>.</summary>
public sealed class BugReportContext
{
    /// <summary>"OptimusVibe" or "optimus-vibe CLI" — which exe the report came from.</summary>
    public string? Source { get; init; }
    public string AppVersion { get; init; } = Updates.AppVersion.Current;
    public string OsVersion { get; init; } = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public string? DeviceName { get; init; }
    public string? DeviceReason { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Cpu { get; init; }
    public IReadOnlyList<string> Gpus { get; init; } = [];

    /// <summary>"git" or "github".</summary>
    public string? SyncProvider { get; init; }
    /// <summary>owner/name of the CONFIG repo (github provider), or the checkout path (git provider).</summary>
    public string? ConfigRepo { get; init; }

    public string? GameId { get; init; }
    public string? Preset { get; init; }

    /// <summary>Recent log lines, oldest first (see <see cref="BugReport.ReadRecentLog"/>).</summary>
    public IReadOnlyList<string> LogLines { get; init; } = [];

    /// <summary>The profile folder to replace with %USERPROFILE% (defaults to this user's).</summary>
    public string? UserProfile { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Gathers the context. Every argument may be null; settings and logs come from <see cref="AppPaths.DefaultDataDir"/>
    /// unless <paramref name="dataDir"/> is given. Never throws.
    /// </summary>
    public static BugReportContext Collect(string? source, DeviceSelection? device, HardwareInfo? hardware,
        string? gameId = null, string? preset = null, string? dataDir = null, int logLines = BugReport.DefaultLogLines)
    {
        dataDir ??= AppPaths.DefaultDataDir;
        AppSettings settings;
        try { settings = AppPaths.LoadSettings(dataDir); }
        catch (Exception) { settings = new AppSettings(); }
        var github = SyncProviders.IsGitHub(settings);
        return new BugReportContext
        {
            Source = source,
            DeviceName = device?.Name,
            DeviceReason = device?.Reason,
            Manufacturer = hardware?.Manufacturer,
            Model = hardware?.Model,
            Cpu = hardware?.Cpu,
            Gpus = hardware?.Gpus ?? [],
            SyncProvider = settings.SyncProvider,
            ConfigRepo = github ? $"{settings.GitHubRepo} ({settings.GitHubBranch})" : settings.ConfigRoot,
            GameId = gameId,
            Preset = preset,
            LogLines = BugReport.ReadRecentLog(Path.Combine(dataDir, "logs"), logLines),
        };
    }
}

/// <summary>A ready-to-file issue.</summary>
public sealed record BugReportDraft(string Title, string Body, IReadOnlyList<string> Labels);

/// <summary>Builds sanitized bug reports and files them as issues on the APP repo (<see cref="AppRepo"/>).</summary>
public static partial class BugReport
{
    public const string Label = "bug-report";
    public const int DefaultLogLines = 200;
    public const int MaxTitleText = 80;
    private const int MaxBodyChars = 60_000; // GitHub's issue body limit is 65,536

    /// <summary>Title "[bug] &lt;first line, ≤ 80 chars&gt;", Markdown body with environment + collapsible log, labels ["bug-report"].</summary>
    public static BugReportDraft Build(string userDescription, BugReportContext ctx)
    {
        if (string.IsNullOrWhiteSpace(userDescription)) throw new ArgumentException("Describe the problem first.", nameof(userDescription));
        var profile = ctx.UserProfile;
        string S(string? text) => Sanitize(text ?? "", profile);

        var description = S(userDescription.Trim().Replace("\r\n", "\n"));
        var firstLine = description.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0);
        if (firstLine.Length > MaxTitleText) firstLine = firstLine[..(MaxTitleText - 1)].TrimEnd() + "…";

        var sb = new StringBuilder();
        sb.Append("## What happened\n\n").Append(description).Append("\n\n");
        sb.Append("## Environment\n\n");
        sb.Append("| | |\n|---|---|\n");
        void Row(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.Append("| ").Append(key).Append(" | ").Append(Cell(S(value))).Append(" |\n");
        }
        Row("App version", ctx.AppVersion + (ctx.Source is null ? "" : $" ({ctx.Source})"));
        Row("OS", ctx.OsVersion);
        Row("Device profile", ctx.DeviceName is null ? null : ctx.DeviceName + (ctx.DeviceReason is null ? "" : $" — {ctx.DeviceReason}"));
        Row("Hardware", string.Join(" ", new[] { ctx.Manufacturer, ctx.Model }.Where(s => !string.IsNullOrWhiteSpace(s))));
        Row("CPU", ctx.Cpu);
        Row("GPU", ctx.Gpus.Count == 0 ? null : string.Join("; ", ctx.Gpus));
        Row("Sync", ctx.SyncProvider is null ? null : ctx.SyncProvider + (ctx.ConfigRepo is null ? "" : $" — {ctx.ConfigRepo}"));
        Row("Game", ctx.GameId is null ? null : ctx.GameId + (ctx.Preset is null ? "" : $" (preset {ctx.Preset})"));
        sb.Append('\n');

        var log = ctx.LogLines.Select(S).ToList();
        if (log.Count > 0)
        {
            var header = $"<details>\n<summary>Log (last {log.Count} lines)</summary>\n\n````text\n";
            const string footer = "````\n\n</details>\n";
            var budget = MaxBodyChars - sb.Length - header.Length - footer.Length;
            // Drop the oldest lines until the body fits.
            var start = 0;
            var size = log.Sum(l => l.Length + 1);
            while (size > budget && start < log.Count) size -= log[start++].Length + 1;
            sb.Append(header);
            foreach (var line in log.Skip(start)) sb.Append(line.Replace("````", "``\u200B``")).Append('\n');
            sb.Append(footer);
        }
        else
        {
            sb.Append("_No log lines found._\n");
        }

        return new BugReportDraft("[bug] " + firstLine, sb.ToString(), [Label]);
    }

    /// <summary>
    /// Files the draft on the APP repo (jrose462/Optimus-Vibe — not the user's config repo) and returns the issue URL.
    /// Retries without labels when GitHub rejects them. Throws <see cref="GitHubException"/> with a clear message.
    /// </summary>
    public static Task<string> SubmitAsync(BugReportDraft draft, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new GitHubException("Sign in to GitHub to send a bug report.");
        return SubmitAsync(draft, new GitHubApi(token), ct);
    }

    internal static async Task<string> SubmitAsync(BugReportDraft draft, GitHubApi api, CancellationToken ct)
    {
        try
        {
            try
            {
                var (_, url) = await api.CreateIssueAsync(AppRepo.FullName, draft.Title, draft.Body, draft.Labels, ct).ConfigureAwait(false);
                return Done(url);
            }
            catch (GitHubApiException ex) when (draft.Labels.Count > 0 && ex.Status is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Forbidden
                                                && !ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"bug report: labels rejected ({ex.Message}); retrying without labels");
                var (_, url) = await api.CreateIssueAsync(AppRepo.FullName, draft.Title, draft.Body, null, ct).ConfigureAwait(false);
                return Done(url);
            }
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            throw new GitHubException($"This GitHub account can't open issues on {AppRepo.FullName} (the repo is private) — ask the owner for access.", ex);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.Gone)
        {
            throw new GitHubException($"Issues are turned off on {AppRepo.FullName}.", ex);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.Forbidden && !ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubException($"GitHub refused the bug report: {ex.Message}", ex);
        }

        static string Done(string url)
        {
            Log.Info($"bug report filed: {url}");
            return url;
        }
    }

    // ------------------------------------------------------------ logs

    /// <summary>Last <paramref name="maxLines"/> lines of today's log, topped up from yesterday's when today's is short. Never throws.</summary>
    public static IReadOnlyList<string> ReadRecentLog(string logsDir, int maxLines = DefaultLogLines, DateTime? today = null)
    {
        var day = (today ?? DateTime.Now).Date;
        var lines = new List<string>();
        foreach (var d in new[] { day, day.AddDays(-1) })
        {
            if (lines.Count >= maxLines) break;
            var file = Path.Combine(logsDir, "optimusvibe-" + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            var content = ReadShared(file);
            if (content.Count == 0) continue;
            var take = Math.Min(content.Count, maxLines - lines.Count);
            lines.InsertRange(0, content.Skip(content.Count - take));
        }
        return lines;
    }

    private static List<string> ReadShared(string file)
    {
        try
        {
            if (!File.Exists(file)) return [];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var result = new List<string>();
            while (reader.ReadLine() is { } line) result.Add(line);
            return result;
        }
        catch (Exception)
        {
            return [];
        }
    }

    // ------------------------------------------------------------ sanitizing

    /// <summary>
    /// Redacts GitHub tokens (ghp_/gho_/ghu_/ghs_/ghr_..., github_pat_...), Bearer / Authorization header values, and
    /// replaces the user's profile folder (C:\Users\&lt;name&gt;) with %USERPROFILE%.
    /// </summary>
    public static string Sanitize(string text, string? userProfile = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = GitHubToken().Replace(text, "[REDACTED-TOKEN]");
        s = AuthHeader().Replace(s, "$1[REDACTED]");
        s = Bearer().Replace(s, "$1[REDACTED]");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var p = userProfile.TrimEnd('\\', '/');
            if (p.Length > 3)
            {
                s = Regex.Replace(s, Regex.Escape(p) + @"(?![^\\/\s""'<>|:;,)\]])", "%USERPROFILE%", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, Regex.Escape(p.Replace('\\', '/')) + @"(?![^\\/\s""'<>|:;,)\]])", "%USERPROFILE%", RegexOptions.IgnoreCase);
            }
        }
        s = AnyProfile().Replace(s, "%USERPROFILE%");
        return s;
    }

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,})")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"(?i)(\bauthorization\s*[:=]\s*""?)[^\s""]+(?:\s+[^\s""]+)?")]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"(?i)(\bbearer\s+)(?!\[REDACTED)[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex Bearer();

    /// <summary>Any C:\Users\&lt;name&gt; (or C:/Users/&lt;name&gt;) except Public / Default / All Users.</summary>
    [GeneratedRegex(@"(?i)\b[A-Z]:[\\/]+Users[\\/]+(?!(?:Public|Default|All Users)(?:[\\/\s""']|$))[^\\/\s""'<>|:;,]+")]
    private static partial Regex AnyProfile();

    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
