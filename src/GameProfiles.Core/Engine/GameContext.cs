using GameProfiles.Config;
using GameProfiles.Devices;
using GameProfiles.Handlers;

namespace GameProfiles.Engine;

/// <summary>One configured file/registry key, resolved for this machine and profile.</summary>
public sealed class ResolvedFile
{
    public required FileDef Def { get; init; }
    public required SettingsHandler Handler { get; init; }

    /// <summary>Expanded path or registry key; null when it could not be resolved (see <see cref="Error"/>).</summary>
    public string? Path { get; init; }
    public string? Error { get; init; }

    /// <summary>Flat key → target value from the resolved profile (base + capture overlay).</summary>
    public Dictionary<string, string> Targets { get; init; } = new();

    /// <summary>Whole-file snapshot captured for this device, applied before key targets.</summary>
    public string? SnapshotFile { get; init; }

    public string Id => Def.Id;
}

/// <summary>Everything needed to act on one game on this machine.</summary>
public sealed class GameContext
{
    public required AppPaths Paths { get; init; }
    public required ConfigStore Store { get; init; }
    public required GameDef Game { get; init; }
    public required DeviceSelection Device { get; init; }
    public required ResolvedProfile Profile { get; init; }
    public required List<ResolvedFile> Files { get; init; }

    /// <summary>Driver settings for this machine's vendor (nvidia: or amd: block).</summary>
    public IReadOnlyDictionary<string, string> DriverTargets =>
        Device.Driver switch
        {
            "nvidia" => Profile.Nvidia,
            "amd" => Profile.Amd,
            _ => new Dictionary<string, string>(),
        };

    public static DeviceSelection SelectDevice(ConfigStore store, string? profileOption)
    {
        var settings = AppPaths.LoadSettings(store.Paths.DataDir);
        var forced = profileOption
                     ?? Environment.GetEnvironmentVariable("GAMEPROFILES_PROFILE")
                     ?? settings.ForcedProfile;
        // Hardware is detected even when forced: the driver vendor comes from the real GPUs.
        return DeviceDetector.Select(store.LoadDevices(), DeviceDetector.Detect(), forced);
    }

    public static GameContext Load(AppPaths paths, string gameId, string? profileOption, PathResolver? resolver = null)
    {
        var store = new ConfigStore(paths);
        var device = SelectDevice(store, profileOption);
        return Load(store, gameId, device, resolver);
    }

    public static GameContext Load(ConfigStore store, string gameId, DeviceSelection device, PathResolver? resolver = null)
    {
        resolver ??= new PathResolver();
        var game = store.LoadGame(gameId);
        var capture = store.LoadCapture(game.Id, device.Name);
        var profile = ProfileResolver.Resolve(game, device.Name, device.Def, capture);
        var snapshotDir = store.Paths.SnapshotDir(game.Id, device.Name);

        var files = new List<ResolvedFile>();
        foreach (var def in game.Files)
        {
            var handler = SettingsHandler.For(def.Handler);
            string? path = null, error = null;
            try
            {
                path = handler.IsFile ? resolver.ResolveFile(def.Path) : resolver.Expand(def.Path);
                if (path is null) error = $"no file matches {def.Path}";
            }
            catch (PathResolveException ex)
            {
                error = ex.Message;
            }

            var snapshot = Path.Combine(snapshotDir, def.Id + ".snap");
            files.Add(new ResolvedFile
            {
                Def = def,
                Handler = handler,
                Path = path,
                Error = error,
                Targets = profile.FileBlocks.TryGetValue(def.Id, out var block) ? handler.Flatten(block) : new(),
                SnapshotFile = File.Exists(snapshot) ? snapshot : null,
            });
        }

        return new GameContext
        {
            Paths = store.Paths,
            Store = store,
            Game = game,
            Device = device,
            Profile = profile,
            Files = files,
        };
    }
}
