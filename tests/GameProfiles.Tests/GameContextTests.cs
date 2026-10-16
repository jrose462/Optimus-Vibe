using GameProfiles.Config;
using GameProfiles.Engine;

namespace GameProfiles.Tests;

public sealed class GameContextTests : IDisposable
{
    private readonly EngineFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void NoSelection_UsesDeviceDefaultPreset()
    {
        var ctx = _f.Load("desktop");
        Assert.Equal(PresetRef.Recommended("performance"), ctx.Preset);
        Assert.False(ctx.IsSelected);
        Assert.Null(ctx.Save);
        Assert.Equal("desktop", ctx.Device.Name);
        Assert.Equal(EngineFixture.GameId, ctx.Game.Id);
    }

    [Fact]
    public void NoSelection_NoDefaultPreset_UsesFirstPresetInDisplayOrder()
    {
        // ally-x has turbo + battery and no defaultPreset: conventional names come first.
        var ctx = _f.Load("ally-x");
        Assert.Equal(PresetRef.Recommended("battery"), ctx.Preset);
        Assert.False(ctx.IsSelected);
    }

    [Fact]
    public void DefaultPresetMissingFromGame_FallsBackToFirstPreset()
    {
        var def = new DeviceDef { DefaultPreset = "battery", Driver = "nvidia" };
        var ctx = _f.Load("desktop", def: def);
        Assert.Equal(PresetRef.Recommended("quality"), ctx.Preset);
    }

    [Fact]
    public void DeviceWithoutPresets_IsEmpty()
    {
        var ctx = _f.Load("laptop");
        Assert.True(ctx.Profile.IsEmpty);
        Assert.Null(ctx.Preset);
        Assert.False(ctx.IsSelected);
        Assert.All(ctx.Files, f => Assert.Empty(f.Targets));
        Assert.Equal(3, ctx.Files.Count);
    }

    [Fact]
    public void Selection_WinsOverDefault()
    {
        _f.Selections.Set("desktop", EngineFixture.GameId, PresetRef.Recommended("quality"));
        var ctx = _f.Load("desktop");
        Assert.Equal(PresetRef.Recommended("quality"), ctx.Preset);
        Assert.True(ctx.IsSelected);
        Assert.Equal("Best looking", ctx.Profile.Label);
        Assert.Equal(120, ctx.Profile.TargetFps);
    }

    [Fact]
    public void Selection_IsPerDevice()
    {
        _f.Selections.Set("ally-x", EngineFixture.GameId, PresetRef.Recommended("turbo"));
        Assert.Equal(PresetRef.Recommended("performance"), _f.Load("desktop").Preset);
        Assert.Equal(PresetRef.Recommended("turbo"), _f.Load("ally-x").Preset);
    }

    [Fact]
    public void ExplicitPreset_WinsOverSelection()
    {
        _f.Selections.Set("desktop", EngineFixture.GameId, PresetRef.Recommended("quality"));
        var ctx = _f.Load("desktop", PresetRef.Recommended("performance"));
        Assert.Equal(PresetRef.Recommended("performance"), ctx.Preset);
        Assert.True(ctx.IsSelected);
        // Loading does not change the remembered selection.
        Assert.Equal(PresetRef.Recommended("quality"), _f.Selections.Get("desktop", EngineFixture.GameId));
    }

    [Fact]
    public void ExplicitPreset_Unknown_Throws()
    {
        Assert.Throws<ConfigException>(() => _f.Load("desktop", PresetRef.Recommended("nope")));
        Assert.Throws<ConfigException>(() => _f.Load("desktop", PresetRef.Manual("nope")));
    }

    [Theory]
    [InlineData("recommended:deleted")]
    [InlineData("manual:deleted")]
    public void StaleSelection_FallsBackToDefault(string stale)
    {
        _f.Selections.Set("desktop", EngineFixture.GameId, PresetRef.Parse(stale));
        var ctx = _f.Load("desktop");
        Assert.Equal(PresetRef.Recommended("performance"), ctx.Preset);
        Assert.False(ctx.IsSelected);
        Assert.Null(ctx.Save);
    }

    [Fact]
    public void StaleSelection_SaveThatNoLongerResolves_FallsBack()
    {
        var save = _f.WriteSave("desktop", "Broken", new() { ["gone-file"] = new Dictionary<string, object?> { ["A"] = "1" } });
        _f.Selections.Set("desktop", EngineFixture.GameId, PresetRef.Manual(save.Slug));
        var ctx = _f.Load("desktop");
        Assert.Equal(PresetRef.Recommended("performance"), ctx.Preset);
        Assert.Null(ctx.Save);
    }

