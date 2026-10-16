using System.Text.Json;

namespace GameProfiles.Config;

/// <summary>Where config (git repo, shared) and local data (backups, logs, state — per machine) live.</summary>
public sealed class AppPaths
{
    public required string ConfigRoot { get; init; }
    public required string DataDir { get; init; }

    public string DevicesFile => Path.Combine(ConfigRoot, "devices.yaml");
    public string GamesDir => Path.Combine(ConfigRoot, "games");
    public string SavesDir => Path.Combine(ConfigRoot, "saves");
    public string BackupsDir => Path.Combine(DataDir, "backups");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string StateFile => Path.Combine(DataDir, "state.json");
    public string SelectionsFile => Path.Combine(DataDir, "selections.json");

    /// <summary>The git working tree that holds the config folder (config/ sits at the repo root).</summary>
    public string RepoRoot => Directory.GetParent(ConfigRoot)?.FullName ?? ConfigRoot;

    public string GameFile(string gameId) => Path.Combine(GamesDir, gameId + ".yaml");
    public string SavesFor(string gameId, string device) => Path.Combine(SavesDir, gameId, device);
    public string SaveDir(string gameId, string device, string slug) => Path.Combine(SavesFor(gameId, device), slug);

    public static string DefaultDataDir =>
        Environment.GetEnvironmentVariable("GAMEPROFILES_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameProfiles");

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
    /// Config root order: --config option, GAMEPROFILES_CONFIG, settings.json, then a "config" folder
    /// next to the exe or in any parent of it (dev checkout).
    /// </summary>
    public static AppPaths Resolve(string? configOption)
    {
        var dataDir = DefaultDataDir;
        var settings = LoadSettings(dataDir);
        var root = configOption
                   ?? Environment.GetEnvironmentVariable("GAMEPROFILES_CONFIG")
                   ?? settings.ConfigRoot
                   ?? FindConfigNear(AppContext.BaseDirectory)
                   ?? FindConfigNear(Environment.CurrentDirectory)
                   ?? throw new ConfigException(
                       "No config folder found. Run `gameprofiles setup --config-root <repo>\\config` once on this machine.");
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
