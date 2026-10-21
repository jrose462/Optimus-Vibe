namespace OptimusVibe.Config;

/// <summary>config/devices.yaml</summary>
public sealed class DevicesFile
{
    public Dictionary<string, DeviceDef> Devices { get; set; } = new();
}

public sealed class DeviceDef
{
    /// <summary>Display name shown in the app ("ROG Xbox Ally X"). The id (the devices.yaml key) stays the identity.</summary>
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>All entries must match (wildcards * and ?). Keys: model, manufacturer, cpu, gpu.</summary>
    public Dictionary<string, string> Match { get; set; } = new();

    /// <summary>Default display mode used when a preset says <c>display: inherit</c>.</summary>
    public DisplayModeDef? Display { get; set; }

    /// <summary>nvidia | amd | none. Derived from the detected GPUs when omitted.</summary>
    public string? Driver { get; set; }

    /// <summary>Recommended preset used for a game until one is picked in the app (e.g. quality, performance).</summary>
    public string? DefaultPreset { get; set; }

    /// <summary>
    /// Another device id whose recommended presets this device uses for games that have none of its own. One level
    /// only (the target's own presetsFrom is not followed).
    /// </summary>
    public string? PresetsFrom { get; set; }

    /// <summary>
    /// Optional: how a big TV (4K, couch) is recognised and which preset it gets. Without it the defaults apply:
    /// an active 16:9 display of 3840 px or more, or a 4K stream client, selects the <c>big-picture</c> preset.
    /// </summary>
    public BigPictureDef? BigPicture { get; set; }
}

/// <summary>devices.yaml <c>bigPicture:</c> block.</summary>
public sealed class BigPictureDef
{
    /// <summary>Preset name used on the TV (default <c>big-picture</c>).</summary>
    public string? Preset { get; set; }

    /// <summary>The TV's target mode when a preset does not set its own display (default 3840x2160@120).</summary>
    public DisplayModeDef? Display { get; set; }

    public BigPictureMatchDef? Match { get; set; }
}

/// <summary>How the TV is detected.</summary>
public sealed class BigPictureMatchDef
{
    /// <summary>Monitor friendly-name wildcard(s): a string or a list (see `optimus-vibe display list`).</summary>
    public object? Name { get; set; }

    /// <summary>Minimum width in pixels for the size test and for a stream client (default 3840).</summary>
    public int? MinWidth { get; set; }

    /// <summary>Aspect ratio for the size test (default 16:9; keeps a 21:9 ultrawide from counting).</summary>
    public string? Aspect { get; set; }

    /// <summary>Also activate when a stream client (Apollo/Sunshine) asks for at least minWidth (default true).</summary>
    public bool? Stream { get; set; }
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

    /// <summary>
    /// Ids in other launchers, used to match auto-detected installs: battlenet (product code, e.g. Fen),
    /// ea (offer id), epic (AppName), gog (game id), xbox (package family name). Steam uses <see cref="SteamAppId"/>.
    /// </summary>
    public Dictionary<string, string> StoreIds { get; set; } = new();

    /// <summary>
    /// Whether the game's graphics settings travel between machines (e.g. Steam Cloud). false = each machine keeps
    /// its own, so the app suggests opting the game out. Null = unknown. Filled in by the skill.
    /// </summary>
    public bool? CloudSyncsSettings { get; set; }

    public LaunchDef? Launch { get; set; }

    /// <summary>
    /// When the preset is written: <c>before</c> (default), <c>afterProcessStart</c> (once the game exe is seen, after a settle delay) or
    /// <c>both</c> (default when <see cref="CloudSyncsSettings"/> is true: before launch, then re-checked after the launcher's cloud sync).
    /// </summary>
    public string? ApplyTiming { get; set; }

    /// <summary>Seconds to wait after the game process appears before the re-apply (default 5).</summary>
    public int? SettleSeconds { get; set; }

    /// <summary>
    /// How the preset reaches the game, so the app can be honest about it: plain-honoured, binary-truth, text-mirror, composite,
    /// local-cloud-split, first-run, registry, launcher-gated or server-held. Informational.
    /// </summary>
    public string? Archetype { get; set; }

    /// <summary>false = the settings cannot be set from outside the game (opaque or server-held); presets are advice only. Null = mappable.</summary>
    public bool? Mappable { get; set; }

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

    /// <summary>
    /// Extra command-line arguments, one token per entry. For an exe launch they follow the exe; for a Steam launch they go into
    /// `steam.exe -applaunch &lt;id&gt; &lt;args&gt;` (Steam drops arguments in a steam://rungameid URI; or after Steam's %command% when wrapped). A preset's <c>launch.args</c> is merged over these.
    /// </summary>
    public List<string> Args { get; set; } = new();

    /// <summary>
    /// How long the wrapper waits for the game process after starting the launcher, in seconds. Default 180, or 600 for the stores
    /// whose launchers ask questions (ea, battlenet, epic).
    /// </summary>
    public int? WaitSeconds { get; set; }

