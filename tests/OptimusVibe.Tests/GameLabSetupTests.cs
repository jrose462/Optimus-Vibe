using System.Text;
using OptimusVibe.GameLab;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

public class GameLabSetupTests
{
    private const string Workflow = "name: game-lab\non:\n  workflow_dispatch:\n    inputs:\n      issue: {}\n";

    private static IReadOnlyDictionary<string, byte[]> Files() => new Dictionary<string, byte[]>
    {
        [".github/workflows/game-lab.yml"] = Encoding.UTF8.GetBytes(Workflow),
        [".github/game-lab/prompt.md"] = Encoding.UTF8.GetBytes("# prompt\n"),
    };

    private static GameLabSetup Setup(FakeLabGitHub fake, GameLabSetupOptions? o = null)
    {
        var s = new GameLabSetup(fake.Api(), FakeLabGitHub.Repo, "main", o);
        s.FileSource = Files;
        return s;
    }

    // ------------------------------------------------------------ sealed box

    [Fact]
    public void Sealed_box_round_trips_with_a_test_key_pair_and_is_not_deterministic()
    {
        var (pub, priv) = SealedBox.GenerateKeyPair();
        var key = Convert.ToBase64String(pub);
        var a = SealedBox.Encrypt("sk-ant-oat01-secret", key);
        var b = SealedBox.Encrypt("sk-ant-oat01-secret", key);
        Assert.NotEqual(a, b);                                  // a fresh ephemeral key every time
        Assert.DoesNotContain("secret", a);
        Assert.Equal("sk-ant-oat01-secret", SealedBox.Decrypt(a, pub, priv));
        Assert.Equal("sk-ant-oat01-secret", SealedBox.Decrypt(b, pub, priv));
        Assert.Throws<ArgumentException>(() => SealedBox.Encrypt("x", "not base64!!"));
        Assert.Throws<ArgumentException>(() => SealedBox.Encrypt("x", Convert.ToBase64String(new byte[5])));
    }

    // ------------------------------------------------------------ whole setup against the fake

    [Fact]
    public async Task Full_setup_installs_everything_and_is_idempotent()
    {
        var fake = new FakeLabGitHub { Scopes = "repo, workflow" }; // no project scope: the board is skipped
        const string claude = "sk-ant-oat01-SUPER-SECRET-TOKEN";
        var setup = Setup(fake, new GameLabSetupOptions { ClaudeToken = claude });

        var results = await setup.RunAllAsync();

        Assert.All(results, r => Assert.True(r.Result.Ok, $"{r.Step.Name}: {r.Result.Message}"));
        Assert.True(results.Single(r => r.Step.Name == "board").Result.Skipped);

        // workflow + prompt in ONE commit on top of the old head
        var files = fake.Git.Files();
        Assert.Equal(Workflow, files[".github/workflows/game-lab.yml"]);
        Assert.Equal("# prompt\n", files[".github/game-lab/prompt.md"]);
        Assert.Equal("Install Optimus-Vibe game lab", fake.Git.CommitMessage(fake.Git.Head));
        Assert.Equal("init", fake.Git.CommitMessage(fake.Git.ParentOf(fake.Git.Head)!));

        // labels
        Assert.Equal(GameLabContract.Labels.Select(l => l.Name).OrderBy(x => x), fake.LabelNames.OrderBy(x => x));

        // secret: decrypts with the repo's key to exactly the token; the token never travelled in clear text or reached the log
        var (enc, keyId) = fake.Secrets[GameLabContract.SecretName];
        Assert.Equal("k1", keyId);
        Assert.Equal(claude, SealedBox.Decrypt(enc, fake.KeyPair.PublicKey, fake.KeyPair.PrivateKey));
        Assert.All(fake.Bodies, b => Assert.DoesNotContain(claude, b));
        Assert.All(fake.Requests, r => Assert.DoesNotContain(claude, r));

        // variables with defaults
        Assert.Equal("5", fake.Variables["GAME_LAB_DAILY_MAX"]);
        Assert.Equal("claude-sonnet-5-5", fake.Variables["GAME_LAB_MODEL"]);

        // settings
        Assert.True(fake.ActionsEnabled);
        Assert.Equal("read", fake.WorkflowPermissions);
        Assert.True(fake.CanApprove);
        Assert.True(fake.Squash);
        Assert.True(fake.DeleteBranch);

        // second time: every check is done (or skipped), and a re-run changes nothing
        foreach (var (step, check) in await setup.CheckAllAsync())
            Assert.True(check.Done || check.Skipped, $"{step.Name}: {check.Detail}");
        var headBefore = fake.Git.Head;
        var writes = fake.Requests.Count(r => !r.StartsWith("GET "));
        var again = await Setup(fake).RunAllAsync();
        Assert.All(again.Where(r => r.Step.Name != "test"), r => Assert.True(r.Result.Ok, $"{r.Step.Name}: {r.Result.Message}"));
        Assert.Equal(headBefore, fake.Git.Head);
        Assert.Equal(writes, fake.Requests.Count(r => !r.StartsWith("GET ")));
    }

