using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OptimusVibe.Config;
using OptimusVibe.Library;
using OptimusVibe.Logging;
using OptimusVibe.Sync;

namespace OptimusVibe.GameLab;

// What the app does around the game lab without being asked: find the repo, route new games to the lab or the local queue,
// answer follow-ups, start queued work and pull merged results. Pure logic over the Core clients, so it is testable without UI.

/// <summary>The config repo the lab works in.</summary>
/// <param name="Source">Where it came from, for the UI ("GitHub account" / "git checkout").</param>
public sealed record LabRepoRef(string Repo, string Branch, string Source);

public static class LabRepoResolver
{
    /// <summary>
    /// The GitHub repo for the lab: the GitHub provider's chosen repo, or the GitHub remote of the local git checkout.
    /// Otherwise null with a sentence that says why. <paramref name="git"/> runs git in a folder (null result = failed); tests inject it.
    /// </summary>
    public static (LabRepoRef? Repo, string? Problem) Resolve(AppSettings settings, AppPaths? paths, Func<string, string[], string?>? git = null)
    {
        if (SyncProviders.IsGitHub(settings))
        {
            var repo = settings.GitHubRepo?.Trim();
            if (string.IsNullOrEmpty(repo))
                return (null, "No config repo is chosen yet. Pick or create one in the Config repo section.");
            if (!GitHubApi.IsValidRepoName(repo))
                return (null, $"“{repo}” isn't an owner/name repository. Fix it in the Config repo section.");
            return (new LabRepoRef(repo, Branch(settings.GitHubBranch), "GitHub account"), null);
        }

        if (paths is null)
            return (null, "No config folder yet. Choose where your game settings live first.");
        git ??= GitText.Run;
        var url = git(paths.RepoRoot, ["remote", "get-url", "origin"]);
        var parsed = ParseGitHubRemote(url);
        if (parsed is null)
            return (null, url is null
                ? "Your config folder is a plain folder or a git checkout without a GitHub remote. The game lab needs a private config repo on GitHub: use the Config repo section."
                : "Your git checkout's remote isn't on GitHub. The game lab works through a private GitHub config repo: use the Config repo section.");
        var branch = git(paths.RepoRoot, ["rev-parse", "--abbrev-ref", "HEAD"]);
        return (new LabRepoRef(parsed, Branch(branch is null or "HEAD" ? settings.GitHubBranch : branch), "git checkout"), null);
    }

    private static string Branch(string? b) => string.IsNullOrWhiteSpace(b) ? "main" : b.Trim();

    /// <summary>"owner/name" from https://github.com/o/n(.git), git@github.com:o/n.git, ssh://git@github.com/o/n.git; null for other hosts.</summary>
    public static string? ParseGitHubRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = Regex.Match(url.Trim(), @"^(?:https?://(?:[^@/]+@)?|ssh://(?:[^@/]+@)?|git@)github\.com[:/]+(?<o>[^/\s]+)/(?<n>[^/\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var repo = $"{m.Groups["o"].Value}/{m.Groups["n"].Value}";
        return GitHubApi.IsValidRepoName(repo) ? repo : null;
    }
}

