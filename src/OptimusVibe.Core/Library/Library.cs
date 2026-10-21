using OptimusVibe.CloudSync;
using OptimusVibe.Config;

namespace OptimusVibe.Library;

/// <summary>A game found installed on this machine by a launcher scanner.</summary>
public sealed record DetectedGame
{
    /// <summary>steam | battlenet | ea | xbox | epic | gog</summary>
    public required string Store { get; init; }

    /// <summary>Store-specific id: Steam app id, Battle.net product code, EA offer id, Xbox package family name, Epic AppName, GOG id.</summary>
    public required string StoreId { get; init; }

    public required string Name { get; init; }
    public string? InstallDir { get; init; }

    /// <summary>URI that starts the game through its launcher (steam://rungameid/..., com.epicgames.launcher://..., shell:AppsFolder\...).</summary>
    public string? LaunchUri { get; init; }

    /// <summary>Exe to start when there is no URI (e.g. Battle.net.exe with --exec, GOG game exe).</summary>
    public string? LaunchExe { get; init; }
    public IReadOnlyList<string> LaunchArgs { get; init; } = [];

    /// <summary>Local cover art (portrait preferred), if the launcher caches one.</summary>
    public string? ArtPath { get; init; }

    /// <summary>Likely game exe file names (for `processes:` and for the request queue).</summary>
    public IReadOnlyList<string> Exes { get; init; } = [];

    public string Key => $"{Store}:{StoreId}";
}

public interface IGameScanner
{
    string Store { get; }

    /// <summary>Installed games. Must not throw: a missing launcher yields nothing; log and skip bad entries.</summary>
    IEnumerable<DetectedGame> Scan();
}

public enum LibraryStatus
{
    /// <summary>Has a game config with presets for this device and is not opted out: settings applied on launch.</summary>
    Managed,

    /// <summary>Installed, no game config yet (queued for Claude).</summary>
    NeedsPresets,

    /// <summary>User opted out: launched plainly, Steam launch option removed.</summary>
    OptedOut,

    /// <summary>Configured in the repo but not installed on this machine.</summary>
    NotInstalled,
}

public sealed class LibraryEntry
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public DetectedGame? Detected { get; init; }
    public GameDef? Game { get; init; }
    public required LibraryStatus Status { get; init; }
    public bool OptedOut { get; init; }

    /// <summary>In library.yaml steamLaunchOptionsSkip: Optimus-Vibe never touches this game's Steam launch option.</summary>
    public bool SteamLaunchOptionSkipped { get; init; }

    /// <summary>The game is flagged "set in-game" (its game file or the user's library.yaml says so): launched with display and driver settings only. See <see cref="GameFlags"/>.</summary>
    public FlagDef? SetInGame { get; init; }

    /// <summary>The cloud-sync check for this game on this device (null when not computed, e.g. no game config).</summary>
    public CloudSyncReport? CloudSync { get; init; }

    /// <summary>
    /// Settings are per machine (by the sync report when there is one, else the game config alone) and nothing ever
    /// changed them behind our back — suggest opting out. Never applied automatically.
    /// </summary>
    public bool SuggestOptOut => !OptedOut && (CloudSync?.SuggestOptOut ?? Game?.CloudSyncsSettings == false);
}
