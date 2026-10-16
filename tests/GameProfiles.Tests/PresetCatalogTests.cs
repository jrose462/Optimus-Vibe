using GameProfiles.Config;
using GameProfiles.Devices;
using GameProfiles.Engine;

namespace GameProfiles.Tests;

public sealed class PresetCatalogTests : IDisposable
{
    private readonly EngineFixture _f = new();
    private const string G = EngineFixture.GameId;

    public void Dispose() => _f.Dispose();

    private GameDef Game => _f.Store.LoadGame(G);

    [Fact]
    public void Recommended_ListsPresetsInOrder_WithMetadata()
    {
        var list = PresetCatalog.Recommended(Game, _f.Device("desktop"), PresetRef.Recommended("quality"));
        Assert.Equal(["quality", "performance"], list.Select(o => o.Ref.Name));
        Assert.All(list, o => Assert.Equal(PresetKind.Recommended, o.Ref.Kind));

        var q = list[0];
        Assert.Equal("Best looking", q.Label);
        Assert.Equal(120, q.TargetFps);
        Assert.True(q.IsActive);
        Assert.False(q.IsDefault);
        Assert.Null(q.Error);

        var p = list[1];
        Assert.Equal("Performance", p.Label);
        Assert.False(p.IsActive);
        Assert.True(p.IsDefault); // devices.yaml defaultPreset
    }

    [Fact]
    public void Recommended_NoActive_NoneActive_FirstIsDefaultWithoutDeviceDefault()
    {
        var list = PresetCatalog.Recommended(Game, _f.Device("ally-x"), null);
        Assert.Equal(["battery", "turbo"], list.Select(o => o.Ref.Name));
        Assert.All(list, o => Assert.False(o.IsActive));
        Assert.True(list[0].IsDefault);
        Assert.False(list[1].IsDefault);
    }

    [Fact]
    public void Recommended_ManualActive_NoneActive()
    {
        var list = PresetCatalog.Recommended(Game, _f.Device("desktop"), PresetRef.Manual("quality"));
        Assert.All(list, o => Assert.False(o.IsActive));
    }

    [Fact]
    public void Recommended_DeviceWithoutPresets_IsEmpty() =>
        Assert.Empty(PresetCatalog.Recommended(Game, _f.Device("laptop"), null));

    [Fact]
    public void Recommended_BrokenPreset_IsErrorEntry()
    {
        var game = Game;
        game.Presets["desktop"]["broken"] = new() { ["extends"] = "ghost" };
        var list = PresetCatalog.Recommended(game, _f.Device("desktop"), PresetRef.Recommended("broken"));
        var broken = list.Single(o => o.Ref.Name == "broken");
        Assert.NotNull(broken.Error);
        Assert.Contains("ghost", broken.Error);
        Assert.Equal("broken", broken.Label);
        Assert.True(broken.IsActive);
        Assert.Equal(3, list.Count);
        Assert.Null(list.Single(o => o.Ref.Name == "quality").Error);
    }

    [Fact]
    public void Recommended_BadTargetFps_IsErrorEntry()
    {
        var game = Game;
        game.Presets["desktop"]["bad"] = new() { ["targetFps"] = "sixty" };
        var list = PresetCatalog.Recommended(game, _f.Device("desktop"), null);
        Assert.NotNull(list.Single(o => o.Ref.Name == "bad").Error);
    }

    [Fact]
    public void Manual_ListsSaves_NewestFirst_WithActive()
    {
        var older = new ManualSave { Name = "Older", Notes = "n", Created = DateTime.Now.AddDays(-2), Updated = DateTime.Now.AddDays(-2) };
        var newer = new ManualSave { Name = "Newer", Created = DateTime.Now.AddDays(-1), Updated = DateTime.Now.AddDays(-1) };
        _f.Store.WriteSave(G, "desktop", older);
        _f.Store.WriteSave(G, "desktop", newer);
        _f.Store.WriteSave(G, "ally-x", new ManualSave { Name = "Other device", Created = DateTime.Now, Updated = DateTime.Now });

        var list = PresetCatalog.Manual(_f.Store, Game, _f.Device("desktop"), PresetRef.Manual("older"));
        Assert.Equal(["Newer", "Older"], list.Select(o => o.Label));
        Assert.Equal(PresetRef.Manual("newer"), list[0].Ref);
        Assert.False(list[0].IsActive);
        Assert.True(list[1].IsActive);
        Assert.Equal("n", list[1].Description);
        Assert.NotNull(list[1].Save);
        Assert.All(list, o => Assert.False(o.IsDefault));

        Assert.All(PresetCatalog.Manual(_f.Store, Game, _f.Device("desktop"), PresetRef.Recommended("older")), o => Assert.False(o.IsActive));
    }

    [Fact]
    public void Manual_None_IsEmpty() => Assert.Empty(PresetCatalog.Manual(_f.Store, Game, _f.Device("desktop"), null));
}
