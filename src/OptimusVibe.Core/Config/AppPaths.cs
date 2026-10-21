using System.Text.Json;

namespace OptimusVibe.Config;

/// <summary>Where config (git repo, shared) and local data (backups, logs, state — per machine) live.</summary>
public sealed class AppPaths
{
    public required string ConfigRoot { get; init; }
    public required string DataDir { get; init; }

    public string DevicesFile => Path.Combine(ConfigRoot, "devices.yaml");
    public string GamesDir => Path.Combine(ConfigRoot, "games");
    public string SavesDir => Path.Combine(ConfigRoot, "saves");
    public string BackupsDir => Path.Combine(DataDir, "backups");
    /// <summary>This device's own copies of cloud-synced whole files (autoSnapshot). Local only, never in the config repo, outside the backups rotation.</summary>
    public string AutoSnapshotsDir => Path.Combine(DataDir, "auto-snapshots");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string StateFile => Path.Combine(DataDir, "state.json");
    public string SelectionsFile => Path.Combine(DataDir, "selections.json");
    public string LibraryFile => Path.Combine(ConfigRoot, "library.yaml");
    public string RequestsFile => Path.Combine(ConfigRoot, "requests.yaml");

    /// <summary>Local copy of a repo for the github sync provider: %LOCALAPPDATA%\OptimusVibe\repos\&lt;owner&gt;\&lt;name&gt;.</summary>
    public static string MirrorDir(string dataDir, string repo) =>
        Path.Combine(dataDir, "repos", Path.Combine(repo.Split('/', 2, StringSplitOptions.TrimEntries)));

    /// <summary>The git working tree that holds the config folder (config/ sits at the repo root).</summary>
    public string RepoRoot => Directory.GetParent(ConfigRoot)?.FullName ?? ConfigRoot;

    public string GameFile(string gameId) => Path.Combine(GamesDir, gameId + ".yaml");
    public string SavesFor(string gameId, string device) => Path.Combine(SavesDir, gameId, device);
    public string SaveDir(string gameId, string device, string slug) => Path.Combine(SavesFor(gameId, device), slug);

    /// <summary>OPTIMUSVIBE_&lt;name&gt;, falling back to the legacy GAMEPROFILES_&lt;name&gt; variable; null when neither is set.</summary>
    public static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable("OPTIMUSVIBE_" + name);
        if (!string.IsNullOrEmpty(v)) return v;
        v = Environment.GetEnvironmentVariable("GAMEPROFILES_" + name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static string DefaultDataDir =>
        AppPaths.Env("DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptimusVibe");

    public static string SettingsFile(string dataDir) => Path.Combine(dataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static AppSettings LoadSettings(string dataDir)
    {
        var file = SettingsFile(dataDir);
        if (!File.Exists(file)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOpts) ?? new AppSettings();
    }

    public static void SaveSettings(string dataDir, AppSettings settings)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(SettingsFile(dataDir), JsonSerializer.Serialize(settings, JsonOpts));
    }

    /// <summary>
    /// Config root order: --config option, OPTIMUSVIBE_CONFIG, the github provider's managed copy, settings.json, then a
    /// "config" folder next to the exe or in a parent of the current directory (running inside a config repo checkout).
    /// </summary>
    public static AppPaths Resolve(string? configOption)
    {
        var dataDir = DefaultDataDir;
        var settings = LoadSettings(dataDir);
        var mirror = settings.SyncProvider.Equals("github", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.GitHubRepo)
            ? Path.Combine(MirrorDir(dataDir, settings.GitHubRepo), "config")
            : null;
        var root = configOption
                   ?? AppPaths.Env("CONFIG")
                   ?? (mirror is not null && File.Exists(Path.Combine(mirror, "devices.yaml")) ? mirror : null)
                   ?? settings.ConfigRoot
                   ?? FindConfigNear(AppContext.BaseDirectory)
                   ?? FindConfigNear(Environment.CurrentDirectory)
                   ?? throw new ConfigException(
                       "No config folder found. Open OptimusVibe.exe and choose where your game settings live, or run `optimus-vibe setup --config-root <your config repo>\\config`.");
        return new AppPaths { ConfigRoot = Path.GetFullPath(root), DataDir = dataDir };
    }

    private static string? FindConfigNear(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "config");
            if (File.Exists(Path.Combine(candidate, "devices.yaml"))) return candidate;
        }
        return null;
    }
}

public sealed class ConfigException(string message) : Exception(message);
