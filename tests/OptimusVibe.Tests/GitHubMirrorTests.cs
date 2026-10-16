using System.Text;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

public sealed class GitHubMirrorTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeGitHub _gh = new(files: new Dictionary<string, string>
    {
        ["config/devices.yaml"] = "devices: []\n",
        ["config/games/a.yaml"] = "id: a\n",
        ["config/saves/a/ally-x/s1/save.yaml"] = "name: S1\n",
        ["README.md"] = "not mirrored\n",
    });

    public void Dispose() => _tmp.Dispose();

    private string Root => _tmp.File("mirror");

    private GitHubMirror Mirror() => new(Root, _gh.Repo, _gh.Branch, _gh.Api(), "tester");

    private GitHubMirror Cloned()
    {
        var r = GitHubMirror.Clone(Root, _gh.Repo, _gh.Branch, _gh.Api());
        Assert.True(r.Ok, r.Message);
        return Mirror();
    }

    private string Local(string repoPath) => Path.Combine(Root, repoPath.Replace('/', '\\'));
    private string Read(string repoPath) => File.ReadAllText(Local(repoPath));
    private void WriteLocal(string repoPath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Local(repoPath))!);
        File.WriteAllText(Local(repoPath), text);
    }

    [Fact]
    public void GitBlobSha_MatchesGitHashObject()
    {
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", GitHubMirror.GitBlobSha(Encoding.ASCII.GetBytes("hello\n")));
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", GitHubMirror.GitBlobSha([]));
    }

    [Fact]
    public void Clone_DownloadsConfigSubtreeOnly()
    {
        var m = Cloned();
        Assert.True(m.IsCloned);
        Assert.True(m.IsReady(out var why), why);
        Assert.Equal("devices: []\n", Read("config/devices.yaml"));
        Assert.Equal("name: S1\n", Read("config/saves/a/ally-x/s1/save.yaml"));
        Assert.False(File.Exists(Local("README.md")));
        Assert.True(File.Exists(Path.Combine(Root, GitHubMirror.StateFileName)));
        Assert.Equal(_gh.Head, m.BaseCommit);
    }

    [Fact]
    public void Clone_RejectsRepoWithoutDevicesYaml()
    {
        var other = new FakeGitHub(files: new() { ["README.md"] = "x" });
        var r = GitHubMirror.Clone(Root, other.Repo, other.Branch, other.Api());
        Assert.False(r.Ok);
        Assert.Contains("doesn't look like an Optimus-Vibe config repo", r.Message);
    }

    [Fact]
    public void Clone_MissingRepo_ClearMessage()
    {
        var r = GitHubMirror.Clone(Root, "someone/else", "main", _gh.Api());
        Assert.False(r.Ok);
        Assert.Contains("someone/else", r.Message);
    }

    [Fact]
    public void Clone_Existing_PullsInsteadOfWiping()
    {
        Cloned();
        WriteLocal("config/games/new.yaml", "id: new\n");
        var r = GitHubMirror.Clone(Root, _gh.Repo, _gh.Branch, _gh.Api());
        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Local("config/games/new.yaml")));
    }

    [Fact]
    public void NotSignedIn_IsNotReady()
    {
        Cloned();
        var m = new GitHubMirror(Root, _gh.Repo, _gh.Branch, null);
        Assert.False(m.IsReady(out var why));
        Assert.Contains("Not signed in", why);
        Assert.False(m.Pull().Ok);
    }

    [Fact]
    public void Pull_RemoteOnlyChange_IsApplied()
    {
        var m = Cloned();
        _gh.Push(new() { ["config/games/a.yaml"] = "id: a\nv: 2\n", ["config/games/b.yaml"] = "id: b\n" });
        var r = m.Pull();
        Assert.True(r.Ok, r.Message);
        Assert.Equal("id: a\nv: 2\n", Read("config/games/a.yaml"));
        Assert.Equal("id: b\n", Read("config/games/b.yaml"));
        Assert.Equal(_gh.Head, m.BaseCommit);
        Assert.Equal("already up to date", m.Pull().Message);
    }

    [Fact]
    public void Pull_LocalOnlyChange_IsPreserved()
    {
        var m = Cloned();
        WriteLocal("config/games/a.yaml", "id: a\nlocal: 1\n");
        _gh.Push(new() { ["config/games/b.yaml"] = "id: b\n" });
        Assert.True(m.Pull().Ok);
        Assert.Equal("id: a\nlocal: 1\n", Read("config/games/a.yaml"));
        Assert.Equal("id: b\n", Read("config/games/b.yaml"));
    }

    [Fact]
    public void Pull_BothChanged_SavesKeepLocal()
    {
        var m = Cloned();
        const string save = "config/saves/a/ally-x/s1/save.yaml";
        WriteLocal(save, "name: mine\n");
        _gh.Push(new() { [save] = "name: theirs\n" });
        Assert.True(m.Pull().Ok);
        Assert.Equal("name: mine\n", Read(save));
        Assert.False(File.Exists(Local(save) + GitHubMirror.ConflictSuffix));

        // Local version is now a change against the new base, so a sync pushes it.
        Assert.True(m.CommitAndSync("sync", Path.Combine(Root, "config", "saves")).Ok);
        Assert.Equal("name: mine\n", _gh.Files()[save]);
    }

    [Fact]
    public void Pull_BothChanged_OtherFilesTakeRemoteAndKeepLocalConflictCopy()
    {
        var m = Cloned();
        WriteLocal("config/games/a.yaml", "id: a\nmine\n");
        _gh.Push(new() { ["config/games/a.yaml"] = "id: a\ntheirs\n" });
        var r = m.Pull();
        Assert.True(r.Ok, r.Message);
        Assert.Contains("1 conflict", r.Message);
        Assert.Equal("id: a\ntheirs\n", Read("config/games/a.yaml"));
        Assert.Equal("id: a\nmine\n", Read("config/games/a.yaml" + GitHubMirror.ConflictSuffix));

        // The conflict copy is never pushed.
        Assert.Equal("nothing to commit; already up to date", m.CommitAndSync("x").Message);
    }

    [Fact]
    public void Pull_RemoteDeletion_DeletesUnchangedLocalFile()
    {
        var m = Cloned();
        _gh.Push(new() { ["config/saves/a/ally-x/s1/save.yaml"] = null, ["config/games/a.yaml"] = null });
        Assert.True(m.Pull().Ok);
        Assert.False(File.Exists(Local("config/games/a.yaml")));
        Assert.False(Directory.Exists(Local("config/saves/a"))); // emptied folders are removed
    }

    [Fact]
    public void Pull_RemoteDeletion_KeepsLocallyModifiedSave()
    {
        var m = Cloned();
        const string save = "config/saves/a/ally-x/s1/save.yaml";
        WriteLocal(save, "name: edited\n");
        _gh.Push(new() { [save] = null });
        Assert.True(m.Pull().Ok);
        Assert.Equal("name: edited\n", Read(save));
    }

    [Fact]
    public void Push_CreatesCommitAndMovesRef()
    {
        var m = Cloned();
        var before = _gh.Head;
        WriteLocal("config/saves/a/ally-x/s2/save.yaml", "name: S2\n");
        File.Delete(Local("config/saves/a/ally-x/s1/save.yaml"));
        WriteLocal("config/games/a.yaml", "id: a\nnot in scope\n");

        var r = m.CommitAndSync("chore(saves): test", Path.Combine(Root, "config", "saves"));
        Assert.True(r.Ok, r.Message);
        Assert.Contains("pushed 2 change(s)", r.Message);
        Assert.NotEqual(before, _gh.Head);
        Assert.Equal(before, _gh.ParentOf(_gh.Head));
        Assert.Equal("chore(saves): test", _gh.CommitMessage(_gh.Head));
        var files = _gh.Files();
        Assert.Equal("name: S2\n", files["config/saves/a/ally-x/s2/save.yaml"]);
        Assert.False(files.ContainsKey("config/saves/a/ally-x/s1/save.yaml"));
        Assert.Equal("id: a\n", files["config/games/a.yaml"]); // outside the given paths
        Assert.Equal("not mirrored\n", files["README.md"]);   // base_tree keeps the rest of the repo
        Assert.Equal(_gh.Head, m.BaseCommit);

        // Nothing left to push in scope; a second sync is a no-op.
        Assert.StartsWith("nothing to commit", m.CommitAndSync("again", Path.Combine(Root, "config", "saves")).Message);
    }

    [Fact]
    public void Push_NonFastForward_PullsAndRetries()
    {
        var m = Cloned();
        WriteLocal("config/saves/a/ally-x/s2/save.yaml", "name: S2\n");
        var raced = 0;
        _gh.BeforeRefUpdate = () =>
        {
            _gh.Push(new() { ["config/games/c.yaml"] = $"id: c{raced}\n" }, "other machine");
            return ++raced < 2; // race twice, then let it through
        };

        var r = m.CommitAndSync("chore(saves): race");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, _gh.RejectedRefUpdates);
        var files = _gh.Files();
        Assert.Equal("name: S2\n", files["config/saves/a/ally-x/s2/save.yaml"]);
        Assert.Equal("id: c1\n", files["config/games/c.yaml"]);
        Assert.Equal("id: c1\n", Read("config/games/c.yaml")); // the other machine's change was pulled
        Assert.Equal("other machine", _gh.CommitMessage(_gh.ParentOf(_gh.Head)!));
    }

    [Fact]
    public void Push_GivesUpAfterRetries()
    {
        var m = Cloned();
        WriteLocal("config/saves/x.yaml", "x\n");
        var n = 0;
        _gh.BeforeRefUpdate = () => { _gh.Push(new() { ["config/games/c.yaml"] = $"{n++}\n" }); return true; };
        var r = m.CommitAndSync("x");
        Assert.False(r.Ok);
        Assert.Equal(4, _gh.RejectedRefUpdates);
    }

    [Fact]
    public void Push_FromSecondMirror_RoundTrips()
    {
        var a = Cloned();
        var otherRoot = _tmp.File("mirror2");
        Assert.True(GitHubMirror.Clone(otherRoot, _gh.Repo, _gh.Branch, _gh.Api()).Ok);
        var b = new GitHubMirror(otherRoot, _gh.Repo, _gh.Branch, _gh.Api());

        WriteLocal("config/saves/a/desktop/q/save.yaml", "name: Q\n");
        Assert.True(a.CommitAndSync("a").Ok);
        Assert.True(b.Pull().Ok);
        Assert.Equal("name: Q\n", File.ReadAllText(Path.Combine(otherRoot, "config", "saves", "a", "desktop", "q", "save.yaml")));
    }

    [Fact]
    public void Api_ListRepos_FiltersPushAndFollowsLinks()
    {
        var handler = new ScriptedHandler(
            _ =>
            {
                var resp = FakeGitHub.Json(System.Text.Json.Nodes.JsonNode.Parse(
                    """[{"full_name":"me/a","default_branch":"main","private":true,"description":"d","permissions":{"push":true}}, {"full_name":"me/ro","default_branch":"main","private":false,"description":null,"permissions":{"push":false}}]""")!);
                resp.Headers.Add("Link", "<https://api.github.com/user/repos?page=2>; rel=\"next\", <https://api.github.com/user/repos?page=2>; rel=\"last\"");
                return resp;
            },
            _ => FakeGitHub.Json(System.Text.Json.Nodes.JsonNode.Parse(
                """[{"full_name":"org/b","default_branch":"dev","private":false,"description":null,"permissions":{"push":true}}]""")!));
        var repos = new GitHubApi("t", handler).ListReposAsync().GetAwaiter().GetResult();
        Assert.Equal(["me/a", "org/b"], repos.Select(r => r.FullName));
        Assert.Equal("dev", repos[1].DefaultBranch);
        Assert.Contains("affiliation=owner,collaborator,organization_member", handler.Uris[0].Query);
        Assert.Equal("?page=2", handler.Uris[1].Query);
    }

    [Fact]
    public void Api_Unauthorized_ClearMessage()
    {
        var handler = new ScriptedHandler(_ => FakeGitHub.Status(System.Net.HttpStatusCode.Unauthorized, "Bad credentials"));
        var ex = Assert.Throws<GitHubApiException>(() => new GitHubApi("t", handler).GetLoginAsync().GetAwaiter().GetResult());
        Assert.Contains("Sign in to GitHub again", ex.Message);
        Assert.DoesNotContain("t ", ex.Message);
    }

    [Fact]
    public void Api_RateLimited_ClearMessage()
    {
        var handler = new ScriptedHandler(_ =>
        {
            var r = FakeGitHub.Status(System.Net.HttpStatusCode.Forbidden, "API rate limit exceeded");
            r.Headers.Add("x-ratelimit-remaining", "0");
            r.Headers.Add("x-ratelimit-reset", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString());
            return r;
        });
        var ex = Assert.Throws<GitHubApiException>(() => new GitHubApi("t", handler).GetLoginAsync().GetAwaiter().GetResult());
        Assert.Contains("rate limit", ex.Message);
    }
}

