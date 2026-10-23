using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptimusVibe.Logging;
using OptimusVibe.Sync;

namespace OptimusVibe.GameLab;

/// <summary>REST error conversion for already-read responses (<see cref="GitHubResponse"/>).</summary>
internal static class LabHttp
{
    private static GitHubApiException Rejected(GitHubResponse r)
    {
        SignInHealth.RecordRejected(r.Headers.GetValueOrDefault("X-GitHub-Request-Id"));
        return new GitHubApiException(r.Status, "GitHub rejected the saved sign-in (token revoked, expired or invalid). Sign in to GitHub again.");
    }

    public static GitHubApiException Error(GitHubResponse r, string? repo)
    {
        var message = r.Json?["message"]?.GetValue<string>();
        var target = repo is null ? "" : $" ({repo})";
        return r.Status switch
        {
            HttpStatusCode.Unauthorized => Rejected(r),
            HttpStatusCode.Forbidden => new GitHubApiException(r.Status, $"GitHub refused access{target}: {message ?? "forbidden"}. The token may lack a scope or permission (see `lab setup`)."),
            HttpStatusCode.NotFound => new GitHubApiException(r.Status, $"GitHub: not found{target}{(message is null ? "" : $" ({message})")}."),
            _ => new GitHubApiException(r.Status, $"GitHub API error {(int)r.Status}{target}: {message ?? r.Status.ToString()}"),
        };
    }
}

/// <summary>The token can't use Projects (no 'project' scope): the board is skipped, not an error.</summary>
public sealed class LabBoardScopeException(string message) : Exception(message);

/// <summary>A located (or created) game-lab Project.</summary>
public sealed record BoardInfo(string ProjectId, int Number, string Url, bool Linked, string? FieldId, string? FieldName,
    IReadOnlyDictionary<string, string> Options)
{
    public bool StatusReady => FieldId is not null && LabBoard.StatusNames.All(n => Options.ContainsKey(n));
}

/// <summary>
/// The private user Project "Optimus-Vibe game lab" that mirrors the requests (GraphQL; needs the 'project' scope). Created once,
/// linked to the config repo, with a single-select status field offering Requested / Researching / Needs evidence / Review / Done.
/// </summary>
public sealed class LabBoard(GitHubApi api, string repo)
{
    public static readonly string[] StatusNames = ["Requested", "Researching", "Needs evidence", "Review", "Done"];
    private static readonly string[] StatusColors = ["GRAY", "YELLOW", "ORANGE", "BLUE", "GREEN"];
    private const string FallbackFieldName = "Lab status";

    /// <summary>Board column for a request. Failed requests sit in Review: they need a human look. Closed requests are Done.</summary>
    public static string StatusFor(LabStatus status, bool isOpen = true) => status switch
    {
        LabStatus.Queued => "Requested",
        LabStatus.Researching => "Researching",
        LabStatus.NeedsEvidence => "Needs evidence",
        LabStatus.Review => "Review",
        LabStatus.Done => "Done",
        LabStatus.Failed => "Review",
        _ => isOpen ? "Requested" : "Done",
    };

