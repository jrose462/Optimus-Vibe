using OptimusVibe.Config;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

public sealed class ConfigRepoTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Seed_PlainRepo_OneCommit_EmbeddedStarterConfig()
    {
        var gh = new FakeGitHub(files: new Dictionary<string, string> { ["README.md"] = "# cfg\n" });
        var before = gh.Head;
        var result = await ConfigRepoSeeder.SeedAsync(gh.Api(), gh.Repo, gh.Branch);
        Assert.Equal(before, gh.ParentOf(gh.Head));
        Assert.Equal(gh.Head, result.CommitSha);
        var files = gh.Files();
        Assert.Equal(new[]
        {
            "README.md", "config/devices.yaml", "config/games/_template.yaml", "config/library.yaml", "config/requests.yaml",
            "config/schema/game.schema.json",
        }, files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains("optOut: []", files["config/library.yaml"]);
        Assert.Contains("requests: []", files["config/requests.yaml"]);
        Assert.DoesNotContain(files.Keys, k => k.Contains("saves") || k.Contains("local-conflict"));
    }

    [Fact]
    public void StarterConfig_IsGeneric_AndWritesWithoutOverwriting()
    {
        var dir = Path.Combine(_tmp.Dir("fresh"), "config");
        var written = SeedConfig.WriteTo(dir);
        Assert.Contains("devices.yaml", written);
        Assert.True(File.Exists(Path.Combine(dir, "games", "_template.yaml")));

        // never overwrites what is already there
        File.WriteAllText(Path.Combine(dir, "devices.yaml"), "devices: {}\n");
        Assert.Empty(SeedConfig.WriteTo(dir));
        Assert.Equal("devices: {}\n", File.ReadAllText(Path.Combine(dir, "devices.yaml")));
    }

    [Fact]
    public void StarterConfig_ContainsNoPersonalData_AndValidates()
    {
        foreach (var (name, bytes) in SeedConfig.Files())
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            // no emails, Steam64 ids or per-user profile paths
            Assert.DoesNotMatch(@"[\w.+-]+@[\w-]+\.[\w.]+", text);
            Assert.DoesNotMatch(@"\b7656119\d{10}\b", text);
            Assert.DoesNotMatch(@"[A-Za-z]:\\Users\\(?!<)[^\\\s]+", text);
        }

        var root = Path.Combine(_tmp.Dir("v"), "config");
        SeedConfig.WriteTo(root);
        var store = new OptimusVibe.Config.ConfigStore(new OptimusVibe.Config.AppPaths { ConfigRoot = root, DataDir = _tmp.Dir("vd") });
        var report = OptimusVibe.Engine.Validator.Run(store);
        Assert.Empty(report.Errors);
        Assert.Equal(["ally-x", "desktop"], store.LoadDevices().Devices.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task Seed_ExistingConfigRepo_Refuses()
    {
        var gh = new FakeGitHub();
        await Assert.ThrowsAsync<GitHubException>(() => ConfigRepoSeeder.SeedAsync(gh.Api(), gh.Repo, gh.Branch));
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
