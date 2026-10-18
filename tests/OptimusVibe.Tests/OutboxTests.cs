using System.Net;
using System.Net.Sockets;
using OptimusVibe.Config;
using OptimusVibe.Feedback;
using OptimusVibe.GameLab;
using OptimusVibe.Library;
using OptimusVibe.Outbox;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

public class OfflineDetectorTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return [new HttpRequestException("dns"), true];
        yield return [new HttpRequestException("socket", new SocketException()), true];
        yield return [new HttpRequestException("bad", null, HttpStatusCode.BadGateway), true];
        yield return [new HttpRequestException("bad", null, HttpStatusCode.NotFound), false];
        yield return [new TaskCanceledException("timeout", new TimeoutException()), true];
        yield return [new TimeoutException(), true];
        yield return [new SocketException(), true];
        yield return [new IOException("reset", new SocketException()), true];
        yield return [new IOException("disk full"), false];
        yield return [new GitHubApiException(HttpStatusCode.BadGateway, "x"), true];
        yield return [new GitHubApiException(HttpStatusCode.ServiceUnavailable, "x"), true];
        yield return [new GitHubApiException(HttpStatusCode.GatewayTimeout, "x"), true];
        yield return [new GitHubApiException(null, "Can't reach GitHub", new HttpRequestException("x")), true];
        yield return [new GitHubApiException(null, "didn't respond", new TaskCanceledException()), true];
        yield return [new GitHubApiException(null, "no number"), false];
        yield return [new GitHubApiException(HttpStatusCode.Unauthorized, "x"), false];
        yield return [new GitHubApiException(HttpStatusCode.Forbidden, "x"), false];
        yield return [new GitHubApiException(HttpStatusCode.NotFound, "x"), false];
        yield return [new GitHubApiException(HttpStatusCode.UnprocessableEntity, "x"), false];
        yield return [new GitHubException("wrapped", new HttpRequestException("x")), true];
        yield return [new GitHubException("limit"), false];
        yield return [new InvalidOperationException("x"), false];
        yield return [new UnauthorizedAccessException(), false];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matrix(Exception ex, bool offline) => Assert.Equal(offline, OfflineDetector.IsOffline(ex));

    [Fact]
    public void A_caller_cancel_is_not_offline()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(OfflineDetector.IsOffline(new TaskCanceledException(), cts.Token));
    }
}

public class OutboxStoreTests
{
    private static OutboxItem Report(string title, DateTime at, bool user = false) =>
        OutboxItem.ForBugReport(new BugReportDraft(title, "body", ["bug-report"]), user, at);

