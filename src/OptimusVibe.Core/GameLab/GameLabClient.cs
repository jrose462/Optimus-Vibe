using System.Net;
using System.Text.Json.Nodes;
using OptimusVibe.Config;
using OptimusVibe.Logging;
using OptimusVibe.Sync;
using OptimusVibe.Updates;

namespace OptimusVibe.GameLab;

/// <summary>An open (or closed) game-lab issue in the config repo.</summary>
public sealed record LabIssue(int Number, string Title, string Url, bool IsOpen, IReadOnlyList<string> Labels, LabStatus Status,
    GameLabContract.RequestInfo? Request, DateTime? CreatedUtc, DateTime? UpdatedUtc, string NodeId = "")
{
    public string Name => Request?.Name ?? Title;
    public LabKind? Kind => Request?.Kind;
    public string Describe() => $"#{Number} {Title} [{GameLabContract.StatusText(Status)}]";
}

/// <summary>A request the workflow could not finish without more files from this PC.</summary>
public sealed record PendingEvidence(LabIssue Issue, NeedsEvidence Request, long CommentId);

/// <summary>What <see cref="GameLabClient.FileRequestAsync"/> did.</summary>
/// <param name="Reused">An open request for the same game and kind already existed; nothing new was filed.</param>
public sealed record FiledRequest(int Number, string Url, bool Reused, int EvidenceComments, string Message);

/// <summary>A request whose evidence is already collected (and confirmed by the user): header plus the rendered evidence parts, ready to post.</summary>
public sealed record PreparedLabRequest(GameLabContract.RequestInfo Info, IReadOnlyList<string> EvidenceParts);

/// <summary>What <see cref="GameLabClient.DrainQueueAsync"/> did.</summary>
/// <param name="Dispatched">Issue number the workflow was started for, or null.</param>
public sealed record DrainResult(int? Dispatched, string Message);

/// <summary>What <see cref="GameLabClient.AnswerNeedsEvidenceAsync"/> sent.</summary>
public sealed record AnsweredEvidence(int Issue, int Comments, int Sections, IReadOnlyList<string> Notes);

/// <summary>
/// Talks to the user's config repo as the user: files game-lab requests, answers "needs evidence" follow-ups, starts the workflow
/// for queued requests and reads results. Construct with the user's OAuth/PAT token wrapped in a <see cref="GitHubApi"/>.
/// </summary>
public sealed class GameLabClient
{
    private readonly GitHubApi _api;
    private readonly string _repo;
    private readonly string _branch;
    private readonly Func<DateTime> _now;
    private (int Issue, DateTime At)? _lastDispatch;

    /// <param name="repo">owner/name of the private config repo.</param>
    public GameLabClient(GitHubApi api, string repo, string branch = "main", Func<DateTime>? now = null)
    {
        if (!GitHubApi.IsValidRepoName(repo)) throw new ArgumentException($"'{repo}' is not a repo name — use owner/name.", nameof(repo));
        _api = api;
        _repo = repo;
        _branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        _now = now ?? (() => DateTime.UtcNow);
    }

    public string Repo => _repo;
    public string Branch => _branch;

    // ------------------------------------------------------------ requests

    /// <summary>
    /// Files a request for a library entry: reuses an open request for the same store:storeId + kind (no new issue, no new
    /// evidence), otherwise collects evidence on this PC, opens the issue (first evidence part in the body, labels
    /// game-lab + lab:queued) and posts the remaining parts as comments.
    /// </summary>
    public Task<FiledRequest> FileRequestAsync(LabKind kind, Library.LibraryEntry entry, string device, EvidenceCollector collector,
        CancellationToken ct = default) =>
        FileRequestAsync(kind, LabTarget.From(entry), device, collector, ct);

