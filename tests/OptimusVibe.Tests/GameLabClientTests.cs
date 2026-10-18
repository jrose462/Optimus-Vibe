using System.Text;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

public class GameLabContractTests
{
    private static GameLabContract.RequestInfo Info(string name = "Borderlands 4", LabKind kind = LabKind.New) =>
        new(kind, "borderlands-4", name, "steam", "1285190", "ally-x", "1.2.3");

    [Fact]
    public void Title_and_body_follow_the_contract()
    {
        Assert.Equal("[game-lab] Borderlands 4 - new", GameLabContract.Title("Borderlands 4", LabKind.New));
        Assert.Equal("[game-lab] A B - sync-check", GameLabContract.Title("  A \n B ", LabKind.SyncCheck));

        var body = GameLabContract.RequestBody(Info(), GameLabContract.EvidenceHeader(1, 1) + "### File: x\n```\na\n```\n");
        var lines = body.Split('\n');
        Assert.Equal("<!-- optimus-vibe:game-lab v1 -->", lines[0]);
        Assert.StartsWith("<!-- lab-request {", lines[1]);
        Assert.EndsWith("} -->", lines[1]);
        Assert.Contains("\"kind\":\"new\"", lines[1]);
        Assert.Contains("\"gameId\":\"borderlands-4\"", lines[1]);
        Assert.Contains("\"store\":\"steam\"", lines[1]);
        Assert.Contains("\"storeId\":\"1285190\"", lines[1]);
        Assert.Contains("\"device\":\"ally-x\"", lines[1]);
        Assert.Contains("\"appVersion\":\"1.2.3\"", lines[1]);
        // human text sits between the request comment and the first evidence block
        Assert.True(body.IndexOf("Borderlands 4", StringComparison.Ordinal) < body.IndexOf("<!-- optimus-vibe:lab-evidence v1 -->", StringComparison.Ordinal));
        Assert.Contains("### File: x", body);
    }

    [Fact]
    public void Request_round_trips_and_hostile_names_cannot_close_the_comment()
    {
        var info = Info(name: "Evil --> <script> & \"quote\" game");
        var body = GameLabContract.RequestBody(info);
        var requestLine = body.Split('\n')[1];
        Assert.Equal(1, requestLine.Split("-->").Length - 1);               // only the real terminator
        Assert.Equal(info, GameLabContract.ParseRequest(body));
        Assert.Null(GameLabContract.ParseRequest("just an issue"));
        Assert.Null(GameLabContract.ParseRequest("<!-- optimus-vibe:game-lab v1 -->\nno request comment"));
    }

    [Fact]
    public void Labels_map_to_status_and_board_columns()
    {
        Assert.Equal(7, GameLabContract.Labels.Count);
        Assert.Equal(LabStatus.Queued, GameLabContract.StatusFromLabels(["game-lab", "lab:queued"]));
        Assert.Equal(LabStatus.Researching, GameLabContract.StatusFromLabels(["game-lab", "lab:researching"]));
        Assert.Equal(LabStatus.NeedsEvidence, GameLabContract.StatusFromLabels(["game-lab", "lab:researching", "lab:needs-evidence"]));
        Assert.Equal(LabStatus.Review, GameLabContract.StatusFromLabels(["lab:review", "lab:researching"]));
        Assert.Equal(LabStatus.Done, GameLabContract.StatusFromLabels(["lab:done", "lab:review"]));
        Assert.Equal(LabStatus.Failed, GameLabContract.StatusFromLabels(["lab:done", "lab:failed"]));
        Assert.Equal(LabStatus.Unknown, GameLabContract.StatusFromLabels(["game-lab"]));

        Assert.Equal("Requested", LabBoard.StatusFor(LabStatus.Queued));
        Assert.Equal("Researching", LabBoard.StatusFor(LabStatus.Researching));
        Assert.Equal("Needs evidence", LabBoard.StatusFor(LabStatus.NeedsEvidence));
        Assert.Equal("Review", LabBoard.StatusFor(LabStatus.Review));
        Assert.Equal("Done", LabBoard.StatusFor(LabStatus.Done));
        Assert.Equal("Review", LabBoard.StatusFor(LabStatus.Failed));
        Assert.Equal("Requested", LabBoard.StatusFor(LabStatus.Unknown, isOpen: true));
        Assert.Equal("Done", LabBoard.StatusFor(LabStatus.Unknown, isOpen: false));
        Assert.Equal(["Requested", "Researching", "Needs evidence", "Review", "Done"], LabBoard.StatusNames);
    }

