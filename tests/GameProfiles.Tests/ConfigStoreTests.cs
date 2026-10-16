using GameProfiles.Config;

namespace GameProfiles.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly ConfigStore _store;

    private const string DevicesYaml = """
        devices:
          desktop:
            description: Main PC
            match: { gpu: "NVIDIA*" }
            display: { width: 2560, height: 1440, refresh: 165 }
          ally-x:
            match:
              cpu: "*Z2 Extreme*"
              gpu: "AMD Radeon*"
            display: { width: 1920, height: 1080, refresh: 120 }
            driver: amd
        """;

    private const string GameYaml = """
        # Cyberpunk 2077
        name: Cyberpunk 2077
        store: steam
        steamAppId: 1091500
        launch: { uri: "steam://rungameid/1091500" }
        processes: [Cyberpunk2077.exe]
        files:
          - id: user
            handler: json
            path: "{LocalAppData}/CD Projekt Red/Cyberpunk 2077/UserSettings.json"
            readOnlyAfterApply: true
          - id: engine
            handler: keyvalue
            format: cvar
            path: "{SteamLibrary:1091500}/engine/config/platform/pc/user.ini"
            createIfMissing: true
        profiles:
          base:
            user: { "graphics.preset": High }
          desktop:
            extends: base
            display: inherit
            nvidia: { FrameRateLimiter: 165 }
            user:
              "data[group_name=/graphics/presets].options[name=DLSS].value": DLSS_Quality
          ally-x:
            extends: base
            display: { refresh: 120 }
            amd: { Chill: { min: 55, max: 60 }, AFMF: false }
            user: { MaxFPS: 60, nested: { deep: { key: 1 } }, list: [a, b] }
        intent:
          ally-x: "Steady 60 fps, battery over visuals"
        notes: Some notes.
        """;

    public ConfigStoreTests()
    {
        var root = _tmp.Dir("config");
        _tmp.Write(Path.Combine("config", "devices.yaml"), DevicesYaml);
        _tmp.Write(Path.Combine("config", "games", "Cyberpunk2077.yaml"), GameYaml);
        _tmp.Write(Path.Combine("config", "games", "eldenring.yaml"), "name: Elden Ring\n");
        _tmp.Write(Path.Combine("config", "games", "_template.yaml"), "name: TEMPLATE\n");
        _tmp.Write(Path.Combine("config", "games", "readme.txt"), "not yaml");
        _store = new ConfigStore(new AppPaths { ConfigRoot = root, DataDir = _tmp.Dir("data") });
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void LoadDevices()
    {
        var d = _store.LoadDevices();
        Assert.Equal(["desktop", "ally-x"], d.Devices.Keys);
        Assert.Equal("Main PC", d.Devices["desktop"].Description);
        Assert.Equal("NVIDIA*", d.Devices["desktop"].Match["gpu"]);
        Assert.Equal(2, d.Devices["ally-x"].Match.Count);
        Assert.Equal(165, d.Devices["desktop"].Display!.Refresh);
        Assert.Equal("2560x1440@165Hz", d.Devices["desktop"].Display!.ToString());
        Assert.Equal("amd", d.Devices["ally-x"].Driver);
        Assert.Null(d.Devices["desktop"].Driver);
    }

    [Fact]
    public void LoadDevices_MissingFile_Throws()
    {
        File.Delete(_store.Paths.DevicesFile);
        Assert.Throws<ConfigException>(() => _store.LoadDevices());
    }

    [Fact]
    public void GameIds_SkipsUnderscoreFiles_AndNonYaml_Sorted()
    {
        Assert.Equal(["Cyberpunk2077", "eldenring"], _store.GameIds());
    }

    [Fact]
    public void GameIds_NoGamesDir_IsEmpty()
    {
        Directory.Delete(_store.Paths.GamesDir, recursive: true);
        Assert.Empty(_store.GameIds());
    }

    [Fact]
    public void LoadGame_ParsesAllFields()
    {
        var g = _store.LoadGame("Cyberpunk2077");
        Assert.Equal("Cyberpunk2077", g.Id); // derived from the file name
        Assert.Equal("Cyberpunk 2077", g.Name);
        Assert.Equal(1091500, g.SteamAppId);
        Assert.Equal("steam://rungameid/1091500", g.Launch!.Uri);
        Assert.Equal(["Cyberpunk2077.exe"], g.Processes);
        Assert.Equal(2, g.Files.Count);
        Assert.Equal("json", g.Files[0].Handler);
        Assert.True(g.Files[0].ReadOnlyAfterApply);
        Assert.Equal("cvar", g.Files[1].Format);
        Assert.True(g.Files[1].CreateIfMissing);
        Assert.Equal("Steady 60 fps, battery over visuals", g.Intent["ally-x"]);
        Assert.Equal("Some notes.", g.Notes);
    }

    [Fact]
    public void LoadGame_NormalizesProfileMapsToStringKeys()
    {
        var g = _store.LoadGame("Cyberpunk2077");
        var ally = g.Profiles["ally-x"];
        var user = Assert.IsType<Dictionary<string, object?>>(ally["user"]);
        var nested = Assert.IsType<Dictionary<string, object?>>(user["nested"]);
        var deep = Assert.IsType<Dictionary<string, object?>>(nested["deep"]);
        Assert.Equal("1", deep["key"]);
        Assert.Equal("60", user["MaxFPS"]);
        Assert.Equal(new List<object?> { "a", "b" }, Assert.IsType<List<object?>>(user["list"]));
        Assert.IsType<Dictionary<string, object?>>(((Dictionary<string, object?>)ally["amd"]!)["Chill"]);
        Assert.Equal("base", ally["extends"]);
        Assert.Equal("DLSS_Quality",
            ((Dictionary<string, object?>)g.Profiles["desktop"]["user"]!)["data[group_name=/graphics/presets].options[name=DLSS].value"]);
    }

    [Fact]
    public void LoadGame_ResolvesEndToEnd()
    {
        var g = _store.LoadGame("Cyberpunk2077");
        var devices = _store.LoadDevices();
        var desktop = ProfileResolver.Resolve(g, "desktop", devices.Devices["desktop"], new());
        Assert.Equal(2560, desktop.Display!.Width);
        Assert.Equal("165", desktop.Nvidia["FrameRateLimiter"]);
        Assert.Equal("High", desktop.FileBlocks["user"]["graphics.preset"]);

        var ally = ProfileResolver.Resolve(g, "ally-x", devices.Devices["ally-x"], new());
        Assert.Equal("1920x1080@120Hz", ally.Display!.ToString());
        Assert.Equal("min=55,max=60", ally.Amd["Chill"]);
        Assert.Equal("false", ally.Amd["AFMF"]);
    }

    [Theory]
    [InlineData("cyberpunk2077")]
    [InlineData("CYBERPUNK2077")]
    public void LoadGame_CaseInsensitiveId(string id)
    {
        Assert.Equal("Cyberpunk 2077", _store.LoadGame(id).Name);
    }

    [Fact]
    public void LoadGame_ExplicitIdInYaml_Wins()
    {
        _tmp.Write(Path.Combine("config", "games", "x.yaml"), "id: custom-id\nname: X\n");
        Assert.Equal("custom-id", _store.LoadGame("x").Id);
    }

    [Fact]
    public void LoadGame_Unknown_Throws()
    {
        Assert.Throws<ConfigException>(() => _store.LoadGame("nope"));
    }

    [Fact]
    public void LoadGame_EmptyFile_GivesDefaults()
    {
        _tmp.Write(Path.Combine("config", "games", "empty.yaml"), "  \n");
        var g = _store.LoadGame("empty");
        Assert.Equal("empty", g.Id);
        Assert.Equal("steam", g.Store);
        Assert.Empty(g.Files);
    }

    [Fact]
    public void Capture_RoundTrip()
    {
        var body = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?>
            {
                ["/Script/Engine.GameUserSettings"] = new Dictionary<string, object?>
                {
                    ["ResolutionSizeX"] = "1920",
                    ["sg.ShadowQuality"] = "1",
                },
                ["data[group_name=/graphics/presets].options[name=DLSS].value"] = "DLSS_Performance",
                ["Version"] = "5",
            },
            ["display"] = new Dictionary<string, object?> { ["refresh"] = "120" },
        };
        _store.SaveCapture("Cyberpunk2077", "ally-x", body);

        var file = _store.Paths.CaptureFile("Cyberpunk2077", "ally-x");
        Assert.True(File.Exists(file));
        Assert.StartsWith("# Captured by", File.ReadAllText(file));

        var loaded = _store.LoadCapture("Cyberpunk2077", "ally-x");
        Assert.Equal(["user", "display"], loaded.Keys);
        var user = (Dictionary<string, object?>)loaded["user"]!;
        var gus = (Dictionary<string, object?>)user["/Script/Engine.GameUserSettings"]!;
        Assert.Equal("1920", gus["ResolutionSizeX"]);
        Assert.Equal("1", gus["sg.ShadowQuality"]);
        Assert.Equal("DLSS_Performance", user["data[group_name=/graphics/presets].options[name=DLSS].value"]);
        Assert.Equal("5", user["Version"]);
        Assert.Equal("120", ((Dictionary<string, object?>)loaded["display"]!)["refresh"]);
    }

    [Fact]
    public void Capture_MissingFile_IsEmpty()
    {
        Assert.Empty(_store.LoadCapture("Cyberpunk2077", "desktop"));
    }

    [Fact]
    public void SaveCapture_Empty_DeletesFile()
    {
        _store.SaveCapture("Cyberpunk2077", "ally-x", new() { ["user"] = new Dictionary<string, object?> { ["a"] = "1" } });
        var file = _store.Paths.CaptureFile("Cyberpunk2077", "ally-x");
        Assert.True(File.Exists(file));
        _store.SaveCapture("Cyberpunk2077", "ally-x", new());
        Assert.False(File.Exists(file));
        _store.SaveCapture("Cyberpunk2077", "ally-x", new()); // no file: no-op
    }

    [Fact]
    public void AppPaths_Layout()
    {
        var p = new AppPaths { ConfigRoot = @"C:\cfg", DataDir = @"C:\data" };
        Assert.Equal(@"C:\cfg\devices.yaml", p.DevicesFile);
        Assert.Equal(@"C:\cfg\games\g.yaml", p.GameFile("g"));
        Assert.Equal(@"C:\cfg\captures\g\dev.yaml", p.CaptureFile("g", "dev"));
        Assert.Equal(@"C:\cfg\snapshots\g\dev", p.SnapshotDir("g", "dev"));
        Assert.Equal(@"C:\data\backups", p.BackupsDir);
        Assert.Equal(@"C:\data\state.json", p.StateFile);
    }

    [Fact]
    public void AppSettings_RoundTrip()
    {
        var dir = _tmp.File("data2");
        Assert.Null(AppPaths.LoadSettings(dir).ConfigRoot);
        AppPaths.SaveSettings(dir, new AppSettings { ConfigRoot = @"D:\repo\config", ForcedProfile = "ally-x" });
        var s = AppPaths.LoadSettings(dir);
        Assert.Equal(@"D:\repo\config", s.ConfigRoot);
        Assert.Equal("ally-x", s.ForcedProfile);
    }

    [Fact]
    public void AppPaths_Resolve_PrefersExplicitOption()
    {
        var root = _store.Paths.ConfigRoot;
        Assert.Equal(Path.GetFullPath(root), AppPaths.Resolve(root).ConfigRoot);
    }
}
