using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using OptimusVibe.Config;

namespace OptimusVibe.Handlers;

/// <summary>
/// XML files. Keys are XPath expressions selecting an element (its text is the value) or an attribute, e.g.
/// <c>//Setting[@name='Resolution']/@value</c>. Keys must already exist; XML structure is never created.
/// </summary>
public sealed class XmlHandler : SettingsHandler
{
    public override string Name => "xml";

    // XPath keys contain '/', so nested YAML maps are not meaningful; keep keys verbatim.
    public override Dictionary<string, string> Flatten(Dictionary<string, object?> block) =>
        block.ToDictionary(kv => kv.Key, kv => Values.ToScalar(kv.Value), StringComparer.Ordinal);

    public override Dictionary<string, string?> Read(FileDef def, string path, IEnumerable<string> keys)
    {
        if (!File.Exists(path)) return keys.ToDictionary(k => k, _ => (string?)null);
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        return keys.ToDictionary(k => k, k => Select(doc, k) switch
        {
            XAttribute a => a.Value,
            XElement e => e.Value,
            _ => null,
        });
    }

    public override void Write(FileDef def, string path, IReadOnlyDictionary<string, string> values)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"XML file does not exist: {path} (XML structure is never created)");
        var bytes = File.ReadAllBytes(path);
        var encoding = TextFile.DetectEncoding(bytes);
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        foreach (var (key, value) in values)
        {
            switch (Select(doc, key))
            {
                case XAttribute a: a.Value = value; break;
                case XElement e: e.Value = value; break;
                default: throw new InvalidOperationException($"XPath '{key}' matched nothing in {path}");
            }
        }

        var settings = new XmlWriterSettings
        {
            Encoding = encoding,
            OmitXmlDeclaration = doc.Declaration is null,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings)) doc.Save(writer);

        // XDocument normalizes CRLF to LF on load; put the file's original line endings back.
        var output = stream.ToArray();
        var preamble = encoding.GetPreamble().Length;
        var originalText = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        if (originalText.Contains("\r\n"))
        {
            var outPreamble = output.AsSpan(0, Math.Min(preamble, output.Length)).SequenceEqual(encoding.GetPreamble()) ? preamble : 0;
            var text = encoding.GetString(output, outPreamble, output.Length - outPreamble).Replace("\r\n", "\n").Replace("\n", "\r\n");
            output = [.. output.AsSpan(0, outPreamble), .. encoding.GetBytes(text)];
        }
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.WriteAllBytes(path, output);
    }

    /// <summary>Every attribute and every leaf element as an XPath (<c>/Root/Setting[2]/@value</c>) with its value; null when the text is not XML. At most 5000.</summary>
    public override IReadOnlyList<(string Key, string Value)>? ReadAll(string text)
    {
        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (XmlException) { return null; }
        if (doc.Root is null) return null;
        var result = new List<(string, string)>();
        void Walk(XElement e, string path)
        {
            if (result.Count >= 5000) return;
            foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration)) result.Add(($"{path}/@{a.Name.LocalName}", a.Value));
            var kids = e.Elements().ToList();
            if (kids.Count == 0)
            {
                if (e.Value.Length > 0) result.Add((path, e.Value));
                return;
            }
            foreach (var group in kids.GroupBy(k => k.Name))
            {
                var list = group.ToList();
                for (var i = 0; i < list.Count; i++)
                    Walk(list[i], $"{path}/{group.Key.LocalName}{(list.Count > 1 ? $"[{i + 1}]" : "")}");
            }
        }
        Walk(doc.Root, "/" + doc.Root.Name.LocalName);
        return result;
    }

    private static XObject? Select(XDocument doc, string xpath)
    {
        var result = doc.XPathEvaluate(xpath);
        return result is IEnumerable<object> seq ? seq.OfType<XObject>().FirstOrDefault() : null;
    }
}
