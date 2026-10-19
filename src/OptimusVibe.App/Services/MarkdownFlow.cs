using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using OptimusVibe.Logging;
using OptimusVibe.Updates;

namespace OptimusVibe.App.Services;

/// <summary>
/// Release notes Markdown (<see cref="MarkdownLite"/>) as a WPF FlowDocument. Text takes the hosting viewer's
/// foreground and font size (so it reads on the feature card and in the pop-out alike); links are underlined in that
/// same colour and open http(s) URLs in the browser.
/// </summary>
public static class MarkdownFlow
{
    public static FlowDocument ToDocument(string? markdown, double fontSize = 14)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = fontSize, TextAlignment = TextAlignment.Left };
        doc.SetResourceReference(FlowDocument.FontFamilyProperty, "UiFont");
        var blocks = MarkdownLite.Parse(markdown);
        if (blocks.Count == 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("No release notes.")) { Margin = new Thickness(0) });
            return doc;
        }

        List? list = null;
        int listDepth = -1;
        bool listOrdered = false;
        foreach (var b in blocks)
        {
            if (b is not MdListItem) list = null;
            switch (b)
            {
                case MdHeading h:
                    var hp = Para(doc, h.Content);
                    hp.FontWeight = FontWeights.SemiBold;
                    hp.FontSize = fontSize * (h.Level switch { 1 => 1.5, 2 => 1.3, 3 => 1.12, _ => 1.0 });
                    hp.Margin = new Thickness(0, doc.Blocks.Count == 0 ? 0 : fontSize * 0.9, 0, fontSize * 0.35);
                    doc.Blocks.Add(hp);
                    break;
                case MdParagraph p:
                    var pp = Para(doc, p.Content);
                    pp.Margin = new Thickness(0, 0, 0, fontSize * 0.6);
                    doc.Blocks.Add(pp);
                    break;
                case MdListItem li:
                    if (list is null || li.Depth != listDepth || li.Ordered != listOrdered)
                    {
                        list = new List
                        {
                            MarkerStyle = li.Ordered ? TextMarkerStyle.Decimal : li.Depth == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle,
                            StartIndex = li.Ordered ? Math.Max(1, li.Number) : 1,
                            Margin = new Thickness(fontSize * 1.4 * li.Depth, 0, 0, fontSize * 0.5),
                            Padding = new Thickness(fontSize * 1.4, 0, 0, 0),
                        };
                        listDepth = li.Depth;
                        listOrdered = li.Ordered;
                        doc.Blocks.Add(list);
                    }
                    var ip = Para(doc, li.Content);
                    ip.Margin = new Thickness(0, 0, 0, fontSize * 0.25);
                    list.ListItems.Add(new ListItem(ip));
                    break;
                case MdRule:
                    var rule = new Paragraph { Margin = new Thickness(0, fontSize * 0.4, 0, fontSize * 0.8), FontSize = 1, BorderThickness = new Thickness(0, 1, 0, 0) };
                    rule.SetResourceReference(Block.BorderBrushProperty, "OutlineBrush");
                    doc.Blocks.Add(rule);
                    break;
                case MdCodeBlock c:
                    var cp = new Paragraph(new Run(c.Text)) { Margin = new Thickness(0, 0, 0, fontSize * 0.6), Padding = new Thickness(10, 8, 10, 8) };
                    cp.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                    cp.Background = CodeBackground;
                    cp.FontSize = fontSize * 0.92;
                    doc.Blocks.Add(cp);
                    break;
            }
        }
        // no trailing gap under the last block
        if (doc.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        return doc;
    }

    /// <summary>Translucent: works on dark cards and on the accent feature card.</summary>
    private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static Paragraph Para(FlowDocument doc, IReadOnlyList<MdInline> content)
    {
        var p = new Paragraph();
        foreach (var inline in Inlines(doc, content)) p.Inlines.Add(inline);
        return p;
    }

    private static IEnumerable<Inline> Inlines(FlowDocument doc, IReadOnlyList<MdInline> content)
    {
        foreach (var i in content)
        {
            switch (i)
            {
                case MdText t:
                    yield return new Run(t.Text);
                    break;
                case MdBold b:
                    var bold = new Bold();
                    foreach (var x in Inlines(doc, b.Content)) bold.Inlines.Add(x);
                    yield return bold;
                    break;
                case MdItalic it:
                    var italic = new Italic();
                    foreach (var x in Inlines(doc, it.Content)) italic.Inlines.Add(x);
                    yield return italic;
                    break;
                case MdCode c:
                    var code = new Run(c.Text) { Background = CodeBackground };
                    code.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                    yield return code;
                    break;
                case MdLink l when Uri.TryCreate(l.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp):
                    var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.ToString(), Focusable = false };
                    foreach (var x in Inlines(doc, l.Content)) link.Inlines.Add(x);
                    // the viewer's text colour (the default hyperlink blue is unreadable on the feature card)
                    link.SetBinding(TextElement.ForegroundProperty, new Binding(nameof(FlowDocument.Foreground)) { Source = doc });
                    link.RequestNavigate += (_, e) => { Open(e.Uri); e.Handled = true; };
                    yield return link;
                    break;
                case MdLink l: // not http(s): plain text
                    foreach (var x in Inlines(doc, l.Content)) yield return x;
                    break;
            }
        }
    }

    private static void Open(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"Could not open {uri}: {ex.Message}"); }
    }
}
