using System.Globalization;
using System.Text;

namespace GameProfiles.Config;

/// <summary>Reads devices and games, and reads/writes manual saves, in the config folder.</summary>
public sealed class ConfigStore(AppPaths paths)
{
    public AppPaths Paths { get; } = paths;

    private static readonly string[] SaveMetaKeys = ["name", "created", "updated", "basedOn", "notes"];

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
            var match = GameIds().FirstOrDefault(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
            if (match is null) throw new ConfigException($"No game config '{gameId}' in {Paths.GamesDir}");
            file = Paths.GameFile(match);
        }
        var game = Yaml.Load<GameDef>(file);
        if (string.IsNullOrEmpty(game.Id)) game.Id = Path.GetFileNameWithoutExtension(file);
        game.Profiles = game.Profiles.ToDictionary(kv => kv.Key, kv => Yaml.NormalizeMap(kv.Value));
        game.Presets = game.Presets.ToDictionary(
            d => d.Key,
            d => d.Value.ToDictionary(p => p.Key, p => Yaml.NormalizeMap(p.Value)));
        return game;
    }

    // ---------------------------------------------------------------- manual saves

    public IReadOnlyList<ManualSave> ListSaves(string gameId, string device)
    {
        var dir = Paths.SavesFor(gameId, device);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateDirectories(dir)
            .Where(d => File.Exists(Path.Combine(d, "save.yaml")))
            .Select(d => LoadSave(gameId, device, Path.GetFileName(d)!))
            .OrderByDescending(s => s.Updated)
            .ToList();
    }

    public ManualSave LoadSave(string gameId, string device, string slug)
    {
        var dir = Paths.SaveDir(gameId, device, slug);
        var file = Path.Combine(dir, "save.yaml");
        if (!File.Exists(file)) throw new ConfigException($"No manual save '{slug}' for {gameId} on {device}");
        var raw = Yaml.NormalizeMap(Yaml.Load<Dictionary<string, object?>>(file));
        var save = new ManualSave
        {
            Slug = slug,
            Dir = dir,
            Name = raw.GetValueOrDefault("name")?.ToString() ?? slug,
            Created = Date(raw.GetValueOrDefault("created")),
            Updated = Date(raw.GetValueOrDefault("updated") ?? raw.GetValueOrDefault("created")),
            BasedOn = raw.GetValueOrDefault("basedOn")?.ToString(),
            Notes = raw.GetValueOrDefault("notes")?.ToString(),
        };
        foreach (var key in SaveMetaKeys) raw.Remove(key);
        save.Body = raw;
        return save;
    }

    /// <summary>Write save.yaml (and the given whole-file snapshots). Sets Slug/Dir when new.</summary>
    public void WriteSave(string gameId, string device, ManualSave save, IReadOnlyDictionary<string, byte[]>? snapshots = null)
    {
        if (string.IsNullOrEmpty(save.Slug)) save.Slug = UniqueSlug(gameId, device, save.Name);
        save.Dir = Paths.SaveDir(gameId, device, save.Slug);
        Directory.CreateDirectory(save.Dir);

        var doc = new Dictionary<string, object?>
        {
            ["name"] = save.Name,
            ["created"] = save.Created.ToString("s", CultureInfo.InvariantCulture),
            ["updated"] = save.Updated.ToString("s", CultureInfo.InvariantCulture),
        };
        if (save.BasedOn is not null) doc["basedOn"] = save.BasedOn;
        if (save.Notes is not null) doc["notes"] = save.Notes;
        foreach (var (k, v) in save.Body) doc[k] = v;

        var header = new StringBuilder()
            .AppendLine($"# Manual save for {gameId} on {device}. Made in GameProfiles; edit freely.")
            .ToString();
        File.WriteAllText(Path.Combine(save.Dir, "save.yaml"), header + Yaml.Serialize(doc));

        foreach (var old in Directory.EnumerateFiles(save.Dir, "*.snap"))
            if (snapshots is null || !snapshots.ContainsKey(Path.GetFileNameWithoutExtension(old))) File.Delete(old);
        if (snapshots is not null)
            foreach (var (fileId, data) in snapshots)
                File.WriteAllBytes(Path.Combine(save.Dir, fileId + ".snap"), data);
    }

    public void DeleteSave(string gameId, string device, string slug)
    {
        var dir = Paths.SaveDir(gameId, device, slug);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public string UniqueSlug(string gameId, string device, string name)
    {
        var baseSlug = Slugify(name);
        var slug = baseSlug;
        for (int n = 2; Directory.Exists(Paths.SaveDir(gameId, device, slug)); n++) slug = $"{baseSlug}-{n}";
        return slug;
    }

    public static string Slugify(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "save" : slug.Length > 48 ? slug[..48].Trim('-') : slug;
    }

    private static DateTime Date(object? v) =>
        v is not null && DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? d
            : DateTime.MinValue;
}