    [Fact]
    public void Add_list_update_remove_roundtrip_oldest_first()
    {
        using var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var b = store.Add(Report("b", t.AddMinutes(1)));
        var a = store.Add(Report("a", t));
        var c = store.Add(Report("c", t.AddMinutes(2), user: true));

        Assert.Equal(["a", "b", "c"], store.List().Select(i => i.BugReport.Title));
        Assert.True(store.List()[2].BugReport.AsSignedInUser);
        Assert.Equal(3, store.Count);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(tmp.Path, "outbox"), "*.json").Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp.Path, "outbox"), "*.tmp"));

        var when = t.AddHours(1);
        Assert.True(store.Update(b with { Attempts = 2, LastError = "nope", LastAttemptUtc = when }));
        var again = store.Get(b.Id)!;
        Assert.Equal(2, again.Attempts);
        Assert.Equal("nope", again.LastError);
        Assert.Equal(when, again.LastAttemptUtc);
        Assert.Equal(["a", "b", "c"], store.List().Select(i => i.BugReport.Title));   // order unchanged

        Assert.True(store.Remove(a.Id));
        Assert.False(store.Remove(a.Id));
        Assert.False(store.Update(a));
        Assert.Equal(["b", "c"], store.List().Select(i => i.BugReport.Title));
    }

    [Fact]
    public void Corrupt_files_are_skipped_and_moved()
    {
        using var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        store.Add(Report("ok", DateTime.UtcNow));
        File.WriteAllText(Path.Combine(store.Dir, "1-bad.json"), "{ not json");
        File.WriteAllText(Path.Combine(store.Dir, "2-wrongversion.json"), "{\"v\":9,\"id\":\"x\",\"kind\":\"bugReport\",\"payload\":{}}");

        var items = store.List();

        Assert.Single(items);
        Assert.Equal(2, Directory.GetFiles(store.CorruptDir).Length);
        Assert.Single(Directory.GetFiles(store.Dir, "*.json"));
        Assert.Single(store.List());   // stable on a second read
    }

    [Fact]
    public void Empty_or_missing_folder_lists_nothing()
    {
        using var tmp = new TempDir();
        Assert.Empty(new OutboxStore(tmp.Path).List());
    }

    [Fact]
    public void Payload_never_contains_a_token_and_has_a_version()
    {
        using var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        var item = store.Add(Report("t", DateTime.UtcNow, user: true));
        var json = File.ReadAllText(Directory.GetFiles(store.Dir).Single());
        Assert.Contains("\"v\": 1", json);
        Assert.Contains("\"asSignedInUser\": true", json);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(item.Id, store.List().Single().Id);
    }

    [Fact]
    public void Lab_request_payload_roundtrips_evidence_unchanged()
    {
        using var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        var info = new GameLabContract.RequestInfo(LabKind.Refresh, "g", "Game \"X\"", "steam", "42", "ally-x", "1.0");
        var prepared = new PreparedLabRequest(info, ["part one\n```\nx\n```\n", "part two ünïcode"]);
        store.Add(OutboxItem.ForLabRequest("me/cfg", "dev", prepared));

        var back = store.List().Single();
        Assert.Equal(OutboxKind.LabRequest, back.Kind);
        Assert.Equal("Game \"X\" (refresh)", back.Summary);
        var p = back.LabRequest;
        Assert.Equal(("me/cfg", "dev"), (p.Repo, p.Branch));
        Assert.Equal(prepared.Info, p.ToPrepared().Info);
        Assert.Equal(prepared.EvidenceParts, p.ToPrepared().EvidenceParts);
    }
}

