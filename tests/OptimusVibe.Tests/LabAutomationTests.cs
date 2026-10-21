using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Library;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

public class LabRepoResolverTests
{
    [Theory]
    [InlineData("https://github.com/jrose462/optimus-config.git", "jrose462/optimus-config")]
    [InlineData("https://github.com/jrose462/optimus-config", "jrose462/optimus-config")]
    [InlineData("https://ghp_x:y@github.com/o/n.git", "o/n")]
    [InlineData("git@github.com:o/n.git", "o/n")]
    [InlineData("ssh://git@github.com/o/n.git", "o/n")]
    [InlineData("https://github.com/o/n/", "o/n")]
    [InlineData("https://gitlab.com/o/n.git", null)]
    [InlineData("https://github.com/onlyowner", null)]
    [InlineData("C:\\repos\\cfg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseGitHubRemote(string? url, string? expected) => Assert.Equal(expected, LabRepoResolver.ParseGitHubRemote(url));

    private static AppPaths Paths() => new() { ConfigRoot = @"C:\x\repo\config", DataDir = @"C:\x\data" };

    [Fact]
    public void GitHubProvider_UsesTheChosenRepo()
    {
        var (repo, problem) = LabRepoResolver.Resolve(new AppSettings { SyncProvider = "github", GitHubRepo = "me/cfg", GitHubBranch = "dev" }, Paths());
        Assert.Null(problem);
        Assert.Equal(new LabRepoRef("me/cfg", "dev", "GitHub account"), repo);
    }

    [Fact]
    public void GitHubProvider_WithoutARepo_ExplainsAndPointsAtTheConfigRepoSection()
    {
        var (repo, problem) = LabRepoResolver.Resolve(new AppSettings { SyncProvider = "github" }, Paths());
        Assert.Null(repo);
        Assert.Contains("Config repo", problem);
    }

    [Fact]
    public void GitCheckout_WithAGitHubRemote_IsAccepted_AndUsesItsBranch()
    {
        string? Git(string dir, string[] args) => args[0] == "remote" ? "git@github.com:me/cfg.git" : "trunk";
        var (repo, _) = LabRepoResolver.Resolve(new AppSettings { SyncProvider = "git" }, Paths(), Git);
        Assert.Equal(new LabRepoRef("me/cfg", "trunk", "git checkout"), repo);
    }

    [Fact]
    public void GitCheckout_WithoutAGitHubRemote_ExplainsWhy()
    {
        var (a, pa) = LabRepoResolver.Resolve(new AppSettings(), Paths(), (_, _) => "https://gitlab.com/o/n.git");
        var (b, pb) = LabRepoResolver.Resolve(new AppSettings(), Paths(), (_, _) => null);
        var (c, pc) = LabRepoResolver.Resolve(new AppSettings(), null);
        Assert.Null(a); Assert.Null(b); Assert.Null(c);
        Assert.Contains("GitHub", pa);
        Assert.Contains("Config repo", pb);
        Assert.NotNull(pc);
    }
}

public class LabThrottleTests
{
    [Fact]
    public void AllowsOneStartPerInterval_AndForceOverrides()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var t = new LabThrottle(TimeSpan.FromMinutes(5), () => now);
        Assert.True(t.TryStart());
        Assert.False(t.TryStart());
        now += TimeSpan.FromMinutes(4);
        Assert.False(t.TryStart());
        Assert.True(t.TryStart(force: true));
        now += TimeSpan.FromMinutes(4);
        Assert.False(t.TryStart());          // the forced start began a new interval
        now += TimeSpan.FromMinutes(1);
        Assert.True(t.TryStart());
        t.Reset();
        Assert.True(t.TryStart());
    }
}

public class LabStatusIndexTests
{
    private static LabIssue Issue(int n, string key, string gameId, bool open, params string[] labels)
    {
        var parts = key.Split(':');
        var info = new GameLabContract.RequestInfo(LabKind.New, gameId, "G", parts[0], parts[1], "desktop", "1");
        return new LabIssue(n, "t", $"https://x/{n}", open, labels, GameLabContract.StatusFromLabels(labels), info, null, null);
    }

    [Fact]
    public void PrefersOpenThenNewest_AndMatchesByKeyOrGameId()
    {
        var issues = new[]
        {
            Issue(1, "steam:10", "", false, "lab:done"),
            Issue(2, "steam:10", "", true, "lab:researching"),
            Issue(3, "steam:10", "", false, "lab:done"),
            Issue(4, "steam:99", "other", true, "lab:queued"),
        };
        Assert.Equal(2, LabStatusIndex.For(issues, "steam:10", null)!.Number);
        Assert.Equal(4, LabStatusIndex.For(issues, null, "OTHER")!.Number);
        Assert.Null(LabStatusIndex.For(issues, "steam:5", "nope"));
        Assert.Equal("Researching", LabStatusIndex.Chip(issues[1]));
        Assert.Equal("Done", LabStatusIndex.Chip(issues[0]));
        Assert.Equal("Requested", LabStatusIndex.Chip(issues[3]));
    }
}

public sealed class LabRouterTests : IDisposable
{
    private readonly LabProfile _p = new();
    private readonly TempDir _data = new();