/// <summary>Runs a git command and returns its trimmed output; null when git is missing, times out or fails.</summary>
internal static class GitText
{
    public static string? Run(string workingDir, string[] args)
    {
        try
        {
            if (!Directory.Exists(workingDir)) return null;
            var psi = new ProcessStartInfo("git")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(8000))
            {
                p.Kill(entireProcessTree: true);
                return null;
            }
            return p.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"git {args.FirstOrDefault()}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Lets an action run at most once per interval (the background lab work: at most every 5 minutes). Thread-safe.</summary>
public sealed class LabThrottle(TimeSpan interval, Func<DateTime>? now = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private DateTime? _last;

    /// <summary>True (and starts a new interval) when the last start is older than the interval, or on <paramref name="force"/>.</summary>
    public bool TryStart(bool force = false)
    {
        lock (_gate)
        {
            var t = _now();
            if (!force && _last is { } last && t - last < interval) return false;
            _last = t;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate) _last = null;
    }
}

/// <summary>Local, per-device lab bookkeeping (<c>gamelab-state.json</c> in the data dir; never synced).</summary>
public sealed class LabState
{
    /// <summary>"store:id" → when the app last filed (or found) a request for it. Stops a closed request from being filed again on every start.</summary>
    public Dictionary<string, DateTime> Requested { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Issue numbers whose merged result was already pulled and announced.</summary>
    public List<int> Applied { get; set; } = [];

    /// <summary>The first background pass counts everything already done as announced, so old requests don't toast on first start.</summary>
    public bool Baselined { get; set; }
}

public sealed class LabStateStore(string dataDir)
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string FilePath => Path.Combine(dataDir, "gamelab-state.json");

    public LabState Load()
    {
        lock (Gate) return LoadCore();
    }

    /// <summary>Read-modify-write. Never throws.</summary>
    public void Update(Action<LabState> change)
    {
        lock (Gate)
        {
            var s = LoadCore();
            change(s);
            try
            {
                Directory.CreateDirectory(dataDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts), new UTF8Encoding(false));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not save {FilePath}: {ex.Message}");
            }
        }
    }

    private LabState LoadCore()
    {
        try
        {
            if (!File.Exists(FilePath)) return new LabState();
            var s = JsonSerializer.Deserialize<LabState>(File.ReadAllText(FilePath), Opts) ?? new LabState();
            s.Requested = new Dictionary<string, DateTime>(s.Requested ?? [], StringComparer.OrdinalIgnoreCase);
            s.Applied ??= [];
            return s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Could not read {FilePath}: {ex.Message}");
            return new LabState();
        }
    }
}

/// <summary>Which lab request belongs to a game, and how the app words its progress.</summary>
public static class LabStatusIndex
{
    /// <summary>The newest request for a game (open ones win over closed ones); matched by store:id, then by game id.</summary>
    public static LabIssue? For(IEnumerable<LabIssue> issues, string? key, string? gameId)
    {
        LabIssue? best = null;
        foreach (var i in issues)
        {
            var r = i.Request;
            if (r is null) continue;
            var byKey = !string.IsNullOrEmpty(key) && r.Store.Length > 0 && r.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            var byId = !string.IsNullOrEmpty(gameId) && r.GameId.Length > 0 && r.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase);
            if (!byKey && !byId) continue;
            if (best is null || (i.IsOpen && !best.IsOpen) || (i.IsOpen == best.IsOpen && i.Number > best.Number)) best = i;
        }
        return best;
    }

    /// <summary>Requested / Researching / Needs evidence / Review / Done: the same words as the board columns.</summary>
    public static string Chip(LabIssue issue) => LabBoard.StatusFor(issue.Status, issue.IsOpen);
}

/// <summary>What <see cref="LabRouter"/> did with the games that need presets.</summary>
/// <param name="Filed">Requests newly filed with the lab.</param>
/// <param name="Reused">Games that already had an open request, or were requested recently.</param>
/// <param name="QueuedLocally">Entries put in the local requests.yaml queue.</param>
public sealed record LabRouteResult(bool UsedLab, int Filed, int Reused, int QueuedLocally, IReadOnlyList<string> Errors);

public static class LabRouter
{
    /// <summary>How long a game is not asked about again after the app requested it (a closed request must not be re-filed on every start).</summary>
    public static readonly TimeSpan RequestMemory = TimeSpan.FromDays(7);

    public static LabKind KindFor(LibraryEntry entry) => entry.Game is null ? LabKind.New : LabKind.Refresh;

    /// <summary>
    /// Routes detected games that need presets. Lab set up and auto-request on: file a lab request per game (open requests and games
    /// requested within <see cref="RequestMemory"/> are skipped); a game whose request fails falls back to the local queue.
    /// Otherwise everything goes to the local requests.yaml queue, as before.
    /// </summary>
    public static async Task<LabRouteResult> RouteAsync(IReadOnlyList<LibraryEntry> needs, bool labReady, bool autoRequest,
        Func<LibraryEntry, CancellationToken, Task<FiledRequest>> fileRequest, Func<IReadOnlyList<LibraryEntry>, int> queueLocally,
        LabStateStore state, Func<DateTime>? now = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (needs.Count == 0) return new LabRouteResult(false, 0, 0, 0, errors);
        if (!labReady || !autoRequest) return new LabRouteResult(false, 0, 0, queueLocally(needs), errors);

        now ??= () => DateTime.UtcNow;
        var known = state.Load().Requested;
        int filed = 0, reused = 0;
        var fallback = new List<LibraryEntry>();
        foreach (var e in needs)
        {
            if (e.Detected is null || e.OptedOut) continue;
            if (known.TryGetValue(e.Key, out var at) && now() - at < RequestMemory) { reused++; continue; }
            try
            {
                var r = await fileRequest(e, ct).ConfigureAwait(false);
                state.Update(s => s.Requested[e.Key] = now());
                if (r.Reused) reused++; else filed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"game lab: request for {e.Name} failed, queueing locally: {ex.Message}");
                errors.Add($"{e.Name}: {ex.Message}");
                fallback.Add(e);
            }
        }
        var queued = fallback.Count > 0 ? queueLocally(fallback) : 0;
        return new LabRouteResult(true, filed, reused, queued, errors);
    }
}

public sealed record LabNewPresets(string GameId, string Name, LabKind Kind);

