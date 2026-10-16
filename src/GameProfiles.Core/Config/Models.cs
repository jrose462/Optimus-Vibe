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

    /// <summary>Default display mode used when a game profile says <c>display: inherit</c>.</summary>
    public DisplayModeDef? Display { get; set; }

    /// <summary>nvidia | amd | none. Derived from the detected GPUs when omitted.</summary>
    public string? Driver { get; set; }
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

    /// <summary>Profile name (device id, or an abstract base) → profile body.</summary>
    public Dictionary<string, Dictionary<string, object?>> Profiles { get; set; } = new();

    /// <summary>Free-text intent per device, written by the user, read by Claude when mapping settings.</summary>
    public Dictionary<string, string> Intent { get; set; } = new();

    public string? Notes { get; set; }
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
}
