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
            ConfigRepo = github ? $"{settings.GitHubRepo ?? "(none)"} ({settings.GitHubBranch})" : settings.ConfigRoot,
            GameId = gameId,
            Preset = preset,
            LogLines = BugReport.ReadRecentLog(Path.Combine(dataDir, "logs"), logLines),
        };
    }
}

/// <summary>A ready-to-file issue.</summary>
public sealed record BugReportDraft(string Title, string Body, IReadOnlyList<string> Labels);

/// <summary>Builds sanitized bug reports and files them as issues on the APP repo (<see cref="AppRepo"/>) with a built-in token, or prefilled in the browser.</summary>
public static partial class BugReport
{
    public const string Label = "bug-report";

    /// <summary>Hidden marker on the first line of every app-generated report; the triage workflow only runs on issues that carry it.</summary>
    public const string Marker = "<!-- optimus-vibe:report v1 -->";
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
        sb.Append(Marker).Append("\n\n");
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
    /// True when this build carries the built-in bug-report token (release builds). Without it (dev builds) the UI falls back to
    /// <see cref="BuildNewIssueUrl"/>: copy the report and open a prefilled GitHub issue form in the browser.
    /// </summary>
    public static bool HasBuiltInToken => BugReportToken.IsAvailable;

    /// <summary>Who a report will be filed as.</summary>
    public enum ReportIdentity
    {
        /// <summary>The signed-in GitHub user (their token): they are the author and get notifications.</summary>
        User,
        /// <summary>The built-in, issues-only token of official builds: filed anonymously via Optimus-Vibe.</summary>
        BuiltIn,
        /// <summary>Neither: copy the report and open a prefilled issue page in the browser.</summary>
        Browser,
    }

    /// <summary>Which identity <see cref="SubmitAsync(BugReportDraft, string?, CancellationToken, string?)"/> will use.</summary>
    public static ReportIdentity ResolveIdentity(string? userToken) =>
        !string.IsNullOrWhiteSpace(userToken) ? ReportIdentity.User
        : HasBuiltInToken ? ReportIdentity.BuiltIn
        : ReportIdentity.Browser;

    /// <summary>"Filed as @login" / "Filed anonymously via Optimus-Vibe" / browser text, for the report panel and CLI.</summary>
    public static string DescribeIdentity(ReportIdentity identity, string? login) => identity switch
    {
        ReportIdentity.User => string.IsNullOrWhiteSpace(login) ? "Filed as your signed-in GitHub account" : $"Filed as @{login}",
        ReportIdentity.BuiltIn => "Filed anonymously via Optimus-Vibe",
        _ => "Opens a prefilled issue page in your browser (submit it with your own GitHub account)",
    };

    /// <summary>
    /// Files the draft on the APP repo (jrose462/Optimus-Vibe, not the user's config repo). With a signed-in
    /// <paramref name="userToken"/> (the OAuth "repo" scope can open issues on a public repo) the user is the author; otherwise the
    /// built-in token files it anonymously. Returns the issue URL. Limited to <see cref="BugReportLimiter.MaxPerDay"/> reports
    /// per day per machine. Labels are tried and dropped when GitHub refuses them (non-collaborators can't label); the body's
    /// hidden marker is what the triage workflow keys on. Never logs a token. Throws <see cref="GitHubException"/>.
    /// </summary>
    public static Task<string> SubmitAsync(BugReportDraft draft, string? userToken = null, CancellationToken ct = default, string? dataDir = null) =>
        SubmitCoreAsync(draft, userToken, BugReportToken.Get(), t => new GitHubApi(t), enforceLimit: true, ct, dataDir);

    /// <summary>
    /// Sends a report that was queued in the offline outbox. Identical to <see cref="SubmitAsync(BugReportDraft, string?, CancellationToken, string?)"/>
    /// except that the daily limit is neither checked nor recorded: it was already counted when the report was queued
    /// (<see cref="OptimusVibe.Outbox.OutboxFlow.QueueBugReport"/>).
    /// </summary>
    public static Task<string> SubmitQueuedAsync(BugReportDraft draft, string? userToken = null, CancellationToken ct = default) =>
        SubmitCoreAsync(draft, userToken, BugReportToken.Get(), t => new GitHubApi(t), enforceLimit: false, ct, null);

    internal static async Task<string> SubmitCoreAsync(BugReportDraft draft, string? userToken, string? builtIn, Func<string, GitHubApi> api,
        bool enforceLimit, CancellationToken ct, string? dataDir)
    {
        var token = !string.IsNullOrWhiteSpace(userToken) ? userToken.Trim() : builtIn
                    ?? throw new GitHubException("This build can't send reports directly. Use the browser option instead.");
        if (enforceLimit) BugReportLimiter.EnsureAllowed(dataDir);
        string url;
        try
        {
            url = await SubmitAsync(draft, api(token), ct, signedIn: token != builtIn).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.Unauthorized && builtIn is not null && token != builtIn)
        {
            Log.Info("bug report: the saved GitHub sign-in was rejected; filing with the built-in access instead");
            url = await SubmitAsync(draft, api(builtIn), ct).ConfigureAwait(false);
        }
        if (enforceLimit) BugReportLimiter.Record(dataDir);
        return url;
    }