public sealed class GitHubAuthTests
{
    private static HttpResponseMessage J(string json) => FakeGitHub.Json(System.Text.Json.Nodes.JsonNode.Parse(json)!);

    private static readonly DeviceCode Code = new("ABCD-1234", "https://github.com/login/device", 900, 5, "dev-code");

    [Fact]
    public void StartDeviceFlow_ParsesResponse()
    {
        var handler = new ScriptedHandler(_ => J("""{"device_code":"dc","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":899,"interval":5}"""));
        var code = GitHubAuth.StartDeviceFlowAsync(GitHubAuth.CreateHttp(handler), "client123", default).GetAwaiter().GetResult();
        Assert.Equal(new DeviceCode("WDJB-MJHT", "https://github.com/login/device", 899, 5, "dc"), code);
        Assert.Equal("https://github.com/login/device/code", handler.Uris[0].ToString());
        Assert.Contains("client_id=client123", handler.Bodies[0]);
        Assert.Contains("scope=repo", handler.Bodies[0]);
    }

    [Fact]
    public void StartDeviceFlow_Disabled_Throws()
    {
        var handler = new ScriptedHandler(_ => J("""{"error":"device_flow_disabled"}"""));
        var ex = Assert.Throws<GitHubAuthException>(() =>
            GitHubAuth.StartDeviceFlowAsync(GitHubAuth.CreateHttp(handler), "c", default).GetAwaiter().GetResult());
        Assert.Contains("Device Flow", ex.Message);
    }

