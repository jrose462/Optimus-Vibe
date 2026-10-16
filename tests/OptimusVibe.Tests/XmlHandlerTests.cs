using System.Text;
using System.Xml.Linq;
using OptimusVibe.Config;
using OptimusVibe.Handlers;

namespace OptimusVibe.Tests;

public sealed class XmlHandlerTests : IDisposable
{
    private const string Fixture = "Settings.xml";
    private const string ResAttr = "//Setting[@name='Resolution']/@value";
    private readonly TempDir _tmp = new();
    private readonly XmlHandler _h = new();
    private readonly FileDef _def = new() { Id = "xml", Handler = "xml" };

    public void Dispose() => _tmp.Dispose();

    private static Dictionary<string, string> V(params (string Key, string Value)[] kv) =>
        kv.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Read_AttributesAndElements()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        var r = _h.Read(_def, path, [ResAttr, "/Settings/Graphics/FrameLimit", "/Settings/@version", "//Nope"]);
        Assert.Equal("2560x1440", r[ResAttr]);
        Assert.Equal("165", r["/Settings/Graphics/FrameLimit"]);
        Assert.Equal("3", r["/Settings/@version"]);
        Assert.Null(r["//Nope"]);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNulls()
    {
        Assert.Null(_h.Read(_def, _tmp.File("none.xml"), [ResAttr])[ResAttr]);
    }

    [Fact]
    public void Write_AttributeAndElement_PreservesRestOfDocument()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        _h.Write(_def, path, V((ResAttr, "1920x1080"), ("/Settings/Graphics/Fullscreen", "false")));

        var r = _h.Read(_def, path, [ResAttr, "/Settings/Graphics/Fullscreen", "//Setting[@name='Quality']/@value", "/Settings/Graphics/FrameLimit"]);
        Assert.Equal("1920x1080", r[ResAttr]);
        Assert.Equal("false", r["/Settings/Graphics/Fullscreen"]);
        Assert.Equal("High", r["//Setting[@name='Quality']/@value"]);
        Assert.Equal("165", r["/Settings/Graphics/FrameLimit"]);

        var text = File.ReadAllText(path);
        Assert.Contains("<!-- graphics -->", text);
        Assert.StartsWith("<?xml", text);
        Assert.Contains("\n    <Setting name=\"Quality\"", text); // indentation whitespace kept
        var doc = XDocument.Parse(text);
        Assert.Equal(2, doc.Descendants("Setting").Count());
    }

    [Fact]
    public void Write_LfFile_IsByteIdenticalExceptChangedValue()
    {
        var original = Fixtures.Text(Fixture, "\n");
        var path = _tmp.Write(Fixture, original);
        _h.Write(_def, path, V(("/Settings/Graphics/FrameLimit", "60")));
        Assert.Equal(original.Replace("<FrameLimit>165<", "<FrameLimit>60<"), File.ReadAllText(path));
    }

    [Fact]
    public void Write_CrlfFile_KeepsCrlf()
    {
        var original = Fixtures.Text(Fixture, "\r\n");
        var path = _tmp.Write(Fixture, original);
        _h.Write(_def, path, V(("/Settings/Graphics/FrameLimit", "60")));
        Assert.Equal(original.Replace("<FrameLimit>165<", "<FrameLimit>60<"), File.ReadAllText(path));
    }

    [Fact]
    public void Write_MissingXPath_Throws_AndLeavesFileUnchanged()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        var before = File.ReadAllBytes(path);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _h.Write(_def, path, V((ResAttr, "1280x720"), ("//Setting[@name='Missing']/@value", "x"))));
        Assert.Contains("matched nothing", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_MissingFile_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _h.Write(_def, _tmp.File("none.xml"), V((ResAttr, "1"))));
    }

    [Fact]
    public void Write_NoDeclaration_StaysWithoutDeclaration()
    {
        var path = _tmp.Write("nd.xml", "<root><a>1</a></root>");
        _h.Write(_def, path, V(("/root/a", "2")));
        Assert.Equal("<root><a>2</a></root>", File.ReadAllText(path));
    }

    [Fact]
    public void Write_Utf8NoBom_DoesNotAddBom()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        _h.Write(_def, path, V((ResAttr, "1280x720")));
        Assert.Equal((byte)'<', File.ReadAllBytes(path)[0]);
    }

    [Fact]
    public void Write_Utf8Bom_KeepsBom()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture, encoding: new UTF8Encoding(true));
        _h.Write(_def, path, V((ResAttr, "1280x720")));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("1280x720", _h.Read(_def, path, [ResAttr])[ResAttr]);
    }

    [Fact]
    public void Write_ReadOnlyFile_IsWritten()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        _h.Write(_def, path, V(("/Settings/Graphics/FrameLimit", "60")));
        Assert.Equal("60", _h.Read(_def, path, ["/Settings/Graphics/FrameLimit"]).Values.Single());
    }

    [Fact]
    public void Flatten_KeepsXPathKeysVerbatim()
    {
        var flat = _h.Flatten(new Dictionary<string, object?> { [ResAttr] = "1920x1080", ["/a/b"] = 3 });
        Assert.Equal("1920x1080", flat[ResAttr]);
        Assert.Equal("3", flat["/a/b"]);
    }
}