    [Fact]
    public void MalformedSelectionEntry_FallsBack()
    {
        Directory.CreateDirectory(_f.Paths.DataDir);
        File.WriteAllText(_f.Paths.SelectionsFile, $$"""{ "desktop/{{EngineFixture.GameId}}": "bogus:x" }""");
        var ctx = _f.Load("desktop");
        Assert.Equal(PresetRef.Recommended("performance"), ctx.Preset);
    }

    [Fact]
    public void Targets_AreFlattenedPerFile()
    {
        var ctx = _f.Load("desktop", PresetRef.Recommended("performance"));
        var user = ctx.Files.Single(f => f.Id == "user");
        Assert.Equal(new Dictionary<string, string> { ["[S]Shadows"] = "1", ["[S]Textures"] = "3" }, user.Targets);
        var engine = ctx.Files.Single(f => f.Id == "engine");
        Assert.Equal(new Dictionary<string, string> { ["[E]Lod"] = "1" }, engine.Targets);
        Assert.Empty(ctx.Files.Single(f => f.Id == "blob").Targets);
        Assert.Equal(_f.GameFile("user.ini"), user.Path);
        Assert.Null(user.Error);
        Assert.All(ctx.Files, f => Assert.Null(f.SnapshotFile));
    }

    [Fact]
    public void DriverTargets_FollowDeviceDriver()
    {
        var desk = _f.Load("desktop", PresetRef.Recommended("quality"));
        Assert.Equal("120", desk.DriverTargets["FrameRateLimiter"]);
        Assert.Equal("2560x1440@165Hz", desk.Profile.Display!.ToString());

        var amdDef = new DeviceDef { Driver = "amd" };
        var amd = GameContext.Load(_f.Store, EngineFixture.GameId, new("desktop", amdDef, "test", "amd"),
            PresetRef.Recommended("quality"), _f.Resolver);
        Assert.Empty(amd.DriverTargets);
    }

    [Fact]
    public void ManualSave_WiresSaveAndSnapshots()
    {
        var save = _f.WriteSave("desktop", "Steady",
            new() { ["extends"] = "base", ["user"] = new Dictionary<string, object?> { ["S"] = new Dictionary<string, object?> { ["Fps"] = "40" } } },
            new() { ["blob"] = [1, 2, 3] });
        _f.Selections.Set("desktop", EngineFixture.GameId, PresetRef.Manual(save.Slug));

        var ctx = _f.Load("desktop");
        Assert.Equal(PresetRef.Manual("steady"), ctx.Preset);
        Assert.True(ctx.IsSelected);
        Assert.NotNull(ctx.Save);
        Assert.Equal("Steady", ctx.Save!.Name);
        Assert.Equal("Steady", ctx.Profile.Label);

        var user = ctx.Files.Single(f => f.Id == "user");
        Assert.Equal("40", user.Targets["[S]Fps"]);
        Assert.Equal("3", user.Targets["[S]Shadows"]); // via extends: base
        Assert.Null(user.SnapshotFile);
        Assert.Equal(Path.Combine(save.Dir, "blob.snap"), ctx.Files.Single(f => f.Id == "blob").SnapshotFile);
        Assert.Null(ctx.Files.Single(f => f.Id == "engine").SnapshotFile);
    }

    [Fact]
    public void ManualSave_OtherDevicesSaveIsNotVisible()
    {
        var save = _f.WriteSave("ally-x", "Handheld", new());
        Assert.Throws<ConfigException>(() => _f.Load("desktop", PresetRef.Manual(save.Slug)));
    }

    [Fact]
    public void UnresolvedPath_SetsError()
    {
        using var f = new EngineFixture(EngineFixture.GameYaml.Replace("\"{T}/engine.ini\"", "\"{T}/nothere*/engine.ini\""));
        var ctx = f.Load("desktop");
        var engine = ctx.Files.Single(x => x.Id == "engine");
        Assert.Null(engine.Path);
        Assert.Contains("no file matches", engine.Error);
    }

    [Fact]
    public void DefaultFor()
    {
        var game = _f.Store.LoadGame(EngineFixture.GameId);
        Assert.Equal("performance", GameContext.DefaultFor(game, _f.Device("desktop")));
        Assert.Equal("battery", GameContext.DefaultFor(game, _f.Device("ally-x")));
        Assert.Null(GameContext.DefaultFor(game, _f.Device("laptop")));
    }
}