/// <param name="Issues">Every recent request (open and closed), newest state as read at the start of the pass.</param>
/// <param name="Answered">Names of the games whose "needs evidence" follow-up was answered.</param>
public sealed record LabCycleResult(IReadOnlyList<LabIssue> Issues, IReadOnlyList<string> Answered, DrainResult? Drain, string? Board,
    IReadOnlyList<LabNewPresets> NewPresets, IReadOnlyList<string> Errors);

public sealed class LabCycleDeps
{
    public required GameLabClient Client { get; init; }

    /// <summary>The game on this PC for a request (install folder and exes for the evidence); null = collect without it.</summary>
    public required Func<LabIssue, LabTarget?> TargetFor { get; init; }

    public required Func<EvidenceCollector> Collector { get; init; }
    public Func<CancellationToken, Task<BoardSyncResult>>? SyncBoard { get; init; }
    public required AppPaths Paths { get; init; }
    public required ISyncProvider Provider { get; init; }
    public required LabStateStore State { get; init; }
    public Func<DateTime>? Now { get; init; }
}

/// <summary>
/// The background pass (app start and after each sync, at most every 5 minutes): answer "needs evidence" follow-ups, start the
/// queue, move the board cards and pull results that were merged. Each phase fails on its own; nothing throws.
/// </summary>
public static class LabCycle
{
    public static async Task<LabCycleResult> RunAsync(LabCycleDeps d, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var answered = new List<string>();
        var newPresets = new List<LabNewPresets>();
        DrainResult? drain = null;
        string? board = null;
        IReadOnlyList<LabIssue> issues = [];

        try { issues = await d.Client.ListAsync(includeClosed: true, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"game lab: could not read requests: {ex.Message}");
            errors.Add(ex.Message);
            return new LabCycleResult(issues, answered, null, null, newPresets, errors);
        }

        try
        {
            foreach (var p in await d.Client.FindNeedsEvidenceAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await d.Client.AnswerNeedsEvidenceAsync(p, d.Collector(), d.TargetFor(p.Issue), ct).ConfigureAwait(false);
                    answered.Add(p.Issue.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"game lab: answering #{p.Issue.Number} failed: {ex.Message}");
                    errors.Add($"#{p.Issue.Number}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(ex.Message); }

        try { drain = await d.Client.DrainQueueAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(ex.Message); }

        if (d.SyncBoard is not null)
        {
            try { board = (await d.SyncBoard(ct).ConfigureAwait(false)).Message; }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"game lab: board sync failed: {ex.Message}"); }
        }

        try { await PullMergedAsync(d, issues, newPresets, errors, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(ex.Message); }

        return new LabCycleResult(issues, answered, drain, board, newPresets, errors);
    }

    private static async Task PullMergedAsync(LabCycleDeps d, IReadOnlyList<LabIssue> issues, List<LabNewPresets> newPresets,
        List<string> errors, CancellationToken ct)
    {
        var now = (d.Now ?? (() => DateTime.UtcNow))();
        var state = d.State.Load();
        var done = issues.Where(i => i.Status == LabStatus.Done || (!i.IsOpen && i.Labels.Contains(GameLabContract.LabelDone, StringComparer.OrdinalIgnoreCase)))
            .Where(i => i.Status != LabStatus.Failed).ToList();

        if (!state.Baselined)
        {
            d.State.Update(s => { s.Baselined = true; foreach (var i in done) if (!s.Applied.Contains(i.Number)) s.Applied.Add(i.Number); });
            return;
        }

        var fresh = new List<(LabIssue Issue, LabResult Result)>();
        var stale = new List<int>();
        foreach (var i in done.Where(i => !state.Applied.Contains(i.Number)))
        {
            var result = await d.Client.GetResultAsync(i.Number, ct).ConfigureAwait(false);
            if (result is { Merged: true }) fresh.Add((i, result));
            else if (result is null && i.UpdatedUtc is { } u && now - u > TimeSpan.FromDays(2)) stale.Add(i.Number); // closed with no result comment: stop asking
        }
        if (stale.Count > 0) d.State.Update(s => s.Applied.AddRange(stale.Where(n => !s.Applied.Contains(n))));
        if (fresh.Count == 0) return;

        var pull = d.Provider.Pull();
        if (!pull.Ok)
        {
            errors.Add($"Sync failed: {pull.Message}");
            return;
        }
        d.State.Update(s => s.Applied.AddRange(fresh.Select(f => f.Issue.Number).Where(n => !s.Applied.Contains(n))));
        foreach (var (issue, result) in fresh)
        {
            var kind = issue.Kind ?? LabKind.New;
            var gameId = string.IsNullOrWhiteSpace(result.GameId) ? issue.Request?.GameId ?? "" : result.GameId;
            if (kind == LabKind.SyncCheck && !File.Exists(d.Paths.GameFile(gameId))) continue;
            newPresets.Add(new LabNewPresets(gameId, issue.Request?.Name is { Length: > 0 } n ? n : issue.Name, kind));
        }
    }
}
