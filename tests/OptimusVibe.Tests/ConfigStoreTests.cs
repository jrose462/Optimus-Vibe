using OptimusVibe.Config;

namespace OptimusVibe.Tests;

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
            defaultPreset: battery
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
        presets:
          desktop:
            quality:
              extends: base
              label: Max quality
              targetFps: 165
              display: inherit
              nvidia: { FrameRateLimiter: 165 }
              user:
                "data[group_name=/graphics/presets].options[name=DLSS].value": DLSS_Quality
          ally-x:
            performance:
              extends: base
              description: 60+ fps
              display: { refresh: 120 }
              amd: { Chill: { min: 55, max: 60 }, AFMF: false }
              user: { MaxFPS: 60, nested: { deep: { key: 1 } }, list: [a, b] }
            battery:
              extends: base
              user: { MaxFPS: 40 }
        intent:
          ally-x: "Steady 60 fps, battery over visuals"
        recommendation:
          generated: "2026-10-01"
          gameVersion: "2.3"
          sources: [https://example.com/guide]
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
        Assert.Equal("battery", d.Devices["ally-x"].DefaultPreset);
        Assert.Null(d.Devices["desktop"].DefaultPreset);
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
        Assert.Equal(["base"], g.Profiles.Keys);
        Assert.Equal(["desktop", "ally-x"], g.Presets.Keys);
        Assert.Equal(["performance", "battery"], g.Presets["ally-x"].Keys);
        Assert.Equal("2026-10-01", g.Recommendation!.Generated);
        Assert.Equal("2.3", g.Recommendation.GameVersion);
        Assert.Equal(["https://example.com/guide"], g.Recommendation.Sources);
    }

    [Fact]
    public void LoadGame_NormalizesPresetMapsToStringKeys()
    {
        var g = _store.LoadGame("Cyberpunk2077");
        var ally = g.Presets["ally-x"]["performance"];
        var user = Assert.IsType<Dictionary<string, object?>>(ally["user"]);
        var nested = Assert.IsType<Dictionary<string, object?>>(user["nested"]);
        var deep = Assert.IsType<Dictionary<string, object?>>(nested["deep"]);
        Assert.Equal("1", deep["key"]);
        Assert.Equal("60", user["MaxFPS"]);
        Assert.Equal(new List<object?> { "a", "b" }, Assert.IsType<List<object?>>(user["list"]));
        Assert.IsType<Dictionary<string, object?>>(((Dictionary<string, object?>)ally["amd"]!)["Chill"]);
        Assert.Equal("base", ally["extends"]);
        Assert.Equal("DLSS_Quality",
            ((Dictionary<string, object?>)g.Presets["desktop"]["quality"]["user"]!)["data[group_name=/graphics/presets].options[name=DLSS].value"]);
        Assert.IsType<Dictionary<string, object?>>(g.Profiles["base"]["user"]);
    }

    [Fact]
    public void LoadGame_ResolvesEndToEnd()
    {
        var g = _store.LoadGame("Cyberpunk2077");
        var devices = _store.LoadDevices();
        var desktop = ProfileResolver.ResolvePreset(g, "desktop", devices.Devices["desktop"], "quality");
        Assert.Equal(2560, desktop.Display!.Width);
        Assert.Equal("165", desktop.Nvidia["FrameRateLimiter"]);
        Assert.Equal("High", desktop.FileBlocks["user"]["graphics.preset"]);
        Assert.Equal("Max quality", desktop.Label);
        Assert.Equal(165, desktop.TargetFps);

        var ally = ProfileResolver.ResolvePreset(g, "ally-x", devices.Devices["ally-x"], "performance");
        Assert.Equal("1920x1080@120Hz", ally.Display!.ToString());
        Assert.Equal("min=55,max=60", ally.Amd["Chill"]);
        Assert.Equal("false", ally.Amd["AFMF"]);
        Assert.Equal("Performance", ally.Label);
        Assert.Equal("60+ fps", ally.Description);
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
        Assert.Empty(g.Presets);
        Assert.Null(g.Recommendation);
    }

    // ------------------------------------------------------------------ manual saves

    private static Dictionary<string, object?> SampleBody() => new()
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
        ["display"] = new Dictionary<string, object?> { ["refresh"] = 120 },
    };

    private ManualSave NewSave(string name, DateTime updated, Dictionary<string, object?>? body = null) => new()
    {
        Name = name,
        Created = updated.AddHours(-1),
        Updated = updated,
        Body = body ?? new(),
    };

    [Fact]
    public void Save_RoundTrip()
    {
        var created = new DateTime(2026, 10, 1, 12, 30, 15);
        var save = new ManualSave
        {
            Name = "Steady 40",
            Created = created,
            Updated = created.AddMinutes(5),
            BasedOn = "recommended:performance",
            Notes = "for flights",
            Body = SampleBody(),
        };
        _store.WriteSave("Cyberpunk2077", "ally-x", save);

        Assert.Equal("steady-40", save.Slug);
        Assert.Equal(_store.Paths.SaveDir("Cyberpunk2077", "ally-x", "steady-40"), save.Dir);
        var file = Path.Combine(save.Dir, "save.yaml");
        Assert.True(File.Exists(file));
        Assert.StartsWith("# Manual save for Cyberpunk2077 on ally-x", File.ReadAllText(file));

        var loaded = _store.LoadSave("Cyberpunk2077", "ally-x", "steady-40");
        Assert.Equal("steady-40", loaded.Slug);
        Assert.Equal(save.Dir, loaded.Dir);
        Assert.Equal("Steady 40", loaded.Name);
        Assert.Equal(created, loaded.Created);
        Assert.Equal(created.AddMinutes(5), loaded.Updated);
        Assert.Equal("recommended:performance", loaded.BasedOn);
        Assert.Equal("for flights", loaded.Notes);

        // Meta keys are not part of the body.
        Assert.Equal(["user", "display"], loaded.Body.Keys);
        var user = (Dictionary<string, object?>)loaded.Body["user"]!;
        var gus = (Dictionary<string, object?>)user["/Script/Engine.GameUserSettings"]!;
        Assert.Equal("1920", gus["ResolutionSizeX"]);
        Assert.Equal("1", gus["sg.ShadowQuality"]);
        Assert.Equal("DLSS_Performance", user["data[group_name=/graphics/presets].options[name=DLSS].value"]);
        Assert.Equal("5", user["Version"]);
        Assert.Equal("120", ((Dictionary<string, object?>)loaded.Body["display"]!)["refresh"]);
    }

    [Fact]
    public void LoadSave_OptionalMetaMissing_Defaults()
    {
        _tmp.Write(Path.Combine("config", "saves", "g", "dev", "hand", "save.yaml"), "created: 2026-01-02T03:04:05\n");
        var s = _store.LoadSave("g", "dev", "hand");
        Assert.Equal("hand", s.Name);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), s.Created);
        Assert.Equal(s.Created, s.Updated); // updated falls back to created
        Assert.Null(s.BasedOn);
        Assert.Null(s.Notes);
        Assert.Empty(s.Body);
    }

    [Fact]
    public void LoadSave_Missing_Throws() =>
        Assert.Throws<ConfigException>(() => _store.LoadSave("Cyberpunk2077", "ally-x", "nope"));

    [Fact]
    public void ListSaves_OrderedByUpdatedDescending_IgnoresFoldersWithoutSaveYaml()
    {
        var t = new DateTime(2026, 5, 1, 10, 0, 0);
        _store.WriteSave("g", "dev", NewSave("Old", t));
        _store.WriteSave("g", "dev", NewSave("Newest", t.AddDays(2)));
        _store.WriteSave("g", "dev", NewSave("Middle", t.AddDays(1)));
        _store.WriteSave("g", "other-dev", NewSave("Elsewhere", t.AddDays(5)));
        Directory.CreateDirectory(_store.Paths.SaveDir("g", "dev", "junk"));

        var list = _store.ListSaves("g", "dev");
        Assert.Equal(["Newest", "Middle", "Old"], list.Select(s => s.Name));
        Assert.Equal(["newest", "middle", "old"], list.Select(s => s.Slug));
    }

    [Fact]
    public void ListSaves_NoFolder_IsEmpty() => Assert.Empty(_store.ListSaves("g", "dev"));

    [Fact]
    public void WriteSave_WritesSnapshots_AndRemovesStaleOnRewrite()
    {
        var save = NewSave("Snap", DateTime.Now);
        _store.WriteSave("g", "dev", save, new Dictionary<string, byte[]> { ["a"] = [1, 2, 3], ["b"] = [4] });
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(save.Dir, "a.snap")));
        Assert.Equal([4], File.ReadAllBytes(Path.Combine(save.Dir, "b.snap")));

        _store.WriteSave("g", "dev", save, new Dictionary<string, byte[]> { ["a"] = [9] });
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(save.Dir, "a.snap")));
        Assert.False(File.Exists(Path.Combine(save.Dir, "b.snap")));
        Assert.Equal("snap", save.Slug); // existing slug kept, no -2

        _store.WriteSave("g", "dev", save);
        Assert.Empty(Directory.EnumerateFiles(save.Dir, "*.snap"));
    }

    [Fact]
    public void WriteSave_SameName_GetsUniqueSlug()
    {
        var a = NewSave("Steady 40", DateTime.Now);
        var b = NewSave("Steady 40", DateTime.Now);
        var c = NewSave("steady-40!", DateTime.Now);
        _store.WriteSave("g", "dev", a);
        _store.WriteSave("g", "dev", b);
        _store.WriteSave("g", "dev", c);
        Assert.Equal("steady-40", a.Slug);
        Assert.Equal("steady-40-2", b.Slug);
        Assert.Equal("steady-40-3", c.Slug);
        Assert.Equal(3, _store.ListSaves("g", "dev").Count);
    }

    [Fact]
    public void UniqueSlug_PerDevice()
    {
        _store.WriteSave("g", "dev", NewSave("X", DateTime.Now));
        Assert.Equal("x-2", _store.UniqueSlug("g", "dev", "X"));
        Assert.Equal("x", _store.UniqueSlug("g", "other", "X"));
        Assert.Equal("x", _store.UniqueSlug("h", "dev", "X"));
    }

    [Fact]
    public void DeleteSave_RemovesFolder_MissingIsNoOp()
    {
        var s = NewSave("Gone", DateTime.Now);
        _store.WriteSave("g", "dev", s, new Dictionary<string, byte[]> { ["a"] = [1] });
        _store.DeleteSave("g", "dev", s.Slug);
        Assert.False(Directory.Exists(s.Dir));
        _store.DeleteSave("g", "dev", s.Slug);
        Assert.Empty(_store.ListSaves("g", "dev"));
    }

    [Theory]
    [InlineData("Steady 40", "steady-40")]
    [InlineData("  Hello, World!  ", "hello-world")]
    [InlineData("a--b__c", "a-b-c")]
    [InlineData("---lead and trail---", "lead-and-trail")]
    [InlineData("", "save")]
    [InlineData("   ", "save")]
    [InlineData("!!!@@@###", "save")]
    [InlineData("60FPS/High", "60fps-high")]
    [InlineData("UPPER case", "upper-case")]
    public void Slugify(string name, string expected) => Assert.Equal(expected, ConfigStore.Slugify(name));

    [Fact]
    public void Slugify_LongName_TruncatedTo48_WithoutTrailingDash()
    {
        Assert.Equal(new string('a', 48), ConfigStore.Slugify(new string('a', 100)));
        // Character 48 would be a dash: it is trimmed.
        var name = new string('b', 47) + " tail";
        var slug = ConfigStore.Slugify(name);
        Assert.Equal(new string('b', 47), slug);
        Assert.True(slug.Length <= 48);
    }

    // ------------------------------------------------------------------ paths / settings

    [Fact]
    public void AppPaths_Layout()
    {
        var p = new AppPaths { ConfigRoot = @"C:\repo\config", DataDir = @"C:\data" };
        Assert.Equal(@"C:\repo\config\devices.yaml", p.DevicesFile);
        Assert.Equal(@"C:\repo\config\games\g.yaml", p.GameFile("g"));
        Assert.Equal(@"C:\repo\config\saves", p.SavesDir);
        Assert.Equal(@"C:\repo\config\saves\g\dev", p.SavesFor("g", "dev"));
        Assert.Equal(@"C:\repo\config\saves\g\dev\slug", p.SaveDir("g", "dev", "slug"));
        Assert.Equal(@"C:\data\backups", p.BackupsDir);
        Assert.Equal(@"C:\data\state.json", p.StateFile);
        Assert.Equal(@"C:\data\selections.json", p.SelectionsFile);
        Assert.Equal(@"C:\repo", p.RepoRoot);
    }

    [Fact]
    public void AppSettings_RoundTrip()
    {
        var dir = _tmp.File("data2");
        var defaults = AppPaths.LoadSettings(dir);
        Assert.Null(defaults.ConfigRoot);
        Assert.True(defaults.AutoSync);
        AppPaths.SaveSettings(dir, new AppSettings { ConfigRoot = @"D:\repo\config", ForcedProfile = "ally-x", AutoSync = false });
        var s = AppPaths.LoadSettings(dir);
        Assert.Equal(@"D:\repo\config", s.ConfigRoot);
        Assert.Equal("ally-x", s.ForcedProfile);
        Assert.False(s.AutoSync);
    }

    [Fact]
    public void AppSettings_MissingAutoSync_DefaultsTrue()
    {
        var dir = _tmp.Dir("data3");
        File.WriteAllText(AppPaths.SettingsFile(dir), """{ "configRoot": "X" }""");
        Assert.True(AppPaths.LoadSettings(dir).AutoSync);
    }

    [Fact]
    public void AppPaths_Resolve_PrefersExplicitOption()
    {
        var root = _store.Paths.ConfigRoot;
        Assert.Equal(Path.GetFullPath(root), AppPaths.Resolve(root).ConfigRoot);
    }
}