    [Fact]
    public async Task Missing_workflow_scope_requires_a_new_sign_in()
    {
        var fake = new FakeLabGitHub { Scopes = "repo" };
        var setup = Setup(fake);

        var check = await setup.Step("scopes")!.CheckAsync();
        Assert.False(check.Done);
        Assert.Contains("workflow", check.Detail);

        var results = await setup.RunAllAsync();
        var r = Assert.Single(results); // stops at the first blocking step
        Assert.True(r.Result.NeedsLogin);
        Assert.False(r.Result.Ok);
        Assert.Empty(fake.Git.Files().Keys.Where(k => k.StartsWith(".github")));
    }

    [Fact]
    public async Task A_refused_workflow_push_without_the_scope_says_to_sign_in_again()
    {
        var fake = new FakeLabGitHub { Scopes = "repo", RejectWorkflowFiles = true };
        var result = await Setup(fake).Step("workflow")!.RunAsync();
        Assert.False(result.Ok);
        Assert.True(result.NeedsLogin);
    }

    [Fact]
    public async Task Fine_grained_tokens_pass_the_scope_check_with_a_permissions_hint()
    {
        var fake = new FakeLabGitHub { Scopes = null }; // no X-OAuth-Scopes header
        var check = await Setup(fake).Step("scopes")!.CheckAsync();
        Assert.True(check.Done);
        Assert.Contains("Workflows", check.Warning);
        Assert.Contains("Secrets", GameLabSetup.FineGrainedPermissionsHelp);
        // and the board is attempted (the API will say if it can't); here the fake answers 403 -> skipped, not failed
        var board = await Setup(fake).Step("board")!.RunAsync();
        Assert.True(board.Ok);
        Assert.True(board.Skipped);
    }

    [Fact]
    public async Task Variables_keep_user_values_but_apply_explicit_ones()
    {
        var fake = new FakeLabGitHub();
        fake.Variables["GAME_LAB_DAILY_MAX"] = "12";
        await Setup(fake).Step("variables")!.RunAsync();
        Assert.Equal("12", fake.Variables["GAME_LAB_DAILY_MAX"]);     // existing value kept
        Assert.Equal("claude-sonnet-5-5", fake.Variables["GAME_LAB_MODEL"]);

        var explicitSetup = Setup(fake, new GameLabSetupOptions { DailyMax = 3, Model = "claude-opus-5" });
        Assert.False((await explicitSetup.Step("variables")!.CheckAsync()).Done);
        await explicitSetup.Step("variables")!.RunAsync();
        Assert.Equal("3", fake.Variables["GAME_LAB_DAILY_MAX"]);
        Assert.Equal("claude-opus-5", fake.Variables["GAME_LAB_MODEL"]);
        Assert.True((await explicitSetup.Step("variables")!.CheckAsync()).Done);
    }

    [Fact]
    public async Task Secret_step_without_a_token_reports_what_to_do()
    {
        var fake = new FakeLabGitHub();
        var r = await Setup(fake).Step("secret")!.RunAsync();
        Assert.False(r.Ok);
        Assert.Contains("claude setup-token", r.Message);
        Assert.Empty(fake.Secrets);
    }

    [Fact]
    public async Task Workflow_step_only_commits_files_that_differ()
    {
        var fake = new FakeLabGitHub();
        var setup = Setup(fake);
        Assert.False((await setup.Step("workflow")!.CheckAsync()).Done);
        await setup.Step("workflow")!.RunAsync();
        Assert.True((await setup.Step("workflow")!.CheckAsync()).Done);

        // an updated template is picked up in one new commit
        var updated = new Dictionary<string, byte[]>(Files()) { [".github/game-lab/prompt.md"] = Encoding.UTF8.GetBytes("# prompt v2\n") };
        setup.FileSource = () => updated;
        Assert.False((await setup.Step("workflow")!.CheckAsync()).Done);
        var head = fake.Git.Head;
        await setup.Step("workflow")!.RunAsync();
        Assert.NotEqual(head, fake.Git.Head);
        Assert.Equal(head, fake.Git.ParentOf(fake.Git.Head));
        Assert.Equal("# prompt v2\n", fake.Git.Files()[".github/game-lab/prompt.md"]);
    }

