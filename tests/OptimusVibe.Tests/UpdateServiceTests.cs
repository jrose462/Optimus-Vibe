using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OptimusVibe.Sync;
using OptimusVibe.Updates;

namespace OptimusVibe.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.4", "1.2.3", 1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("v0.3.0", "0.3.0", 0)]
    [InlineData("0.3.0", "0.3.0+abc123", 0)]
    [InlineData("1.0.0", "1.0.0-beta.1", 1)]
    [InlineData("0.2.9", "0.3.0", -1)]
    public void Compare_Semver(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(AppVersion.Compare(a, b)));

    [Fact]
    public void Current_HasNoBuildMetadata()
    {
        Assert.DoesNotContain("+", AppVersion.Current);
        Assert.True(AppVersion.TryParse(AppVersion.Current, out _));
        Assert.Equal("jrose462/Optimus-Vibe", AppRepo.FullName);
    }

    private static string Release(string tag, params (string Name, int Size)[] assets)
    {
        var arr = new JsonArray();
        var id = 1;
        foreach (var (n, s) in assets)
            arr.Add(new JsonObject { ["name"] = n, ["size"] = s, ["url"] = $"https://api.github.com/repos/jrose462/Optimus-Vibe/releases/assets/{id++}", ["browser_download_url"] = "https://github.com/x/" + n });
        return new JsonObject { ["tag_name"] = tag, ["name"] = tag, ["body"] = "notes", ["html_url"] = "https://github.com/jrose462/Optimus-Vibe/releases/" + tag, ["assets"] = arr }.ToJsonString();
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Ok(string json) =>
        _ => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Func<HttpRequestMessage, HttpResponseMessage> Code(HttpStatusCode c) =>
        _ => new(c) { Content = new StringContent("{\"message\":\"Not Found\"}", Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("v0.4.0", "0.3.0", UpdateStatus.UpdateAvailable)]
    [InlineData("v0.3.0", "0.3.0", UpdateStatus.UpToDate)]
    [InlineData("v0.2.0", "0.3.0", UpdateStatus.UpToDate)]
    public async Task Check_ComparesVersions(string tag, string current, UpdateStatus expected)
    {
        var h = new ScriptedHandler(Ok(Release(tag, ("optimus-vibe.exe", 5))));
        var r = await UpdateService.CheckAsync(h, current, default);
        Assert.Equal(expected, r.Status);
        Assert.Equal(expected == UpdateStatus.UpdateAvailable, r.UpdateAvailable);
        Assert.EndsWith("repos/jrose462/Optimus-Vibe/releases/latest", h.Uris[0].AbsolutePath.TrimStart('/'));
    }

    [Fact]
    public async Task Check_404_IsNoReleasesNotException()
    {
        var r = await UpdateService.CheckAsync(new ScriptedHandler(Code(HttpStatusCode.NotFound)), "0.3.0", default);
        Assert.Equal(UpdateStatus.NoReleases, r.Status);
        Assert.DoesNotContain("sign in", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(r.Update);
    }

    [Fact]
    public async Task Check_RateLimited_IsFriendlyFailure_AndSendsNoAuthorization()
    {
        bool? hadAuth = null;
        var h = new ScriptedHandler(req =>
        {
            hadAuth = req.Headers.Authorization is not null;
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"API rate limit exceeded for 1.2.3.4.\"}"),
            };
            resp.Headers.Add("x-ratelimit-remaining", "0");
            return resp;
        });
        var r = await UpdateService.CheckAsync(h, "0.3.0", default);
        Assert.Equal(UpdateStatus.Failed, r.Status);
        Assert.Contains("limiting requests", r.Message);
        Assert.False(hadAuth);
        Assert.Null(r.Update);
    }

    private static UpdateInfo Info(string exeName, byte[] content, string? shaText, int? sizeOverride = null)
    {
        var assets = new List<(string, int)> { (exeName, sizeOverride ?? content.Length) };
        if (shaText is not null) assets.Add((exeName + ".sha256", shaText.Length));
        return UpdateService.ParseRelease(JsonNode.Parse(Release("v0.4.0", assets.ToArray()))!, "0.3.0")!;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static Func<HttpRequestMessage, HttpResponseMessage> Bytes(byte[] b, Action<HttpRequestMessage>? inspect = null) => r =>
    {
        inspect?.Invoke(r);
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
    };

    [Fact]
    public async Task Download_StagesNewFile_WithOctetStreamAccept()
    {
        var content = Encoding.ASCII.GetBytes("new exe bytes");
        var exe = _tmp.Write("optimus-vibe.exe", "old");
        var info = Info("optimus-vibe.exe", content, "x");
        string? accept = null;
        var h = new ScriptedHandler(Ok($"{Sha(content)}  optimus-vibe.exe\n"), Bytes(content, r => accept = r.Headers.Accept.ToString()));
        var staged = await UpdateService.DownloadAndStageAsync(h, info, exe, null, default);
        Assert.Equal(exe + ".new", staged);
        Assert.Equal(content, File.ReadAllBytes(staged));
        Assert.Equal("application/octet-stream", accept);
        Assert.False(File.Exists(exe + ".part"));
    }

    [Fact]
    public async Task Download_ShaMismatch_Throws_AndLeavesNothingStaged()
    {
        var content = Encoding.ASCII.GetBytes("new exe bytes");
        var exe = _tmp.Write("optimus-vibe.exe", "old");
        var info = Info("optimus-vibe.exe", content, "x");
        var h = new ScriptedHandler(Ok(new string('a', 64) + "  optimus-vibe.exe\n"), Bytes(content));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateService.DownloadAndStageAsync(h, info, exe, null, default));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(exe + ".new"));
        Assert.False(File.Exists(exe + ".part"));
        Assert.Equal("old", File.ReadAllText(exe));
    }

    [Fact]
    public async Task Download_SizeMismatch_Throws()
    {
        var content = Encoding.ASCII.GetBytes("short");
        var exe = _tmp.Write("optimus-vibe.exe", "old");
        var info = Info("optimus-vibe.exe", content, "x", sizeOverride: content.Length + 50);
        var h = new ScriptedHandler(Ok($"{Sha(content)}  optimus-vibe.exe\n"), Bytes(content));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateService.DownloadAndStageAsync(h, info, exe, null, default));
        Assert.Contains("incomplete", ex.Message);
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void ApplyStagedAndRestart_SwapsFiles_AndStartsNewExe()
    {
        var exe = _tmp.Write("optimus-vibe.exe", "old");
        File.WriteAllText(exe + ".new", "new");
        var sibling = _tmp.Write("OptimusVibe.exe", "old-app");
        File.WriteAllText(sibling + ".new", "new-app");
        ProcessStartInfo? started = null;
        UpdateService.ApplyStagedAndRestart(exe, ["--x", "y"], psi => { started = psi; return null; });
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.Equal("new-app", File.ReadAllText(sibling));
        Assert.False(File.Exists(exe + ".new"));
        Assert.Equal(exe, started!.FileName);
        Assert.Equal(["--x", "y"], started.ArgumentList);

        UpdateService.CleanupOld(exe);
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(sibling + ".old"));
    }

    [Fact]
    public void ApplyStaged_WithoutStagedFile_Throws()
    {
        var exe = _tmp.Write("optimus-vibe.exe", "old");
        Assert.Throws<UpdateException>(() => UpdateService.ApplyStaged(exe));
    }
}
