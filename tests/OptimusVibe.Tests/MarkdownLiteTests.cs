using OptimusVibe.Updates;

namespace OptimusVibe.Tests;

public sealed class MarkdownLiteTests
{
    // Compact form of a parse, e.g. "H2:What's new|LI0:**app:** fix|P:text"
    private static string Shape(IEnumerable<MdBlock> blocks) => string.Join("|", blocks.Select(b => b switch
    {
        MdHeading h => $"H{h.Level}:{Show(h.Content)}",
        MdParagraph p => $"P:{Show(p.Content)}",
        MdListItem l => $"{(l.Ordered ? $"OL{l.Number}." : "LI")}{l.Depth}:{Show(l.Content)}",
        MdRule => "HR",
        MdCodeBlock c => $"CODE:{c.Text}",
        _ => "?",
    }));

    private static string Show(IEnumerable<MdInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        MdText t => t.Text,
        MdBold b => $"**{Show(b.Content)}**",
        MdItalic it => $"_{Show(it.Content)}_",
        MdCode c => $"`{c.Text}`",
        MdLink l => $"[{Show(l.Content)}]({l.Url})",
        _ => "?",
    }));

    [Fact]
    public void GitHubReleaseNotes()
    {
        const string md = """
            ## What's new in v0.6.4

            ### Features

            - **app:** Steam review sync confidence (#30) ([`73cacb2`](https://github.com/o/r/commit/73cacb2))

            <!-- hidden -->
            Install or update: download `OptimusVibe.exe` below.

            ## What's Changed
            * fix(game-lab): stop duplicate runs by @jrose462 in https://github.com/o/r/pull/29
            ---
            **Full Changelog**: https://github.com/o/r/compare/v0.6.3...v0.6.4
            """;
        Assert.Equal(
            "H2:What's new in v0.6.4|H3:Features|" +
            "LI0:**app:** Steam review sync confidence (#30) ([`73cacb2`](https://github.com/o/r/commit/73cacb2))|" +
            "P:Install or update: download `OptimusVibe.exe` below.|" +
            "H2:What's Changed|LI0:fix(game-lab): stop duplicate runs by @jrose462 in [https://github.com/o/r/pull/29](https://github.com/o/r/pull/29)|" +
            "HR|P:**Full Changelog**: [https://github.com/o/r/compare/v0.6.3...v0.6.4](https://github.com/o/r/compare/v0.6.3...v0.6.4)",
            Shape(MarkdownLite.Parse(md)));
    }

    [Fact]
    public void ListsNestAndContinue_ParagraphLinesJoin()
    {
        const string md = "1. one\n   still one\n2. two\n  - nested\n\nline a\nline b\r\n";
        Assert.Equal("OL1.0:one still one|OL2.0:two|LI1:nested|P:line a line b", Shape(MarkdownLite.Parse(md)));
    }

    [Fact]
    public void FencedCode_IsKeptVerbatim() =>
        Assert.Equal("P:x|CODE:a **b**\n  c|P:y", Shape(MarkdownLite.Parse("x\n```bash\na **b**\n  c\n```\ny")));

    [Theory]
    [InlineData("*italic* and _also_", "_italic_ and _also_")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    [InlineData("**bold [link](https://x.y)**", "**bold [link](https://x.y)**")]
    [InlineData(@"\*not italic\*", "*not italic*")]
    [InlineData("see https://x.y/z.", "see [https://x.y/z](https://x.y/z).")]
    [InlineData("unclosed `tick and **star", "unclosed `tick and **star")]
    public void Inlines(string md, string expected) => Assert.Equal(expected, Show(MarkdownLite.Inlines(md)));

    [Fact]
    public void EmptyInput() => Assert.Empty(MarkdownLite.Parse("  \n "));

    [Fact]
    public void PlainText_DropsMarkup() =>
        Assert.Equal("app: fix code link", MarkdownLite.PlainText(MarkdownLite.Inlines("**app:** _fix_ `code` [link](https://x.y)")));
}
