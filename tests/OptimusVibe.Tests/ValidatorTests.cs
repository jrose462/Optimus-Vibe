using OptimusVibe.Config;
using OptimusVibe.Engine;

namespace OptimusVibe.Tests;

public sealed class ValidatorTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly ConfigStore _store;

    private const string Devices = """
        devices:
          desktop:
            match: { gpu: "NVIDIA*" }
            display: { width: 2560, height: 1440, refresh: 165 }
            defaultPreset: quality
          ally-x:
            match: { cpu: "*Z2*" }
            driver: amd
            defaultPreset: performance
        """;

    private const string ValidGame = """
        name: Sample
        store: steam
        steamAppId: 123
        cloudSyncsSettings: false
        processes: [Sample.exe]
        files:
          - id: user
            handler: ini
            path: "{Documents}/Sample/user.ini"
          - id: prefs
            handler: json
            path: "{LocalAppData}/Sample/prefs.json"
        profiles:
          base:
            user: { S: { Shadows: 3 } }
        presets:
          desktop:
            quality:
              extends: base
              label: Quality
              targetFps: 165
              display: inherit
              nvidia: { FrameRateLimiter: 165 }
              prefs: { "graphics.preset": High }
          ally-x:
            performance:
              extends: base
              display: { refresh: 120 }
              amd: { AFMF: false }
              user: { S: { Fps: 60 } }
            battery:
              extends: base
              user: { S: { Fps: 40 } }
        """;

    public ValidatorTests()
    {
        var root = _tmp.Dir("config");
        _tmp.Write(Path.Combine("config", "devices.yaml"), Devices);
        _store = new ConfigStore(new AppPaths { ConfigRoot = root, DataDir = _tmp.Dir("data") });
    }

    public void Dispose() => _tmp.Dispose();

    private ValidationReport Run(string gameYaml)
    {
        _tmp.Write(Path.Combine("config", "games", "sample.yaml"), gameYaml);
        return Validator.Run(_store);
    }

    [Fact]
    public void ValidSample_NoErrorsOrWarnings()
    {
        var r = Run(ValidGame);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Warnings);
        Assert.Equal(1, r.GamesChecked);
    }

    [Fact]
    public void AmdKeysAreValidated()
    {
        var r = Run(ValidGame.Replace("amd: { AFMF: false }", "amd: { AfmfEnabled: maybe, Turbo: 1, ChillMaxFps: 45 }"));
        Assert.Contains(r.Errors, e => e.Contains("amd.AfmfEnabled"));
        Assert.Contains(r.Errors, e => e.Contains("amd.Turbo") && e.Contains("unknown amd setting"));
        Assert.DoesNotContain(r.Errors, e => e.Contains("amd.ChillMaxFps"));
    }

    [Fact]
    public void AmdAliasAndCanonicalKeyInOneBlock_IsError()
    {
        var r = Run(ValidGame.Replace("amd: { AFMF: false }", "amd: { AFMF: false, AfmfEnabled: true }"));
        Assert.Contains(r.Errors, e => e.Contains("set the same setting"));
    }

    [Fact]
    public void AmdChillMinAboveMax_IsError()
    {
        var r = Run(ValidGame.Replace("amd: { AFMF: false }", "amd: { ChillMinFps: 100, ChillMaxFps: 50 }"));
        Assert.Contains(r.Errors, e => e.Contains("ChillMinFps") && e.Contains("above"));
    }

    [Fact]
    public void AmdBlockInABaseProfile_IsValidatedToo()
    {
        var r = Run(ValidGame.Replace("base:\n    user: { S: { Shadows: 3 } }", "base:\n    user: { S: { Shadows: 3 } }\n    amd: { Turbo: 1, AFMF: true, AfmfEnabled: true }")
            .Replace("          base:\r\n            user: { S: { Shadows: 3 } }", "          base:\r\n            user: { S: { Shadows: 3 } }\r\n            amd: { Turbo: 1, AFMF: true, AfmfEnabled: true }")
            .Replace("          base:\n            user: { S: { Shadows: 3 } }", "          base:\n            user: { S: { Shadows: 3 } }\n            amd: { Turbo: 1, AFMF: true, AfmfEnabled: true }"));
        Assert.Contains(r.Errors, e => e.Contains("profiles.base") && e.Contains("amd.Turbo"));
        Assert.Contains(r.Errors, e => e.Contains("profiles.base") && e.Contains("set the same setting"));
    }

    [Fact]
    public void ValidSample_WithValidSave_NoErrors()
    {
        _store.WriteSave("sample", "desktop", new ManualSave
        {
            Name = "Mine", Created = DateTime.Now, Updated = DateTime.Now,
            Body = new() { ["extends"] = "base", ["user"] = new Dictionary<string, object?> { ["S"] = new Dictionary<string, object?> { ["Fps"] = "50" } } },
        });
        var r = Run(ValidGame);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void PresetsForUnknownDevice_IsError()
    {
        var r = Run(ValidGame + """

              steamdeck:
                battery: { user: { S: { Fps: 30 } } }
            """);
        Assert.Contains(r.Errors, e => e.Contains("presets.steamdeck") && e.Contains("not a device"));
    }

    [Fact]
    public void ProfileNotExtended_IsWarning()
    {
        var yaml = ValidGame.Replace("""
            profiles:
              base:
            """, """
            profiles:
              unused:
                user: { S: { Shadows: 0 } }
              base:
            """);
        var r = Run(yaml);
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Contains("profiles.unused") && w.Contains("not extended"));
    }

    [Fact]
    public void ProfileExtendedOnlyByAnotherProfile_IsNotWarned()
    {
        var yaml = ValidGame
            .Replace("""
                profiles:
                  base:
                """, """
                profiles:
                  root:
                    user: { S: { Textures: 1 } }
                  base:
                    extends: root
                """);
        var r = Run(yaml);
        Assert.Empty(r.Errors);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("profiles.root"));
    }

    [Fact]
    public void DefaultPresetMissing_IsWarning()
    {
        var r = Run(ValidGame.Replace("    performance:\n", "    turbo:\n").Replace("    performance:\r\n", "    turbo:\r\n"));
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Contains("presets.ally-x has no 'performance'"));
    }

    [Fact]
    public void DeviceWithoutPresets_IsWarning()
    {
        var yaml = ValidGame[..ValidGame.IndexOf("  ally-x:", StringComparison.Ordinal)];
        var r = Run(yaml);
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Contains("no recommended presets for device 'ally-x'"));
    }

    [Fact]
    public void InvalidSave_IsError()
    {
        _store.WriteSave("sample", "ally-x", new ManualSave
        {
            Name = "Broken", Created = DateTime.Now, Updated = DateTime.Now,
            Body = new() { ["bogus"] = new Dictionary<string, object?> { ["A"] = "1" } },
        });
        var r = Run(ValidGame);
        Assert.Contains(r.Errors, e => e.StartsWith("saves/sample/ally-x/broken:") && e.Contains("bogus"));
    }

    [Fact]
    public void SaveExtendingUnknownProfile_IsError()
    {
        _store.WriteSave("sample", "desktop", new ManualSave
        {
            Name = "Ghost", Created = DateTime.Now, Updated = DateTime.Now, Body = new() { ["extends"] = "ghost" },
        });
        var r = Run(ValidGame);
        Assert.Contains(r.Errors, e => e.StartsWith("saves/sample/desktop/ghost:"));
    }

    [Fact]
    public void UnreadableSaveYaml_IsError()
    {
        _tmp.Write(Path.Combine("config", "saves", "sample", "desktop", "bad", "save.yaml"), "name: [unclosed\n");
        var r = Run(ValidGame);
        Assert.Contains(r.Errors, e => e.StartsWith("saves/sample/desktop"));
    }

    [Fact]
    public void PresetExtendsUnknown_IsError()
    {
        var r = Run(ValidGame.Replace("""
                battery:
                  extends: base
            """, """
                battery:
                  extends: nothing
            """));
        Assert.Contains(r.Errors, e => e.Contains("nothing"));
    }

    [Fact]
    public void PresetBadTargetFps_IsError()
    {
        var r = Run(ValidGame.Replace("targetFps: 165", "targetFps: fast"));
        Assert.Single(r.Errors);
    }

    [Fact]
    public void PresetSetsUnknownFile_IsError()
    {
        var r = Run(ValidGame.Replace("prefs: { \"graphics.preset\": High }", "graphics: { a: 1 }"));
        Assert.Contains(r.Errors, e => e.Contains("graphics"));
    }

    [Fact]
    public void PresetSetsNothing_IsWarning()
    {
        var empty = Run(ValidGame.Replace("""
                battery:
                  extends: base
                  user: { S: { Fps: 40 } }
            """, """
                battery: {}
            """));
        Assert.Empty(empty.Errors);
        Assert.Contains(empty.Warnings, w => w.Contains("presets.ally-x.battery sets nothing"));
    }

    [Fact]
    public void ReservedFileId_IsError()
    {
        var r = Run(ValidGame.Replace("- id: prefs", "- id: label").Replace("prefs: {", "label: {"));
        Assert.Contains(r.Errors, e => e.Contains("files.label") && e.Contains("reserved"));
    }

    [Fact]
    public void LaunchAndTimingFields_ValidAreAccepted()
    {
        var r = Run(ValidGame.Replace("processes: [Sample.exe]", """
            processes: [Sample.exe]
            applyTiming: both
            settleSeconds: 8
            archetype: binary-truth
            mappable: true
            launch:
              uri: steam://rungameid/123
              args: ["+com_adaptiveTickMaxHz", "120"]
              waitSeconds: 600
              launcherProcesses: [EADesktop.exe]
            """).Replace("targetFps: 165", "targetFps: 165\n      launch: { args: [-skipStartScreen] }").Replace("display: inherit", "display: keep"));
        Assert.Empty(r.Errors);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void LaunchAndTimingFields_BadValuesAreErrors()
    {
        var r = Run(ValidGame.Replace("processes: [Sample.exe]", """
            processes: [Sample.exe]
            applyTiming: sometimes
            settleSeconds: 999
            archetype: mystery
            launch:
              uri: steam://rungameid/123
              waitSeconds: 0
              launcherProcesses: [Launcher]
            """));
        Assert.Contains(r.Errors, e => e.Contains("applyTiming"));
        Assert.Contains(r.Errors, e => e.Contains("settleSeconds"));
        Assert.Contains(r.Errors, e => e.Contains("archetype"));
        Assert.Contains(r.Errors, e => e.Contains("waitSeconds"));
        Assert.Contains(r.Errors, e => e.Contains("launcherProcesses"));
    }

    [Fact]
    public void LaunchArgs_AsOneStringWithValue_WarnsAndBadPresetLaunchIsAnError()
    {
        var r = Run(ValidGame.Replace("processes: [Sample.exe]", """
            processes: [Sample.exe]
            launch:
              uri: steam://rungameid/123
              args: ["+cvar 120"]
            """).Replace("targetFps: 165", "targetFps: 165\n      launch: { exe: x }"));
        Assert.Contains(r.Warnings, w => w.Contains("single tokens"));
        Assert.Contains(r.Errors, e => e.Contains("launch.exe"));
    }
}
