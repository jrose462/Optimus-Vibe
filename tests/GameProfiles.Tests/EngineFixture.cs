using GameProfiles.Config;
using GameProfiles.Devices;
using GameProfiles.Engine;
using GameProfiles.Logging;

namespace GameProfiles.Tests;

/// <summary>
/// A throwaway config folder (devices.yaml + games/testgame.yaml), data dir and "game install" folder for engine tests.
/// Game files live under {T}, a path token resolved to <see cref="GameDir"/>. AutoSync is off in settings.json.
/// </summary>
public sealed class EngineFixture : IDisposable
{
    public const string GameId = "testgame";

    public const string DevicesYaml = """
        devices:
          desktop:
            match: { gpu: "NVIDIA*" }
            display: { width: 2560, height: 1440, refresh: 165 }
            driver: nvidia
            defaultPreset: performance
          ally-x:
            match: { cpu: "*Z2*" }
            display: { width: 1920, height: 1080, refresh: 120 }
            driver: amd
          laptop:
            match: { model: "Laptop*" }
        """;

    public const string GameYaml = """
        name: Test Game
        store: other
        processes: [Test.exe]
        files:
          - id: user
            handler: ini
            path: "{T}/user.ini"
          - id: engine
            handler: ini
            path: "{T}/engine.ini"
          - id: blob
            handler: file
            path: "{T}/blob.bin"
        profiles:
          base:
            user: { S: { Shadows: 3, Textures: 3 } }
        presets:
          desktop:
            quality:
              extends: base
              label: Best looking
              targetFps: 120
              display: inherit
              nvidia: { FrameRateLimiter: 120 }
              user: { S: { Fps: 0 } }
            performance:
              extends: base
              user: { S: { Shadows: 1 } }
              engine: { E: { Lod: 1 } }
          ally-x:
            turbo:
              extends: base
            battery:
              extends: base
              user: { S: { Fps: 40 } }
        """;

    public TempDir Tmp { get; } = new();
    public AppPaths Paths { get; }
    public ConfigStore Store { get; }
    public string GameDir { get; }
    public PathResolver Resolver { get; }
    public DevicesFile Devices { get; }

    public EngineFixture(string? gameYaml = null)
    {
        Log.Quiet = true;
        var root = Tmp.Dir("repo", "config");
        Tmp.Write(Path.Combine("repo", "config", "devices.yaml"), DevicesYaml);
        Tmp.Write(Path.Combine("repo", "config", "games", GameId + ".yaml"), gameYaml ?? GameYaml);
        Paths = new AppPaths { ConfigRoot = root, DataDir = Tmp.Dir("data") };
        AppPaths.SaveSettings(Paths.DataDir, new AppSettings { AutoSync = false });
        Store = new ConfigStore(Paths);
        GameDir = Tmp.Dir("game");
        Resolver = new PathResolver(overrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["T"] = GameDir });
        Devices = Store.LoadDevices();
    }

    public string GameFile(string name) => Path.Combine(GameDir, name);

    public void WriteGameFile(string name, string text) => File.WriteAllText(GameFile(name), text);

    public string ReadGameFile(string name) => File.ReadAllText(GameFile(name));

    public DeviceSelection Device(string name, DeviceDef? def = null) =>
        new(name, def ?? Devices.Devices.GetValueOrDefault(name), "test", def?.Driver ?? Devices.Devices.GetValueOrDefault(name)?.Driver ?? "none");

    public GameContext Load(string device = "desktop", PresetRef? preset = null, DeviceDef? def = null) =>
        GameContext.Load(Store, GameId, Device(device, def), preset, Resolver);

    public SelectionStore Selections => new(Paths.SelectionsFile);

    /// <summary>Write a manual save by hand (body + optional snapshots).</summary>
    public ManualSave WriteSave(string device, string name, Dictionary<string, object?> body, Dictionary<string, byte[]>? snaps = null)
    {
        var save = new ManualSave { Name = name, Created = DateTime.Now, Updated = DateTime.Now, Body = body };
        Store.WriteSave(GameId, device, save, snaps);
        return save;
    }

    public void Dispose() => Tmp.Dispose();
}