    /// <summary>
    /// Launcher / stub process names (e.g. EADesktop.exe, "Diablo IV Launcher.exe"). While one is running and the game is not,
    /// the wrapper reports "waiting on launcher" instead of giving up.
    /// </summary>
    public List<string> LauncherProcesses { get; set; } = new();
}

public sealed class FileDef
{
    public string Id { get; set; } = "";

    /// <summary>ini | json | xml | registry | keyvalue | gvas | file</summary>
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

    /// <summary>
    /// Keep this device's own copy of the file between sessions (for files a launcher cloud-syncs that presets can't edit):
    /// perPreset | perDevice; null = off. See <c>Engine.AutoSnapshots</c>.
    /// </summary>
    public string? AutoSnapshot { get; set; }
}

/// <summary>%LOCALAPPDATA%\OptimusVibe\settings.json — per machine, not in git.</summary>
public sealed class AppSettings
{
    public string? ConfigRoot { get; set; }
    public string? ForcedProfile { get; set; }

    /// <summary>Commit and push manual saves to the config repo automatically.</summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>
    /// How the config repo is synced: "git" = ConfigRoot is a git checkout synced with the git CLI (desktop default);
    /// "github" = the app keeps its own copy under %LOCALAPPDATA%\OptimusVibe\repos and syncs through the GitHub
    /// API after a device-code sign-in (no git needed — for the Ally).
    /// </summary>
    public string SyncProvider { get; set; } = "git";

    /// <summary>
    /// owner/name of the user's own (private) config repo for the github provider. Null until the user picks or creates one:
    /// there is deliberately no default, because the app repo holds code only.
    /// </summary>
    public string? GitHubRepo { get; set; }
    public string GitHubBranch { get; set; } = "main";

    /// <summary>Login of the signed-in GitHub account (display only; the token lives in Windows Credential Manager).</summary>
    public string? GitHubLogin { get; set; }

    /// <summary>Client id of the GitHub OAuth App used for device-code sign-in (public, not a secret).</summary>
    public string? GitHubClientId { get; set; }

    /// <summary>Launchers to scan for installed games: steam, battlenet, ea, xbox, epic, gog.</summary>
    public List<string> ScanStores { get; set; } = ["steam", "battlenet", "ea", "xbox", "epic", "gog"];

    /// <summary>Write `OptimusVibe.exe run` into Steam launch options for managed games.</summary>
    public bool WriteSteamLaunchOptions { get; set; } = true;

    /// <summary>App start view: auto (grid on the handheld / small screens), grid, or list.</summary>
    public string LibraryMode { get; set; } = "auto";

    /// <summary>Colour theme (per machine): default | majestic | circuit.</summary>
    public string Theme { get; set; } = "default";

    /// <summary>Layout density (per machine): auto (compact on short windows, e.g. the Ally's 7" screen) | compact | comfortable.</summary>
    public string Density { get; set; } = "auto";

    /// <summary>
    /// Game lab (this machine): newly detected games without presets get a lab request automatically instead of only the
    /// local requests.yaml queue. Only applies once the lab is set up.
    /// </summary>
    public bool GameLabAutoRequest { get; set; } = true;

    /// <summary>The last full game-lab check passed (every required setup step done). Gates the per-game actions and the background work.</summary>
    public bool GameLabReady { get; set; }

    /// <summary>The wizard's choices for the repo variables GAME_LAB_DAILY_MAX / GAME_LAB_MODEL.</summary>
    public int GameLabDailyMax { get; set; } = 5;
    public string GameLabModel { get; set; } = "claude-sonnet-5-5";

    /// <summary>Pull the config repo in the background (startup, every 15 minutes, window re-activated) and before a game starts, so new presets arrive by themselves.</summary>
    public bool AutoPull { get; set; } = true;

    /// <summary>Suggested name when creating the config repo.</summary>
    public const string SuggestedConfigRepoName = "optimus-vibe-config";
}

/// <summary>config/library.yaml — shared library choices (git-synced).</summary>
public sealed class LibraryFile
{
    /// <summary>Detected games the user opted out of ("store:id", e.g. steam:1285190): not managed, launched plainly.</summary>
    public List<string> OptOut { get; set; } = new();

    /// <summary>Steam games ("steam:appid") whose Steam launch option Optimus-Vibe never touches (neither adds nor removes the wrapper).</summary>
    public List<string> SteamLaunchOptionsSkip { get; set; } = new();
}

/// <summary>config/requests.yaml — detected games without presets, queued for Claude's game-settings-recommender skill.</summary>
public sealed class RequestsFile
{
    public List<GameRequest> Requests { get; set; } = new();
}

public sealed class GameRequest
{
    public string Store { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? InstallDir { get; set; }
    public List<string> Exes { get; set; } = new();
    public string? Requested { get; set; }
    public string? Device { get; set; }
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