    internal async Task<JsonNode> GraphQlAsync(string query, JsonObject? variables, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query };
        if (variables is not null) body["variables"] = variables;
        var r = await api.RequestAsync(HttpMethod.Post, "graphql", body, null, false, ct).ConfigureAwait(false);
        if (r.Status == HttpStatusCode.Forbidden) throw new LabBoardScopeException("GitHub says this sign-in can't use Projects (classic token with the 'project' scope needed).");
        if (!r.Ok) throw LabHttp.Error(r, null);
        if (r.Json?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var text = string.Join("; ", errors.Select(e => e?["message"]?.GetValue<string>() ?? ""));
            var types = string.Join(",", errors.Select(e => e?["type"]?.GetValue<string>() ?? ""));
            if (types.Contains("INSUFFICIENT_SCOPES", StringComparison.OrdinalIgnoreCase) || text.Contains("scope", StringComparison.OrdinalIgnoreCase)
                || types.Contains("FORBIDDEN", StringComparison.OrdinalIgnoreCase))
                throw new LabBoardScopeException("GitHub says this sign-in can't use Projects (it needs the 'project' scope).");
            throw new GitHubApiException(r.Status, "GitHub (Projects): " + text);
        }
        return r.Json?["data"] ?? throw new GitHubApiException(r.Status, "GitHub (Projects) returned no data.");
    }

    /// <summary>The existing board, or null.</summary>
    public async Task<BoardInfo?> FindAsync(CancellationToken ct = default)
    {
        var data = await GraphQlAsync("""
            query { viewer { id projectsV2(first: 50) { nodes { id number title url closed } } } }
            """, null, ct).ConfigureAwait(false);
        var project = (data["viewer"]?["projectsV2"]?["nodes"] as JsonArray)?
            .FirstOrDefault(n => n is not null && n["closed"]?.GetValue<bool>() != true && n["title"]?.GetValue<string>() == GameLabSetup.BoardTitle);
        if (project is null) return null;
        return await DescribeAsync(project["id"]!.GetValue<string>(), project["number"]?.GetValue<int>() ?? 0, project["url"]?.GetValue<string>() ?? "", ct).ConfigureAwait(false);
    }

    private async Task<BoardInfo> DescribeAsync(string projectId, int number, string url, CancellationToken ct)
    {
        var data = await GraphQlAsync("""
            query($id: ID!) { node(id: $id) { ... on ProjectV2 {
              repositories(first: 50) { nodes { nameWithOwner } }
              fields(first: 40) { nodes { ... on ProjectV2SingleSelectField { id name options { id name } } } } } } }
            """, new JsonObject { ["id"] = projectId }, ct).ConfigureAwait(false);
        var node = data["node"];
        var linked = (node?["repositories"]?["nodes"] as JsonArray)?.Any(r => string.Equals(r?["nameWithOwner"]?.GetValue<string>(), repo, StringComparison.OrdinalIgnoreCase)) == true;

        // Prefer a field whose options already cover our statuses ("Status" after we edit it, or our own "Lab status").
        string? fieldId = null, fieldName = null;
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in node?["fields"]?["nodes"] as JsonArray ?? [])
        {
            if (f?["id"] is null) continue; // non single-select fields come back empty
            var name = f["name"]?.GetValue<string>() ?? "";
            var opts = (f["options"] as JsonArray ?? []).Where(o => o is not null)
                .ToDictionary(o => o!["name"]!.GetValue<string>(), o => o!["id"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
            var covers = StatusNames.All(opts.ContainsKey);
            if (covers || (fieldId is null && name.Equals("Status", StringComparison.OrdinalIgnoreCase)))
            {
                fieldId = f["id"]!.GetValue<string>();
                fieldName = name;
                options = opts;
                if (covers) break;
            }
        }
        return new BoardInfo(projectId, number, url, linked, fieldId, fieldName, options);
    }

    /// <summary>Creates what is missing (project, repo link, status options) and returns the ready board.</summary>
    public async Task<BoardInfo> EnsureAsync(CancellationToken ct = default)
    {
        var info = await FindAsync(ct).ConfigureAwait(false);
        if (info is null)
        {
            var viewer = await GraphQlAsync("query { viewer { id } }", null, ct).ConfigureAwait(false);
            var ownerId = viewer["viewer"]?["id"]?.GetValue<string>() ?? throw new GitHubApiException(null, "GitHub returned no user id.");
            var created = await GraphQlAsync("""
                mutation($owner: ID!, $title: String!) { createProjectV2(input: { ownerId: $owner, title: $title }) { projectV2 { id number url } } }
                """, new JsonObject { ["owner"] = ownerId, ["title"] = GameLabSetup.BoardTitle }, ct).ConfigureAwait(false);
            var p = created["createProjectV2"]?["projectV2"] ?? throw new GitHubApiException(null, "GitHub did not create the project.");
            Log.Info($"game lab: created project {p["url"]}");
            info = await DescribeAsync(p["id"]!.GetValue<string>(), p["number"]?.GetValue<int>() ?? 0, p["url"]?.GetValue<string>() ?? "", ct).ConfigureAwait(false);
        }

        if (!info.Linked)
        {
            var repoJson = await api.RequestAsync(HttpMethod.Get, $"repos/{repo}", null, repo, true, ct).ConfigureAwait(false);
            var repoId = repoJson.Json?["node_id"]?.GetValue<string>() ?? throw new GitHubApiException(null, "GitHub returned no repository id.");
            await GraphQlAsync("""
                mutation($p: ID!, $r: ID!) { linkProjectV2ToRepository(input: { projectId: $p, repositoryId: $r }) { repository { id } } }
                """, new JsonObject { ["p"] = info.ProjectId, ["r"] = repoId }, ct).ConfigureAwait(false);
            info = info with { Linked = true };
        }

        if (!info.StatusReady)
        {
            var optionInput = () => new JsonArray(StatusNames.Select((n, i) => (JsonNode?)new JsonObject
                { ["name"] = n, ["color"] = StatusColors[i], ["description"] = "" }).ToArray());
            var done = false;
            if (info.FieldId is not null && string.Equals(info.FieldName, "Status", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await GraphQlAsync("""
                        mutation($f: ID!, $o: [ProjectV2SingleSelectFieldOptionInput!]) { updateProjectV2Field(input: { fieldId: $f, singleSelectOptions: $o }) { projectV2Field { ... on ProjectV2SingleSelectField { id } } } }
                        """, new JsonObject { ["f"] = info.FieldId, ["o"] = optionInput() }, ct).ConfigureAwait(false);
                    done = true;
                }
                catch (GitHubApiException ex)
                {
                    Log.Info($"game lab: could not edit the Status field ({ex.Message}); adding a '{FallbackFieldName}' field instead");
                }
            }
            if (!done)
            {
                await GraphQlAsync("""
                    mutation($p: ID!, $n: String!, $o: [ProjectV2SingleSelectFieldOptionInput!]) { createProjectV2Field(input: { projectId: $p, dataType: SINGLE_SELECT, name: $n, singleSelectOptions: $o }) { projectV2Field { ... on ProjectV2SingleSelectField { id } } } }
                    """, new JsonObject { ["p"] = info.ProjectId, ["n"] = FallbackFieldName, ["o"] = optionInput() }, ct).ConfigureAwait(false);
            }
            info = await DescribeAsync(info.ProjectId, info.Number, info.Url, ct).ConfigureAwait(false);
            if (!info.StatusReady) throw new GitHubApiException(null, "The project's status field could not be set up.");
        }
        return info;
    }
}

