using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using OptimusVibe.GameLab;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

/// <summary>A fake user profile on disk (temp dir) with injected known folders and a fake registry.</summary>
public sealed class LabProfile : IDisposable
{
    public TempDir Tmp { get; } = new();
    public string Root { get; }
    public string Documents { get; }
    public string LocalAppData { get; }
    public string AppData { get; }
    public string LocalLow { get; }
    public string SavedGames { get; }
    public string Install { get; }
    public LabFakeRegistry Registry { get; } = new();

    public LabProfile(string user = "alice")
    {
        Root = Tmp.Dir("Users", user);
        Documents = Tmp.Dir("Users", user, "Documents");
        AppData = Tmp.Dir("Users", user, "AppData", "Roaming");
        LocalAppData = Tmp.Dir("Users", user, "AppData", "Local");
        LocalLow = Tmp.Dir("Users", user, "AppData", "LocalLow");
        SavedGames = Tmp.Dir("Users", user, "Saved Games");
        Install = Tmp.Dir("Games", "steamapps", "common", "Borderlands 4");
        User = user;
    }

    public string User { get; }

    public LabFolders Folders => new(new Dictionary<string, string>
    {
        ["Documents"] = Documents, ["LocalAppData"] = LocalAppData, ["AppData"] = AppData, ["LocalLow"] = LocalLow,
        ["SavedGames"] = SavedGames, ["UserProfile"] = Root,
    });

    public EvidenceOptions Options(Func<LabTarget, string?>? cloud = null, Func<LabTarget, string?>? config = null) => new()
    {
        Folders = Folders, Registry = Registry, CloudCheck = cloud, CurrentConfig = config,
        UserProfile = Root, UserName = User, MachineName = "TESTPC",
    };

    public string Write(string root, string rel, string text)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public string WriteBytes(string root, string rel, byte[] bytes)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public LabTarget Target(string name = "Borderlands 4", string store = "steam", string id = "1285190", string? publisher = "Gearbox") => new()
    {
        Name = name, Store = store, StoreId = id, InstallDir = Install, Exes = ["Borderlands4.exe"], Publisher = publisher, GameId = "borderlands-4",
    };

    public void Dispose() => Tmp.Dispose();
}

public sealed class LabFakeRegistry : IRegistryView
{
    private readonly Dictionary<string, List<(string, string)>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public LabFakeRegistry Add(string key, params (string Name, string Value)[] values)
    {
        _values[key] = values.ToList();
        for (var k = key; k.Contains('\\'); k = k[..k.LastIndexOf('\\')]) _keys.Add(k);
        _keys.Add(key);
        return this;
    }

    public List<string> Read { get; } = new();

    public IReadOnlyList<string> SubKeyNames(string key) =>
        _keys.Where(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', key.Length + 1) < 0)
            .Select(k => k[(key.Length + 1)..]).ToList();

    public IReadOnlyList<(string Name, string Value)>? Values(string key)
    {
        Read.Add(key);
        return _values.TryGetValue(key, out var v) ? v : null;
    }
}

/// <summary>
/// A fake GitHub for the game lab: issues, comments, labels, workflow dispatch, secrets, variables, repo/actions settings. Git data
/// API requests are forwarded to an inner <see cref="FakeGitHub"/>. Records every request.
/// </summary>
public sealed class FakeLabGitHub : HttpMessageHandler
{
    public const string Repo = "me/cfg";
    public FakeGitHub Git { get; } = new(Repo);
    private readonly HttpMessageInvoker _git;
    private readonly object _lock = new();

    public (byte[] PublicKey, byte[] PrivateKey) KeyPair { get; } = SealedBox.GenerateKeyPair();

    public string? Scopes { get; set; } = "repo, workflow, project";
    public List<string> Requests { get; } = new();
    public List<string> Bodies { get; } = new();

    public sealed class Issue
    {
        public int Number;
        public string Title = "";
        public string Body = "";
        public bool Open = true;
        public List<string> Labels = new();
        public List<string> Comments = new();
    }

    public List<Issue> Issues { get; } = new();
    public HashSet<string> LabelNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Variables { get; } = new();
    public Dictionary<string, (string Encrypted, string KeyId)> Secrets { get; } = new();
    public List<int> Dispatched { get; } = new();
    public int ActiveRuns { get; set; }

    public bool ActionsEnabled { get; set; }
    public string WorkflowPermissions { get; set; } = "write";
    public bool CanApprove { get; set; }
    public bool Squash { get; set; }
    public bool DeleteBranch { get; set; }
    public bool AutoMerge { get; set; }
    public bool AutoMergeUnavailable { get; set; }
    public bool RejectWorkflowFiles { get; set; }