    [Fact]
    public void Needs_evidence_and_result_comments_parse()
    {
        var ask = GameLabContract.ParseNeedsEvidence(
            "<!-- optimus-vibe:lab-needs-evidence v1 -->\nPlease send:\n```json\n{\"files\":[\"{LocalAppData}\\\\Foo\\\\*.ini\"],\"registry\":[\"HKCU\\\\Software\\\\Foo\"],\"listDirs\":[\"{Documents}\\\\Foo\"]}\n```\n");
        Assert.NotNull(ask);
        Assert.Equal(@"{LocalAppData}\Foo\*.ini", Assert.Single(ask!.Files));
        Assert.Equal(@"HKCU\Software\Foo", Assert.Single(ask.Registry));
        Assert.Equal(@"{Documents}\Foo", Assert.Single(ask.ListDirs));
        Assert.Null(GameLabContract.ParseNeedsEvidence("hello"));
        Assert.Null(GameLabContract.ParseNeedsEvidence("<!-- optimus-vibe:lab-needs-evidence v1 -->\nno json"));
        Assert.Null(GameLabContract.ParseNeedsEvidence("<!-- optimus-vibe:lab-needs-evidence v1 -->\n```json\n{not json\n```"));

        var res = GameLabContract.ParseResult(
            "<!-- optimus-vibe:lab-result v1 -->\n```json\n{\"gameId\":\"borderlands-4\",\"presets\":[\"quality\",\"performance\"],\"cloudSyncsSettings\":false,\"syncConfidence\":\"high\",\"prNumber\":12,\"merged\":true}\n```");
        Assert.NotNull(res);
        Assert.Equal("borderlands-4", res!.GameId);
        Assert.Equal(["quality", "performance"], res.Presets);
        Assert.Equal(2, res.PresetCount);
        Assert.False(res.CloudSyncsSettings);
        Assert.Equal("high", res.SyncConfidence);
        Assert.Equal(12, res.PrNumber);
        Assert.True(res.Merged);

        var counted = GameLabContract.ParseResult("<!-- optimus-vibe:lab-result v1 -->\n```json\n{\"gameId\":\"x\",\"presets\":3,\"merged\":false}\n```");
        Assert.Equal(3, counted!.PresetCount);
        Assert.False(counted.Merged);
    }
}

public class GameLabClientTests
{
    private static LabTarget Target(LabProfile p) => p.Target();

    private static (FakeLabGitHub Fake, GameLabClient Client) Make()
    {
        var fake = new FakeLabGitHub();
        return (fake, new GameLabClient(fake.Api(), FakeLabGitHub.Repo));
    }

    [Fact]
    public async Task FileRequest_opens_a_labelled_issue_with_the_first_evidence_part_in_the_body()
    {
        using var p = new LabProfile();
        p.Write(p.Documents, @"My Games\Borderlands 4\GameUserSettings.ini", "[A]\nb=1\n");
        var (fake, client) = Make();

        var filed = await client.FileRequestAsync(LabKind.New, Target(p), "desktop", new EvidenceCollector(p.Options()));

        Assert.False(filed.Reused);
        var issue = Assert.Single(fake.Issues);
        Assert.Equal("[game-lab] Borderlands 4 - new", issue.Title);
        Assert.Equal(["game-lab"], issue.Labels);   // the workflow starts on "opened" and queues itself when busy
        Assert.StartsWith("<!-- optimus-vibe:game-lab v1 -->\n<!-- lab-request {", issue.Body);
        Assert.Contains("<!-- optimus-vibe:lab-evidence v1 -->", issue.Body);
        Assert.Contains("### File: {Documents}\\My Games\\Borderlands 4\\GameUserSettings.ini", issue.Body);
        Assert.Empty(issue.Comments);
        Assert.DoesNotContain(p.User, issue.Body);
        Assert.Equal(issue.Number, filed.Number);
    }