/// <summary>What <see cref="BoardSync.SyncAsync"/> did.</summary>
public sealed record BoardSyncResult(int Updated, int Unchanged, bool Skipped, string Message);

/// <summary>
/// Moves each game-lab issue's card to the column its lab:* labels say (the workflow has no project token, so the app does this
/// whenever it syncs). A small local cache (issue → last column) avoids repeating API calls.
/// </summary>
public sealed class BoardSync(GitHubApi api, string repo, string? dataDir = null)
{
    private string? CacheFile => dataDir is null ? null : Path.Combine(dataDir, "gamelab-board.json");

    public async Task<BoardSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var board = new LabBoard(api, repo);
        BoardInfo? info;
        try
        {
            info = await board.FindAsync(ct).ConfigureAwait(false);
        }
        catch (LabBoardScopeException ex)
        {
            return new BoardSyncResult(0, 0, true, "Board skipped: " + ex.Message);
        }
        if (info is null || !info.StatusReady) return new BoardSyncResult(0, 0, true, "Board skipped: run `lab setup --step board` first.");

        var client = new GameLabClient(api, repo);
        var issues = await client.ListAsync(includeClosed: true, ct).ConfigureAwait(false);
        var cache = LoadCache();
        var updated = 0;
        var unchanged = 0;
        foreach (var issue in issues)
        {
            var wanted = LabBoard.StatusFor(issue.Status, issue.IsOpen);
            if (cache.TryGetValue(issue.Number.ToString(), out var last) && last == $"{info.ProjectId}|{wanted}") { unchanged++; continue; }
            if (issue.NodeId.Length == 0) continue;
            var added = await board.GraphQlAsync("""
                mutation($p: ID!, $c: ID!) { addProjectV2ItemById(input: { projectId: $p, contentId: $c }) { item { id } } }
                """, new JsonObject { ["p"] = info.ProjectId, ["c"] = issue.NodeId }, ct).ConfigureAwait(false);
            var itemId = added["addProjectV2ItemById"]?["item"]?["id"]?.GetValue<string>();
            if (itemId is null || !info.Options.TryGetValue(wanted, out var optionId)) continue;
            await board.GraphQlAsync("""
                mutation($p: ID!, $i: ID!, $f: ID!, $o: String!) { updateProjectV2ItemFieldValue(input: { projectId: $p, itemId: $i, fieldId: $f, value: { singleSelectOptionId: $o } }) { projectV2Item { id } } }
                """, new JsonObject { ["p"] = info.ProjectId, ["i"] = itemId, ["f"] = info.FieldId, ["o"] = optionId }, ct).ConfigureAwait(false);
            cache[issue.Number.ToString()] = $"{info.ProjectId}|{wanted}";
            updated++;
        }
        SaveCache(cache);
        return new BoardSyncResult(updated, unchanged, false, $"Board: {updated} card(s) updated, {unchanged} already right.");
    }

    private Dictionary<string, string> LoadCache()
    {
        try
        {
            if (CacheFile is { } f && File.Exists(f))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    private void SaveCache(Dictionary<string, string> cache)
    {
        try
        {
            if (CacheFile is not { } f) return;
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info($"game lab: board cache not saved: {ex.Message}"); }
    }
}
