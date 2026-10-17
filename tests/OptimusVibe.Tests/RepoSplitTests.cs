using System.Text.Json.Nodes;
using OptimusVibe.Config;
using OptimusVibe.Feedback;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

/// <summary>The app repo (public, code only) vs the user's own config repo: migration, defaults, client id, no-sign-in reporting.</summary>
public sealed class RepoSplitTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private string Data => _tmp.Dir("data");
    private string SettingsPath => Path.Combine(Data, "settings.json");

    [Fact]
    public void NewSettings_HaveNoDefaultConfigRepo()
    {
        var s = new AppSettings();
        Assert.Null(s.GitHubRepo);
        Assert.Equal("git", s.SyncProvider);
    }

    [Theory]
    [InlineData("jrose462/Optimus-Vibe")]
    [InlineData("jrose462/GameProfiles")]
    [InlineData("JROSE462/optimus-vibe")]
    public void AppRepoAsConfigRepo_IsClearedOnce(string repo)
    {
        File.WriteAllText(SettingsPath, $$"""{"syncProvider":"github","gitHubRepo":"{{repo}}","gitHubBranch":"main","autoSync":false}""");
        Assert.True(LegacyMigration.MigrateRepoSplit(Data, out var notes));
        Assert.NotEmpty(notes);

        var s = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Null((string?)s["gitHubRepo"]);
        Assert.Equal("git", (string?)s["syncProvider"]);
        Assert.Equal(false, (bool?)s["autoSync"]);
        Assert.Null(AppPaths.LoadSettings(Data).GitHubRepo);

        // runs once: a repo picked later is never touched
        File.WriteAllText(SettingsPath, """{"gitHubRepo":"jrose462/Optimus-Vibe"}""");
        Assert.False(LegacyMigration.MigrateRepoSplit(Data, out _));
        Assert.Equal("jrose462/Optimus-Vibe", AppPaths.LoadSettings(Data).GitHubRepo);
    }

    [Fact]
    public void OwnConfigRepo_IsLeftAlone()
    {
        File.WriteAllText(SettingsPath, """{"syncProvider":"github","gitHubRepo":"me/my-config"}""");
        Assert.False(LegacyMigration.MigrateRepoSplit(Data, out _));
        var s = AppPaths.LoadSettings(Data);
        Assert.Equal("me/my-config", s.GitHubRepo);
        Assert.Equal("github", s.SyncProvider);
    }

    [Fact]
    public void NoSettingsFile_IsFine()
    {
        Assert.False(LegacyMigration.MigrateRepoSplit(Path.Combine(Data, "fresh"), out _));
    }

    [Fact]
    public void ClientId_Precedence_SettingsThenEnvThenBaked()
    {
        var old = Environment.GetEnvironmentVariable("OPTIMUSVIBE_GITHUB_CLIENT_ID");
        try
        {
            Environment.SetEnvironmentVariable("OPTIMUSVIBE_GITHUB_CLIENT_ID", "from-env");
            Assert.Equal("from-settings", GitHubAuth.ResolveClientId(new AppSettings { GitHubClientId = " from-settings " }));
            Assert.Equal("from-env", GitHubAuth.ResolveClientId(new AppSettings { GitHubClientId = "  " }));
            Environment.SetEnvironmentVariable("OPTIMUSVIBE_GITHUB_CLIENT_ID", null);
            Assert.Equal(BuildInfo.GitHubClientId, GitHubAuth.ResolveClientId(new AppSettings()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPTIMUSVIBE_GITHUB_CLIENT_ID", old);
        }
        Assert.Equal("a", GitHubAuth.FirstNonBlank(null, " ", "a", "b"));
        Assert.Equal("", GitHubAuth.FirstNonBlank(null, ""));
    }

    // ------------------------------------------------------------ bug reports without sign-in

    private static BugReportDraft Draft(string body) => new("[bug] boom", body, ["bug-report"]);

    [Fact]
    public void NewIssueUrl_ShortReport_IsPrefilledAndComplete()
    {
        var url = BugReport.BuildNewIssueUrl(Draft("## What happened\n\nIt broke & burned"), out var truncated);
        Assert.False(truncated);
        Assert.StartsWith("https://github.com/jrose462/Optimus-Vibe/issues/new?", url);
        Assert.Contains("title=%5Bbug%5D%20boom", url);
        Assert.Contains("labels=bug-report", url);
        Assert.Contains("It%20broke%20%26%20burned", url);
    }

    [Fact]
    public void NewIssueUrl_LongReport_IsTruncatedToASafeLength()
    {
        var body = string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"log line {i} with some text ä ö ü"));
        var url = BugReport.BuildNewIssueUrl(Draft(body), out var truncated);
        Assert.True(truncated);
        Assert.True(url.Length < BugReport.MaxPrefillQueryChars + 200, $"{url.Length} chars");
        Assert.Contains("shortened", Uri.UnescapeDataString(url));
        Assert.Contains("log%20line%200%20", url);
    }

    [Fact]
    public async Task DevBuild_HasNoBuiltInToken_AndSubmitSaysSo()
    {
        // The test build doesn't set BUG_REPORT_TOKEN. (If a developer has it set in the environment, there's nothing to assert.)
        if (BugReport.HasBuiltInToken) return;
        var ex = await Assert.ThrowsAsync<GitHubException>(() => BugReport.SubmitAsync(Draft("x"), null, default, Data));
        Assert.Contains("directly", ex.Message);
    }

    [Fact]
    public void BakedToken_RoundTrips_WhenTheBuildHadOne()
    {
        // Release pipeline check: when BUG_REPORT_TOKEN was set for the build, the XOR-obfuscated copy decodes to it.
        var fromEnv = Environment.GetEnvironmentVariable("BUG_REPORT_TOKEN")?.Trim();
        if (string.IsNullOrEmpty(fromEnv)) return;
        Assert.True(BugReport.HasBuiltInToken);
        Assert.Equal(fromEnv, BugReportToken.Get());
        // the plain token must not sit next to its key in the generated arrays
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(fromEnv), Feedback.BakedSecrets.BugReportData);
    }

    [Fact]
    public void Identity_UserTokenWins_ThenBuiltIn_ThenBrowser()
    {
        Assert.Equal(BugReport.ReportIdentity.User, BugReport.ResolveIdentity("gho_x"));
        Assert.Equal(BugReport.HasBuiltInToken ? BugReport.ReportIdentity.BuiltIn : BugReport.ReportIdentity.Browser, BugReport.ResolveIdentity(null));
        Assert.Equal("Filed as @octo", BugReport.DescribeIdentity(BugReport.ReportIdentity.User, "octo"));
        Assert.Equal("Filed anonymously via Optimus-Vibe", BugReport.DescribeIdentity(BugReport.ReportIdentity.BuiltIn, null));
    }

    [Fact]
    public async Task SignedInUserToken_IsUsedToFileTheIssue()
    {
        string? auth = null;
        var handler = new ScriptedHandler(req =>
        {
            auth = req.Headers.Authorization?.Parameter;
            return FakeGitHub.Json(new JsonObject { ["number"] = 1, ["html_url"] = "https://github.com/jrose462/Optimus-Vibe/issues/1" }, System.Net.HttpStatusCode.Created);
        });
        var url = await BugReport.SubmitAsync(Draft("x"), new GitHubApi("gho_usertoken", handler), default, signedIn: true);
        Assert.EndsWith("/issues/1", url);
        Assert.Equal("gho_usertoken", auth);
    }

    [Fact]
    public void Report_StartsWithTriageMarker()
    {
        var d = BugReport.Build("it broke", new BugReportContext { UserProfile = null });
        Assert.StartsWith("<!-- optimus-vibe:report v1 -->", d.Body);
        Assert.Contains("optimus-vibe%3Areport%20v1", BugReport.BuildNewIssueUrl(d, out _));
    }

    [Fact]
    public void RateLimit_AllowsFivePerRollingDay()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < BugReportLimiter.MaxPerDay; i++)
        {
            BugReportLimiter.EnsureAllowed(Data, t0.AddMinutes(i));
            BugReportLimiter.Record(Data, t0.AddMinutes(i));
        }
        var ex = Assert.Throws<GitHubException>(() => BugReportLimiter.EnsureAllowed(Data, t0.AddHours(1)));
        Assert.Contains("5 reports", ex.Message);
        BugReportLimiter.EnsureAllowed(Data, t0.AddHours(25)); // a day later: fine again
    }
}
