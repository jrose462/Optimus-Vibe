namespace GameProfiles.Config;

/// <summary>config/devices.yaml</summary>
public sealed class DevicesFile
{
    public Dictionary<string, DeviceDef> Devices { get; set; } = new();
}

public sealed class DeviceDef
{
    public string? Description { get; set; }

    /// <summary>All entries must match (wildcards * and ?). Keys: model, manufacturer, cpu, gpu.</summary>
    public Dictionary<string, string> Match { get; set; } = new();

    /// <summary>Default display mode used when a preset says <c>display: inherit</c>.</summary>
    public DisplayModeDef? Display { get; set; }

    /// <summary>nvidia | amd | none. Derived from the detected GPUs when omitted.</summary>
    public string? Driver { get; set; }

    /// <summary>Recommended preset used for a game until one is picked in the app (e.g. quality, performance).</summary>
    public string? DefaultPreset { get; set; }
}

public sealed class DisplayModeDef
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Refresh { get; set; }

    public override string ToString() =>
        $"{(Width?.ToString() ?? "*")}x{(Height?.ToString() ?? "*")}@{(Refresh?.ToString() ?? "*")}Hz";
}

/// <summary>config/games/&lt;id&gt;.yaml</summary>
public sealed class GameDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Store { get; set; } = "steam";
    public int? SteamAppId { get; set; }
    public LaunchDef? Launch { get; set; }

    /// <summary>Process names (e.g. Game.exe) the wrapper waits on; launcher stubs often exit early.</summary>
    public List<string> Processes { get; set; } = new();

    public List<FileDef> Files { get; set; } = new();

    /// <summary>Shared building blocks that presets <c>extends</c> (e.g. base). Never applied directly.</summary>
    public Dictionary<string, Dictionary<string, object?>> Profiles { get; set; } = new();

    /// <summary>
    /// Claude-recommended presets: device id → preset name → body. Conventional names:
    /// desktop: quality, performance; ally-x: performance (60+ fps), battery.
    /// </summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, object?>>> Presets { get; set; } = new();

    /// <summary>Free-text intent per device, written by the user, read by Claude when generating presets.</summary>
    public Dictionary<string, string> Intent { get; set; } = new();

    /// <summary>When and how the recommended presets were generated (filled in by the skill).</summary>
    public RecommendationInfo? Recommendation { get; set; }

    public string? Notes { get; set; }
}

public sealed class RecommendationInfo
{
    public string? Generated { get; set; }
    public string? GameVersion { get; set; }
    public List<string> Sources { get; set; } = new();
}

public sealed class LaunchDef
{
    public string? Uri { get; set; }
    public string? Exe { get; set; }
    public List<string> Args { get; set; } = new();
}

public sealed class FileDef
{
    public string Id { get; set; } = "";

    /// <summary>ini | json | xml | registry | keyvalue | file</summary>
    public string Handler { get; set; } = "";

    /// <summary>File path with tokens ({Documents}, {SteamLibrary:123}...), or a registry key for the registry handler.</summary>
    public string Path { get; set; } = "";

    /// <summary>keyvalue handler: preset name (cvar, quoted, equals, equals-spaced).</summary>
    public string? Format { get; set; }

    /// <summary>keyvalue handler: custom line template containing {key} and {value}.</summary>
    public string? Template { get; set; }

    /// <summary>Mark the file read-only after applying (for games that reset settings on boot).</summary>
    public bool ReadOnlyAfterApply { get; set; }

    /// <summary>Create the file if it does not exist yet (otherwise the file is skipped with a warning).</summary>
    public bool CreateIfMissing { get; set; }
}

/// <summary>%LOCALAPPDATA%\GameProfiles\settings.json — per machine, not in git.</summary>
public sealed class AppSettings
{
    public string? ConfigRoot { get; set; }
    public string? ForcedProfile { get; set; }

    /// <summary>Commit and push manual saves to the config repo automatically.</summary>
    public bool AutoSync { get; set; } = true;
}

public enum PresetKind { Recommended, Manual }

/// <summary>Which settings a game uses: a recommended preset ("recommended:battery") or a manual save ("manual:steady-40").</summary>
public sealed record PresetRef(PresetKind Kind, string Name)
{
    public static PresetRef Recommended(string name) => new(PresetKind.Recommended, name);
    public static PresetRef Manual(string slug) => new(PresetKind.Manual, slug);

    public static PresetRef Parse(string value)
    {
        var colon = value.IndexOf(':');
        if (colon < 0) return Recommended(value.Trim());
        var kind = value[..colon].Trim().ToLowerInvariant();
        var name = value[(colon + 1)..].Trim();
        return kind switch
        {
            "recommended" or "preset" or "r" => Recommended(name),
            "manual" or "save" or "m" => Manual(name),
            _ => throw new ConfigException($"Unknown preset kind '{kind}' (use recommended:<name> or manual:<name>)"),
        };
    }

    public override string ToString() => $"{(Kind == PresetKind.Recommended ? "recommended" : "manual")}:{Name}";
}

/// <summary>A user-made save of a game's settings for one device (config/saves/&lt;game&gt;/&lt;device&gt;/&lt;slug&gt;/save.yaml).</summary>
public sealed class ManualSave
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }

    /// <summary>The preset that was active when this was saved, e.g. recommended:performance.</summary>
    public string? BasedOn { get; set; }
    public string? Notes { get; set; }

    /// <summary>Settings body: file id → block, plus display/nvidia/amd like a preset.</summary>
    public Dictionary<string, object?> Body { get; set; } = new();

    /// <summary>Folder holding save.yaml and any whole-file snapshots (&lt;fileId&gt;.snap).</summary>
    public string Dir { get; set; } = "";
}