    public void Dispose() { _p.Dispose(); _data.Dispose(); }

    private LibraryEntry Entry(string id = "1285190", string name = "Borderlands 4", GameDef? game = null) => new()
    {
        Key = "steam:" + id, Name = name, Status = LibraryStatus.NeedsPresets, Game = game,
        Detected = new DetectedGame { Store = "steam", StoreId = id, Name = name, InstallDir = _p.Install, Exes = ["Borderlands4.exe"] },
    };

    private Func<LibraryEntry, CancellationToken, Task<FiledRequest>> Filer(GameLabClient client, List<string>? calls = null) =>
        (e, ct) =>
        {
            calls?.Add(e.Key);
            return client.FileRequestAsync(LabRouter.KindFor(e), LabTarget.From(e), "desktop", new EvidenceCollector(_p.Options()), ct);
        };

    [Fact]
    public async Task LabSetUp_FilesALabRequest_InsteadOfTheLocalQueue()
    {
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        var local = new List<LibraryEntry>();

        var r = await LabRouter.RouteAsync([Entry()], labReady: true, autoRequest: true, Filer(client), e => { local.AddRange(e); return e.Count; },
            new LabStateStore(_data.Path));

        Assert.True(r.UsedLab);
        Assert.Equal(1, r.Filed);
        Assert.Equal(0, r.QueuedLocally);
        Assert.Empty(local);
        var issue = Assert.Single(fake.Issues);
        Assert.Contains("[game-lab] Borderlands 4 - new", issue.Title);
    }