    [Fact]
    public void Poll_PendingSlowDownThenToken()
    {
        var delays = new List<TimeSpan>();
        var handler = new ScriptedHandler(
            _ => J("""{"error":"authorization_pending"}"""),
            _ => J("""{"error":"slow_down","interval":10}"""),
            _ => J("""{"error":"authorization_pending"}"""),
            _ => J("""{"access_token":"gho_secret","token_type":"bearer","scope":"repo"}"""));
        var token = GitHubAuth.PollForTokenAsync(GitHubAuth.CreateHttp(handler), "c", Code,
            (d, _) => { delays.Add(d); return Task.CompletedTask; }, default).GetAwaiter().GetResult();
        Assert.Equal("gho_secret", token);
        Assert.Equal([5, 5, 10, 10], delays.Select(d => (int)d.TotalSeconds));
        Assert.All(handler.Bodies, b => Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", b));
        Assert.All(handler.Uris, u => Assert.Equal("https://github.com/login/oauth/access_token", u.ToString()));
    }

    [Fact]
    public void Poll_SlowDownWithoutInterval_AddsFiveSeconds()
    {
        var delays = new List<TimeSpan>();
        var handler = new ScriptedHandler(_ => J("""{"error":"slow_down"}"""), _ => J("""{"access_token":"t"}"""));
        GitHubAuth.PollForTokenAsync(GitHubAuth.CreateHttp(handler), "c", Code,
            (d, _) => { delays.Add(d); return Task.CompletedTask; }, default).GetAwaiter().GetResult();
        Assert.Equal([5, 10], delays.Select(d => (int)d.TotalSeconds));
    }

    [Theory]
    [InlineData("expired_token", "expired")]
    [InlineData("access_denied", "denied")]
    [InlineData("unsupported_grant_type", "unsupported_grant_type")]
    public void Poll_TerminalErrors_Throw(string error, string expected)
    {
        var handler = new ScriptedHandler(_ => J($$"""{"error":"{{error}}"}"""));
        var ex = Assert.Throws<GitHubAuthException>(() => GitHubAuth.PollForTokenAsync(GitHubAuth.CreateHttp(handler), "c", Code,
            (_, _) => Task.CompletedTask, default).GetAwaiter().GetResult());
        Assert.Contains(expected, ex.Message);
        Assert.Equal(error, ex.Error);
    }

    [Fact]
    public void Poll_StopsWhenCodeLifetimeElapses()
    {
        var shortCode = Code with { ExpiresInSeconds = 12 };
        var handler = new ScriptedHandler(Enumerable.Repeat<Func<HttpRequestMessage, HttpResponseMessage>>(_ => J("""{"error":"authorization_pending"}"""), 10).ToArray());
        var ex = Assert.Throws<GitHubAuthException>(() => GitHubAuth.PollForTokenAsync(GitHubAuth.CreateHttp(handler), "c", shortCode,
            (_, _) => Task.CompletedTask, default).GetAwaiter().GetResult());
        Assert.Equal("expired_token", ex.Error);
        Assert.Equal(3, handler.Uris.Count); // polls at 5, 10, 15 s
    }

    [Fact]
    public void Poll_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new ScriptedHandler();
        Assert.ThrowsAny<OperationCanceledException>(() => GitHubAuth.PollForTokenAsync(GitHubAuth.CreateHttp(handler), "c", Code,
            (_, ct) => Task.Delay(1, ct), cts.Token).GetAwaiter().GetResult());
    }

    [Fact]
    public void CredentialManager_RoundTrip()
    {
        var target = "OptimusVibe:test:" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(GitHubAuth.LoadToken(target));
            GitHubAuth.SaveToken("ghp_example_token_123", target);
            Assert.Equal("ghp_example_token_123", GitHubAuth.LoadToken(target));
            GitHubAuth.SaveToken("ghp_replaced", target);
            Assert.Equal("ghp_replaced", GitHubAuth.LoadToken(target));
        }
        finally
        {
            GitHubAuth.ClearToken(target);
        }
        Assert.Null(GitHubAuth.LoadToken(target));
        GitHubAuth.ClearToken(target); // deleting a missing credential is fine
    }
}
