using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameProfiles.Config;

namespace GameProfiles.Handlers;

/// <summary>
/// JSON files. Keys are paths: <c>graphics.dlss</c>, <c>items[2].value</c>, or array filters such as
/// <c>data[group_name=/graphics/presets].options[name=DLSS].value</c>. Quote names containing dots: <c>['a.b'].c</c>.
/// Existing value types (number/bool/string) are preserved on write.
/// </summary>
public sealed class JsonHandler : SettingsHandler
{
    public override string Name => "json";

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Flat keys are already paths, so nested YAML maps are joined with '.' by the base class.

    public override Dictionary<string, string?> Read(FileDef def, string path, IEnumerable<string> keys)
    {
        var root = Load(path);
        return keys.ToDictionary(k => k, k => Find(root, Parse(k), create: false) is { } node ? NodeToString(node) : null);
    }

    public override void Write(FileDef def, string path, IReadOnlyDictionary<string, string> values)
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var encoding = bytes is null ? new UTF8Encoding(false) : TextFile.DetectEncoding(bytes);
        var root = Load(path) ?? new JsonObject();
        foreach (var (key, value) in values)
        {
            var segments = Parse(key);
            var parentSegments = segments.Take(segments.Count - 1).ToList();
            var last = segments[^1];
            var parent = parentSegments.Count == 0 ? root : Find(root, parentSegments, create: true)
                ?? throw new InvalidOperationException($"JSON path '{key}' not found and cannot be created");

            var existing = Find(parent, [last], create: false);
            var newNode = Convert(value, existing);
            switch (last)
            {
                case PropSegment p when parent is JsonObject obj:
                    obj[p.Name] = newNode;
                    break;
                case IndexSegment ix when parent is JsonArray arr && ix.Index < arr.Count:
                    arr[ix.Index] = newNode;
                    break;
                case FilterSegment when existing is not null:
                    existing.ReplaceWith(newNode);
                    break;
                default:
                    throw new InvalidOperationException($"Cannot write JSON path '{key}'");
            }
        }

        bool indented = bytes is null || encoding.GetString(bytes).Contains('\n');
        var text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
        TextFile.SaveText(path, text, encoding);
    }

    private static JsonNode? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var encoding = TextFile.DetectEncoding(bytes);
        var text = encoding.GetString(bytes, encoding.GetPreamble().Length, bytes.Length - encoding.GetPreamble().Length);
        return JsonNode.Parse(text, documentOptions: DocOptions);
    }

    private static string NodeToString(JsonNode node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(),
    };

    private static JsonNode? Convert(string value, JsonNode? existing)
    {
        var kind = existing?.GetValueKind();
        if (kind is JsonValueKind.String) return JsonValue.Create(value);
        if (kind is JsonValueKind.True or JsonValueKind.False && bool.TryParse(value, out var b)) return JsonValue.Create(b);
        if (kind is JsonValueKind.Number || kind is null)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        }
        if (kind is null && bool.TryParse(value, out var b2)) return JsonValue.Create(b2);
        if (kind is null && value == "null") return null;
        return JsonValue.Create(value);
    }

    // ---- path parsing ----

    internal abstract record Segment;
    internal sealed record PropSegment(string Name) : Segment;
    internal sealed record IndexSegment(int Index) : Segment;
    internal sealed record FilterSegment(string Field, string Value) : Segment;

    internal static List<Segment> Parse(string path)
    {
        var result = new List<Segment>();
        int i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.') { i++; continue; }
            if (path[i] == '[')
            {
                int close = FindClose(path, i);
                var inner = path[(i + 1)..close];
                i = close + 1;
                if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"'))
                    result.Add(new PropSegment(inner[1..^1]));
                else if (int.TryParse(inner, out var idx))
                    result.Add(new IndexSegment(idx));
                else if (inner.IndexOf('=') is var eq and > 0)
                    result.Add(new FilterSegment(inner[..eq].Trim(), inner[(eq + 1)..].Trim().Trim('\'', '"')));
                else
                    throw new ConfigException($"Bad JSON path segment [{inner}] in '{path}'");
                continue;
            }
            int start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
            result.Add(new PropSegment(path[start..i]));
        }
        if (result.Count == 0) throw new ConfigException("Empty JSON path");
        return result;
    }

    private static int FindClose(string path, int open)
    {
        char? quote = null;
        for (int i = open + 1; i < path.Length; i++)
        {
            if (quote is not null) { if (path[i] == quote) quote = null; continue; }
            if (path[i] is '\'' or '"') quote = path[i];
            else if (path[i] == ']') return i;
        }
        throw new ConfigException($"Unclosed [ in JSON path '{path}'");
    }

    private static JsonNode? Find(JsonNode? node, IEnumerable<Segment> segments, bool create)
    {
        foreach (var seg in segments)
        {
            if (node is null) return null;
            switch (seg)
            {
                case PropSegment p when node is JsonObject obj:
                    if (!obj.TryGetPropertyValue(p.Name, out var child) || child is null)
                    {
                        if (!create) return null;
                        child = new JsonObject();
                        obj[p.Name] = child;
                    }
                    node = child;
                    break;
                case IndexSegment ix when node is JsonArray arr:
                    node = ix.Index < arr.Count ? arr[ix.Index] : null;
                    break;
                case FilterSegment f when node is JsonArray arr:
                    node = arr.FirstOrDefault(e => e is JsonObject o && o.TryGetPropertyValue(f.Field, out var v) &&
                                                   v is not null && Values.Equivalent(NodeToString(v), f.Value));
                    break;
                default:
                    return null;
            }
        }
        return node;
    }
}