    public async Task<FiledRequest> FileRequestAsync(LabKind kind, LabTarget target, string device, EvidenceCollector collector,
        CancellationToken ct = default)
    {
        var info = new GameLabContract.RequestInfo(kind, target.GameId ?? "", target.Name, target.Store, target.StoreId, device, AppVersion.Current);

        var existing = await FindOpenAsync(info, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            Log.Info($"game lab: reusing open request #{existing.Number} for {info.Key} ({GameLabContract.KindText(kind)})");
            return new FiledRequest(existing.Number, existing.Url, true, 0,
                $"A {GameLabContract.KindText(kind)} request for {target.Name} is already open (#{existing.Number}, {GameLabContract.StatusText(existing.Status)}).");
        }

        var bundle = collector.Collect(target);
        var parts = bundle.Split();
        var body = GameLabContract.RequestBody(info, parts[0]);
        // The header + human text are tiny; if the first part still doesn't fit, put it in a comment instead.
        var inBody = body.Length <= 65_000;
        if (!inBody) body = GameLabContract.RequestBody(info);

        var (number, url) = await _api.CreateIssueAsync(_repo, GameLabContract.Title(target.Name, kind), body,
            [GameLabContract.LabelGameLab], ct).ConfigureAwait(false);   // the workflow starts on "opened"; it adds lab:queued itself when busy
        if (number == 0) throw new GitHubApiException(null, "GitHub did not return an issue number.");

        var comments = 0;
        foreach (var part in inBody ? parts.Skip(1) : parts)
        {
            await PostCommentAsync(number, part, ct).ConfigureAwait(false);
            comments++;
        }
        Log.Info($"game lab: filed #{number} for {info.Key} ({GameLabContract.KindText(kind)}), {parts.Count} evidence part(s)");
        return new FiledRequest(number, url, false, comments, $"Filed #{number}: {GameLabContract.Title(target.Name, kind)}");
    }

    // ------------------------------------------------------------ prepared requests (offline outbox)

    /// <summary>The request header (kind, game, device, app version) for a target; no I/O.</summary>
    public static GameLabContract.RequestInfo InfoFor(LabKind kind, LabTarget target, string device) =>
        new(kind, target.GameId ?? "", target.Name, target.Store, target.StoreId, device, AppVersion.Current);

    /// <summary>
    /// Collects the evidence on this PC now (no network) and returns it as a <see cref="PreparedLabRequest"/>, exactly what
    /// <see cref="FilePreparedAsync"/> will post, so it can be stored in the outbox and sent later unchanged.
    /// </summary>
    public static PreparedLabRequest PrepareRequest(LabKind kind, LabTarget target, string device, EvidenceCollector collector) =>
        new(InfoFor(kind, target, device), collector.Collect(target).Split());

    /// <summary>
    /// Files a prepared request through the same path as <see cref="FileRequestAsync(LabKind, LabTarget, string, EvidenceCollector, CancellationToken)"/>:
    /// an open request for the same game and kind is reused (nothing new is filed) unless <paramref name="skipDedupe"/> says the
    /// caller just checked. Posts the stored evidence unchanged.
    /// </summary>
    public async Task<FiledRequest> FilePreparedAsync(PreparedLabRequest request, bool skipDedupe = false, CancellationToken ct = default)
    {
        var info = request.Info;
        if (!skipDedupe)
        {
            var existing = await FindOpenAsync(info, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                Log.Info($"game lab: reusing open request #{existing.Number} for {info.Key} ({GameLabContract.KindText(info.Kind)})");
                return new FiledRequest(existing.Number, existing.Url, true, 0,
                    $"A {GameLabContract.KindText(info.Kind)} request for {info.Name} is already open (#{existing.Number}, {GameLabContract.StatusText(existing.Status)}).");
            }
        }

        var parts = request.EvidenceParts;
        var body = GameLabContract.RequestBody(info, parts.Count > 0 ? parts[0] : null);
        var inBody = body.Length <= 65_000;
        if (!inBody) body = GameLabContract.RequestBody(info);

        var (number, url) = await _api.CreateIssueAsync(_repo, GameLabContract.Title(info.Name, info.Kind), body,
            [GameLabContract.LabelGameLab], ct).ConfigureAwait(false);
        if (number == 0) throw new GitHubApiException(null, "GitHub did not return an issue number.");

        var comments = 0;
        foreach (var part in inBody ? parts.Skip(1) : parts)
        {
            await PostCommentAsync(number, part, ct).ConfigureAwait(false);
            comments++;
        }
        Log.Info($"game lab: filed #{number} for {info.Key} ({GameLabContract.KindText(info.Kind)}), {parts.Count} evidence part(s)");
        return new FiledRequest(number, url, false, comments, $"Filed #{number}: {GameLabContract.Title(info.Name, info.Kind)}");
    }

    /// <summary>The open request with the same store:storeId and kind, if any (dedupe).</summary>
    public async Task<LabIssue?> FindOpenAsync(GameLabContract.RequestInfo info, CancellationToken ct = default)
    {
        foreach (var issue in await ListAsync(includeClosed: false, ct).ConfigureAwait(false))
        {
            var r = issue.Request;
            if (r is null || r.Kind != info.Kind) continue;
            if (r.Store.Length > 0 && info.Store.Length > 0 && r.Key.Equals(info.Key, StringComparison.OrdinalIgnoreCase)) return issue;
            if (r.GameId.Length > 0 && r.GameId.Equals(info.GameId, StringComparison.OrdinalIgnoreCase)) return issue;
        }
        return null;
    }

