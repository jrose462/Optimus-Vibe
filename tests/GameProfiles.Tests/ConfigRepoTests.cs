using GameProfiles.Sync;

namespace GameProfiles.Tests;

public sealed class ConfigRepoTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private string SeedDir()
    {
        _tmp.Write("cfg/devices.yaml", "devices: [a]\n");
        _tmp.Write("cfg/games/g.yaml", "id: g\n");
        _tmp.Write("cfg/schema/s.json", "{}");
        _tmp.Write("cfg/saves/g/ally/s1/save.yaml", "name: x\n");
        _tmp.Write("cfg/games/g.yaml.local-conflict", "conflict");
        _tmp.Write("cfg/library.yaml", "optOut: [steam:1]\n");
        _tmp.Write("cfg/requests.yaml", "requests: [a, b]\n");
        return _tmp.File("cfg");
    }

    [Fact]
    public async Task Seed_PlainRepo_OneCommit_ExpectedPaths()
    {
        var gh = new FakeGitHub(files: new Dictionary<string, string> { ["README.md"] = "# cfg\n" });
        var before = gh.Head;
        var result = await ConfigRepoSeeder.SeedAsync(gh.Api(), gh.Repo, gh.Branch, SeedDir());
        Assert.Equal(before, gh.ParentOf(gh.Head));
        Assert.Equal(gh.Head, result.CommitSha);
        var files = gh.Files();
        Assert.Equal(new[]
        {
            "README.md", "config/devices.yaml", "config/games/g.yaml", "config/library.yaml", "config/requests.yaml", "config/schema/s.json",
        }, files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("devices: [a]\n", files["config/devices.yaml"]);
        Assert.Contains("optOut: []", files["config/library.yaml"]);
        Assert.DoesNotContain("steam:1", files["config/library.yaml"]);
        Assert.Contains("requests: []", files["config/requests.yaml"]);
        Assert.DoesNotContain(files.Keys, k => k.Contains("saves") || k.Contains("local-conflict"));
    }

    [Fact]
    public async Task Seed_ExistingConfigRepo_Refuses()
    {
        var gh = new FakeGitHub();
        await Assert.ThrowsAsync<GitHubException>(() => ConfigRepoSeeder.SeedAsync(gh.Api(), gh.Repo, gh.Branch, SeedDir()));
    }

    [Fact]
    public async Task Probe_ConfigRepo()
    {
        var gh = new FakeGitHub();
        var s = await ConfigRepoProbe.CheckAsync(gh.Api(), gh.Repo, gh.Branch);
        Assert.True(s.IsConfigRepo);
        Assert.True(s.Accessible);
        Assert.False(s.CanSeed);
    }

    [Fact]
    public async Task Probe_PlainRepo_CanSeed()
    {
        var gh = new FakeGitHub(files: new Dictionary<string, string> { ["README.md"] = "x" });
        var s = await ConfigRepoProbe.CheckAsync(gh.Api(), gh.Repo, gh.Branch);
        Assert.False(s.IsConfigRepo);
        Assert.False(s.IsEmpty);
        Assert.True(s.CanSeed);
    }

    [Fact]
    public async Task Probe_MissingRepo_NotAccessible()
    {
        var gh = new FakeGitHub();
        var s = await ConfigRepoProbe.CheckAsync(gh.Api(), "me/other", "main");
        Assert.False(s.Accessible);
        Assert.False(s.IsConfigRepo);
        Assert.False(string.IsNullOrWhiteSpace(s.Message));
    }
}
