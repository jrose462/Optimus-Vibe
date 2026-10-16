namespace GameProfiles.Config;

/// <summary>Reads devices, games and captured overlays from the config folder.</summary>
public sealed class ConfigStore(AppPaths paths)
{
    public AppPaths Paths { get; } = paths;

    public DevicesFile LoadDevices()
    {
        if (!File.Exists(Paths.DevicesFile))
            throw new ConfigException($"Missing {Paths.DevicesFile}");
        return Yaml.Load<DevicesFile>(Paths.DevicesFile);
    }

    public IEnumerable<string> GameIds() =>
        Directory.Exists(Paths.GamesDir)
            ? Directory.EnumerateFiles(Paths.GamesDir, "*.yaml")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null && !n.StartsWith('_'))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            : [];

    public GameDef LoadGame(string gameId)
    {
        var file = Paths.GameFile(gameId);
        if (!File.Exists(file))
        {
            // allow matching by display name or case-insensitive id
            var match = GameIds().FirstOrDefault(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
            if (match is null) throw new ConfigException($"No game config '{gameId}' in {Paths.GamesDir}");
            file = Paths.GameFile(match);
        }
        var game = Yaml.Load<GameDef>(file);
        if (string.IsNullOrEmpty(game.Id)) game.Id = Path.GetFileNameWithoutExtension(file);
        game.Profiles = game.Profiles.ToDictionary(kv => kv.Key, kv => Yaml.NormalizeMap(kv.Value));
        return game;
    }

    /// <summary>User-captured overrides for one device (config/captures/&lt;game&gt;/&lt;device&gt;.yaml).</summary>
    public Dictionary<string, object?> LoadCapture(string gameId, string device)
    {
        var file = Paths.CaptureFile(gameId, device);
        if (!File.Exists(file)) return new();
        return Yaml.NormalizeMap(Yaml.Load<Dictionary<string, object?>>(file));
    }

    public void SaveCapture(string gameId, string device, Dictionary<string, object?> body)
    {
        var file = Paths.CaptureFile(gameId, device);
        if (body.Count == 0)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var header = $"# Captured by `gameprofiles capture` on {DateTime.Now:yyyy-MM-dd HH:mm}.\n" +
                     $"# Overrides profiles.{device} in games/{gameId}.yaml. Delete this file to go back to the base profile.\n";
        File.WriteAllText(file, header + Yaml.Serialize(body));
    }
}
