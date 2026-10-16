using System.Text.RegularExpressions;
using OptimusVibe.Config;

namespace OptimusVibe.Handlers;

/// <summary>
/// One setting per line, in a shape given by a preset or a custom template containing {key} and {value}:
/// <list type="bullet">
/// <item><c>cvar</c>: <c>SET gxResolution "1920x1080"</c> (World of Warcraft Config.wtf)</item>
/// <item><c>quoted</c>: <c>Key "value"</c> (Diablo IV LocalPrefs.txt)</item>
/// <item><c>equals</c>: <c>Key=value</c>; <c>equals-spaced</c>: <c>Key = value</c></item>
/// <item><c>colon</c>: <c>Key: value</c></item>
/// </list>
/// Unknown keys are appended at the end of the file.
/// </summary>
public sealed class KeyValueTextHandler : SettingsHandler
{
    public override string Name => "keyvalue";

    public static readonly Dictionary<string, string> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cvar"] = "SET {key} \"{value}\"",
        ["quoted"] = "{key} \"{value}\"",
        ["equals"] = "{key}={value}",
        ["equals-spaced"] = "{key} = {value}",
        ["colon"] = "{key}: {value}",
    };

    public override Dictionary<string, string> Flatten(Dictionary<string, object?> block) =>
        block.ToDictionary(kv => kv.Key, kv => Values.ToScalar(kv.Value), StringComparer.Ordinal);

    public static string TemplateFor(FileDef def)
    {
        if (!string.IsNullOrEmpty(def.Template)) return def.Template;
        if (string.IsNullOrEmpty(def.Format)) return Presets["equals"];
        return Presets.TryGetValue(def.Format, out var t)
            ? t
            : throw new ConfigException($"Unknown keyvalue format '{def.Format}' ({string.Join(", ", Presets.Keys)})");
    }

    internal static Regex BuildRegex(string template)
    {
        if (!template.Contains("{key}") || !template.Contains("{value}"))
            throw new ConfigException($"keyvalue template must contain {{key}} and {{value}}: {template}");
        var escaped = Regex.Escape(template)
            .Replace(@"\ ", @"\s+")           // any run of whitespace between tokens
            .Replace(@"\{key}", @"(?<key>[^\s=:""]+)")
            .Replace(@"\{value}", @"(?<value>.*?)");
        return new Regex(@"^\s*" + escaped + @"\s*$", RegexOptions.IgnoreCase);
    }

    public override Dictionary<string, string?> Read(FileDef def, string path, IEnumerable<string> keys)
    {
        var regex = BuildRegex(TemplateFor(def));
        var parsed = Parse(TextFile.Load(path).Lines, regex);
        return keys.ToDictionary(k => k, k => parsed.TryGetValue(k, out var hit) ? hit.Value : null);
    }

    public override void Write(FileDef def, string path, IReadOnlyDictionary<string, string> values)
    {
        var template = TemplateFor(def);
        var regex = BuildRegex(template);
        var file = TextFile.Load(path);
        var parsed = Parse(file.Lines, regex);
        foreach (var (key, value) in values)
        {
            if (parsed.TryGetValue(key, out var hit))
            {
                var line = file.Lines[hit.Line];
                var m = regex.Match(line);
                var g = m.Groups["value"];
                file.Lines[hit.Line] = line[..g.Index] + value + line[(g.Index + g.Length)..];
            }
            else
            {
                file.Lines.Add(template.Replace("{key}", key).Replace("{value}", value));
            }
        }
        file.Save(path);
    }

    private static Dictionary<string, (int Line, string Value)> Parse(List<string> lines, Regex regex)
    {
        var result = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var m = regex.Match(lines[i]);
            if (m.Success) result.TryAdd(m.Groups["key"].Value, (i, m.Groups["value"].Value));
        }
        return result;
    }
}

/// <summary>Opaque file: no keys, only whole-file snapshots (for games Claude has not mapped yet).</summary>
public sealed class RawFileHandler : SettingsHandler
{
    public override string Name => "file";

    public override Dictionary<string, string?> Read(FileDef def, string path, IEnumerable<string> keys) =>
        keys.ToDictionary(k => k, _ => (string?)null);

    public override void Write(FileDef def, string path, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count > 0)
            throw new ConfigException($"File '{def.Id}' uses handler 'file', which has no keys; use capture --snapshot instead");
    }
}
