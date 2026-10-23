using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimusVibe.Sync;

/// <summary>A GitHub REST call failed. The message is user-facing; <see cref="Status"/> is the HTTP status, if any.</summary>
public sealed class GitHubApiException(HttpStatusCode? status, string message, Exception? inner = null) : GitHubException(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>One entry of a git tree (path relative to the tree root, '/' separators).</summary>
public sealed record GitTreeEntry(string Path, string Mode, string Type, string Sha, long? Size);

public sealed record GitTree(string Sha, IReadOnlyList<GitTreeEntry> Entries, bool Truncated);

/// <summary>A change for <see cref="GitHubApi.CreateTreeAsync"/>: Sha = blob to put at Path, or null to delete Path.</summary>
public sealed record GitTreeChange(string Path, string? Sha);

/// <summary>A raw REST answer from <see cref="GitHubApi.RequestAsync"/>. <see cref="Next"/> is the Link rel="next" URL, if any.</summary>
public sealed record GitHubResponse(HttpStatusCode Status, JsonNode? Json, IReadOnlyDictionary<string, string> Headers, string? Next = null)
{
    public bool Ok => (int)Status is >= 200 and < 300;
}

/// <summary>Minimal GitHub REST client: account, repos, and the git data API used by <see cref="GitHubMirror"/>.</summary>
public sealed partial class GitHubApi
{
    private const int MaxRepoPages = 10;

    private static readonly SocketsHttpHandler SharedHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    private readonly HttpClient _http;
    private readonly SignInHealth? _health;

    /// <param name="trackSignIn">False for a token that is not (yet) the saved sign-in (a candidate being checked, the bug-report token): its answers stay out of <see cref="SignInHealth"/>.</param>
    public GitHubApi(string token, bool trackSignIn = true) : this(token, SharedHandler, trackSignIn ? SignInHealth.Default : null) { }

    internal GitHubApi(string token, HttpMessageHandler handler, SignInHealth? health = null)
    {
        _health = health;
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Token = token.Trim();
        _http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Optimus-Vibe");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public string Token { get; }

    /// <summary>True for "owner/name".</summary>
    public static bool IsValidRepoName(string? repo) => repo is not null && RepoName().IsMatch(repo);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoName();

    // ------------------------------------------------------------ account / repos

    /// <summary>Login of the token's user (validates the token).</summary>
    public async Task<string> GetLoginAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("user", null, ct).ConfigureAwait(false);
        return json["login"]?.GetValue<string>() ?? throw new GitHubApiException(null, "GitHub returned no login for this token.");
    }

    /// <summary>Repos the user can push to (owner, collaborator, org member), most recently pushed first.</summary>
    public async Task<IReadOnlyList<GitHubRepoInfo>> ListReposAsync(CancellationToken ct = default)
    {
        var result = new List<GitHubRepoInfo>();
        string? url = "user/repos?affiliation=owner,collaborator,organization_member&sort=pushed&per_page=100";
        for (var page = 0; url is not null && page < MaxRepoPages; page++)
        {
            using var resp = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw await FailAsync(resp, null, ct).ConfigureAwait(false);
            var json = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
            foreach (var r in json.AsArray())
            {
                if (r is null || r["permissions"]?["push"]?.GetValue<bool>() != true) continue;
                result.Add(ToRepoInfo(r));
            }
            url = NextLink(resp);
        }
        return result;
    }

    /// <summary>Repo metadata (validates existence and access).</summary>
    public async Task<GitHubRepoInfo> GetRepoAsync(string repo, CancellationToken ct = default) =>
        ToRepoInfo(await GetJsonAsync($"repos/{RepoPath(repo)}", repo, ct).ConfigureAwait(false));

    /// <summary>
    /// Creates a repo under the signed-in account (POST /user/repos, auto_init so it has a first commit + README).
    /// <paramref name="name"/> is the bare repo name (an "owner/" prefix is ignored).
    /// </summary>
    public async Task<GitHubRepoInfo> CreateRepoAsync(string name, bool isPrivate, string? description, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Contains('/')) name = name[(name.LastIndexOf('/') + 1)..];
        if (!BareRepoName().IsMatch(name)) throw new GitHubApiException(null, $"'{name}' is not a valid repo name (letters, digits, '.', '-', '_').");
        var body = new JsonObject
        {
            ["name"] = name,
            ["private"] = isPrivate,
            ["description"] = description,
            ["auto_init"] = true,
        };
        using var resp = await SendAsync(HttpMethod.Post, "user/repos", body, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new GitHubApiException(resp.StatusCode, text.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                ? $"A repo named '{name}' already exists on this account — pick another name, or use it with `github use`."
                : $"GitHub refused to create '{name}': {TryMessage(text) ?? "validation failed"}");
        }
        if (!resp.IsSuccessStatusCode) throw await FailAsync(resp, null, ct).ConfigureAwait(false);
        return ToRepoInfo(await ReadJsonAsync(resp, ct).ConfigureAwait(false));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex BareRepoName();

    /// <summary>
    /// Creates (or overwrites when <paramref name="existingSha"/> is given) one file with a commit, via the contents API.
    /// Unlike the git data API this works on an empty repo (it creates the first commit). Returns the new commit sha.
    /// </summary>
    public async Task<string> PutFileAsync(string repo, string path, byte[] content, string message, string? branch = null,
        string? existingSha = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["message"] = message, ["content"] = Convert.ToBase64String(content) };
        if (branch is not null) body["branch"] = branch;
        if (existingSha is not null) body["sha"] = existingSha;
        var url = $"repos/{RepoPath(repo)}/contents/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
        var json = await SendJsonAsync(HttpMethod.Put, url, body, repo, ct).ConfigureAwait(false);
        return json["commit"]?["sha"]?.GetValue<string>() ?? throw new GitHubApiException(null, "GitHub returned no commit for the file.");
    }

    /// <summary>Opens an issue (POST /repos/{repo}/issues). Returns (number, html URL). Errors as <see cref="GitHubApiException"/> with Status.</summary>
    public async Task<(int Number, string Url)> CreateIssueAsync(string repo, string title, string body, IReadOnlyList<string>? labels,
        CancellationToken ct = default)
    {
        var json = new JsonObject { ["title"] = title, ["body"] = body };
        if (labels is { Count: > 0 }) json["labels"] = new JsonArray(labels.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
        var result = await SendJsonAsync(HttpMethod.Post, $"repos/{RepoPath(repo)}/issues", json, repo, ct).ConfigureAwait(false);
        return (result["number"]?.GetValue<int>() ?? 0, result["html_url"]?.GetValue<string>() ?? "");
    }

    private static string? TryMessage(string text)
    {
        try { return JsonNode.Parse(text)?["message"]?.GetValue<string>(); }
        catch (Exception) { return null; }
    }

    private static GitHubRepoInfo ToRepoInfo(JsonNode r) => new(
        r["full_name"]?.GetValue<string>() ?? "",
        r["default_branch"]?.GetValue<string>() ?? "main",
        r["private"]?.GetValue<bool>() ?? false,
        r["description"]?.GetValue<string?>());

    internal static string? NextLink(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in string.Join(",", values).Split(','))
        {
            var m = Regex.Match(part, @"<([^>]+)>\s*;\s*rel=""next""");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    // ------------------------------------------------------------ git data API

    /// <summary>Commit sha at the tip of the branch.</summary>
    public async Task<string> GetBranchHeadAsync(string repo, string branch, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"repos/{RepoPath(repo)}/git/ref/heads/{BranchPath(branch)}", null, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new GitHubApiException(HttpStatusCode.NotFound, $"Branch '{branch}' not found in {repo}, or this account can't access the repo.");
        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new GitHubApiException(HttpStatusCode.Conflict, $"{repo} is empty — it has no commits yet.");
        if (!resp.IsSuccessStatusCode) throw await FailAsync(resp, repo, ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        return json["object"]?["sha"]?.GetValue<string>() ?? throw new GitHubApiException(null, "GitHub returned a ref without a sha.");
    }

    /// <summary>Tree sha of a commit.</summary>
    public async Task<string> GetCommitTreeAsync(string repo, string commitSha, CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"repos/{RepoPath(repo)}/git/commits/{commitSha}", repo, ct).ConfigureAwait(false);
        return json["tree"]?["sha"]?.GetValue<string>() ?? throw new GitHubApiException(null, "GitHub returned a commit without a tree.");
    }

    public async Task<GitTree> GetTreeAsync(string repo, string treeSha, bool recursive, CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"repos/{RepoPath(repo)}/git/trees/{treeSha}{(recursive ? "?recursive=1" : "")}", repo, ct).ConfigureAwait(false);
        var entries = new List<GitTreeEntry>();
        foreach (var e in json["tree"]?.AsArray() ?? [])
        {
            if (e is null) continue;
            entries.Add(new GitTreeEntry(
                e["path"]!.GetValue<string>(), e["mode"]?.GetValue<string>() ?? "", e["type"]?.GetValue<string>() ?? "",
                e["sha"]!.GetValue<string>(), e["size"]?.GetValue<long>()));
        }
        return new GitTree(json["sha"]?.GetValue<string>() ?? treeSha, entries, json["truncated"]?.GetValue<bool>() ?? false);
    }

    public async Task<byte[]> GetBlobAsync(string repo, string sha, CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"repos/{RepoPath(repo)}/git/blobs/{sha}", repo, ct).ConfigureAwait(false);
        var content = json["content"]?.GetValue<string>() ?? "";
        var encoding = json["encoding"]?.GetValue<string>() ?? "base64";
        return encoding == "base64"
            ? Convert.FromBase64String(content.Replace("\n", "").Replace("\r", ""))
            : Encoding.UTF8.GetBytes(content);
    }

    /// <summary>Uploads content; returns the blob sha.</summary>
    public async Task<string> CreateBlobAsync(string repo, byte[] content, CancellationToken ct = default)
    {
        var body = new JsonObject { ["content"] = Convert.ToBase64String(content), ["encoding"] = "base64" };
        var json = await SendJsonAsync(HttpMethod.Post, $"repos/{RepoPath(repo)}/git/blobs", body, repo, ct).ConfigureAwait(false);
        return json["sha"]!.GetValue<string>();
    }

    /// <summary>New tree = baseTree with the changes applied (paths from the repo root; null sha deletes). Returns its sha.</summary>
    public async Task<string> CreateTreeAsync(string repo, string baseTree, IEnumerable<GitTreeChange> changes, CancellationToken ct = default)
    {
        var tree = new JsonArray();
        foreach (var c in changes)
            tree.Add(new JsonObject { ["path"] = c.Path, ["mode"] = "100644", ["type"] = "blob", ["sha"] = c.Sha });
        var body = new JsonObject { ["base_tree"] = baseTree, ["tree"] = tree };
        var json = await SendJsonAsync(HttpMethod.Post, $"repos/{RepoPath(repo)}/git/trees", body, repo, ct).ConfigureAwait(false);
        return json["sha"]!.GetValue<string>();
    }

    /// <summary>Creates a commit object (does not move any branch). Returns its sha.</summary>
    public async Task<string> CreateCommitAsync(string repo, string message, string treeSha, string parentSha, CancellationToken ct = default)
    {
        var body = new JsonObject { ["message"] = message, ["tree"] = treeSha, ["parents"] = new JsonArray(parentSha) };
        var json = await SendJsonAsync(HttpMethod.Post, $"repos/{RepoPath(repo)}/git/commits", body, repo, ct).ConfigureAwait(false);
        return json["sha"]!.GetValue<string>();
    }

    /// <summary>Fast-forwards the branch to sha. False when GitHub rejects it as not a fast-forward (someone pushed meanwhile).</summary>
    public async Task<bool> UpdateRefAsync(string repo, string branch, string sha, CancellationToken ct = default)
    {
        var body = new JsonObject { ["sha"] = sha, ["force"] = false };
        using var resp = await SendAsync(HttpMethod.Patch, $"repos/{RepoPath(repo)}/git/refs/heads/{BranchPath(branch)}", body, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.UnprocessableEntity) return false;
        if (!resp.IsSuccessStatusCode) throw await FailAsync(resp, repo, ct).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------ generic REST (used by the game lab)

    /// <summary>
    /// Any REST call. <paramref name="url"/> is relative to https://api.github.com/ (or an absolute "next" link). With
    /// <paramref name="throwOnError"/> false a non-2xx answer is returned instead of thrown (404 probes, 422 "already exists").
    /// An empty body (204) gives a null <see cref="GitHubResponse.Json"/>.
    /// </summary>
    public async Task<GitHubResponse> RequestAsync(HttpMethod method, string url, JsonNode? body = null, string? repo = null,
        bool throwOnError = true, CancellationToken ct = default)
    {
        using var resp = await SendAsync(method, url, body, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && throwOnError) throw await FailAsync(resp, repo, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { json = JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) when (!resp.IsSuccessStatusCode) { }
            catch (System.Text.Json.JsonException ex) { throw new GitHubApiException(resp.StatusCode, "GitHub returned a response that isn't JSON.", ex); }
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
        return new GitHubResponse(resp.StatusCode, json, headers, NextLink(resp));
    }

    /// <summary>All pages of a list endpoint (follows Link rel=next, at most <paramref name="maxPages"/>). <paramref name="arrayProperty"/> names the array inside an object answer ("secrets", "variables", "workflow_runs").</summary>
    public async Task<List<JsonNode>> GetAllAsync(string url, string? repo = null, string? arrayProperty = null, int maxPages = 10, CancellationToken ct = default)
    {
        var all = new List<JsonNode>();
        string? next = url;
        for (var page = 0; next is not null && page < maxPages; page++)
        {
            var r = await RequestAsync(HttpMethod.Get, next, null, repo, true, ct).ConfigureAwait(false);
            var arr = arrayProperty is null ? r.Json as JsonArray : r.Json?[arrayProperty] as JsonArray;
            foreach (var n in arr ?? []) if (n is not null) all.Add(n);
            next = r.Next;
        }
        return all;
    }

    /// <summary>
    /// Scopes of an OAuth / classic-PAT token (X-OAuth-Scopes of GET /user), lower-case. Null when GitHub sends no such
    /// header (fine-grained tokens and GitHub App tokens: their permissions can't be read back).
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetTokenScopesAsync(CancellationToken ct = default)
    {
        var r = await RequestAsync(HttpMethod.Get, "user", null, null, true, ct).ConfigureAwait(false);
        return ParseScopes(r.Headers.GetValueOrDefault("X-OAuth-Scopes"));
    }

    internal static IReadOnlyList<string>? ParseScopes(string? header) =>
        header is null ? null : header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToList();

    // ------------------------------------------------------------ plumbing

    private static string RepoPath(string repo)
    {
        if (!IsValidRepoName(repo)) throw new GitHubApiException(null, $"'{repo}' is not a repo name — use owner/name.");
        return repo;
    }

    private static string BranchPath(string branch) => string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));

    private async Task<JsonNode> GetJsonAsync(string url, string? repo, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw await FailAsync(resp, repo, ct).ConfigureAwait(false);
        return await ReadJsonAsync(resp, ct).ConfigureAwait(false);
    }

    private async Task<JsonNode> SendJsonAsync(HttpMethod method, string url, JsonNode body, string? repo, CancellationToken ct)
    {
        using var resp = await SendAsync(method, url, body, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw await FailAsync(resp, repo, ct).ConfigureAwait(false);
        return await ReadJsonAsync(resp, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) _health?.Ok();
            return resp;
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException(null, $"Can't reach GitHub: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new GitHubApiException(null, "GitHub didn't respond in time.", ex);
        }
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(text) ?? throw new GitHubApiException(resp.StatusCode, "GitHub returned an empty response.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new GitHubApiException(resp.StatusCode, "GitHub returned a response that isn't JSON.", ex);
        }
    }

    /// <summary>Like <see cref="ErrorAsync"/>, and notes a 401 in <see cref="SignInHealth"/>.</summary>
    private async Task<GitHubApiException> FailAsync(HttpResponseMessage resp, string? repo, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            _health?.Reject(resp.Headers.TryGetValues("X-GitHub-Request-Id", out var id) ? id.FirstOrDefault() : null);
        return await ErrorAsync(resp, repo, ct).ConfigureAwait(false);
    }

    internal static async Task<GitHubApiException> ErrorAsync(HttpResponseMessage resp, string? repo, CancellationToken ct)
    {
        string? message = null;
        try
        {
            message = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["message"]?.GetValue<string>();
        }
        catch (Exception) { }

        var status = resp.StatusCode;
        var remaining = resp.Headers.TryGetValues("x-ratelimit-remaining", out var r) ? r.FirstOrDefault() : null;
        if ((status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) && (remaining == "0" || status == HttpStatusCode.TooManyRequests ||
                                                                                       message?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true))
        {
            var reset = resp.Headers.TryGetValues("x-ratelimit-reset", out var rs) && long.TryParse(rs.FirstOrDefault(), out var epoch)
                ? $" Try again after {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():t}."
                : resp.Headers.RetryAfter?.Delta is { } delta ? $" Try again in {Math.Ceiling(delta.TotalMinutes)} min." : "";
            return new GitHubApiException(status, "GitHub API rate limit reached." + reset);
        }

        var target = repo is null ? "" : $" ({repo})";
        return status switch
        {
            HttpStatusCode.Unauthorized => new GitHubApiException(status,
                "GitHub rejected the saved sign-in (token revoked, expired or invalid). Sign in to GitHub again."),
            HttpStatusCode.Forbidden => new GitHubApiException(status,
                $"GitHub refused access{target}: {message ?? "forbidden"}. The token may lack the 'repo' scope or org SSO authorization."),
            HttpStatusCode.NotFound => new GitHubApiException(status, repo is null
                ? $"GitHub: not found ({message})."
                : $"Repo {repo} not found, or this GitHub account has no access to it."),
            HttpStatusCode.Conflict => new GitHubApiException(status, $"GitHub reported a conflict{target}: {message}"),
            _ => new GitHubApiException(status, $"GitHub API error {(int)status}{target}: {message ?? resp.ReasonPhrase}"),
        };
    }
}
