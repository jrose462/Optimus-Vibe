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

    /// <summary>
    /// Recommended presets only: slug of the user's manual save (same game and device) whose whole-file snapshots
    /// (<c>&lt;fileId&gt;.snap</c>) are restored along with this preset. Null for manual saves and presets without it.
    /// </summary>
    public string? SnapshotsFrom { get; init; }

    /// <summary>Null = leave the display alone.</summary>
    public DisplayModeDef? Display { get; init; }

    /// <summary>
    /// <c>display: keep</c>: never change the display for this preset, not even to the Big Picture (TV) mode a preset without
    /// <c>display:</c> would get. For games that read the desktop mode or store their own resolution.
    /// </summary>
    public bool DisplayKeep { get; init; }

    /// <summary>Extra launch arguments from the preset's <c>launch.args</c>; merged over the game's own (the preset wins per flag).</summary>
    public List<string> LaunchArgs { get; init; } = new();

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
    public const string SnapshotsFrom = "snapshotsFrom";

    /// <summary>
    /// Preset/profile key <c>launch: { args: [...] }</c>. Not in <see cref="ReservedKeys"/> so a game that already has a file
    /// with the id "launch" keeps working: the key is only the launch block when no such file exists.
    /// </summary>
    public const string Launch = "launch";

    public static readonly string[] ReservedKeys = [Extends, Display, Nvidia, Amd, Label, Description, TargetFps, SnapshotsFrom];

    /// <summary>Conventional preset names, in display order.</summary>
    public static readonly string[] KnownPresetNames = ["quality", "performance", "battery"];

    /// <summary>
    /// The device key whose presets apply to <paramref name="device"/> for this game: its own when the game has any,
    /// else the device's <see cref="DeviceDef.PresetsFrom"/> (one level, never followed further) when that has some.
    /// </summary>
    public static string PresetsDevice(GameDef game, string device, DeviceDef? def)
    {
        if (HasPresets(game, device)) return device;
        var from = def?.PresetsFrom?.Trim();
        if (!string.IsNullOrEmpty(from) && !string.Equals(from, device, StringComparison.Ordinal) && HasPresets(game, from)) return from;
        return device;
    }

    private static bool HasPresets(GameDef game, string device) =>
        game.Presets.TryGetValue(device, out var p) && p.Count > 0;

    /// <summary>Preset names for a device (falling back to its presetsFrom device), conventional ones first.</summary>
    public static IReadOnlyList<string> PresetNames(GameDef game, string device, DeviceDef? def = null) =>
        game.Presets.TryGetValue(PresetsDevice(game, device, def), out var presets)
            ? presets.Keys
                .OrderBy(k => Array.IndexOf(KnownPresetNames, k) is var i and >= 0 ? i : KnownPresetNames.Length)
                .ThenBy(k => k, StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>A preset body with its extends chain (into game.Profiles) merged.</summary>
    public static Dictionary<string, object?> FlattenPreset(GameDef game, string device, string preset, DeviceDef? def = null)
    {
        var source = PresetsDevice(game, device, def);
        if (!game.Presets.TryGetValue(source, out var presets) || !presets.TryGetValue(preset, out var body))
            throw new ConfigException($"{game.Id}: no preset '{preset}' for device '{device}'");
        return FlattenBody(game, body, $"presets.{source}.{preset}");
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
        Resolve(game, device, deviceDef, FlattenPreset(game, device, preset, deviceDef), PresetRef.Recommended(preset));

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
            DisplayKeep = body.GetValueOrDefault(Display) is string keep && keep.Equals("keep", StringComparison.OrdinalIgnoreCase),
            LaunchArgs = fileIds.Contains(Launch) ? [] : LaunchArgsOf(body.GetValueOrDefault(Launch), game.Id),
            SnapshotsFrom = source.Kind == PresetKind.Recommended && body.GetValueOrDefault(SnapshotsFrom) is { } sf
                            && Values.ToScalar(sf).Trim() is { Length: > 0 } slug ? slug : null,
        };
        CopyScalars(body.GetValueOrDefault(Nvidia), result.Nvidia);
        CopyScalars(body.GetValueOrDefault(Amd), result.Amd);
        foreach (var (key, value) in body)
        {
            if (ReservedKeys.Contains(key)) continue;
            if (key.Equals(Launch, StringComparison.OrdinalIgnoreCase) && !fileIds.Contains(key)) continue;
            if (!fileIds.Contains(key))
                throw new ConfigException($"{game.Id}: {source} sets '{key}', which is not a file id or a reserved key");
            if (value is Dictionary<string, object?> block)
                result.FileBlocks[game.Files.First(f => f.Id.Equals(key, StringComparison.OrdinalIgnoreCase)).Id] = block;
        }
        return result;
    }

    /// <summary>The <c>args</c> list of a preset's <c>launch:</c> block (each entry one command-line token).</summary>
    public static List<string> LaunchArgsOf(object? launch, string gameId)
    {
        if (launch is null) return [];
        if (launch is not Dictionary<string, object?> map)
            throw new ConfigException($"{gameId}: launch must be a map with an args list");
        foreach (var k in map.Keys.Where(k => !k.Equals("args", StringComparison.OrdinalIgnoreCase)))
            throw new ConfigException($"{gameId}: launch.{k} is not supported in a preset (only args)");
        if (!map.TryGetValue("args", out var args) || args is null) return [];
        if (args is not System.Collections.IEnumerable list || args is string)
            throw new ConfigException($"{gameId}: launch.args must be a list of strings");
        return list.Cast<object?>().Select(a => Values.ToScalar(a)).Where(a => a.Length > 0).ToList();
    }

    /// <summary>A manual save folder name: letters, digits, dot, dash, underscore; no path separators.</summary>
    public static bool IsSlug(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && value is not ("." or "..");

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
