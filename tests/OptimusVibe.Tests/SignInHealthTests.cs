using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OptimusVibe.Feedback;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

/// <summary>Keeps tests that go through the machine-wide <see cref="SignInHealth.Default"/> (the game lab's 401 mapping) out of the real data dir.</summary>
internal static class SignInHealthTestSetup
{
    [ModuleInitializer]
    internal static void Init() =>
        SignInHealth.Default = new SignInHealth(Path.Combine(Path.GetTempPath(), "OptimusVibeTests", "signin-default-" + Guid.NewGuid().ToString("N")));
}

/// <summary>github-signin.json: saved / ok / rejected transitions, the write throttle, one event per rejection streak, the summary line, bug report row.</summary>
public sealed class SignInHealthTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 21, 18, 2, 0, DateTimeKind.Utc);

    private readonly TempDir _tmp = new();
    private DateTime _now = T0;
    private bool _hasToken = true;

    public void Dispose() => _tmp.Dispose();

    private SignInHealth Make(string? dir = null) => new(dir ?? _tmp.Path, () => _now, () => _hasToken);

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private JsonNode Disk() => JsonNode.Parse(File.ReadAllText(SignInHealth.FileFor(_tmp.Path)))!;

    private static DateTime Utc(JsonNode? n) => DateTime.Parse(n!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Fact]
    public void Saved_RecordsWhenAndHow_AndClearsARejection()
    {
        var h = Make();
        h.Saved(SignInHealth.ViaDevice);
        Assert.Equal(T0, Utc(Disk()["savedUtc"]));
        Assert.Equal("device sign-in", Disk()["savedVia"]!.GetValue<string>());

        _now = T0.AddHours(1);
        h.Reject("AB12:34CD");
        Assert.True(h.Rejecting);

        _now = T0.AddHours(2);
        h.Saved(SignInHealth.ViaToken);
        Assert.False(h.Rejecting);
        Assert.Null(Disk()["rejectedSinceUtc"]);
        Assert.Equal("token", Disk()["savedVia"]!.GetValue<string>());
        Assert.Equal(T0.AddHours(2), Utc(Disk()["savedUtc"]));
    }

    [Fact]
    public void Ok_SetsLastOk_AndThrottlesDiskWrites()
    {
        var h = Make();
        h.Ok();
        Assert.Equal(T0, Utc(Disk()["lastOkUtc"]));

        _now = T0.AddMinutes(5);
        h.Ok();
        Assert.Equal(T0, Utc(Disk()["lastOkUtc"])); // inside the 10 minutes: memory only
        Assert.Contains($"last worked {Local(T0.AddMinutes(5))}", h.Summary());

        _now = T0.AddMinutes(11);
        h.Ok();
        Assert.Equal(T0.AddMinutes(11), Utc(Disk()["lastOkUtc"]));
    }

    [Fact]
    public void Ok_EndingARejection_WritesAtOnce()
    {
        var h = Make();
        h.Ok();
        _now = T0.AddMinutes(1);
        h.Reject("AB12");
        Assert.NotNull(Disk()["rejectedSinceUtc"]);

        _now = T0.AddMinutes(2);
        h.Ok();
        Assert.False(h.Rejecting);
        Assert.Null(Disk()["rejectedSinceUtc"]);
        Assert.Equal(T0.AddMinutes(2), Utc(Disk()["lastOkUtc"]));
    }

    [Fact]
    public void Reject_StartsOneStreak_OneEventPerStreak_AndKeepsTheRequestId()
    {
        var h = Make();
        var started = 0;
        h.StreakStarted += () => started++;
        h.Saved(SignInHealth.ViaDevice);

        _now = T0.AddHours(3);
        h.Reject("FIRST:1");
        h.Reject("SECOND:2");
        h.Reject(null);
        Assert.Equal(1, started);
        Assert.Equal(T0.AddHours(3), Utc(Disk()["rejectedSinceUtc"]));
        Assert.Equal("FIRST:1", Disk()["lastRejectedRequestId"]!.GetValue<string>()); // later ids only update memory
        Assert.Contains("SECOND:2", h.Summary());

        h.Ok(); // streak over
        h.Reject("THIRD:3");
        Assert.Equal(2, started);
    }

    [Fact]
    public void Reject_AfterARestart_IsStillTheSameStreak()
    {
        var first = Make();
        first.Reject("AB12");

        var again = Make();
        var started = 0;
        again.StreakStarted += () => started++;
        Assert.True(again.Rejecting);
        again.Reject("CD34");
        Assert.Equal(0, started);
    }

    [Fact]
    public void Reject_DropsAnythingThatIsNotARequestId()
    {
        var h = Make();
        h.Reject("not an id; DROP");
        Assert.Contains("request id unknown", h.Summary());
    }

    [Fact]
    public void Describe_NotSignedIn_Unknown_Accepted_Rejected()
    {
        _hasToken = false;
        Assert.Equal("not signed in", Make().Summary());

        _hasToken = true;
        Assert.Equal("unknown (signed in before this was recorded)", Make().Summary());

        var h = Make();
        h.Saved(SignInHealth.ViaDevice);
        _now = T0.AddHours(4).AddMinutes(35);
        h.Ok();
        Assert.Equal($"accepted: saved {Local(T0)} via device sign-in, last worked {Local(T0.AddHours(4).AddMinutes(35))}", h.Summary());

        _now = T0.AddHours(20);
        h.Reject("AB12:34CD");
        Assert.Equal($"rejected since {Local(T0.AddHours(20))} (request id AB12:34CD): saved {Local(T0)} via device sign-in, " +
                     $"last worked {Local(T0.AddHours(4).AddMinutes(35))}", h.Summary());
    }

    [Fact]
    public void Describe_OkWithoutASavedRecord_SaysSoInsteadOfInventingADate()
    {
        var h = Make();
        h.Ok();
        Assert.Equal($"accepted: save time not recorded, last worked {Local(T0)}", h.Summary());
    }

    [Fact]
    public void IoFailures_AreSwallowed()
    {
        var blocker = _tmp.Write("not-a-dir", "x");
        var h = Make(blocker); // the "data dir" is a file: creating github-signin.json under it fails
        h.Saved(SignInHealth.ViaToken);
        h.Ok();
        h.Reject("AB12");
        Assert.True(h.Rejecting); // still tracked in memory

        _tmp.Write("corrupt/github-signin.json", "{ not json");
        var c = Make(_tmp.Dir("corrupt"));
        Assert.False(c.Rejecting);
        c.Reject("AB12");
        Assert.True(c.Rejecting);
    }

    [Fact]
    public void NeverStoresTheToken()
    {
        const string token = "ghp_SecretTokenText0123456789abcdef";
        var h = Make();
        var api = new GitHubApi(token, new ScriptedHandler(
            _ => FakeGitHub.Json(new JsonObject { ["login"] = "someone" }),
            _ => FakeGitHub.Status(HttpStatusCode.Unauthorized, "Bad credentials")), h);
        api.GetLoginAsync().GetAwaiter().GetResult();
        Assert.Throws<GitHubApiException>(() => api.GetLoginAsync().GetAwaiter().GetResult());

        Assert.DoesNotContain("SecretTokenText", File.ReadAllText(SignInHealth.FileFor(_tmp.Path)));
        Assert.DoesNotContain("SecretTokenText", h.Summary());
        Assert.DoesNotContain("ghp_", h.Summary());
    }

    // ---------------------------------------------------------------- GitHubApi

    [Fact]
    public void Api_401_RecordsTheRequestId_AndASuccessClearsIt()
    {
        var h = Make();
        var handler = new ScriptedHandler(
            _ =>
            {
                var r = FakeGitHub.Status(HttpStatusCode.Unauthorized, "Bad credentials");
                r.Headers.Add("X-GitHub-Request-Id", "7F3A:21B4:0C9D:5E88:6540A1B2");
                return r;
            },
            _ => FakeGitHub.Status(HttpStatusCode.Unauthorized, "Bad credentials"),
            _ => FakeGitHub.Json(new JsonObject { ["login"] = "someone" }));
        var api = new GitHubApi("t", handler, h);
        var started = 0;
        h.StreakStarted += () => started++;

        var ex = Assert.Throws<GitHubApiException>(() => api.GetLoginAsync().GetAwaiter().GetResult());
        Assert.Contains("Sign in to GitHub again", ex.Message);
        Assert.True(h.Rejecting);
        Assert.Equal("7F3A:21B4:0C9D:5E88:6540A1B2", Disk()["lastRejectedRequestId"]!.GetValue<string>());

        Assert.Throws<GitHubApiException>(() => api.GetLoginAsync().GetAwaiter().GetResult());
        Assert.Equal(1, started); // the second 401 is the same streak

        api.GetLoginAsync().GetAwaiter().GetResult();
        Assert.False(h.Rejecting);
        Assert.Null(Disk()["rejectedSinceUtc"]);
    }

    [Fact]
    public void Api_401_WithoutTheHeader_StillStartsAStreak()
    {
        var h = Make();
        var api = new GitHubApi("t", new ScriptedHandler(_ => FakeGitHub.Status(HttpStatusCode.Unauthorized, "Bad credentials")), h);
        Assert.Throws<GitHubApiException>(() => api.GetLoginAsync().GetAwaiter().GetResult());
        Assert.True(h.Rejecting);
        Assert.Contains("request id unknown", h.Summary());
    }

    [Fact]
    public void Api_NotTheSavedToken_LeavesTheHealthAlone()
    {
        var h = Make();
        var api = new GitHubApi("t", new ScriptedHandler(_ => FakeGitHub.Status(HttpStatusCode.Unauthorized, "Bad credentials"))); // no health: a candidate token
        Assert.Throws<GitHubApiException>(() => api.GetLoginAsync().GetAwaiter().GetResult());
        Assert.False(h.Rejecting);
        Assert.False(File.Exists(SignInHealth.FileFor(_tmp.Path)));
    }

    [Fact]
    public void Api_OtherErrors_AreNotRejections()
    {
        var h = Make();
        var api = new GitHubApi("t", new ScriptedHandler(_ => FakeGitHub.Status(HttpStatusCode.Forbidden, "Resource not accessible")), h);
        Assert.Throws<GitHubApiException>(() => api.GetLoginAsync().GetAwaiter().GetResult());
        Assert.False(h.Rejecting);
    }

    // ---------------------------------------------------------------- bug report

    [Fact]
    public void BugReport_HasAGitHubSignInRow()
    {
        var ctx = new BugReportContext { SignIn = "rejected since 2026-10-22 08:14 (request id AB12): saved 2026-10-21 18:02 via device sign-in", UserProfile = "" };
        var body = BugReport.Build("It stopped syncing", ctx).Body;
        Assert.Contains("| GitHub sign-in | rejected since 2026-10-22 08:14 (request id AB12): saved 2026-10-21 18:02 via device sign-in |", body);
    }

    [Fact]
    public void BugReport_Collect_FillsTheRowFromTheDataDir()
    {
        var h = Make();
        h.Saved(SignInHealth.ViaToken);
        var ctx = BugReportContext.Collect("test", null, null, dataDir: _tmp.Path);
        Assert.False(string.IsNullOrWhiteSpace(ctx.SignIn));
        Assert.Contains("| GitHub sign-in |", BugReport.Build("x", ctx).Body);
    }
}
