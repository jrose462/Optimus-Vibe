using System.Globalization;

namespace OptimusVibe.Config;

/// <summary>A recommended preset or manual save after `extends` chains are merged, ready to apply.</summary>
public sealed class ResolvedProfile
{
    public required string Device { get; init; }

    /// <summary>What this was resolved from; null when the device has nothing configured.</summary>
    public PresetRef? Source { get; init; }

    public string? Label { get; init; }
    public string? Description { get; init; }
    public int? TargetFps { get; init; }

    /// <summary>Null = leave the display alone.</summary>
    public DisplayModeDef? Display { get; init; }

    public Dictionary<string, string> Nvidia { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Amd { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File id → raw (possibly nested) settings block from YAML.</summary>
    public Dictionary<string, Dictionary<string, object?>> FileBlocks { get; init; } = new();

    public bool IsEmpty => Source is null;
}

public static class ProfileResolver
{
    public const string Extends = "extends";
    public const string Display = "display";
    public const string Nvidia = "nvidia";
    public const string Amd = "amd";
    public const string Label = "label";
    public const string Description = "description";
    public const string TargetFps = "targetFps";

    public static readonly string[] ReservedKeys = [Extends, Display, Nvidia, Amd, Label, Description, TargetFps];

    /// <summary>Conventional preset names, in display order.</summary>
    public static readonly string[] KnownPresetNames = ["quality", "performance", "battery"];

    /// <summary>Preset names for a device, conventional ones first.</summary>
    public static IReadOnlyList<string> PresetNames(GameDef game, string device) =>
        game.Presets.TryGetValue(device, out var presets)
            ? presets.Keys
                .OrderBy(k => Array.IndexOf(KnownPresetNames, k) is var i and >= 0 ? i : KnownPresetNames.Length)
                .ThenBy(k => k, StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>A preset body with its extends chain (into game.Profiles) merged.</summary>
    public static Dictionary<string, object?> FlattenPreset(GameDef game, string device, string preset)
    {
        if (!game.Presets.TryGetValue(device, out var presets) || !presets.TryGetValue(preset, out var body))
            throw new ConfigException($"{game.Id}: no preset '{preset}' for device '{device}'");
        return FlattenBody(game, body, $"presets.{device}.{preset}");
    }

    /// <summary>Merge a body over the profiles it extends (recursively). The body itself wins.</summary>
    public static Dictionary<string, object?> FlattenBody(GameDef game, Dictionary<string, object?> body, string where)
    {
        var chain = new List<Dictionary<string, object?>> { body };
        var seen = new List<string> { where };
        var next = body.GetValueOrDefault(Extends)?.ToString();
        while (next is not null)
        {
            if (seen.Contains(next))
                throw new ConfigException($"{game.Id}: extends cycle: {string.Join(" -> ", seen)} -> {next}");
            if (!game.Profiles.TryGetValue(next, out var parent))
                throw new ConfigException($"{game.Id}: {seen.Last()} extends unknown profile '{next}' (extends must name an entry under profiles:)");
            seen.Add(next);
            chain.Add(parent);
            next = parent.GetValueOrDefault(Extends)?.ToString();
        }

        var merged = new Dictionary<string, object?>();
        for (int i = chain.Count - 1; i >= 0; i--) DeepMerge(merged, chain[i]);
        merged.Remove(Extends);
        return merged;
    }

    public static ResolvedProfile ResolvePreset(GameDef game, string device, DeviceDef? deviceDef, string preset) =>
        Resolve(game, device, deviceDef, FlattenPreset(game, device, preset), PresetRef.Recommended(preset));

    public static ResolvedProfile ResolveSave(GameDef game, string device, DeviceDef? deviceDef, ManualSave save) =>
        Resolve(game, device, deviceDef, FlattenBody(game, save.Body, $"saves/{save.Slug}"), PresetRef.Manual(save.Slug),
            label: save.Name, description: save.Notes);

    public static ResolvedProfile Empty(string device) => new() { Device = device };

    public static ResolvedProfile Resolve(GameDef game, string device, DeviceDef? deviceDef,
        Dictionary<string, object?> body, PresetRef source, string? label = null, string? description = null)
    {
        var fileIds = game.Files.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new ResolvedProfile
        {
            Device = device,
            Source = source,
            Label = label ?? body.GetValueOrDefault(Label)?.ToString() ?? Titleize(source.Name),
            Description = description ?? body.GetValueOrDefault(Description)?.ToString(),
            TargetFps = Int(body.GetValueOrDefault(TargetFps)),
            Display = ResolveDisplay(body.GetValueOrDefault(Display), deviceDef),
        };
        CopyScalars(body.GetValueOrDefault(Nvidia), result.Nvidia);
        CopyScalars(body.GetValueOrDefault(Amd), result.Amd);
        foreach (var (key, value) in body)
        {
            if (ReservedKeys.Contains(key)) continue;
            if (!fileIds.Contains(key))
                throw new ConfigException($"{game.Id}: {source} sets '{key}', which is not a file id or a reserved key");
            if (value is Dictionary<string, object?> block)
                result.FileBlocks[game.Files.First(f => f.Id.Equals(key, StringComparison.OrdinalIgnoreCase)).Id] = block;
        }
        return result;
    }

    private static string Titleize(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].Replace('-', ' ');

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

    private static int? Int(object? v)
    {
        if (v is null) return null;
        var s = Convert.ToString(v, CultureInfo.InvariantCulture);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new ConfigException($"'{s}' is not a whole number");
    }

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
