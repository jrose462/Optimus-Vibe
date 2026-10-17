using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Engine;

namespace OptimusVibe.App.Services;

/// <summary>A device to pick in a button list: the id is what gets forced, the name is what people read.</summary>
public sealed record DeviceChoice(string Id, string Name);

/// <summary>Resolved config folder, store and detected device for this run of the app. Immutable; Refresh makes a new one.</summary>
public sealed class AppSession
{
    public required AppPaths Paths { get; init; }
    public required ConfigStore Store { get; init; }
    public required DeviceSelection Device { get; init; }
}

/// <summary>Outcome of starting up: ready, or needs the user to pick a config folder / device.</summary>
public sealed class StartupResult
{
    public AppSession? Session { get; init; }
    public List<GameSnapshot> Games { get; init; } = new();

    /// <summary>Why setup is needed (no config folder, unknown device...).</summary>
    public string? SetupMessage { get; init; }

    /// <summary>Set when the config folder is fine but this machine matches no device: choose one to force.</summary>
    public List<DeviceChoice> DeviceChoices { get; init; } = new();

    public string? ConfigRoot { get; init; }

    /// <summary>
    /// Same resolution order as the CLI (OPTIMUSVIBE_CONFIG, settings.json, a config folder near the exe).
    /// Slow (WMI device detection, every game file): call off the UI thread.
    /// </summary>
    public static StartupResult Load()
    {
        AppPaths paths;
        try
        {
            paths = AppPaths.Resolve(null);
        }
        catch (ConfigException ex)
        {
            return new StartupResult { SetupMessage = ex.Message };
        }
        if (!File.Exists(paths.DevicesFile))
            return new StartupResult { SetupMessage = $"{paths.ConfigRoot} has no devices.yaml — pick the repo's config folder.", ConfigRoot = paths.ConfigRoot };

        var store = new ConfigStore(paths);
        DeviceSelection device;
        try
        {
            device = GameContext.SelectDevice(store, null);
        }
        catch (ConfigException ex)
        {
            List<DeviceChoice> choices;
            try { choices = store.LoadDevices().Devices.Select(kv => new DeviceChoice(kv.Key, DeviceNames.Display(kv.Key, kv.Value))).ToList(); }
            catch (Exception) { choices = new(); }
            return new StartupResult { SetupMessage = ex.Message, DeviceChoices = choices, ConfigRoot = paths.ConfigRoot };
        }

        var session = new AppSession { Paths = paths, Store = store, Device = device };
        var games = store.GameIds().Select(id => GameSnapshot.Load(session, id)).ToList();
        return new StartupResult { Session = session, Games = games, ConfigRoot = paths.ConfigRoot };
    }
}