    public FakeLabGitHub() => _git = new HttpMessageInvoker(Git, disposeHandler: false);

    public GitHubApi Api() => new("test-token", this);

    public Issue Add(string title, string body, params string[] labels)
    {
        var i = new Issue { Number = Issues.Count + 1, Title = title, Body = body, Labels = labels.ToList() };
        Issues.Add(i);
        return i;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        var raw = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var body = string.IsNullOrEmpty(raw) ? null : JsonNode.Parse(raw);
        lock (_lock)
        {
            Requests.Add($"{request.Method} {path}{request.RequestUri.Query}");
            if (raw is not null) Bodies.Add(raw);
        }

        if (path == "user")
        {
            var resp = FakeGitHub.Json(new JsonObject { ["login"] = "tester" });
            if (Scopes is not null) resp.Headers.Add("X-OAuth-Scopes", Scopes);
            return resp;
        }

        var prefix = $"repos/{Repo}";
        if (path == "graphql") return FakeGitHub.Status(HttpStatusCode.Forbidden, "Resource not accessible");
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return FakeGitHub.Status(HttpStatusCode.NotFound, "Not Found");
        var rest = path[prefix.Length..];

        lock (_lock)
        {
            var routed = Route(request.Method, rest, query, body);
            if (routed is not null) return routed;
        }

        if (RejectWorkflowFiles && request.Method == HttpMethod.Post && rest == "/git/trees" &&
            raw!.Contains(".github/workflows/", StringComparison.Ordinal))
            return FakeGitHub.Status(HttpStatusCode.NotFound, "Not Found");

        // Git data API
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (raw is not null) clone.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        clone.Headers.Authorization = request.Headers.Authorization;
        foreach (var h in request.Headers) if (!clone.Headers.Contains(h.Key)) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return await _git.SendAsync(clone, ct);
    }

