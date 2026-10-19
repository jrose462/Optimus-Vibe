using System.Text;
using System.Text.RegularExpressions;

namespace OptimusVibe.Updates;

// The small Markdown subset GitHub release notes use: headings, paragraphs, bullet / numbered lists (nested by
// indent), rules, fenced code, **bold**, *italic*, `code`, [links](url) and bare URLs. HTML comments are dropped;
// anything else is kept as plain text.

public abstract record MdBlock;
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Content) : MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Content) : MdBlock;
public sealed record MdListItem(bool Ordered, int Number, int Depth, IReadOnlyList<MdInline> Content) : MdBlock;
public sealed record MdRule : MdBlock;
public sealed record MdCodeBlock(string Text) : MdBlock;

public abstract record MdInline;
public sealed record MdText(string Text) : MdInline;
public sealed record MdBold(IReadOnlyList<MdInline> Content) : MdInline;
public sealed record MdItalic(IReadOnlyList<MdInline> Content) : MdInline;
public sealed record MdCode(string Text) : MdInline;
public sealed record MdLink(IReadOnlyList<MdInline> Content, string Url) : MdInline;

public static class MarkdownLite
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    private static readonly Regex ListItem = new(@"^(\s*)([-*+]|(\d{1,9})[.)])\s+(.*)$");
    private static readonly Regex Rule = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$");
    private static readonly Regex Fence = new(@"^\s*(```|~~~)");
    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline);
    private static readonly Regex LinkAt = new(@"\G\[([^\]]+)\]\(([^)\s]+)\)");
    private static readonly Regex UrlAt = new(@"\Ghttps?://[^\s<>()\[\]]+");

    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;
        var lines = Comment.Replace(markdown, "").Replace("\r\n", "\n").Split('\n');

        var para = new StringBuilder();
        (bool Ordered, int Number, int Depth, StringBuilder Text)? item = null;
        void Flush()
        {
            if (para.Length > 0) { blocks.Add(new MdParagraph(Inlines(para.ToString()))); para.Clear(); }
            if (item is { } it) { blocks.Add(new MdListItem(it.Ordered, it.Number, it.Depth, Inlines(it.Text.ToString()))); item = null; }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (Fence.IsMatch(line))
            {
                Flush();
                var code = new List<string>();
                for (i++; i < lines.Length && !Fence.IsMatch(lines[i]); i++) code.Add(lines[i].TrimEnd());
                blocks.Add(new MdCodeBlock(string.Join("\n", code)));
                continue;
            }
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (Rule.IsMatch(line)) { Flush(); blocks.Add(new MdRule()); continue; }
            if (Heading.Match(line) is { Success: true } h)
            {
                Flush();
                blocks.Add(new MdHeading(h.Groups[1].Length, Inlines(h.Groups[2].Value)));
                continue;
            }
            if (ListItem.Match(line) is { Success: true } li)
            {
                Flush();
                var indent = li.Groups[1].Value.Replace("\t", "    ").Length;
                var ordered = li.Groups[3].Success;
                item = (ordered, ordered ? int.Parse(li.Groups[3].Value) : 0, Math.Min(indent / 2, 4), new StringBuilder(li.Groups[4].Value.Trim()));
                continue;
            }
            // a continuation of the list item or paragraph above
            var text = line.Trim();
            if (item is { } open) open.Text.Append(' ').Append(text);
            else (para.Length > 0 ? para.Append(' ') : para).Append(text);
        }
        Flush();
        return blocks;
    }

    /// <summary>Inline spans of one line / paragraph of text.</summary>
    public static IReadOnlyList<MdInline> Inlines(string text)
    {
        var result = new List<MdInline>();
        var plain = new StringBuilder();
        void Add(MdInline inline)
        {
            if (plain.Length > 0) { result.Add(new MdText(plain.ToString())); plain.Clear(); }
            result.Add(inline);
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#+-.!<>".Contains(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`' && text.IndexOf('`', i + 1) is var endCode and > 0)
            {
                Add(new MdCode(text[(i + 1)..endCode]));
                i = endCode + 1;
                continue;
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c
                && text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal) is var endBold and > 0 && endBold > i + 2)
            {
                Add(new MdBold(Inlines(text[(i + 2)..endBold])));
                i = endBold + 2;
                continue;
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])
                && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                && ClosingEmphasis(text, i + 1, c) is var endEm and > 0)
            {
                Add(new MdItalic(Inlines(text[(i + 1)..endEm])));
                i = endEm + 1;
                continue;
            }
            if (c == '[' && LinkAt.Match(text, i) is { Success: true } link)
            {
                Add(new MdLink(Inlines(link.Groups[1].Value), link.Groups[2].Value));
                i += link.Length;
                continue;
            }
            if (c == 'h' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && UrlAt.Match(text, i) is { Success: true } url)
            {
                var u = url.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
                Add(new MdLink([new MdText(u)], u));
                i += u.Length;
                continue;
            }
            plain.Append(c);
            i++;
        }
        if (plain.Length > 0) result.Add(new MdText(plain.ToString()));
        return result;
    }

    /// <summary>The closing single * / _ of an emphasis starting before <paramref name="from"/>, or -1.</summary>
    private static int ClosingEmphasis(string text, int from, char c)
    {
        for (var j = from + 1; j < text.Length; j++)
        {
            if (text[j] != c || char.IsWhiteSpace(text[j - 1])) continue;
            if (j + 1 < text.Length && text[j + 1] == c) { j++; continue; } // part of a ** pair
            if (c == '_' && j + 1 < text.Length && char.IsLetterOrDigit(text[j + 1])) continue; // snake_case
            return j;
        }
        return -1;
    }

    /// <summary>The text without markup (for screen readers and tests).</summary>
    public static string PlainText(IEnumerable<MdInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        MdText t => t.Text,
        MdCode c => c.Text,
        MdBold b => PlainText(b.Content),
        MdItalic it => PlainText(it.Content),
        MdLink l => PlainText(l.Content),
        _ => "",
    }));
}
