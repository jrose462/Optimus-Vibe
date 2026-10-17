using System.Net;
using System.Text.Json.Nodes;
using OptimusVibe.Feedback;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

public sealed class BugReportTests
{
    private const string Profile = @"C:\Users\jdoe";

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("gho_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghu_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghs_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghr_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijkl_mnopqrstuvwxyz0123456789")]
    public void Sanitize_RedactsTokens(string token)
    {
        var s = BugReport.Sanitize($"token is {token} ok");
        Assert.DoesNotContain(token, s);
        Assert.Contains("[REDACTED-TOKEN]", s);
    }

    [Fact]
    public void Sanitize_RedactsBearerAndUserFolder()
    {
        var s = BugReport.Sanitize("Authorization: Bearer abcdef1234567890\nbearer zzzzzzzzzzzz\nC:\\Users\\jdoe\\AppData\\x.log and C:\\Users\\other\\y", Profile);
        Assert.DoesNotContain("abcdef1234567890", s);
        Assert.DoesNotContain("zzzzzzzzzzzz", s);
        Assert.DoesNotContain("jdoe", s);
        Assert.DoesNotContain("other", s);
        Assert.Contains(@"%USERPROFILE%\AppData\x.log", s);
    }

    [Fact]
    public void Sanitize_RedactsUserNameMachineEmailIpAndSteamIds()
    {
        var s = BugReport.Sanitize(
            "user jdoe on PC-JDOE-9 mail jdoe@example.com ip 192.168.1.20 and fe80::1ff:fe23:4567:890a and 2001:db8:0:0:0:0:0:1 " +
            "id 76561198012345678 path D:\\Steam\\userdata\\12345678\\config\\localconfig.vdf ver 10.0.26100.1 Foo::Bar",
            Profile, userName: "jdoe", machineName: "PC-JDOE-9");
        Assert.DoesNotContain("jdoe", s);
        Assert.DoesNotContain("PC-JDOE-9", s);
        Assert.DoesNotContain("example.com", s);
        Assert.DoesNotContain("192.168.1.20", s);
        Assert.DoesNotContain("fe80", s);
        Assert.DoesNotContain("2001:db8", s);
        Assert.DoesNotContain("76561198012345678", s);
        Assert.DoesNotContain("12345678", s);
        Assert.Contains("[EMAIL]", s);
        Assert.Contains("[MACHINE]", s);
        Assert.Contains("10.0.26100.1", s);
        Assert.Contains("Foo::Bar", s);
    }

    private static BugReportContext Ctx(IReadOnlyList<string>? log = null) => new()
    {
        Source = "optimus-vibe CLI",
        AppVersion = "0.3.0",
        OsVersion = "Windows 11",
        DeviceName = "ally-x",
        DeviceReason = "auto",
        Manufacturer = "ASUS",
        Model = "RC72LA",
        Cpu = "Z1 Extreme",
        Gpus = ["AMD Radeon"],
        SyncProvider = "github",
        ConfigRepo = "me/cfg (main)",
        GameId = "cyberpunk",
        Preset = "performance",
        UserProfile = Profile,
        LogLines = log ?? [],
    };

    [Fact]
    public void Build_DraftFormat()
    {
        var log = Enumerable.Range(1, 3).Select(i => $"line {i} C:\\Users\\jdoe\\f ghp_abcdefghijklmnopqrstuvwxyz0123456789").ToList();
        var d = BugReport.Build("It crashed on apply\nsecond line", Ctx(log));
        Assert.Equal("[bug] It crashed on apply", d.Title);
        Assert.Equal(["bug-report"], d.Labels);
        Assert.Contains("0.3.0", d.Body);
        Assert.Contains("Windows 11", d.Body);
        Assert.Contains("ally-x", d.Body);
        Assert.Contains("RC72LA", d.Body);
        Assert.Contains("me/cfg (main)", d.Body);
        Assert.Contains("cyberpunk (preset performance)", d.Body);
        Assert.Contains("<details>", d.Body);
        Assert.Contains("line 3", d.Body);
        Assert.DoesNotContain("ghp_", d.Body);
        Assert.DoesNotContain("jdoe", d.Body);
    }

    [Fact]
    public void Build_TitleTruncatedTo80()
    {
        var d = BugReport.Build(new string('x', 200), Ctx());
        Assert.True(d.Title.Length <= "[bug] ".Length + BugReport.MaxTitleText);
    }

    [Fact]
    public void Build_EmptyDescription_Throws() =>
        Assert.Throws<ArgumentException>(() => BugReport.Build("  ", Ctx()));

    [Fact]
    public async Task Submit_RetriesWithoutLabels_WhenLabelsRefused()
    {
        var handler = new ScriptedHandler(
            _ => FakeGitHub.Status(HttpStatusCode.UnprocessableEntity, "Validation Failed: labels"),
            _ => FakeGitHub.Json(new JsonObject { ["number"] = 7, ["html_url"] = "https://github.com/jrose462/Optimus-Vibe/issues/7" }, HttpStatusCode.Created));
        var api = new GitHubApi("tok", handler);
        var url = await BugReport.SubmitAsync(BugReport.Build("boom", Ctx()), api, default);
        Assert.Equal("https://github.com/jrose462/Optimus-Vibe/issues/7", url);
        Assert.Equal(2, handler.Uris.Count);
        Assert.Contains("repos/jrose462/Optimus-Vibe/issues", handler.Uris[0].AbsolutePath);
        Assert.Contains("\"labels\"", handler.Bodies[0]);
        Assert.DoesNotContain("\"labels\"", handler.Bodies[1]);
    }
}
