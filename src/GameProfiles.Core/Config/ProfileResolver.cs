using System.Globalization;

namespace GameProfiles.Config;

/// <summary>A device profile after `extends` chains and the capture overlay are merged.</summary>
public sealed class ResolvedProfile
{
    public required string Name { get; init; }

    /// <summary>Null = leave the display alone.</summary>
    public DisplayModeDef? Display { get; init; }

    public Dictionary<string, string> Nvidia { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Amd { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File id → raw (possibly nested) settings block from YAML.</summary>
    public Dictionary<string, Dictionary<string, object?>> FileBlocks { get; init; } = new();

    public bool HasProfile { get; init; }
    public bool HasCapture { get; init; }
}

public static class ProfileResolver
{
    public const string Extends = "extends";
    public const string Display = "display";
    public const string Nvidia = "nvidia";
    public const string Amd = "amd";
    public static readonly string[] ReservedKeys = [Extends, Display, Nvidia, Amd];

    /// <summary>The device profile with its extends chain merged (no capture overlay).</summary>
    public static Dictionary<string, object?>? Flatten(GameDef game, string profileName)
    {
        if (!game.Profiles.ContainsKey(profileName)) return null;
        var chain = new List<string>();
        for (var name = profileName; name is not null;)
        {
            if (chain.Contains(name))
                throw new ConfigException($"{game.Id}: profile extends cycle: {string.Join(" -> ", chain)} -> {name}");
            if (!game.Profiles.TryGetValue(name, out var body))
                throw new ConfigException($"{game.Id}: profile '{chain.Last()}' extends unknown profile '{name}'");
            chain.Add(name);
            name = body.TryGetValue(Extends, out var ext) ? ext?.ToString() : null;
        }

        var merged = new Dictionary<string, object?>();
        foreach (var name in Enumerable.Reverse(chain))
            DeepMerge(merged, game.Profiles[name]);
        merged.Remove(Extends);
        return merged;
    }

    public static ResolvedProfile Resolve(GameDef game, string device, DeviceDef? deviceDef, Dictionary<string, object?> capture)
    {
        var body = Flatten(game, device) ?? new Dictionary<string, object?>();
        bool hasProfile = game.Profiles.ContainsKey(device);
        DeepMerge(body, capture);

        var fileIds = game.Files.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new ResolvedProfile
        {
            Name = device,
            HasProfile = hasProfile,
            HasCapture = capture.Count > 0,
            Display = ResolveDisplay(body.GetValueOrDefault(Display), deviceDef),
        };
        CopyScalars(body.GetValueOrDefault(Nvidia), result.Nvidia);
        CopyScalars(body.GetValueOrDefault(Amd), result.Amd);
        foreach (var (key, value) in body)
        {
            if (ReservedKeys.Contains(key)) continue;
            if (!fileIds.Contains(key))
                throw new ConfigException($"{game.Id}: profile '{device}' sets '{key}', which is not a file id or a reserved key");
            if (value is Dictionary<string, object?> block)
                result.FileBlocks[game.Files.First(f => f.Id.Equals(key, StringComparison.OrdinalIgnoreCase)).Id] = block;
        }
        return result;
    }

    private static DisplayModeDef? ResolveDisplay(object? value, DeviceDef? device)
    {
        switch (value)
        {
            case null:
                return null;
            case string s when s.Equals("inherit", StringComparison.OrdinalIgnoreCase) || s.Equals("device", StringComparison.OrdinalIgnoreCase):
                return device?.Display;
            case string s when s.Equals("none", StringComparison.OrdinalIgnoreCase) || s.Equals("keep", StringComparison.OrdinalIgnoreCase):
                return null;
            case Dictionary<string, object?> map:
                return new DisplayModeDef
                {
                    Width = Int(map.GetValueOrDefault("width")) ?? device?.Display?.Width,
                    Height = Int(map.GetValueOrDefault("height")) ?? device?.Display?.Height,
                    Refresh = Int(map.GetValueOrDefault("refresh")) ?? device?.Display?.Refresh,
                };
            default:
                throw new ConfigException($"display must be 'inherit', 'none', or a map with width/height/refresh (got '{value}')");
        }
    }

    private static int? Int(object? v) =>
        v is null ? null : int.Parse(Convert.ToString(v, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    private static void CopyScalars(object? source, Dictionary<string, string> target)
    {
        if (source is not Dictionary<string, object?> map) return;
        foreach (var (k, v) in map)
            target[k] = v switch
            {
                Dictionary<string, object?> nested => string.Join(",", nested.Select(n => $"{n.Key}={n.Value}")),
                _ => Values.ToScalar(v),
            };
    }

    /// <summary>Overlay wins; nested maps merge recursively; null in overlay removes the key.</summary>
    public static void DeepMerge(Dictionary<string, object?> target, Dictionary<string, object?> overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (value is Dictionary<string, object?> map && target.TryGetValue(key, out var existing) &&
                existing is Dictionary<string, object?> existingMap)
            {
                DeepMerge(existingMap, map);
            }
            else if (value is Dictionary<string, object?> fresh)
            {
                var copy = new Dictionary<string, object?>();
                DeepMerge(copy, fresh);
                target[key] = copy;
            }
            else if (value is null)
            {
                target.Remove(key);
            }
            else
            {
                target[key] = value;
            }
        }
    }
}