public class OutboxSenderTests
{
    private static (OutboxStore Store, TempDir Tmp) Make(int n)
    {
        var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= n; i++) store.Add(OutboxItem.ForBugReport(new BugReportDraft($"r{i}", "b", []), false, t.AddMinutes(i)));
        return (store, tmp);
    }

    [Fact]
    public async Task Success_removes_items_oldest_first()
    {
        var (store, tmp) = Make(3);
        using var _ = tmp;
        var order = new List<string>();
        var result = await new OutboxSender(store).DrainAsync((item, _) => { order.Add(item.BugReport.Title); return Task.FromResult("url/" + item.BugReport.Title); });

        Assert.Equal(["r1", "r2", "r3"], order);
        Assert.Equal(["url/r1", "url/r2", "url/r3"], result.Sent.Select(s => s.Result));
        Assert.Empty(result.Failed);
        Assert.False(result.StillOffline);
        Assert.Equal(0, result.Remaining);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Offline_stops_the_drain_and_keeps_the_rest()
    {
        var (store, tmp) = Make(3);
        using var _ = tmp;
        var calls = 0;
        var result = await new OutboxSender(store).DrainAsync((item, _) =>
        {
            calls++;
            return item.BugReport.Title == "r2" ? throw new HttpRequestException("no network") : Task.FromResult("ok");
        });

        Assert.Equal(2, calls);
        Assert.Single(result.Sent);
        Assert.True(result.StillOffline);
        Assert.Equal(2, result.Remaining);
        Assert.Equal(["r2", "r3"], store.List().Select(i => i.BugReport.Title));
        Assert.Equal(0, store.List()[0].Attempts);   // being offline is not a failed attempt
    }

    [Fact]
    public async Task Other_errors_are_recorded_and_the_drain_continues()
    {
        var (store, tmp) = Make(3);
        using var _ = tmp;
        var result = await new OutboxSender(store).DrainAsync((item, _) =>
            item.BugReport.Title == "r1" ? throw new GitHubApiException(HttpStatusCode.UnprocessableEntity, "validation failed") : Task.FromResult("ok"));

        Assert.False(result.StillOffline);
        Assert.Equal(2, result.Sent.Count);
        var failed = Assert.Single(result.Failed);
        Assert.Equal("validation failed", failed.Error);
        Assert.Equal(1, result.Remaining);
        var left = store.List().Single();
        Assert.Equal("r1", left.BugReport.Title);
        Assert.Equal(1, left.Attempts);
        Assert.Equal("validation failed", left.LastError);
        Assert.NotNull(left.LastAttemptUtc);
    }

    [Fact]
    public async Task A_second_drain_at_the_same_time_returns_busy()
    {
        var (store, tmp) = Make(1);
        using var _ = tmp;
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var sender = new OutboxSender(store);
        var first = sender.DrainAsync(async (_, _) => { entered.SetResult(); await gate.Task; return "ok"; });
        await entered.Task;

        var second = await new OutboxSender(new OutboxStore(Path.GetDirectoryName(store.Dir))).DrainAsync((_, _) => Task.FromResult("never"));
        Assert.True(second.Busy);
        Assert.Empty(second.Sent);

        gate.SetResult();
        var done = await first;
        Assert.False(done.Busy);
        Assert.Single(done.Sent);
        Assert.False((await sender.DrainAsync((_, _) => Task.FromResult("x"))).Busy);
    }
}

