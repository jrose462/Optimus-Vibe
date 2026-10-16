using System.Text;

namespace GameProfiles.Config;

/// <summary>
/// Minimal Valve KeyValues (text VDF) parser and writer — enough for libraryfolders.vdf, appmanifest, loginusers.vdf
/// and localconfig.vdf. Objects are case-insensitive dictionaries that keep the key case first seen; key order is
/// file order (Dictionary keeps insertion order as long as nothing is removed, so editors only add or update keys).
/// </summary>
internal static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        int pos = 0;
        return ParseObject(text, ref pos, topLevel: true);
    }

    public static Dictionary<string, object> Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Valve text KeyValues: tab-indented, quoted keys/values (two tabs between), \ " newline and tab escaped.</summary>
    public static string Serialize(Dictionary<string, object> root, string newLine = "\n")
    {
        var sb = new StringBuilder();
        Write(sb, root, 0, newLine);
        return sb.ToString();
    }

    /// <summary>Write atomically (temp file in the same folder, then replace), UTF-8 without BOM.</summary>
    public static void Save(string path, Dictionary<string, object> root, string newLine = "\n")
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, Serialize(root, newLine), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
            else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>The child object under <paramref name="key"/> (any case); created (appended) if missing.</summary>
    public static Dictionary<string, object> GetOrAddChild(this Dictionary<string, object> obj, string key)
    {
        if (obj.TryGetValue(key, out var v) && v is Dictionary<string, object> existing) return existing;
        var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        obj[key] = child; // replaces a string value in place, otherwise appends
        return child;
    }

    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\t': sb.Append(@"\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, Dictionary<string, object> obj, int depth, string nl)
    {
        var indent = new string('\t', depth);
        foreach (var (key, value) in obj)
        {
            if (value is Dictionary<string, object> child)
            {
                sb.Append(indent).Append('"').Append(Escape(key)).Append('"').Append(nl);
                sb.Append(indent).Append('{').Append(nl);
                Write(sb, child, depth + 1, nl);
                sb.Append(indent).Append('}').Append(nl);
            }
            else
            {
                sb.Append(indent).Append('"').Append(Escape(key)).Append("\"\t\t\"")
                  .Append(Escape(value?.ToString() ?? "")).Append('"').Append(nl);
            }
        }
    }

    public static Dictionary<string, object>? Child(this Dictionary<string, object> obj, string key) =>
        obj.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    public static string? Str(this Dictionary<string, object> obj, string key) =>
        obj.TryGetValue(key, out var v) ? v as string : null;

    private static Dictionary<string, object> ParseObject(string s, ref int pos, bool topLevel)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var key = NextToken(s, ref pos);
            if (key is null) return result;
            if (key == "}") return result;
            var value = NextToken(s, ref pos);
            if (value is null) return result;
            if (value == "{")
                result[key] = ParseObject(s, ref pos, topLevel: false);
            else
                result[key] = value;
        }
    }

    private static string? NextToken(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            char c = s[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }
            if (c == '{' || c == '}') { pos++; return c.ToString(); }
            if (c == '"')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < s.Length && s[pos] != '"')
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length)
                    {
                        pos++;
                        sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', _ => s[pos] });
                    }
                    else sb.Append(s[pos]);
                    pos++;
                }
                pos++; // closing quote
                return sb.ToString();
            }
            // unquoted token
            int start = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '{' && s[pos] != '}' && s[pos] != '"') pos++;
            return s[start..pos];
        }
        return null;
    }
}
