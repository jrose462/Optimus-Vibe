using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Engine;

namespace OptimusVibe.Tests;

public sealed class PresetsFromTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static GameDef GameWith(params (string Device, string Preset)[] presets)
    {
        var game = new GameDef { Id = "g", Files = [new FileDef { Id = "user", Handler = "ini" }] };
        foreach (var (device, preset) in presets)
        {
            if (!game.Presets.TryGetValue(device, out var map)) game.Presets[device] = map = new();
            map[preset] = new Dictionary<string, object?> { ["user"] = new Dictionary<string, object?> { ["S"] = new Dictionary<string, object?> { ["Fps"] = device } } };
        }
        return game;
    }

    private static readonly DeviceDef Laptop = new() { PresetsFrom = "desktop", DefaultPreset = "performance" };

    [Fact]
    public void NoOwnPresets_FallsBackToPresetsFromDevice()
    {
        var game = GameWith(("desktop", "quality"), ("desktop", "performance"));
        Assert.Equal(["quality", "performance"], ProfileResolver.PresetNames(game, "laptop", Laptop));
        Assert.Empty(ProfileResolver.PresetNames(game, "laptop", null));
        var p = ProfileResolver.ResolvePreset(game, "laptop", Laptop, "quality");
        Assert.Equal("laptop", p.Device);
        Assert.Equal("desktop", ((Dictionary<string, object?>)p.FileBlocks["user"]["S"]!)["Fps"]);
    }

    [Fact]
    public void OwnPresetsWin_OverPresetsFrom()
    {
        var game = GameWith(("desktop", "quality"), ("laptop", "battery"));
        Assert.Equal(["battery"], ProfileResolver.PresetNames(game, "laptop", Laptop));
        Assert.Throws<ConfigException>(() => ProfileResolver.ResolvePreset(game, "laptop", Laptop, "quality"));
    }

    [Fact]
    public void FallbackIsOneLevel_NotChained()
    {
        var game = GameWith(("desktop", "quality"));
        var a = new DeviceDef { PresetsFrom = "b" };
        Assert.Empty(ProfileResolver.PresetNames(game, "a", a)); // b has none of its own and would itself point at desktop: not followed
    }

    [Fact]
    public void SelfReference_IsIgnored()
    {
        var game = GameWith(("desktop", "quality"));
        Assert.Empty(ProfileResolver.PresetNames(game, "x", new DeviceDef { PresetsFrom = "x" }));
    }

    [Fact]
    public void DefaultFor_UsesPresetsFromAndDeviceDefault()
    {
        var game = GameWith(("desktop", "quality"), ("desktop", "performance"));
        var sel = new DeviceSelection("laptop", Laptop, "test", "none");
        Assert.Equal("performance", GameContext.DefaultFor(game, sel));
        Assert.Equal("quality", GameContext.DefaultFor(game, sel with { Def = new DeviceDef { PresetsFrom = "desktop", DefaultPreset = "missing" } }));
    }

    // ---------------------------------------------------------------- validator

    private ValidationReport Validate(string devices, string? game = null)
    {
        var root = _tmp.Dir("config");
        _tmp.Write(Path.Combine("config", "devices.yaml"), devices);
        if (game is not null) _tmp.Write(Path.Combine("config", "games", "g.yaml"), game);
        return Validator.Run(new ConfigStore(new AppPaths { ConfigRoot = root, DataDir = _tmp.Dir("data") }));
    }

    [Fact]
    public void Validator_UnknownPresetsFrom_IsError()
    {
        var r = Validate("devices:\n  a:\n    match: { cpu: \"*\" }\n    presetsFrom: nope\n");
        Assert.Contains(r.Errors, e => e.Contains("devices.a") && e.Contains("presetsFrom") && e.Contains("nope"));
    }

    [Fact]
    public void Validator_SelfAndCycle_AreErrors()
    {
        var self = Validate("devices:\n  a:\n    match: { cpu: \"*\" }\n    presetsFrom: a\n");
        Assert.Contains(self.Errors, e => e.Contains("itself"));
        var cycle = Validate("devices:\n  a:\n    match: { cpu: x }\n    presetsFrom: b\n  b:\n    match: { cpu: y }\n    presetsFrom: a\n");
        Assert.Contains(cycle.Errors, e => e.Contains("cycle"));
    }

    [Fact]
    public void Validator_ChainOfTwo_WarnsOnly()
    {
        var r = Validate("devices:\n  a:\n    match: { cpu: x }\n    presetsFrom: b\n  b:\n    match: { cpu: y }\n    presetsFrom: c\n  c:\n    match: { cpu: z }\n");
        Assert.DoesNotContain(r.Errors, e => e.Contains("presetsFrom"));
        Assert.Contains(r.Warnings, w => w.Contains("only one level"));
    }

    [Fact]
    public void Validator_DeviceWithPresetsFrom_HasNoMissingPresetsWarning()
    {
        const string game = """
            name: G
            store: steam
            steamAppId: 1
            cloudSyncsSettings: false
            processes: [g.exe]
            files:
              - id: user
                handler: ini
                path: "{Documents}/g/user.ini"
            presets:
              desktop:
                quality: { user: { S: { Fps: 60 } } }
            """;
        var r = Validate("devices:\n  desktop:\n    match: { gpu: \"NVIDIA*\" }\n  laptop:\n    match: { gpu: \"AMD*\" }\n    presetsFrom: desktop\n", game);
        Assert.Empty(r.Errors);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("laptop"));
    }
}