    [Fact]
    public async Task LabNotSetUp_UsesTheLocalRequestsQueue()
    {
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        var local = new List<LibraryEntry>();

        var r = await LabRouter.RouteAsync([Entry()], labReady: false, autoRequest: true, Filer(client), e => { local.AddRange(e); return e.Count; },
            new LabStateStore(_data.Path));

        Assert.False(r.UsedLab);
        Assert.Equal(1, r.QueuedLocally);
        Assert.Single(local);
        Assert.Empty(fake.Issues);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task AutoRequestOff_KeepsTheLocalQueue_EvenWhenTheLabIsReady()
    {
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        var r = await LabRouter.RouteAsync([Entry()], labReady: true, autoRequest: false, Filer(client), e => e.Count, new LabStateStore(_data.Path));
        Assert.False(r.UsedLab);
        Assert.Equal(1, r.QueuedLocally);
        Assert.Empty(fake.Issues);
    }

    [Fact]
    public async Task ARequestedGame_IsNotRequestedAgain_NorWhenTheIssueWasClosed()
    {
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        var state = new LabStateStore(_data.Path);
        var calls = new List<string>();
        var now = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

        await LabRouter.RouteAsync([Entry()], true, true, Filer(client, calls), e => 0, state, () => now);
        fake.Issues[0].Open = false;                         // closed without presets
        var again = await LabRouter.RouteAsync([Entry()], true, true, Filer(client, calls), e => 0, state, () => now.AddDays(2));
        Assert.Single(calls);
        Assert.Equal(1, again.Reused);
        Assert.Single(fake.Issues);

        await LabRouter.RouteAsync([Entry()], true, true, Filer(client, calls), e => 0, state, () => now.AddDays(8)); // memory expired
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task AnOpenRequest_IsReusedNotDuplicated()
    {
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        await client.FileRequestAsync(LabKind.New, LabTarget.From(Entry()), "desktop", new EvidenceCollector(_p.Options()));

        var r = await LabRouter.RouteAsync([Entry()], true, true, Filer(client), e => 0, new LabStateStore(_data.Path));

        Assert.Equal(0, r.Filed);
        Assert.Equal(1, r.Reused);
        Assert.Single(fake.Issues);
    }

    [Fact]
    public async Task AFailingRequest_FallsBackToTheLocalQueue_ForThatGameOnly()
    {
        var local = new List<LibraryEntry>();
        Task<FiledRequest> File(LibraryEntry e, CancellationToken ct) =>
            e.Key.EndsWith("2") ? throw new GitHubApiException(null, "boom") : Task.FromResult(new FiledRequest(1, "u", false, 0, "ok"));

        var r = await LabRouter.RouteAsync([Entry("1"), Entry("2", "Two")], true, true, File, e => { local.AddRange(e); return e.Count; },
            new LabStateStore(_data.Path));

        Assert.True(r.UsedLab);
        Assert.Equal(1, r.Filed);
        Assert.Equal("Two", Assert.Single(local).Name);
        Assert.Equal(1, r.QueuedLocally);
        Assert.Contains("boom", Assert.Single(r.Errors));
    }

    [Fact]
    public void KindFor_IsNewWithoutAConfig_AndRefreshWithOne()
    {
        Assert.Equal(LabKind.New, LabRouter.KindFor(Entry()));
        Assert.Equal(LabKind.Refresh, LabRouter.KindFor(Entry(game: new GameDef { Id = "x", Name = "X", Store = "steam" })));
    }
}

public sealed class LabCycleTests : IDisposable
{
    private readonly LabProfile _p = new();
    private readonly TempDir _tmp = new();
    private readonly FakeLabGitHub _fake = new();

    public void Dispose() { _p.Dispose(); _tmp.Dispose(); }

    private sealed class FakeProvider(AppPaths paths) : ISyncProvider
    {
        public int Pulls { get; private set; }
        public bool Fail { get; set; }
        public string Name => "git";
        public string Describe() => "fake";
        public bool IsReady(out string reason) { reason = ""; return true; }
        public OptimusVibe.Engine.GitResult CommitAndSync(string message, params string[] paths) => new(true, "ok");

        public OptimusVibe.Engine.GitResult Pull()
        {
            Pulls++;
            if (Fail) return new(false, "offline");
            Directory.CreateDirectory(paths.GamesDir);
            File.WriteAllText(paths.GameFile("borderlands-4"), "name: Borderlands 4\n");
            return new(true, "pulled");
        }
    }

    private (LabCycleDeps Deps, FakeProvider Provider, LabStateStore State) Deps(Func<DateTime>? now = null)
    {
        var paths = new AppPaths { ConfigRoot = _tmp.Dir("repo", "config"), DataDir = _tmp.Dir("data") };
        var provider = new FakeProvider(paths);
        var state = new LabStateStore(paths.DataDir);
        var deps = new LabCycleDeps
        {
            Client = new GameLabClient(_fake.Api(), FakeLabGitHub.Repo),
            TargetFor = i => _p.Target(i.Request?.Name ?? "x", i.Request?.Store ?? "steam", i.Request?.StoreId ?? "1"),
            Collector = () => new EvidenceCollector(_p.Options()),
            Paths = paths, Provider = provider, State = state, Now = now,
        };
        return (deps, provider, state);
    }

    private static string Body(LabKind kind = LabKind.New) =>
        GameLabContract.RequestBody(new GameLabContract.RequestInfo(kind, "borderlands-4", "Borderlands 4", "steam", "1285190", "desktop", "1"));

    private const string AskComment = "<!-- optimus-vibe:lab-needs-evidence v1 -->\nSend:\n```json\n{\"files\":[\"{Documents}\\\\My Games\\\\*.ini\"],\"registry\":[],\"listDirs\":[]}\n```";
    private const string ResultComment = "<!-- optimus-vibe:lab-result v1 -->\n```json\n{\"gameId\":\"borderlands-4\",\"presets\":[\"quality\",\"performance\"],\"merged\":true,\"prNumber\":3}\n```";

    [Fact]
    public async Task AnswersNeedsEvidence_AndStartsTheQueue()
    {
        _p.Write(_p.Documents, @"My Games\Borderlands 4\GameUserSettings.ini", "[A]\nb=1\n");
        var asking = _fake.Add("[game-lab] Borderlands 4 - new", Body(), "game-lab", "lab:needs-evidence");
        asking.Comments.Add(AskComment);
        var queued = _fake.Add("[game-lab] Other - new", GameLabContract.RequestBody(new GameLabContract.RequestInfo(LabKind.New, "", "Other", "steam", "5", "desktop", "1")), "game-lab", "lab:queued");
        var (deps, _, _) = Deps();

        var r = await LabCycle.RunAsync(deps);

        Assert.Equal(["Borderlands 4"], r.Answered);
        Assert.Contains(asking.Comments, c => c.Contains("optimus-vibe:lab-evidence"));
        Assert.DoesNotContain("lab:needs-evidence", asking.Labels);
        Assert.Equal(queued.Number, r.Drain?.Dispatched);
        Assert.Contains(queued.Number, _fake.Dispatched);
        Assert.Empty(r.Errors);

        // answered once: the next pass sends nothing more
        var again = await LabCycle.RunAsync(deps);
        Assert.Empty(again.Answered);
    }

    [Fact]
    public async Task MergedResults_ArePulledOnce_AndAnnouncedOnce_AfterTheFirstPassBaselines()
    {
        var old = _fake.Add("[game-lab] Old - new", Body(), "game-lab", "lab:done");
        old.Open = false;
        old.Comments.Add(ResultComment);
        var (deps, provider, _) = Deps();

        var first = await LabCycle.RunAsync(deps);
        Assert.Empty(first.NewPresets);                    // history is not announced
        Assert.Equal(0, provider.Pulls);

        var fresh = _fake.Add("[game-lab] Borderlands 4 - new", Body(), "game-lab", "lab:done");
        fresh.Open = false;
        fresh.Comments.Add(ResultComment);
        var second = await LabCycle.RunAsync(deps);
        var np = Assert.Single(second.NewPresets);
        Assert.Equal("borderlands-4", np.GameId);
        Assert.Equal("Borderlands 4", np.Name);
        Assert.Equal(LabKind.New, np.Kind);
        Assert.Equal(1, provider.Pulls);
        Assert.True(File.Exists(deps.Paths.GameFile("borderlands-4")));

        var third = await LabCycle.RunAsync(deps);
        Assert.Empty(third.NewPresets);
        Assert.Equal(1, provider.Pulls);
    }

    [Fact]
    public async Task APullThatFails_IsReportedAndRetriedNextPass()
    {
        var (deps, provider, _) = Deps();
        await LabCycle.RunAsync(deps);                      // baseline
        var fresh = _fake.Add("[game-lab] Borderlands 4 - new", Body(), "game-lab", "lab:done");
        fresh.Open = false;
        fresh.Comments.Add(ResultComment);

        provider.Fail = true;
        var failed = await LabCycle.RunAsync(deps);
        Assert.Empty(failed.NewPresets);
        Assert.Contains(failed.Errors, e => e.Contains("offline"));

        provider.Fail = false;
        var ok = await LabCycle.RunAsync(deps);
        Assert.Single(ok.NewPresets);
    }

    [Fact]
    public async Task ARequestWithoutAMergedResult_IsNotAnnounced()
    {
        var (deps, provider, _) = Deps();
        await LabCycle.RunAsync(deps);
        var review = _fake.Add("[game-lab] Borderlands 4 - new", Body(), "game-lab", "lab:review");
        review.Comments.Add(ResultComment.Replace("\"merged\":true", "\"merged\":false"));
        var r = await LabCycle.RunAsync(deps);
        Assert.Empty(r.NewPresets);
        Assert.Equal(0, provider.Pulls);
    }

    [Fact]
    public async Task APulledResult_MarksWhenThePresetsArrived_AndAgainstWhichBuild()
    {
        var t = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);
        var (baseDeps, _, state) = Deps(() => t);
        var deps = new LabCycleDeps
        {
            Client = baseDeps.Client, Collector = baseDeps.Collector, Paths = baseDeps.Paths, Provider = baseDeps.Provider, State = state, Now = baseDeps.Now,
            TargetFor = i => _p.Target() with { Detected = new DetectedGame { Store = "steam", StoreId = "1285190", Name = "Borderlands 4", BuildId = "555" } },
        };
        await LabCycle.RunAsync(deps); // baseline: history marks nothing
        Assert.Empty(state.Load().PresetsMade);

        var fresh = _fake.Add("[game-lab] Borderlands 4 - refresh", Body(LabKind.Refresh), "game-lab", "lab:done");
        fresh.Open = false;
        fresh.Comments.Add(ResultComment);
        await LabCycle.RunAsync(deps);

        var mark = state.Load().PresetsFor("borderlands-4")!;
        Assert.Equal(t, mark.MadeUtc);
        Assert.Equal("steam:555", mark.BuildId);
        Assert.Equal(fresh.Number, mark.Issue);
        Assert.Equal("refresh", mark.Kind);
    }

    [Fact]
    public async Task ASyncCheckResult_LeavesThePresetsMarkAlone()
    {
        var (deps, _, state) = Deps();
        await LabCycle.RunAsync(deps);
        var check = _fake.Add("[game-lab] Borderlands 4 - sync-check", Body(LabKind.SyncCheck), "game-lab", "lab:done");
        check.Open = false;
        check.Comments.Add(ResultComment);
        Directory.CreateDirectory(deps.Paths.GamesDir);
        File.WriteAllText(deps.Paths.GameFile("borderlands-4"), "name: Borderlands 4\n");

        var r = await LabCycle.RunAsync(deps);

        Assert.Equal(LabKind.SyncCheck, Assert.Single(r.NewPresets).Kind);
        Assert.Null(state.Load().PresetsFor("borderlands-4"));
    }
}