    /// <summary>Posts evidence comments (marker + part x/n) for an issue. Returns how many comments were created.</summary>
    public async Task<int> PostEvidenceAsync(int issue, EvidenceBundle bundle, CancellationToken ct = default)
    {
        var parts = bundle.Split();
        foreach (var part in parts) await PostCommentAsync(issue, part, ct).ConfigureAwait(false);
        return parts.Count;
    }

    // ------------------------------------------------------------ listing

    /// <summary>game-lab issues, oldest first. Open ones by default; <paramref name="includeClosed"/> adds closed ones (latest 100).</summary>
    public async Task<IReadOnlyList<LabIssue>> ListAsync(bool includeClosed = false, CancellationToken ct = default)
    {
        var url = $"repos/{_repo}/issues?labels={Uri.EscapeDataString(GameLabContract.LabelGameLab)}&state={(includeClosed ? "all" : "open")}&sort=created&direction=asc&per_page=100";
        var items = await _api.GetAllAsync(url, _repo, null, includeClosed ? 1 : 5, ct).ConfigureAwait(false);
        return items.Where(i => i["pull_request"] is null).Select(ToIssue).OrderBy(i => i.Number).ToList();
    }

    /// <summary>Open requests (status from the lab:* labels).</summary>
    public Task<IReadOnlyList<LabIssue>> ListOpenAsync(CancellationToken ct = default) => ListAsync(false, ct);

