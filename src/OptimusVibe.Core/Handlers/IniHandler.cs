using System.Text.RegularExpressions;
using OptimusVibe.Config;

namespace OptimusVibe.Handlers;

/// <summary>
/// INI files (Unreal GameUserSettings.ini, Overwatch Settings_v0.txt...). YAML blocks are nested by section:
/// <code>user: { "/Script/Engine.GameUserSettings": { ResolutionSizeX: 1920 } }</code>
/// Flat keys are "[Section]Key"; keys before the first section use "[]Key".
/// </summary>
public sealed partial class IniHandler : SettingsHandler
{
    public override string Name => "ini";

    [GeneratedRegex(@"^\s*\[(?<section>[^\]]*)\]\s*$")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^(?<prefix>\s*(?<key>[^=;#\[\s][^=]*?)\s*=\s*)(?<value>.*?)\s*$")]
    private static partial Regex KeyRegex();

    public static string MakeKey(string section, string key) => $"[{section}]{key}";

    public static (string Section, string Key) SplitKey(string flat)
    {
        if (!flat.StartsWith('[')) return ("", flat);
        int close = flat.IndexOf(']');
        return close < 0 ? ("", flat) : (flat[1..close], flat[(close + 1)..]);
    }

    public override Dictionary<string, string> Flatten(Dictionary<string, object?> block)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in block)
        {
            if (v is Dictionary<string, object?> section)
                foreach (var (key, value) in section)
                    result[MakeKey(k, key)] = Values.ToScalar(value);
            else
                result[MakeKey("", k)] = Values.ToScalar(v);
        }
        return result;
    }

    public override Dictionary<string, object?> Unflatten(IReadOnlyDictionary<string, string> flat)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (flatKey, value) in flat)
        {
            var (section, key) = SplitKey(flatKey);
            if (section.Length == 0) { result[key] = value; continue; }
            if (result.GetValueOrDefault(section) is not Dictionary<string, object?> map)
                result[section] = map = new Dictionary<string, object?>();
            map[key] = value;
        }
        return result;
    }

    public override Dictionary<string, string?> Read(FileDef def, string path, IEnumerable<string> keys)
    {
        var file = TextFile.Load(path);
        var all = Parse(file.Lines);
        return keys.ToDictionary(k => k, k =>
        {
            var (section, key) = SplitKey(k);
            return all.TryGetValue((section.ToLowerInvariant(), key.ToLowerInvariant()), out var hit) ? hit.Value : null;
        });
    }

    public override void Write(FileDef def, string path, IReadOnlyDictionary<string, string> values)
    {
        var file = TextFile.Load(path);
        var lines = file.Lines;
        foreach (var (flatKey, value) in values)
        {
            var (section, key) = SplitKey(flatKey);
            var all = Parse(lines);
            if (all.TryGetValue((section.ToLowerInvariant(), key.ToLowerInvariant()), out var hit))
            {
                var m = KeyRegex().Match(lines[hit.Line]);
                lines[hit.Line] = m.Groups["prefix"].Value + value;
                continue;
            }
            InsertKey(lines, section, key, value);
        }
        file.Save(path);
    }

    private static void InsertKey(List<string> lines, string section, string key, string value)
    {
        var entry = $"{key}={value}";
        if (section.Length == 0)
        {
            int firstSection = lines.FindIndex(l => SectionRegex().IsMatch(l));
            lines.Insert(firstSection < 0 ? lines.Count : firstSection, entry);
            return;
        }
        int header = lines.FindIndex(l => SectionRegex().Match(l) is { Success: true } m &&
                                          string.Equals(m.Groups["section"].Value, section, StringComparison.OrdinalIgnoreCase));
        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(entry);
            return;
        }
        // after the last non-blank line of this section
        int end = header + 1;
        while (end < lines.Count && !SectionRegex().IsMatch(lines[end])) end++;
        int insertAt = end;
        while (insertAt > header + 1 && lines[insertAt - 1].Trim().Length == 0) insertAt--;
        lines.Insert(insertAt, entry);
    }

    /// <summary>Every "[Section]Key" with its value, in file order; a repeated key (Unreal's +Array lines) gets " #2", " #3"... after it.</summary>
    public override IReadOnlyList<(string Key, string Value)>? ReadAll(string text)
    {
        var result = new List<(string, string)>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var sm = SectionRegex().Match(line);
            if (sm.Success) { section = sm.Groups["section"].Value; continue; }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            var km = KeyRegex().Match(line);
            if (!km.Success) continue;
            var key = MakeKey(section, km.Groups["key"].Value.Trim());
            var n = seen.GetValueOrDefault(key) + 1;
            seen[key] = n;
            result.Add((n == 1 ? key : $"{key} #{n}", km.Groups["value"].Value));
        }
        return result;
    }

    /// <summary>(section, key) lower-cased → first occurrence.</summary>
    private static Dictionary<(string, string), (int Line, string Value)> Parse(List<string> lines)
    {
        var result = new Dictionary<(string, string), (int, string)>();
        string section = "";
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var sm = SectionRegex().Match(line);
            if (sm.Success) { section = sm.Groups["section"].Value.ToLowerInvariant(); continue; }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            var km = KeyRegex().Match(line);
            if (!km.Success) continue;
            var k = (section, km.Groups["key"].Value.Trim().ToLowerInvariant());
            result.TryAdd(k, (i, km.Groups["value"].Value));
        }
        return result;
    }
}