    /// <summary>Most characters of the percent-encoded query we put in a prefilled "new issue" link (browsers and GitHub cap URLs near 8 KB).</summary>
    public const int MaxPrefillQueryChars = 6500;

    /// <summary>
    /// A https://github.com/jrose462/Optimus-Vibe/issues/new link with the title, body and label prefilled. The body is cut
    /// (at a line break, with a note) until the encoded link fits; the caller also puts the full report on the clipboard.
    /// No sign-in is needed to open it (submitting the form needs a GitHub account).
    /// </summary>
    public static string BuildNewIssueUrl(BugReportDraft draft, out bool truncated)
    {
        const string note = "\n\n_(Report shortened to fit a link. The full report is on your clipboard: paste it here.)_";
        string Url(string body) =>
            $"https://github.com/{AppRepo.FullName}/issues/new?title={Uri.EscapeDataString(draft.Title)}" +
            $"&labels={Uri.EscapeDataString(string.Join(",", draft.Labels))}&body={Uri.EscapeDataString(body)}";
        bool Fits(string u) => u.Length - ("https://github.com/" + AppRepo.FullName + "/issues/new?").Length <= MaxPrefillQueryChars;

        truncated = false;
        var full = Url(draft.Body);
        if (Fits(full)) return full;
        truncated = true;
        var lines = draft.Body.Replace("\r\n", "\n").Split('\n');
        var keep = lines.Length;
        while (keep > 1)
        {
            keep = (int)(keep * 0.8);
            var candidate = string.Join("\n", lines.Take(keep)) + note;
            var u = Url(candidate);
            if (Fits(u)) return u;
        }
        return Url(draft.Body[..Math.Min(draft.Body.Length, 1500)] + note);
    }


    internal static async Task<string> SubmitAsync(BugReportDraft draft, GitHubApi api, CancellationToken ct, bool signedIn = false)
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
            throw new GitHubException($"Couldn't open an issue on {AppRepo.FullName}. Open one yourself at {AppRepo.IssuesUrl}.", ex);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.Unauthorized && !signedIn)
        {
            throw new GitHubException($"The built-in bug-report access was rejected (it may have been rotated). Update the app, or open an issue yourself at {AppRepo.IssuesUrl}.", ex);
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
    public static string Sanitize(string text, string? userProfile = null, string? userName = null, string? machineName = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = GitHubToken().Replace(text, "[REDACTED-TOKEN]");
        s = AuthHeader().Replace(s, "$1[REDACTED]");
        s = Bearer().Replace(s, "$1[REDACTED]");
        s = Email().Replace(s, "[EMAIL]");
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
        s = SteamUserdata().Replace(s, "$1[STEAM-ID]");
        s = Steam64().Replace(s, "[STEAM-ID]");
        s = IPv4().Replace(s, "[IP]");
        s = IPv6().Replace(s, m => m.Value.Any(char.IsDigit) ? "[IP]" : m.Value);

        // Names last, as whole words (paths and emails are already handled). Names shorter than 3 chars are skipped.
        var machine = (machineName ?? SafeMachineName())?.Trim();
        if (machine is { Length: >= 3 })
            s = Regex.Replace(s, @"(?<![A-Za-z0-9_])" + Regex.Escape(machine) + @"(?![A-Za-z0-9_])", "[MACHINE]", RegexOptions.IgnoreCase);
        var names = new List<string>();
        var user = (userName ?? SafeUserName())?.Trim();
        if (!string.IsNullOrEmpty(user)) names.Add(user);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var leaf = userProfile.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(leaf)) names.Add(leaf);
        }
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
            if (n.Length >= 3) s = Regex.Replace(s, @"(?<![A-Za-z0-9_])" + Regex.Escape(n) + @"(?![A-Za-z0-9_])", "[USER]", RegexOptions.IgnoreCase);
        return s;
    }

    private static string? SafeUserName() { try { return Environment.UserName; } catch (Exception) { return null; } }
    private static string? SafeMachineName() { try { return Environment.MachineName; } catch (Exception) { return null; } }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?i)(\buserdata[\\/]+)\d{3,}")]
    private static partial Regex SteamUserdata();

    [GeneratedRegex(@"\b7656119\d{10}\b")]
    private static partial Regex Steam64();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.]*\d)")]
    private static partial Regex IPv4();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6})?|::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6}))(?![0-9A-Fa-f:])")]
    private static partial Regex IPv6();

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
