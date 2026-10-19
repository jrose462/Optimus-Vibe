using System.Text.RegularExpressions;

namespace OptimusVibe.Config;

/// <summary>
/// Sets one device's line in a game file's <c>intent:</c> block ("what you want from it") by editing the text, so
/// the rest of the Claude-written file (comments, order, formatting) is kept exactly.
/// </summary>
public static class GameIntent
{
    public const int MaxLength = 300;

    private static readonly Regex BlockStart = new(@"^intent:\s*(\{\s*\})?\s*(#.*)?$", RegexOptions.CultureInvariant);

    /// <summary>Text with the intent for <paramref name="device"/> set to <paramref name="intent"/> (one line, double-quoted).</summary>
    public static string Set(string yaml, string device, string intent)
    {
        if (!Regex.IsMatch(device, "^[A-Za-z0-9_-]{1,64}$")) throw new ArgumentException($"Bad device name '{device}'.", nameof(device));
        var value = Clean(intent);
        if (value.Length == 0) throw new ArgumentException("Write what you want from the game first.", nameof(intent));

        var nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var trailingNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (trailingNewline) lines.RemoveAt(lines.Count - 1);

        var start = lines.FindIndex(l => BlockStart.IsMatch(l));
        if (start < 0)
        {
            lines.Add("intent:");
            lines.Add($"  {device}: {Quote(value)}");
        }
        else
        {
            lines[start] = "intent:";
            // the block: indented, blank or comment lines after "intent:"
            var end = start + 1;
            while (end < lines.Count && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0]) || lines[end].StartsWith('#'))) end++;
            var lastEntry = start;
            for (var i = start + 1; i < end; i++)
                if (lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith('#')) lastEntry = i;

            var entry = new Regex($"^(?<ind>\\s+)[\"']?{Regex.Escape(device)}[\"']?\\s*:", RegexOptions.CultureInvariant);
            var at = -1;
            for (var i = start + 1; i <= lastEntry; i++)
                if (entry.IsMatch(lines[i])) { at = i; break; }

            if (at >= 0)
            {
                var indent = entry.Match(lines[at]).Groups["ind"].Value;
                // a multi-line value continues on lines indented deeper than the key
                var stop = at + 1;
                while (stop <= lastEntry && (lines[stop].Trim().Length == 0 || Indent(lines[stop]) > indent.Length)) stop++;
                lines.RemoveRange(at, stop - at);
                lines.Insert(at, $"{indent}{device}: {Quote(value)}");
            }
            else
            {
                var indent = "  ";
                for (var i = start + 1; i <= lastEntry; i++)
                    if (lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith('#')) { indent = new string(' ', Indent(lines[i])); break; }
                lines.Insert(lastEntry + 1, $"{indent}{device}: {Quote(value)}");
            }
        }

        var text = string.Join(nl, lines);
        return trailingNewline || start < 0 ? text + nl : text;
    }

    /// <summary>
    /// <see cref="Set(string, string, string)"/> on a game file: the edited text must still load as a game with exactly
    /// that intent, or nothing is written. Returns the saved intent.
    /// </summary>
    public static string SetInFile(string path, string device, string intent)
    {
        var updated = Set(File.ReadAllText(path), device, intent);
        var value = Clean(intent);
        var check = Yaml.Parse<GameDef>(updated);
        if (check.Intent.GetValueOrDefault(device) != value)
            throw new InvalidOperationException($"Could not update the intent in {Path.GetFileName(path)}; nothing was changed.");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, updated);
        File.Move(tmp, path, overwrite: true);
        return value;
    }

    /// <summary>One line, no control characters, at most <see cref="MaxLength"/> characters.</summary>
    public static string Clean(string? intent)
    {
        var s = Regex.Replace(intent ?? "", @"\s+", " ").Trim();
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return s.Length <= MaxLength ? s : s[..MaxLength].TrimEnd();
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static int Indent(string line) => line.Length - line.TrimStart().Length;
}
