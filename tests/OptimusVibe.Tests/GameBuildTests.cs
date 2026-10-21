using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Library;
using OptimusVibe.Library.Scanners;

namespace OptimusVibe.Tests;

/// <summary>Which build of a game is installed (Steam build id, else an exe fingerprint), and the per-device "presets made against" mark.</summary>
public sealed class GameBuildTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _steam;

    public GameBuildTests()
    {
        _steam = _tmp.Dir("Steam");
        Directory.CreateDirectory(Path.Combine(_steam, "steamapps"));
    }

    public void Dispose() => _tmp.Dispose();

    private void Manifest(int appId, string dir, string? buildId, string? lastUpdated = null)
    {
        var extra = (buildId is null ? "" : $"\t\"buildid\"\t\t\"{buildId}\"\n") + (lastUpdated is null ? "" : $"\t\"LastUpdated\"\t\t\"{lastUpdated}\"\n");
        File.WriteAllText(Path.Combine(_steam, "steamapps", $"appmanifest_{appId}.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"name\"\t\t\"Game {appId}\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"{dir}\"\n{extra}}}\n");
        Directory.CreateDirectory(Path.Combine(_steam, "steamapps", "common", dir));
    }

    [Fact]
    public void SteamScanner_ReadsTheBuildId_AndWhenItWasUpdated()
    {
        Manifest(10, "Ten", "18000042", "1760000000");
        Manifest(20, "Twenty", null);
        Manifest(30, "Thirty", "0");

        var games = new SteamScanner(new SteamLocator(_steam)).Scan().ToDictionary(g => g.StoreId);

        Assert.Equal("18000042", games["10"].BuildId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000).UtcDateTime, games["10"].BuildUpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, games["10"].BuildUpdatedUtc!.Value.Kind);
        Assert.Null(games["20"].BuildId);
        Assert.Null(games["20"].BuildUpdatedUtc);
        Assert.Null(games["30"].BuildId); // "0" = not recorded
        Assert.Equal("steam:18000042", GameBuild.Of(games["10"]));
    }

    [Fact]
    public void SteamLocator_BuildId_ReadsTheManifest()
    {
        Manifest(10, "Ten", "777");
        var steam = new SteamLocator(_steam);
        Assert.Equal("777", steam.BuildId(10));
        Assert.Null(steam.BuildId(11));
        Assert.EndsWith("appmanifest_10.acf", steam.ManifestPath(10));
        Assert.Null(steam.ManifestPath(11));
    }

    [Fact]
    public void ForGame_UsesTheSteamBuild_ElseTheExeFingerprint_AndMatchesTheScan()
    {
        Manifest(10, "Ten", "777");
        var steam = new SteamLocator(_steam);
        var install = Path.Combine(_steam, "steamapps", "common", "Ten");
        FakeFiles.Touch(Path.Combine(install, "Ten.exe"), 500);

        var steamGame = new GameDef { Id = "ten", Name = "Ten", SteamAppId = 10 };
        Assert.Equal("steam:777", GameBuild.ForGame(steamGame, install, steam));
        var scanned = new SteamScanner(steam).Scan().Single();
        Assert.Equal(GameBuild.Of(scanned), GameBuild.ForGame(steamGame, install, steam));

        var other = new GameDef { Id = "other", Name = "Other" };
        var fp = GameBuild.ForGame(other, install, steam);
        Assert.StartsWith("exe:ten.exe:500:", fp);
        Assert.Equal(fp, GameBuild.Of(new DetectedGame { Store = "gog", StoreId = "1", Name = "Ten", InstallDir = install }));
        Assert.Null(GameBuild.ForGame(other, null, steam));
        Assert.Null(GameBuild.ForGame(new GameDef { Id = "gone", Name = "Gone", SteamAppId = 99 }, null, steam)); // not installed
    }

    [Fact]
    public void ExeFingerprint_PicksTheBiggestNonLauncherExe_AndChangesWithIt()
    {
        var dir = _tmp.Dir("Game");
        FakeFiles.Touch(Path.Combine(dir, "GameLauncher.exe"), 9000);
        FakeFiles.Touch(Path.Combine(dir, "Binaries", "Win64", "Game-Win64-Shipping.exe"), 4000);
        FakeFiles.Touch(Path.Combine(dir, "Game.exe"), 100);
        FakeFiles.Touch(Path.Combine(dir, "CrashReporter.exe"), 99999);

        var before = GameBuild.ExeFingerprint(dir);
        Assert.StartsWith("exe:game-win64-shipping.exe:4000:", before);
        Assert.Equal(before, GameBuild.ExeFingerprint(dir)); // stable

        FakeFiles.Touch(Path.Combine(dir, "Binaries", "Win64", "Game-Win64-Shipping.exe"), 4100); // patched
        var after = GameBuild.ExeFingerprint(dir);
        Assert.True(GameBuild.Changed(before, after));

        Assert.Null(GameBuild.ExeFingerprint(_tmp.Dir("Empty")));
        Assert.Null(GameBuild.ExeFingerprint(_tmp.File("missing")));
        Assert.Null(GameBuild.ExeFingerprint(null));
    }

    [Fact]
    public void Changed_NeedsBothBuilds_OfTheSameKind()
    {
        Assert.True(GameBuild.Changed("steam:1", "steam:2"));
        Assert.False(GameBuild.Changed("steam:1", "steam:1"));
        Assert.False(GameBuild.Changed(null, "steam:2"));
        Assert.False(GameBuild.Changed("steam:1", null));
        Assert.False(GameBuild.Changed("steam:1", "exe:a.exe:1:2"));
        Assert.True(GameBuild.Changed("exe:a.exe:1:2", "exe:a.exe:1:3"));
        Assert.True(GameBuild.IsStoreBuild("steam:1"));
        Assert.False(GameBuild.IsStoreBuild("exe:a.exe:1:2"));
        Assert.False(GameBuild.IsStoreBuild(null));
    }

    // ---------------------------------------------------------------- presets mark (gamelab-state.json)

    [Fact]
    public void MarkPresets_RemembersWhenAndAgainstWhichBuild_PerGame()
    {
        var store = new LabStateStore(_tmp.Path);
        var t = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

        store.MarkPresets("hades", t, "steam:5", 12, LabKind.Refresh);

        var m = store.Load().PresetsFor("HADES")!;
        Assert.Equal(t, m.MadeUtc);
        Assert.Equal("steam:5", m.BuildId);
        Assert.Equal(12, m.Issue);
        Assert.Equal("refresh", m.Kind);
        Assert.Null(store.Load().PresetsFor("other"));

        store.MarkPresets("hades", t.AddDays(1), null);
        Assert.Null(store.Load().PresetsFor("hades")!.BuildId); // a newer arrival replaces the mark
    }

    [Fact]
    public void ObservePresetsBuild_FillsAnUnknownBuildOnce()
    {
        var store = new LabStateStore(_tmp.Path);
        Assert.False(store.ObservePresetsBuild("hades", "steam:5")); // no mark: nothing to fill

        store.MarkPresets("hades", DateTime.UtcNow, null);
        Assert.False(store.ObservePresetsBuild("hades", null));
        Assert.True(store.ObservePresetsBuild("hades", "steam:5"));
        Assert.False(store.ObservePresetsBuild("hades", "steam:6")); // a known build is kept
        Assert.Equal("steam:5", store.Load().PresetsFor("hades")!.BuildId);
    }

    [Fact]
    public void OldStateFile_WithoutMarks_StillLoads()
    {
        var store = new LabStateStore(_tmp.Path);
        File.WriteAllText(store.FilePath, """{ "requested": { "steam:1": "2026-10-01T00:00:00Z" }, "applied": [3], "baselined": true }""");

        var s = store.Load();
        Assert.Empty(s.PresetsMade);
        Assert.Equal([3], s.Applied);
        store.MarkPresets("g", DateTime.UtcNow, "steam:1");
        Assert.Single(store.Load().Requested);
        Assert.NotNull(store.Load().PresetsFor("g"));
    }
}