    [Fact]
    public async Task Large_evidence_continues_in_comments_of_at_most_60k()
    {
        using var p = new LabProfile();
        for (var i = 0; i < 8; i++)
            p.Write(p.Documents, $@"My Games\Borderlands 4\Settings{i}.ini", string.Join("\n", Enumerable.Repeat("Key=Value0123456789", 5000)));
        var (fake, client) = Make();

        var filed = await client.FileRequestAsync(LabKind.New, Target(p), "desktop", new EvidenceCollector(p.Options()));

        var issue = fake.Issues.Single();
        Assert.True(issue.Comments.Count >= 1);
        Assert.Equal(issue.Comments.Count, filed.EvidenceComments);
        Assert.All(issue.Comments, c =>
        {
            Assert.True(c.Length <= 60_000);
            Assert.StartsWith("<!-- optimus-vibe:lab-evidence v1 -->", c);
        });
        Assert.True(issue.Body.Length <= 65_000);
    }

    [Fact]
    public async Task An_open_request_for_the_same_store_id_and_kind_is_reused()
    {
        using var p = new LabProfile();
        var (fake, client) = Make();
        var collector = new EvidenceCollector(p.Options());

        var first = await client.FileRequestAsync(LabKind.New, Target(p), "desktop", collector);
        var second = await client.FileRequestAsync(LabKind.New, Target(p), "ally-x", collector);
        var other = await client.FileRequestAsync(LabKind.SyncCheck, Target(p), "desktop", collector); // different kind: new issue
        var other2 = await client.FileRequestAsync(LabKind.New, Target(p) with { StoreId = "999", GameId = "other" }, "desktop", collector);

        Assert.True(second.Reused);
        Assert.Equal(first.Number, second.Number);
        Assert.False(other.Reused);
        Assert.False(other2.Reused);
        Assert.Equal(3, fake.Issues.Count);

        fake.Issues[0].Open = false; // once closed, a new request is allowed
        Assert.False((await client.FileRequestAsync(LabKind.New, Target(p), "desktop", collector)).Reused);
    }

    [Fact]
    public async Task ListOpen_reports_status_from_labels_and_ignores_other_issues()
    {
        var (fake, client) = Make();
        var body = GameLabContract.RequestBody(new GameLabContract.RequestInfo(LabKind.Refresh, "g", "G", "steam", "1", "desktop", "1"));
        fake.Add("[game-lab] G - refresh", body, "game-lab", "lab:researching");
        fake.Add("unrelated bug", "text", "bug");

        var open = await client.ListOpenAsync();

        var i = Assert.Single(open);
        Assert.Equal(LabStatus.Researching, i.Status);
        Assert.Equal(LabKind.Refresh, i.Kind);
        Assert.Equal("G", i.Name);
    }

