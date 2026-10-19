using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Display;
using OptimusVibe.Handlers;
using OptimusVibe.Logging;

namespace OptimusVibe.Engine;

/// <summary>One configured file/registry key, resolved for this machine and the active preset.</summary>
public sealed class ResolvedFile
{
    public required FileDef Def { get; init; }
    public required SettingsHandler Handler { get; init; }

    /// <summary>Expanded path or registry key; null when it could not be resolved (see <see cref="Error"/>).</summary>
    public string? Path { get; init; }
    public string? Error { get; init; }

    /// <summary>Flat key → target value from the active preset or manual save.</summary>
    public Dictionary<string, string> Targets { get; init; } = new();

    /// <summary>Whole-file snapshot from the active manual save, applied before key targets.</summary>
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

    /// <summary>The manual save in use, when the active preset is a manual one.</summary>
    public ManualSave? Save { get; init; }

    /// <summary>True when the preset came from an explicit choice (app/CLI) rather than the device default.</summary>
    public bool IsSelected { get; init; }

    public PresetRef? Preset => Profile.Source;

    /// <summary>Big Picture detection result for this load (null when not asked for or suppressed).</summary>
    public BigPictureResult? BigPicture { get; init; }

    /// <summary>The Big Picture preset was chosen automatically for this load.</summary>
    public bool BigPictureUsed { get; init; }

    /// <summary>The user opted this game out of Big Picture on this device.</summary>
    public bool BigPictureOptedOut { get; init; }

    /// <summary>Set when the TV is in use but the game has no Big Picture preset: "No Big Picture preset for X; using Y".</summary>
    public string? BigPictureNote { get; init; }

    /// <summary>Display device to change for this run: the TV when it was detected as a display, else null (the primary).</summary>
    public string? DisplayTarget => BigPictureUsed ? BigPicture?.TargetDisplay : null;

    public SelectionStore Selections => new(Paths.SelectionsFile);

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
                     ?? AppPaths.Env("PROFILE")
                     ?? settings.ForcedProfile;
        // Hardware is detected even when forced: the driver vendor comes from the real GPUs.
        return DeviceDetector.Select(store.LoadDevices(), DeviceDetector.Detect(), forced);
    }

    public static GameContext Load(AppPaths paths, string gameId, string? profileOption, PresetRef? preset = null,
        PathResolver? resolver = null, BigPictureOptions? bigPicture = null)
    {
        var store = new ConfigStore(paths);
        var device = SelectDevice(store, profileOption);
        return Load(store, gameId, device, preset, resolver, bigPicture);
    }

    /// <summary>
    /// Resolve the game for a device. The preset is, in order: <paramref name="preset"/>, the remembered selection,
    /// the device's defaultPreset, the device's first preset. A stale selection (deleted save/preset) falls back.
    /// </summary>
    public static GameContext Load(ConfigStore store, string gameId, DeviceSelection device, PresetRef? preset = null,
        PathResolver? resolver = null, BigPictureOptions? bigPicture = null)
    {
        resolver ??= new PathResolver();
        var game = store.LoadGame(gameId);
        var selections = new SelectionStore(store.Paths.SelectionsFile);
        var chosen = preset;
        if (chosen is null)
        {
            try { chosen = selections.Get(device.Name, game.Id); }
            catch (ConfigException ex) { Log.Warn($"Ignoring unreadable selection for {game.Id}: {ex.Message}"); }
        }

        // Big Picture (TV): a docked 4K display or a 4K stream client picks the TV preset, unless the user chose a
        // preset explicitly (preset != null), opted this game out, or the game has no such preset.
        BigPictureResult? bp = null;
        bool optedOut = false, bpWanted = false;
        string? bpNote = null;
        if (bigPicture is { Suppress: false })
        {
            bp = (bigPicture.Detect ?? BigPictureDetector.DetectNow)(device.Def);
            try { optedOut = selections.GetNeverBigPicture(device.Name, game.Id); } catch (Exception) { /* unreadable: not opted out */ }
            if (bp.Active && preset is null)
            {
                if (optedOut)
                    Log.Info($"Big Picture is active ({bp.Reason}) but {game.Id} is opted out");
                else if (ProfileResolver.PresetNames(game, device.Name, device.Def).Contains(bp.Preset))
                {
                    chosen = PresetRef.Recommended(bp.Preset);
                    bpWanted = true;
                    Log.Info($"Big Picture: using '{bp.Preset}' for {game.Id} ({bp.Reason})");
                }
                else
                {
                    var usedName = chosen?.ToString() ?? DefaultFor(game, device) ?? "(none)";
                    bpNote = $"No Big Picture preset for {game.Name}; using {usedName}";
                    Log.Info(bpNote);
                }
            }
        }

        ManualSave? save = null;
        ResolvedProfile? profile = null;
        if (chosen is not null)
        {
            try
            {
                if (chosen.Kind == PresetKind.Manual)
                {
                    save = store.LoadSave(game.Id, device.Name, chosen.Name);
                    profile = ProfileResolver.ResolveSave(game, device.Name, device.Def, save);
                }
                else
                {
                    profile = ProfileResolver.ResolvePreset(game, device.Name, device.Def, chosen.Name);
                }
            }
            catch (ConfigException ex) when (preset is null)
            {
                Log.Warn($"Selected preset {chosen} is no longer available ({ex.Message}); using the default");
                save = null;
            }
        }
        bool isSelected = profile is not null;
        profile ??= DefaultFor(game, device) is { } fallback
            ? ProfileResolver.ResolvePreset(game, device.Name, device.Def, fallback)
            : ProfileResolver.Empty(device.Name);

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

            var snapshot = save is null ? null : System.IO.Path.Combine(save.Dir, def.Id + ".snap");
            files.Add(new ResolvedFile
            {
                Def = def,
                Handler = handler,
                Path = path,
                Error = error,
                Targets = profile.FileBlocks.TryGetValue(def.Id, out var block) ? handler.Flatten(block) : new(),
                SnapshotFile = snapshot is not null && File.Exists(snapshot) ? snapshot : null,
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
            Save = save,
            IsSelected = isSelected,
            BigPicture = bp,
            BigPictureUsed = bpWanted && isSelected,
            BigPictureOptedOut = optedOut,
            BigPictureNote = bpNote,
        };
    }

    /// <summary>The device's defaultPreset if this game has it, else the first preset, else null.</summary>
    public static string? DefaultFor(GameDef game, DeviceSelection device)
    {
        var names = ProfileResolver.PresetNames(game, device.Name, device.Def);
        if (device.Def?.DefaultPreset is { } d && names.Contains(d)) return d;
        return names.FirstOrDefault();
    }
}