    internal static LabIssue ToIssue(JsonNode i)
    {
        var labels = (i["labels"] as JsonArray ?? []).Select(l => l?["name"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        return new LabIssue(
            i["number"]?.GetValue<int>() ?? 0,
            i["title"]?.GetValue<string>() ?? "",
            i["html_url"]?.GetValue<string>() ?? "",
            !string.Equals(i["state"]?.GetValue<string>(), "closed", StringComparison.OrdinalIgnoreCase),
            labels,
            GameLabContract.StatusFromLabels(labels),
            GameLabContract.ParseRequest(i["body"]?.GetValue<string>()),
            Date(i["created_at"]), Date(i["updated_at"]),
            i["node_id"]?.GetValue<string>() ?? "");
    }

    private static DateTime? Date(JsonNode? n) =>
        DateTime.TryParse(n?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    // ------------------------------------------------------------ needs evidence

    /// <summary>Open requests labelled lab:needs-evidence with the parsed ask from the workflow's latest needs-evidence comment.</summary>
    public async Task<IReadOnlyList<PendingEvidence>> FindNeedsEvidenceAsync(CancellationToken ct = default)
    {
        var result = new List<PendingEvidence>();
        foreach (var issue in (await ListAsync(false, ct).ConfigureAwait(false)).Where(i => i.Status == LabStatus.NeedsEvidence))
        {
            var pending = await GetPendingAsync(issue, ct).ConfigureAwait(false);
            if (pending is not null) result.Add(pending);
        }
        return result;
    }

    /// <summary>The latest needs-evidence ask on an issue; null when there is none or it is unreadable.</summary>
    public async Task<PendingEvidence?> GetPendingAsync(LabIssue issue, CancellationToken ct = default)
    {
        var comments = await CommentsAsync(issue.Number, ct).ConfigureAwait(false);
        foreach (var c in comments.AsEnumerable().Reverse())
        {
            var text = c["body"]?.GetValue<string>();
            if (text is null) continue;
            if (text.TrimStart().StartsWith(GameLabContract.EvidenceMarker, StringComparison.Ordinal)) return null; // already answered after the ask
            var ask = GameLabContract.ParseNeedsEvidence(text);
            if (ask is not null) return new PendingEvidence(issue, ask, c["id"]?.GetValue<long>() ?? 0);
        }
        return null;
    }

    /// <summary>
    /// Collects exactly what the workflow asked for (policy-checked), posts it as evidence comment(s) and removes
    /// lab:needs-evidence. The evidence comment itself re-triggers the workflow (it queues the request if busy or capped).
    /// </summary>
    public async Task<AnsweredEvidence> AnswerNeedsEvidenceAsync(PendingEvidence pending, EvidenceCollector collector, LabTarget? target = null,
        CancellationToken ct = default)
    {
        var bundle = collector.CollectRequested(pending.Request, target);
        var comments = await PostEvidenceAsync(pending.Issue.Number, bundle, ct).ConfigureAwait(false);
        await RemoveLabelAsync(pending.Issue.Number, GameLabContract.LabelNeedsEvidence, ct).ConfigureAwait(false);
        Log.Info($"game lab: answered needs-evidence on #{pending.Issue.Number} ({bundle.Sections.Count} section(s), {comments} comment(s))");
        return new AnsweredEvidence(pending.Issue.Number, comments, bundle.Sections.Count, bundle.Notes);
    }

    // ------------------------------------------------------------ queue

    /// <summary>
    /// When there are lab:queued requests and nothing is running (no lab:researching issue, no queued/in-progress workflow run),
    /// starts the workflow for the oldest one (workflow_dispatch game-lab.yml, input <c>issue</c>).
    /// With <paramref name="retryFailed"/> (an explicit Run now, never the background cycle, so a broken setup can't loop)
    /// and nothing queued, requeues the oldest failed request instead: adding lab:queued starts the workflow by itself.
    /// </summary>
    public async Task<DrainResult> DrainQueueAsync(CancellationToken ct = default, bool retryFailed = false)
    {
        var open = await ListAsync(false, ct).ConfigureAwait(false);
        var queued = open.Where(i => i.Status == LabStatus.Queued).OrderBy(i => i.Number).ToList();
        if (queued.Count == 0)
        {
            var failed = retryFailed ? open.Where(i => i.Status == LabStatus.Failed).MinBy(i => i.Number) : null;
            if (failed is null || open.Any(i => i.Status == LabStatus.Researching)) return new DrainResult(null, "Nothing is queued.");
            await AddLabelsAsync(failed.Number, [GameLabContract.LabelQueued], ct).ConfigureAwait(false);
            _lastDispatch = (failed.Number, _now());
            Log.Info($"game lab: requeued failed request #{failed.Number}");
            return new DrainResult(failed.Number, $"Retrying #{failed.Number} ({failed.Name}), which failed last time.");
        }
        if (open.Any(i => i.Status == LabStatus.Researching))
            return new DrainResult(null, $"Busy: #{open.First(i => i.Status == LabStatus.Researching).Number} is being researched; {queued.Count} request(s) wait.");
        if (_lastDispatch is { } last && (_now() - last.At) < TimeSpan.FromMinutes(2) && queued.Any(q => q.Number == last.Issue))
            return new DrainResult(null, $"#{last.Issue} was just started; waiting for the workflow to pick it up.");
        if (await ActiveRunsAsync(ct).ConfigureAwait(false) is > 0)
            return new DrainResult(null, $"Busy: the game-lab workflow is running; {queued.Count} request(s) wait.");

        var next = queued[0];
        // The gate already turned it away today for the daily cap: starting it again would only burn Actions minutes.
        if (await DailyCapHitTodayAsync(next.Number, ct).ConfigureAwait(false))
            return new DrainResult(null, $"Daily limit reached; #{next.Number} ({next.Name}) starts after 00:00 UTC.");
        var branch = _branch;
        try { branch = (await _api.GetRepoAsync(_repo, ct).ConfigureAwait(false)).DefaultBranch; }
        catch (GitHubException) { }

        var body = new JsonObject { ["ref"] = branch, ["inputs"] = new JsonObject { ["issue"] = next.Number.ToString() } };
        var resp = await _api.RequestAsync(HttpMethod.Post, $"repos/{_repo}/actions/workflows/{GameLabContract.WorkflowFile}/dispatches", body, _repo, false, ct).ConfigureAwait(false);
        if (resp.Status == HttpStatusCode.NotFound)
            return new DrainResult(null, $"The workflow {GameLabContract.WorkflowFile} is not installed in {_repo} (run `optimus-vibe lab setup`).");
        if (resp.Status == HttpStatusCode.UnprocessableEntity)
            return new DrainResult(null, $"GitHub refused to start the workflow: {resp.Json?["message"]?.GetValue<string>() ?? "422"}. Is {GameLabContract.WorkflowFile} on {branch} with a workflow_dispatch trigger?");
        if (!resp.Ok) throw LabHttp.Error(resp, _repo);
        _lastDispatch = (next.Number, _now());
        Log.Info($"game lab: started the workflow for #{next.Number}");
        return new DrainResult(next.Number, $"Started the game lab for #{next.Number} ({next.Name}).");
    }

    private async Task<int> ActiveRunsAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var status in new[] { "in_progress", "queued" })
        {
            var r = await _api.RequestAsync(HttpMethod.Get,
                $"repos/{_repo}/actions/workflows/{GameLabContract.WorkflowFile}/runs?status={status}&per_page=1", null, _repo, false, ct).ConfigureAwait(false);
            if (!r.Ok) continue; // can't tell: rely on the labels
            total += r.Json?["total_count"]?.GetValue<int>() ?? 0;
        }
        return total;
    }

    // ------------------------------------------------------------ result

    /// <summary>The latest lab-result comment of an issue; null when there is none yet.</summary>
    public async Task<LabResult?> GetResultAsync(int issue, CancellationToken ct = default)
    {
        var comments = await CommentsAsync(issue, ct).ConfigureAwait(false);
        foreach (var c in comments.AsEnumerable().Reverse())
            if (GameLabContract.ParseResult(c["body"]?.GetValue<string>()) is { } result) return result;
        return null;
    }

    /// <summary>
    /// Pulls the config repo through the sync provider so games\&lt;id&gt;.yaml from a merged result is on this PC.
    /// Fails (with a message) when the result isn't merged yet, the pull fails, or the file still isn't there.
    /// </summary>
    public static (bool Ok, string Message) ApplyResultLocally(LabResult result, AppPaths paths, ISyncProvider provider)
    {
        if (!result.Merged) return (false, result.PrNumber is { } pr ? $"PR #{pr} is not merged yet." : "The result is not merged yet.");
        if (string.IsNullOrWhiteSpace(result.GameId)) return (false, "The result names no game.");
        var pull = provider.Pull();
        if (!pull.Ok) return (false, $"Sync failed: {pull.Message}");
        var file = paths.GameFile(result.GameId);
        return File.Exists(file)
            ? (true, $"{result.GameId} is up to date on this PC ({pull.Message}).")
            : (false, $"Pulled ({pull.Message}) but games\\{result.GameId}.yaml is not on this PC.");
    }

    // ------------------------------------------------------------ plumbing

    /// <summary>
    /// True when the gate posted its daily-limit note on this issue today (UTC): the dated marker
    /// <c>&lt;!-- optimus-vibe:lab-queued v1 yyyy-MM-dd --&gt;</c>, or the older undated one with today's created_at.
    /// </summary>
    private async Task<bool> DailyCapHitTodayAsync(int issue, CancellationToken ct)
    {
        var today = _now().ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var c in await CommentsAsync(issue, ct).ConfigureAwait(false))
        {
            var body = c["body"]?.GetValue<string>() ?? "";
            if (body.Contains($"{GameLabContract.QueuedMarkerPrefix} {today} -->", StringComparison.Ordinal)) return true;
            if (body.Contains($"{GameLabContract.QueuedMarkerPrefix} -->", StringComparison.Ordinal)
                && (c["created_at"]?.GetValue<string>() ?? "").StartsWith(today, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private async Task<List<JsonNode>> CommentsAsync(int issue, CancellationToken ct) =>
        await _api.GetAllAsync($"repos/{_repo}/issues/{issue}/comments?per_page=100", _repo, null, 20, ct).ConfigureAwait(false);

    private async Task PostCommentAsync(int issue, string body, CancellationToken ct) =>
        await _api.RequestAsync(HttpMethod.Post, $"repos/{_repo}/issues/{issue}/comments", new JsonObject { ["body"] = body }, _repo, true, ct).ConfigureAwait(false);

    private async Task AddLabelsAsync(int issue, IEnumerable<string> labels, CancellationToken ct) =>
        await _api.RequestAsync(HttpMethod.Post, $"repos/{_repo}/issues/{issue}/labels",
            new JsonObject { ["labels"] = new JsonArray(labels.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()) }, _repo, true, ct).ConfigureAwait(false);

    private async Task RemoveLabelAsync(int issue, string label, CancellationToken ct) =>
        await _api.RequestAsync(HttpMethod.Delete, $"repos/{_repo}/issues/{issue}/labels/{Uri.EscapeDataString(label)}", null, _repo, false, ct).ConfigureAwait(false);

    private static Task<GitHubApiException> ErrorFromAsync(GitHubResponse r) =>
        Task.FromResult(new GitHubApiException(r.Status, $"GitHub API error {(int)r.Status}: {r.Json?["message"]?.GetValue<string>()}"));
}