    [Fact]
    public async Task Needs_evidence_is_found_answered_and_requeued()
    {
        using var p = new LabProfile();
        p.Write(p.LocalAppData, @"OakGame\Saved\Config\Windows\Engine.ini", "[A]\nb=1\n");
        var (fake, client) = Make();
        var body = GameLabContract.RequestBody(new GameLabContract.RequestInfo(LabKind.New, "borderlands-4", "Borderlands 4", "steam", "1285190", "desktop", "1"));
        var issue = fake.Add("[game-lab] Borderlands 4 - new", body, "game-lab", "lab:researching", "lab:needs-evidence");
        issue.Comments.Add("<!-- optimus-vibe:lab-needs-evidence v1 -->\n```json\n{\"files\":[\"{LocalAppData}\\\\OakGame\\\\Saved\\\\Config\\\\Windows\\\\*.ini\",\"C:\\\\Windows\\\\win.ini\"],\"registry\":[],\"listDirs\":[]}\n```");

        var pending = Assert.Single(await client.FindNeedsEvidenceAsync());
        Assert.Equal(issue.Number, pending.Issue.Number);
        Assert.Equal(2, pending.Request.Files.Count);

        var answered = await client.AnswerNeedsEvidenceAsync(pending, new EvidenceCollector(p.Options()), Target(p));

        Assert.Equal(1, answered.Sections);
        Assert.Contains(answered.Notes, n => n.Contains("Refused"));
        Assert.StartsWith("<!-- optimus-vibe:lab-evidence v1 -->", issue.Comments[^1]);
        Assert.Contains("Engine.ini", issue.Comments[^1]);
        Assert.DoesNotContain("lab:needs-evidence", issue.Labels);
        Assert.DoesNotContain("lab:queued", issue.Labels);   // the evidence comment re-triggers the workflow
        Assert.Empty(await client.FindNeedsEvidenceAsync());
    }

    [Fact]
    public async Task An_already_answered_ask_is_not_asked_again()
    {
        var (fake, client) = Make();
        var issue = fake.Add("[game-lab] x - new", GameLabContract.RequestBody(new GameLabContract.RequestInfo(LabKind.New, "", "x", "steam", "1", "d", "1")),
            "game-lab", "lab:needs-evidence");
        issue.Comments.Add("<!-- optimus-vibe:lab-needs-evidence v1 -->\n```json\n{\"files\":[\"{Documents}\\\\a.ini\"]}\n```");
        issue.Comments.Add("<!-- optimus-vibe:lab-evidence v1 -->\n### File: x");
        Assert.Empty(await client.FindNeedsEvidenceAsync());
    }

    [Fact]
    public async Task Drain_starts_the_oldest_queued_request_only_when_nothing_is_running()
    {
        var (fake, client) = Make();
        string Req(string id) => GameLabContract.RequestBody(new GameLabContract.RequestInfo(LabKind.New, "", id, "steam", id, "d", "1"));
        fake.Add("a", Req("1"), "game-lab", "lab:done");
        fake.Add("b", Req("2"), "game-lab", "lab:queued");
        fake.Add("c", Req("3"), "game-lab", "lab:queued");

        fake.ActiveRuns = 1;
        var busy = await client.DrainQueueAsync();
        Assert.Null(busy.Dispatched);
        Assert.Empty(fake.Dispatched);

        fake.ActiveRuns = 0;
        var res = await client.DrainQueueAsync();
        Assert.Equal(2, res.Dispatched);
        Assert.Equal([2], fake.Dispatched);

        // right after a dispatch the same issue isn't started twice
        Assert.Null((await client.DrainQueueAsync()).Dispatched);
        Assert.Single(fake.Dispatched);

        // a researching issue blocks the queue
        var (fake2, client2) = Make();
        fake2.Add("r", Req("9"), "game-lab", "lab:researching");
        fake2.Add("q", Req("8"), "game-lab", "lab:queued");
        Assert.Null((await client2.DrainQueueAsync()).Dispatched);
        Assert.Empty(fake2.Dispatched);

        // nothing queued
        var (_, client3) = Make();
        Assert.Contains("Nothing", (await client3.DrainQueueAsync()).Message);
    }

    [Fact]
    public async Task Drain_does_not_restart_a_request_the_gate_capped_today()
    {
        var fake = new FakeLabGitHub();
        var day = new DateTime(2026, 10, 18, 18, 0, 0, DateTimeKind.Utc);
        var now = day;
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo, now: () => now);
        var body = GameLabContract.RequestBody(new GameLabContract.RequestInfo(LabKind.Refresh, "", "x", "steam", "1", "d", "1"));
        var issue = fake.Add("q", body, "game-lab", "lab:queued");
        issue.Comments.Add("<!-- optimus-vibe:lab-queued v1 2026-10-18 -->\nDaily limit reached");