    [Fact]
    public async Task Status_summarizes_steps_and_open_requests()
    {
        var fake = new FakeLabGitHub { Scopes = "repo, workflow" };
        var setup = Setup(fake, new GameLabSetupOptions { ClaudeToken = "sk-ant-oat01-test-token-value" });
        var before = await setup.GetStatusAsync(new GameLabClient(fake.Api(), FakeLabGitHub.Repo));
        Assert.False(before.Ready);
        Assert.Contains("incomplete", before.Summary);

        await setup.RunAllAsync();
        string Req(string id) => GameLabContract.RequestBody(new GameLabContract.RequestInfo(LabKind.New, "", id, "steam", id, "d", "1"));
        fake.Add("a", Req("1"), "game-lab", "lab:queued");
        fake.Add("b", Req("2"), "game-lab", "lab:needs-evidence");
        var after = await setup.GetStatusAsync(new GameLabClient(fake.Api(), FakeLabGitHub.Repo));
        Assert.True(after.Ready, after.Summary);
        Assert.Equal(1, after.Queued);
        Assert.Equal(1, after.NeedsEvidence);
        Assert.Contains("2 open request(s)", after.Summary);
    }

    [Fact]
    public async Task Scope_header_is_parsed_and_missing_scopes_are_computed()
    {
        var fake = new FakeLabGitHub { Scopes = "repo, read:org, workflow" };
        var scopes = await fake.Api().GetTokenScopesAsync();
        Assert.Equal(["repo", "read:org", "workflow"], scopes);
        Assert.Empty(GitHubAuth.MissingScopes(scopes));
        Assert.Equal(["project"], GitHubAuth.MissingScopes(scopes, includeOptional: true));
        Assert.Equal(["workflow"], GitHubAuth.MissingScopes(["repo"]));
        Assert.Empty(GitHubAuth.MissingScopes(null)); // fine-grained: unknown, not missing
        Assert.Equal("repo workflow project", GitHubAuth.Scopes);
    }

    [Theory]
    [InlineData("sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("gho_0123456789abcdefghijklmnopqrstuvwxyzAB", false)] // GitHub token (40 chars)
    [InlineData("sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)] // API key
    [InlineData("sk-ant-oat01-", false)]
    [InlineData(null, false)]
    public void Only_subscription_tokens_are_accepted(string? token, bool ok) =>
        Assert.Equal(ok, GameLabSetup.LooksLikeClaudeToken(token));

    [Fact]
    public async Task Secret_step_refuses_a_github_token_and_sends_nothing()
    {
        var fake = new FakeLabGitHub { Scopes = "repo, workflow" };
        var setup = Setup(fake, new GameLabSetupOptions { ClaudeToken = "gho_0123456789abcdefghijklmnopqrstuvwxyzAB" });

        var r = await setup.RunStepAsync(setup.Step("secret")!);

        Assert.False(r.Ok);
        Assert.Contains("claude setup-token", r.Message);
        Assert.Empty(fake.Secrets);
    }

    [Fact]
    public async Task Settings_step_passes_on_a_free_private_repo_that_refuses_auto_merge()
    {
        var fake = new FakeLabGitHub { Scopes = "repo, workflow", AutoMergeUnavailable = true };
        var setup = Setup(fake, new GameLabSetupOptions { ClaudeToken = "sk-ant-oat01-test-token-value" });

        var results = await setup.RunAllAsync();

        var settings = results.Single(r => r.Step.Name == "settings").Result;
        Assert.True(settings.Ok, settings.Message);
        Assert.True(fake.Squash);
        Assert.True(fake.DeleteBranch);
        Assert.False(fake.AutoMerge);

        // a second check doesn't ask for auto-merge again
        var again = await setup.RunAllAsync();
        Assert.True(again.Single(r => r.Step.Name == "settings").Result.Ok);
    }
}

public class GameLabEmbeddedFilesTests
{
    [Fact]
    public void The_workflow_and_prompt_files_are_embedded_from_seed_config_repo()
    {
        var files = GameLabSetup.EmbeddedFiles();
        Assert.True(files.ContainsKey(".github/workflows/game-lab.yml"), string.Join(", ", files.Keys));
        var yml = Encoding.UTF8.GetString(files[".github/workflows/game-lab.yml"]);
        Assert.Contains("workflow_dispatch", yml);
        Assert.Contains("issue", yml);
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", yml);
        Assert.All(files.Keys, k => Assert.StartsWith(".github/", k)); // repo-relative paths, '/' separators
        Assert.All(files.Values, v => Assert.DoesNotContain((byte)'\r', v)); // LF only, whatever the checkout used
        // the gate must not pipe a large body into `grep -q` (SIGPIPE under pipefail made big requests look markerless)
        Assert.DoesNotContain("\"$body\" | grep -q", yml);
    }
}
