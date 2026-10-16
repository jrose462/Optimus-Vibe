using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GameProfiles.Sync;

namespace GameProfiles.Tests;

/// <summary>
/// In-memory GitHub (one repo, git data API subset) behind an HttpMessageHandler. Trees are stored flat
/// (full path → blob sha), which is all the mirror needs.
/// </summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public string Repo { get; }
    public string Branch { get; }

    private readonly Dictionary<string, byte[]> _blobs = new();
    private readonly Dictionary<string, Dictionary<string, string>> _trees = new();
    private readonly Dictionary<string, (string Tree, string? Parent, string Message)> _commits = new();
    private readonly Dictionary<string, string> _refs = new();
    private int _commitCounter;

    public List<string> Requests { get; } = new();

    /// <summary>Called right before a PATCH ref is evaluated (simulate someone else pushing). Return true to keep the hook.</summary>
    public Func<bool>? BeforeRefUpdate { get; set; }

    public int RejectedRefUpdates { get; private set; }

    public FakeGitHub(string repo = "me/cfg", string branch = "main", Dictionary<string, string>? files = null)
    {
        Repo = repo;
        Branch = branch;
        var initial = files ?? new Dictionary<string, string>
        {
            ["config/devices.yaml"] = "devices: []\n",
            ["README.md"] = "hi\n",
        };
        var tree = PutTree(initial.ToDictionary(kv => kv.Key, kv => PutBlob(Encoding.UTF8.GetBytes(kv.Value))));
        _refs[branch] = PutCommit(tree, null, "init");
    }

    public GitHubApi Api() => new("test-token", this);

    public string Head => _refs[Branch];

    public string? CommitMessage(string sha) => _commits[sha].Message;

    public string? ParentOf(string sha) => _commits[sha].Parent;

    /// <summary>Files at the branch head.</summary>
    public Dictionary<string, string> Files()
    {
        var tree = _trees[_commits[Head].Tree];
        return tree.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(_blobs[kv.Value]));
    }

    /// <summary>Simulate a push from another machine: null value deletes.</summary>
    public string Push(Dictionary<string, string?> changes, string message = "remote change")
    {
        var tree = new Dictionary<string, string>(_trees[_commits[Head].Tree]);
        foreach (var (path, text) in changes)
            if (text is null) tree.Remove(path);
            else tree[path] = PutBlob(Encoding.UTF8.GetBytes(text));
        return _refs[Branch] = PutCommit(PutTree(tree), Head, message);
    }

    private string PutBlob(byte[] content)
    {
        var sha = GitHubMirror.GitBlobSha(content);
        _blobs[sha] = content;
        return sha;
    }

    private string PutTree(Dictionary<string, string> flat)
    {
        var text = string.Join("\n", flat.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + " " + kv.Value));
        var sha = Hex(SHA1.HashData(Encoding.UTF8.GetBytes("tree\n" + text)));
        _trees[sha] = new Dictionary<string, string>(flat);
        return sha;
    }

    private string PutCommit(string tree, string? parent, string message)
    {
        var sha = Hex(SHA1.HashData(Encoding.UTF8.GetBytes($"commit {++_commitCounter} {tree} {parent} {message}")));
        _commits[sha] = (tree, parent, message);
        return sha;
    }

    private bool IsAncestor(string ancestor, string sha)
    {
        for (string? c = sha; c is not null; c = _commits[c].Parent)
            if (c == ancestor) return true;
        return false;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.True(request.Headers.Contains("X-GitHub-Api-Version"));
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var query = request.RequestUri.Query;
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
        Requests.Add($"{request.Method} {path}{query}");
        lock (this) return Route(request.Method, path, query, body);
    }

    private HttpResponseMessage Route(HttpMethod method, string path, string query, JsonNode? body)
    {
        if (path == "user") return Json(new JsonObject { ["login"] = "tester" });
        var prefix = $"repos/{Repo}";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return Status(HttpStatusCode.NotFound, "Not Found");
        var rest = path[prefix.Length..];

        if (rest == "")
            return Json(new JsonObject { ["full_name"] = Repo, ["default_branch"] = Branch, ["private"] = true, ["description"] = null });

        if (method == HttpMethod.Get && rest.StartsWith("/git/ref/heads/"))
        {
            var branch = Uri.UnescapeDataString(rest["/git/ref/heads/".Length..]);
            return _refs.TryGetValue(branch, out var sha)
                ? Json(new JsonObject { ["ref"] = "refs/heads/" + branch, ["object"] = new JsonObject { ["sha"] = sha, ["type"] = "commit" } })
                : Status(HttpStatusCode.NotFound, "Not Found");
        }
        if (method == HttpMethod.Get && rest.StartsWith("/git/commits/"))
        {
            var sha = rest["/git/commits/".Length..];
            if (!_commits.TryGetValue(sha, out var c)) return Status(HttpStatusCode.NotFound, "Not Found");
            var parents = new JsonArray();
            if (c.Parent is not null) parents.Add(new JsonObject { ["sha"] = c.Parent });
            return Json(new JsonObject { ["sha"] = sha, ["tree"] = new JsonObject { ["sha"] = c.Tree }, ["parents"] = parents, ["message"] = c.Message });
        }
        if (method == HttpMethod.Get && rest.StartsWith("/git/trees/"))
        {
            var sha = rest["/git/trees/".Length..];
            Assert.Equal("?recursive=1", query);
            if (!_trees.TryGetValue(sha, out var flat)) return Status(HttpStatusCode.NotFound, "Not Found");
            var entries = new JsonArray();
            var dirs = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in flat.Keys)
                for (var i = p.IndexOf('/'); i > 0; i = p.IndexOf('/', i + 1)) dirs.Add(p[..i]);
            foreach (var d in dirs) entries.Add(new JsonObject { ["path"] = d, ["mode"] = "040000", ["type"] = "tree", ["sha"] = new string('d', 40) });
            foreach (var (p, b) in flat.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                entries.Add(new JsonObject { ["path"] = p, ["mode"] = "100644", ["type"] = "blob", ["sha"] = b, ["size"] = _blobs[b].Length });
            return Json(new JsonObject { ["sha"] = sha, ["tree"] = entries, ["truncated"] = false });
        }
        if (method == HttpMethod.Get && rest.StartsWith("/git/blobs/"))
        {
            var sha = rest["/git/blobs/".Length..];
            if (!_blobs.TryGetValue(sha, out var content)) return Status(HttpStatusCode.NotFound, "Not Found");
            // GitHub wraps base64 at 60 chars with \n.
            var b64 = Convert.ToBase64String(content);
            var wrapped = string.Join("\n", Enumerable.Range(0, (b64.Length + 59) / 60).Select(i => b64.Substring(i * 60, Math.Min(60, b64.Length - i * 60))));
            return Json(new JsonObject { ["sha"] = sha, ["content"] = wrapped + "\n", ["encoding"] = "base64", ["size"] = content.Length });
        }
        if (method == HttpMethod.Post && rest == "/git/blobs")
        {
            Assert.Equal("base64", body!["encoding"]!.GetValue<string>());
            return Json(new JsonObject { ["sha"] = PutBlob(Convert.FromBase64String(body["content"]!.GetValue<string>())) }, HttpStatusCode.Created);
        }
        if (method == HttpMethod.Post && rest == "/git/trees")
        {
            var baseTree = body!["base_tree"]!.GetValue<string>();
            if (!_trees.TryGetValue(baseTree, out var flat)) return Status(HttpStatusCode.UnprocessableEntity, "base_tree not found");
            var tree = new Dictionary<string, string>(flat);
            foreach (var e in body["tree"]!.AsArray())
            {
                var p = e!["path"]!.GetValue<string>();
                var sha = e["sha"]?.GetValue<string>();
                if (sha is null)
                {
                    if (!tree.Remove(p)) return Status(HttpStatusCode.UnprocessableEntity, "path not in base tree");
                }
                else
                {
                    if (!_blobs.ContainsKey(sha)) return Status(HttpStatusCode.UnprocessableEntity, "blob missing");
                    tree[p] = sha;
                }
            }
            return Json(new JsonObject { ["sha"] = PutTree(tree) }, HttpStatusCode.Created);
        }
        if (method == HttpMethod.Post && rest == "/git/commits")
        {
            var tree = body!["tree"]!.GetValue<string>();
            var parent = body["parents"]!.AsArray().Single()!.GetValue<string>();
            return Json(new JsonObject { ["sha"] = PutCommit(tree, parent, body["message"]!.GetValue<string>()) }, HttpStatusCode.Created);
        }
        if (method == HttpMethod.Patch && rest.StartsWith("/git/refs/heads/"))
        {
            var branch = Uri.UnescapeDataString(rest["/git/refs/heads/".Length..]);
            Assert.False(body!["force"]!.GetValue<bool>());
            if (BeforeRefUpdate is { } hook && !hook()) BeforeRefUpdate = null;
            var sha = body["sha"]!.GetValue<string>();
            if (!IsAncestor(_refs[branch], sha))
            {
                RejectedRefUpdates++;
                return Status(HttpStatusCode.UnprocessableEntity, "Update is not a fast forward");
            }
            _refs[branch] = sha;
            return Json(new JsonObject { ["ref"] = "refs/heads/" + branch, ["object"] = new JsonObject { ["sha"] = sha } });
        }
        return Status(HttpStatusCode.NotFound, "Not Found");
    }

    internal static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    internal static HttpResponseMessage Status(HttpStatusCode status, string message) =>
        Json(new JsonObject { ["message"] = message }, status);
}

/// <summary>Replays canned responses in order and records request bodies.</summary>
public sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _next;
    public List<string> Bodies { get; } = new();
    public List<Uri> Uris { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Uris.Add(request.RequestUri!);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        if (_next >= responses.Length) throw new InvalidOperationException("unexpected extra request " + request.RequestUri);
        return responses[_next++](request);
    }
}