    private HttpResponseMessage? Route(HttpMethod method, string rest, System.Collections.Specialized.NameValueCollection query, JsonNode? body)
    {
        if (rest == "" && method == HttpMethod.Get)
            return FakeGitHub.Json(new JsonObject
            {
                ["full_name"] = Repo, ["default_branch"] = "main", ["private"] = true, ["node_id"] = "R_1",
                ["allow_squash_merge"] = Squash, ["delete_branch_on_merge"] = DeleteBranch, ["allow_auto_merge"] = AutoMerge,
            });
        if (rest == "" && method == HttpMethod.Patch)
        {
            if (body!["allow_squash_merge"] is { } sq) Squash = sq.GetValue<bool>();
            if (body["delete_branch_on_merge"] is { } db) DeleteBranch = db.GetValue<bool>();
            if (body["allow_auto_merge"] is { } am)
            {
                // Free-plan private repos: GitHub refuses auto-merge.
                if (AutoMergeUnavailable)
                    return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("{\"message\":\"Validation Failed\"}") };
                AutoMerge = am.GetValue<bool>();
            }
            return FakeGitHub.Json(new JsonObject());
        }
        if (rest == "/actions/permissions")
        {
            if (method == HttpMethod.Put) { ActionsEnabled = body!["enabled"]!.GetValue<bool>(); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            return FakeGitHub.Json(new JsonObject { ["enabled"] = ActionsEnabled, ["allowed_actions"] = "all" });
        }
        if (rest == "/actions/permissions/workflow")
        {
            if (method == HttpMethod.Put)
            {
                WorkflowPermissions = body!["default_workflow_permissions"]!.GetValue<string>();
                CanApprove = body["can_approve_pull_request_reviews"]!.GetValue<bool>();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return FakeGitHub.Json(new JsonObject { ["default_workflow_permissions"] = WorkflowPermissions, ["can_approve_pull_request_reviews"] = CanApprove });
        }

        if (rest.StartsWith("/labels"))
        {
            if (method == HttpMethod.Get)
                return FakeGitHub.Json(new JsonArray(LabelNames.Select(n => (JsonNode?)new JsonObject { ["name"] = n }).ToArray()));
            var name = body!["name"]!.GetValue<string>();
            if (!LabelNames.Add(name)) return FakeGitHub.Status(HttpStatusCode.UnprocessableEntity, "already_exists");
            return FakeGitHub.Json(new JsonObject { ["name"] = name }, HttpStatusCode.Created);
        }

        if (rest == "/actions/secrets/public-key")
            return FakeGitHub.Json(new JsonObject { ["key_id"] = "k1", ["key"] = Convert.ToBase64String(KeyPair.PublicKey) });
        if (rest.StartsWith("/actions/secrets/"))
        {
            var name = rest["/actions/secrets/".Length..];
            if (method == HttpMethod.Put)
            {
                var existed = Secrets.ContainsKey(name);
                Secrets[name] = (body!["encrypted_value"]!.GetValue<string>(), body["key_id"]!.GetValue<string>());
                return new HttpResponseMessage(existed ? HttpStatusCode.NoContent : HttpStatusCode.Created);
            }
            return Secrets.ContainsKey(name) ? FakeGitHub.Json(new JsonObject { ["name"] = name }) : FakeGitHub.Status(HttpStatusCode.NotFound, "Not Found");
        }
        if (rest.StartsWith("/actions/variables"))
        {
            if (method == HttpMethod.Post) { Variables[body!["name"]!.GetValue<string>()] = body["value"]!.GetValue<string>(); return FakeGitHub.Json(new JsonObject(), HttpStatusCode.Created); }
            var name = rest["/actions/variables/".Length..];
            if (method == HttpMethod.Patch) { Variables[name] = body!["value"]!.GetValue<string>(); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            return Variables.TryGetValue(name, out var v) ? FakeGitHub.Json(new JsonObject { ["name"] = name, ["value"] = v }) : FakeGitHub.Status(HttpStatusCode.NotFound, "Not Found");
        }

        if (rest.StartsWith("/actions/workflows/game-lab.yml/runs"))
        {
            var status = query["status"];
            return FakeGitHub.Json(new JsonObject { ["total_count"] = status == "in_progress" ? ActiveRuns : 0, ["workflow_runs"] = new JsonArray() });
        }
        if (rest == "/actions/workflows/game-lab.yml/dispatches")
        {
            Dispatched.Add(int.Parse(body!["inputs"]!["issue"]!.GetValue<string>()));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (rest.StartsWith("/issues"))
        {
            var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries); // issues [n] [comments|labels] [name]
            if (parts.Length == 1)
            {
                if (method == HttpMethod.Post)
                {
                    var i = Add(body!["title"]!.GetValue<string>(), body["body"]!.GetValue<string>(),
                        (body["labels"] as JsonArray ?? []).Select(l => l!.GetValue<string>()).ToArray());
                    foreach (var l in i.Labels) LabelNames.Add(l);
                    return FakeGitHub.Json(IssueJson(i), HttpStatusCode.Created);
                }
                var wantLabel = query["labels"];
                var state = query["state"] ?? "open";
                var list = Issues.Where(i => (state == "all" || (state == "open") == i.Open) && (wantLabel is null || i.Labels.Contains(wantLabel)));
                return FakeGitHub.Json(new JsonArray(list.Select(i => (JsonNode?)IssueJson(i)).ToArray()));
            }
            var issue = Issues.FirstOrDefault(i => i.Number == int.Parse(parts[1]));
            if (issue is null) return FakeGitHub.Status(HttpStatusCode.NotFound, "Not Found");
            if (parts.Length == 2) return FakeGitHub.Json(IssueJson(issue));
            if (parts[2] == "comments")
            {
                if (method == HttpMethod.Post)
                {
                    issue.Comments.Add(body!["body"]!.GetValue<string>());
                    return FakeGitHub.Json(new JsonObject { ["id"] = issue.Comments.Count }, HttpStatusCode.Created);
                }
                return FakeGitHub.Json(new JsonArray(issue.Comments.Select((c, n) => (JsonNode?)new JsonObject { ["id"] = n + 1, ["body"] = c }).ToArray()));
            }
            if (parts[2] == "labels")
            {
                if (method == HttpMethod.Post)
                {
                    foreach (var l in body!["labels"]!.AsArray()) if (!issue.Labels.Contains(l!.GetValue<string>())) issue.Labels.Add(l.GetValue<string>());
                    return FakeGitHub.Json(new JsonArray());
                }
                if (method == HttpMethod.Delete)
                {
                    var removed = issue.Labels.Remove(Uri.UnescapeDataString(parts[3]));
                    return removed ? FakeGitHub.Json(new JsonArray()) : FakeGitHub.Status(HttpStatusCode.NotFound, "Label does not exist");
                }
            }
        }
        return null;
    }

    private static JsonObject IssueJson(Issue i) => new()
    {
        ["number"] = i.Number, ["title"] = i.Title, ["body"] = i.Body, ["state"] = i.Open ? "open" : "closed",
        ["html_url"] = $"https://github.com/{Repo}/issues/{i.Number}", ["node_id"] = $"I_{i.Number}",
        ["labels"] = new JsonArray(i.Labels.Select(l => (JsonNode?)new JsonObject { ["name"] = l }).ToArray()),
        ["created_at"] = "2026-10-01T00:00:00Z", ["updated_at"] = "2026-10-01T00:00:00Z",
    };
}
