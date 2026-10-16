using System.Text;

namespace GameProfiles.Config;

/// <summary>Minimal Valve KeyValues (text VDF) parser — enough for libraryfolders.vdf, appmanifest, loginusers.vdf.</summary>
internal static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        int pos = 0;
        return ParseObject(text, ref pos, topLevel: true);
    }

    public static Dictionary<string, object> Load(string path) => Parse(File.ReadAllText(path));

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