        var capped = await client.DrainQueueAsync();
        Assert.Null(capped.Dispatched);
        Assert.Contains("Daily limit", capped.Message);
        Assert.Empty(fake.Dispatched);

        now = day.AddDays(1); // a new UTC day: try again
        Assert.Equal(issue.Number, (await client.DrainQueueAsync()).Dispatched);
    }

    [Fact]
    public async Task Run_now_retries_the_oldest_failed_request_but_the_background_drain_does_not()
    {
        var (fake, client) = Make();
        string Req(string id) => GameLabContract.RequestBody(new GameLabContract.RequestInfo(LabKind.New, "", id, "steam", id, "d", "1"));
        fake.Add("a", Req("1"), "game-lab", "lab:failed");
        fake.Add("b", Req("2"), "game-lab", "lab:failed");

        var background = await client.DrainQueueAsync();
        Assert.Null(background.Dispatched);
        Assert.Contains("Nothing", background.Message);

        var res = await client.DrainQueueAsync(retryFailed: true);
        Assert.Equal(1, res.Dispatched);
        Assert.Contains("Retrying #1", res.Message);
        Assert.Empty(fake.Dispatched); // the lab:queued label starts the workflow; no second dispatch
    }

    [Fact]
    public async Task GetResult_returns_the_latest_result_comment()
    {
        var (fake, client) = Make();
        var issue = fake.Add("x", GameLabContract.RequestBody(new GameLabContract.RequestInfo(LabKind.New, "", "x", "steam", "1", "d", "1")), "game-lab");
        Assert.Null(await client.GetResultAsync(issue.Number));
        issue.Comments.Add("<!-- optimus-vibe:lab-result v1 -->\n```json\n{\"gameId\":\"x\",\"presets\":[\"quality\"],\"merged\":false,\"prNumber\":3}\n```");
        issue.Comments.Add("chatter");
        issue.Comments.Add("<!-- optimus-vibe:lab-result v1 -->\n```json\n{\"gameId\":\"x\",\"presets\":[\"quality\"],\"merged\":true,\"prNumber\":3}\n```");
        var r = await client.GetResultAsync(issue.Number);
        Assert.True(r!.Merged);
        Assert.Equal(3, r.PrNumber);
    }

    [Fact]
    public void ApplyResultLocally_pulls_and_checks_the_game_file()
    {
        using var t = new TempDir();
        var paths = new OptimusVibe.Config.AppPaths { ConfigRoot = t.Dir("config"), DataDir = t.Dir("data") };
        var provider = new StubProvider(() => t.Write(@"config\games\borderlands-4.yaml", "id: borderlands-4\n"));
        var merged = new LabResult("borderlands-4", ["quality"], 1, false, "high", 5, true);

        Assert.False(GameLabClient.ApplyResultLocally(merged with { Merged = false }, paths, provider).Ok);
        Assert.Equal(0, provider.Pulls);
        var (ok, msg) = GameLabClient.ApplyResultLocally(merged, paths, provider);
        Assert.True(ok, msg);
        Assert.Equal(1, provider.Pulls);
        Assert.False(GameLabClient.ApplyResultLocally(merged with { GameId = "missing" }, paths, provider).Ok);
    }

    private sealed class StubProvider(Action onPull) : ISyncProvider
    {
        public int Pulls { get; private set; }
        public string Name => "stub";
        public string Describe() => "stub";
        public bool IsReady(out string reason) { reason = ""; return true; }
        public GitResult Pull() { Pulls++; onPull(); return new GitResult(true, "pulled"); }
        public GitResult CommitAndSync(string message, params string[] paths) => new(true, "");
    }
}