public class OutboxFlowTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Created() => new(HttpStatusCode.Created)
    {
        Content = new StringContent("{\"number\":5,\"html_url\":\"https://github.com/o/r/issues/5\"}"),
    };

    private static readonly BugReportDraft Draft = new("[bug] x", BugReport.Marker + "\nbody", ["bug-report"]);

    [Fact]
    public async Task Limit_is_counted_when_queued_and_not_again_when_sent()
    {
        using var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        for (var i = 0; i < 4; i++) BugReportLimiter.Record(tmp.Path);

        OutboxFlow.QueueBugReport(store, Draft, false, tmp.Path);            // 5th of the day
        Assert.Throws<GitHubException>(() => BugReportLimiter.EnsureAllowed(tmp.Path));   // now used up
        Assert.Throws<GitHubException>(() => OutboxFlow.QueueBugReport(store, Draft, false, tmp.Path));
        Assert.Single(store.List());                                          // the refused one was not queued

        // Draining sends it even though the quota is used up, and does not record another one.
        var handler = new Handler(_ => Created());
        var url = await BugReport.SubmitCoreAsync(Draft, "ghp_usertoken", "builtin", t => new GitHubApi(t, handler), enforceLimit: false, default, tmp.Path);
        Assert.Equal("https://github.com/o/r/issues/5", url);
        Assert.Equal(1, handler.Calls);
        Assert.Throws<GitHubException>(() => BugReportLimiter.EnsureAllowed(tmp.Path));

        // ...whereas the normal path still enforces it.
        await Assert.ThrowsAsync<GitHubException>(() =>
            BugReport.SubmitCoreAsync(Draft, "ghp_usertoken", "builtin", t => new GitHubApi(t, handler), enforceLimit: true, default, tmp.Path));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Normal_path_records_the_limit_on_success()
    {
        using var tmp = new TempDir();
        var handler = new Handler(_ => Created());
        for (var i = 0; i < 5; i++)
        {
            await BugReport.SubmitCoreAsync(Draft, "ghp_usertoken", null, t => new GitHubApi(t, handler), true, default, tmp.Path);
        }
        await Assert.ThrowsAsync<GitHubException>(() =>
            BugReport.SubmitCoreAsync(Draft, "ghp_usertoken", null, t => new GitHubApi(t, handler), true, default, tmp.Path));
    }

    [Fact]
    public async Task Drain_sender_files_reports_without_a_token_in_the_item()
    {
        using var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        OutboxFlow.QueueBugReport(store, Draft, asSignedInUser: true, tmp.Path);
        string? seenToken = "unset";
        var send = OutboxFlow.CreateSender(() => "ghp_now", (_, _) => null, (d, token, _) => { seenToken = token; return Task.FromResult("url"); });

        var result = await new OutboxSender(store).DrainAsync(send);

        Assert.Equal("ghp_now", seenToken);
        Assert.Single(result.Sent);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Offline_lab_request_is_queued_with_its_evidence_then_filed_unchanged_with_dedupe()
    {
        using var p = new LabProfile();
        p.Write(p.Documents, @"My Games\Borderlands 4\GameUserSettings.ini", "[A]\nb=1\n");
        using var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        var offline = new GameLabClient(new GitHubApi("t", new Handler(_ => throw new HttpRequestException("no network"))), FakeLabGitHub.Repo, "main");

        var queued = await OutboxFlow.FileLabRequestAsync(store, offline, LabKind.New, p.Target(), "desktop", new EvidenceCollector(p.Options()));

        Assert.True(queued.WasQueued);
        Assert.Null(queued.Filed);
        var item = store.List().Single();
        Assert.Equal(OutboxKind.LabRequest, item.Kind);
        Assert.Contains("GameUserSettings.ini", string.Concat(item.LabRequest.EvidenceParts));
        Assert.Equal(FakeLabGitHub.Repo, item.LabRequest.Repo);

        // back online
        var fake = new FakeLabGitHub();
        var online = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        var send = OutboxFlow.CreateSender(() => null, (repo, _) => repo == FakeLabGitHub.Repo ? online : null);
        var drained = await new OutboxSender(store).DrainAsync(send);

        Assert.Single(drained.Sent);
        var issue = Assert.Single(fake.Issues);
        Assert.Equal("[game-lab] Borderlands 4 - new", issue.Title);
        Assert.Contains("### File: {Documents}\\My Games\\Borderlands 4\\GameUserSettings.ini", issue.Body);
        Assert.Empty(store.List());

        // the same request again (queued earlier, filed meanwhile from elsewhere): reused, no second issue
        store.Add(OutboxItem.ForLabRequest(FakeLabGitHub.Repo, "main", item.LabRequest.ToPrepared()));
        var again = await new OutboxSender(store).DrainAsync(send);
        Assert.Single(again.Sent);
        Assert.Single(fake.Issues);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Online_lab_request_is_filed_directly_and_nothing_is_queued()
    {
        using var p = new LabProfile();
        using var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);

        var first = await OutboxFlow.FileLabRequestAsync(store, client, LabKind.New, p.Target(), "desktop", new EvidenceCollector(p.Options()));
        var second = await OutboxFlow.FileLabRequestAsync(store, client, LabKind.New, p.Target(), "desktop", new EvidenceCollector(p.Options()));

        Assert.False(first.WasQueued);
        Assert.False(first.Filed!.Reused);
        Assert.True(second.Filed!.Reused);
        Assert.Single(fake.Issues);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Missing_lab_client_keeps_the_item_for_later()
    {
        using var tmp = new TempDir();
        var store = new OutboxStore(tmp.Path);
        var info = new GameLabContract.RequestInfo(LabKind.New, "", "G", "steam", "1", "d", "1.0");
        store.Add(OutboxItem.ForLabRequest("me/cfg", "main", new PreparedLabRequest(info, ["e"])));

        var result = await new OutboxSender(store).DrainAsync(OutboxFlow.CreateSender(() => null, (_, _) => null));

        Assert.Single(result.Failed);
        Assert.Equal(1, store.List().Single().Attempts);
    }
}

public class RequestsMigrationTests
{
    private static GameRequest Req(string store, string id, string? device) => new() { Store = store, StoreId = id, Name = id, Device = device, Requested = "2026-01-01" };
    private static DetectedGame Game(string store, string id) => new() { Store = store, StoreId = id, Name = id };

    [Fact]
    public void Only_own_device_and_installed_here()
    {
        var reqs = new[] { Req("steam", "1", "desktop"), Req("steam", "2", "desktop"), Req("steam", "3", "ally-x"), Req("epic", "E", "DESKTOP"), Req("steam", "5", null) };
        var installed = new[] { Game("steam", "1"), Game("steam", "3"), Game("epic", "e"), Game("steam", "5") };

        var send = RequestsMigration.ToSendFromHere(reqs, "desktop", installed);

        Assert.Equal(["steam:1", "epic:E", "steam:5"], send.Select(r => $"{r.Store}:{r.StoreId}"));
        var withInstalls = RequestsMigration.ToSendFromHereWithInstalls(reqs, "desktop", installed);
        Assert.Equal(3, withInstalls.Count);
        Assert.Equal("e", withInstalls[1].Install.StoreId);
    }

    [Fact]
    public void Target_uses_the_install_when_known()
    {
        var r = Req("steam", "1", "desktop");
        r.InstallDir = @"C:\old";
        var t = RequestsMigration.ToTarget(r, new DetectedGame { Store = "steam", StoreId = "1", Name = "One", InstallDir = @"D:\new", Exes = ["a.exe"] });
        Assert.Equal(@"D:\new", t.InstallDir);
        Assert.Equal("1", t.StoreId);
        Assert.Equal(["a.exe"], t.Exes);
    }

    [Fact]
    public void RemoveFiled_removes_only_that_entry_from_requests_yaml()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("config");
        File.WriteAllText(Path.Combine(root, "requests.yaml"),
            "# header\nrequests:\n- store: steam\n  storeId: '1'\n  name: One\n  device: desktop\n- store: steam\n  storeId: '1'\n  name: One\n  device: ally-x\n- store: steam\n  storeId: '2'\n  name: Two\n  device: desktop\n");
        var store = new ConfigStore(new AppPaths { ConfigRoot = root, DataDir = tmp.Dir("data") });

        Assert.True(RequestsMigration.RemoveFiled(store, Req("steam", "1", "desktop")));
        Assert.False(RequestsMigration.RemoveFiled(store, Req("steam", "1", "desktop")));

        var left = LibraryService.LoadRequests(store).Requests;
        Assert.Equal(["steam:1:ally-x", "steam:2:desktop"], left.Select(r => $"{r.Store}:{r.StoreId}:{r.Device}"));
        Assert.StartsWith("# header", File.ReadAllText(Path.Combine(root, "requests.yaml")));
    }
}

public class QueueSummaryTests
{
    private static LabIssue Issue(LabStatus s, bool open = true) => new(1, "t", "u", open, [], s, null, null, null);
    private static GameRequest Req(string device) => new() { Store = "steam", StoreId = Guid.NewGuid().ToString("N"), Name = "n", Device = device };
    private static OutboxItem Lab() => OutboxItem.ForLabRequest("o/r", "main", new PreparedLabRequest(new GameLabContract.RequestInfo(LabKind.New, "", "G", "steam", "1", "d", "1"), ["e"]));
    private static OutboxItem Report() => OutboxItem.ForBugReport(new BugReportDraft("t", "b", []), false);

    [Fact]
    public void Full_text_omits_nothing_and_counts_only_active_lab_statuses()
    {
        var s = QueueSummary.Build(
            [Issue(LabStatus.Queued), Issue(LabStatus.Done), Issue(LabStatus.Review), Issue(LabStatus.Failed), Issue(LabStatus.Researching, open: false)],
            [Lab(), Report()],
            [Req("ally-x"), Req("ally-x"), Req("desktop")], "desktop", labReady: true);

        Assert.Equal(1, s.Lab);
        Assert.Equal(1, s.WaitingToSend);
        Assert.Equal("1 in the game lab · 1 waiting to send · 2 waiting on ally-x", s.Text);
        Assert.Equal("1 in the game lab · 1 waiting to send · 2 waiting on Ally X", s.Describe(d => d == "ally-x" ? "Ally X" : d));
    }

    [Fact]
    public void Zero_parts_are_omitted_and_empty_is_empty()
    {
        Assert.Equal("", QueueSummary.Build(null, [], [], "desktop", true).Text);
        Assert.True(QueueSummary.Build(null, [], [], "desktop", true).IsEmpty);
        Assert.Equal("3 in the game lab", QueueSummary.Build([Issue(LabStatus.Queued), Issue(LabStatus.Researching), Issue(LabStatus.NeedsEvidence)], [], [], "d", true).Text);
        Assert.Equal("1 waiting on ally-x", QueueSummary.Build(null, [], [Req("ally-x")], "d", true).Text);
    }

    [Fact]
    public void Several_devices_are_listed_largest_first()
    {
        var s = QueueSummary.Build(null, [], [Req("b"), Req("a"), Req("b"), Req("c")], "me", true);
        Assert.Equal("2 waiting on b · 1 waiting on a · 1 waiting on c", s.Text);
    }

    [Fact]
    public void Without_a_lab_the_local_queue_reads_as_today()
    {
        Assert.Equal("1 game queued for Claude", QueueSummary.Build(null, [], [Req("d")], "d", false).Text);
        var s = QueueSummary.Build(null, [], [Req("d"), Req("other")], "d", false);
        Assert.Equal(2, s.LocalOnly);
        Assert.Equal("2 games queued for Claude", s.Text);
        Assert.Empty(s.WaitingOnOtherDevices);
    }
}

public class LegacyNoDeviceRequestTests
{
    private static GameRequest Legacy(string id) => new() { Store = "steam", StoreId = id, Name = id, Device = null };

    [Fact]
    public void Legacy_entry_without_device_migrates_from_the_first_device_that_has_the_game()
    {
        var installed = new[] { new DetectedGame { Store = "steam", StoreId = "1", Name = "One" } };
        var reqs = new[] { Legacy("1"), Legacy("2") };

        Assert.Equal(["1"], RequestsMigration.ToSendFromHere(reqs, "ally-x", installed).Select(r => r.StoreId));
        Assert.Empty(RequestsMigration.ToSendFromHere(reqs, "ally-x", []));
    }

    [Fact]
    public void RemoveFiled_for_a_legacy_entry_leaves_other_devices_entries()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("config");
        File.WriteAllText(Path.Combine(root, "requests.yaml"),
            "requests:\n- store: steam\n  storeId: '1'\n  name: One\n- store: steam\n  storeId: '1'\n  name: One\n  device: ally-x\n");
        var store = new ConfigStore(new AppPaths { ConfigRoot = root, DataDir = tmp.Dir("data") });

        Assert.True(RequestsMigration.RemoveFiled(store, Legacy("1")));
        Assert.Equal(["ally-x"], LibraryService.LoadRequests(store).Requests.Select(r => r.Device));
    }

    [Fact]
    public void Summary_counts_a_legacy_entry_installed_here_as_mine_and_otherwise_as_another_device()
    {
        var installed = new[] { new DetectedGame { Store = "steam", StoreId = "1", Name = "One" } };
        Assert.True(QueueSummary.Build(null, [], [Legacy("1")], "desktop", true, installed).IsEmpty);
        Assert.Equal("1 waiting on another device", QueueSummary.Build(null, [], [Legacy("2")], "desktop", true, installed).Text);
        Assert.Equal("1 waiting on another device", QueueSummary.Build(null, [], [Legacy("1")], "desktop", true).Text);
    }
}
