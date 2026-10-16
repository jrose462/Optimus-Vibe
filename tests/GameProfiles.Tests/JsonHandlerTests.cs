using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameProfiles.Config;
using GameProfiles.Handlers;

namespace GameProfiles.Tests;

public sealed class JsonHandlerTests : IDisposable
{
    private const string Fixture = "UserSettings.json";
    private const string Presets = "data[group_name=/graphics/presets].options";
    private readonly TempDir _tmp = new();
    private readonly JsonHandler _h = new();
    private readonly FileDef _def = new() { Id = "user", Handler = "json" };

    public void Dispose() => _tmp.Dispose();

    private static Dictionary<string, string> V(params (string Key, string Value)[] kv) =>
        kv.ToDictionary(p => p.Key, p => p.Value);

    private static JsonNode Parse(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    private static JsonNode Option(JsonNode root, string group, string name) =>
        root["data"]!.AsArray().Single(g => (string)g!["group_name"]! == group)!["options"]!.AsArray()
            .Single(o => (string)o!["name"]! == name)!["value"]!;

    [Fact]
    public void Read_FilterPaths_PlainPaths_IndexPaths_QuotedNames()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        var r = _h.Read(_def, path, [
            $"{Presets}[name=DLSS].value",
            $"{Presets}[name=MaxFPS].value",
            $"{Presets}[name=Sharpness].value",
            $"{Presets}[name=VSync].value",
            "data[group_name='/video/display'].options[name=\"Resolution\"].value",
            "graphics.quality",
            "graphics.hdr",
            "items[1].value",
            "['a.b'].c",
            "version",
            "graphics",
            "graphics.missing",
            $"{Presets}[name=Nope].value",
            "items[9].value",
        ]);
        Assert.Equal("DLSS_Quality", r[$"{Presets}[name=DLSS].value"]);
        Assert.Equal("60", r[$"{Presets}[name=MaxFPS].value"]);
        Assert.Equal("0.5", r[$"{Presets}[name=Sharpness].value"]);
        Assert.Equal("true", r[$"{Presets}[name=VSync].value"]);
        Assert.Equal("2560x1440", r["data[group_name='/video/display'].options[name=\"Resolution\"].value"]);
        Assert.Equal("3", r["graphics.quality"]);
        Assert.Equal("false", r["graphics.hdr"]);
        Assert.Equal("20", r["items[1].value"]);
        Assert.Equal("1", r["['a.b'].c"]);
        Assert.Equal("2", r["version"]);
        Assert.NotNull(r["graphics"]); // object → its JSON text
        Assert.Contains("\"preset\"", r["graphics"]);
        Assert.Null(r["graphics.missing"]);
        Assert.Null(r[$"{Presets}[name=Nope].value"]);
        Assert.Null(r["items[9].value"]);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNulls()
    {
        Assert.Null(_h.Read(_def, _tmp.File("none.json"), ["a.b"])["a.b"]);
    }

    [Fact]
    public void Write_FilterPaths_PreserveTypes()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        _h.Write(_def, path, V(
            ($"{Presets}[name=DLSS].value", "DLSS_Performance"),
            ($"{Presets}[name=MaxFPS].value", "120"),
            ($"{Presets}[name=Sharpness].value", "0.25"),
            ($"{Presets}[name=VSync].value", "False"),
            ($"{Presets}[name=FSR2].value", "1")));

        var root = Parse(path);
        var dlss = Option(root, "/graphics/presets", "DLSS");
        Assert.Equal(JsonValueKind.String, dlss.GetValueKind());
        Assert.Equal("DLSS_Performance", (string)dlss!);

        var fps = Option(root, "/graphics/presets", "MaxFPS");
        Assert.Equal(JsonValueKind.Number, fps.GetValueKind());
        Assert.Equal(120, (long)fps);

        var sharp = Option(root, "/graphics/presets", "Sharpness");
        Assert.Equal(JsonValueKind.Number, sharp.GetValueKind());
        Assert.Equal(0.25, (double)sharp);

        var vsync = Option(root, "/graphics/presets", "VSync");
        Assert.Equal(JsonValueKind.False, vsync.GetValueKind());

        // string stays string even when the new value looks numeric
        var fsr = Option(root, "/graphics/presets", "FSR2");
        Assert.Equal(JsonValueKind.String, fsr.GetValueKind());
        Assert.Equal("1", (string)fsr!);

        // untouched siblings survive
        Assert.Equal("Off", (string)root["data"]![0]!["options"]![0]!["default_value"]!);
        Assert.Equal("Fullscreen", (string)Option(root, "/video/display", "WindowMode")!);
    }

    [Fact]
    public void Write_RoundTripsThroughRead()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        var values = V(
            ($"{Presets}[name=MaxFPS].value", "30"),
            ("graphics.preset", "Low"),
            ("graphics.hdr", "true"),
            ("items[0].value", "11"),
            ("['a.b'].c", "7"));
        _h.Write(_def, path, values);
        var read = _h.Read(_def, path, values.Keys);
        foreach (var (k, v) in values) Assert.Equal(v, read[k]);
    }

    [Fact]
    public void Write_PlainAndIndexAndQuotedPaths()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        _h.Write(_def, path, V(("graphics.quality", "1"), ("items[2].value", "33"), ("['a.b'].c", "2"), ("version", "3")));
        var root = Parse(path);
        Assert.Equal(1, (long)root["graphics"]!["quality"]!);
        Assert.Equal(33, (long)root["items"]![2]!["value"]!);
        Assert.Equal(2, (long)root["a.b"]!["c"]!);
        Assert.Equal(3, (long)root["version"]!);
        Assert.Null(root["a"]); // quoted name must not split on '.'
    }

    [Fact]
    public void Write_ReplaceArrayElementByIndex()
    {
        var path = _tmp.Write("arr.json", "{ \"list\": [1, 2, 3] }");
        _h.Write(_def, path, V(("list[1]", "5")));
        Assert.Equal(5, (long)Parse(path)["list"]![1]!);
    }

    [Fact]
    public void Write_CommentsAndTrailingCommas_AreTolerated()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        Assert.Contains("//", File.ReadAllText(path));
        _h.Write(_def, path, V(("graphics.quality", "2")));
        // output is strict JSON again
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(2, (long)root["graphics"]!["quality"]!);
    }

    [Fact]
    public void Write_CreatesMissingObjectProperties()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        _h.Write(_def, path, V(("graphics.newKey", "42"), ("brandNew.nested.flag", "true"), ("brandNew.nested.name", "x")));
        var root = Parse(path);
        Assert.Equal(JsonValueKind.Number, root["graphics"]!["newKey"]!.GetValueKind());
        Assert.Equal(42, (long)root["graphics"]!["newKey"]!);
        Assert.Equal(JsonValueKind.True, root["brandNew"]!["nested"]!["flag"]!.GetValueKind());
        Assert.Equal("x", (string)root["brandNew"]!["nested"]!["name"]!);
        Assert.Equal("High", (string)root["graphics"]!["preset"]!);
    }

    [Fact]
    public void Write_MissingFile_CreatesIndentedObject()
    {
        var path = _tmp.File("new", "settings.json");
        _h.Write(_def, path, V(("a.b", "1.5"), ("a.s", "text")));
        var text = File.ReadAllText(path);
        Assert.Contains("\n", text);
        var root = JsonNode.Parse(text)!;
        Assert.Equal(1.5, (double)root["a"]!["b"]!);
        Assert.Equal("text", (string)root["a"]!["s"]!);
    }

    [Fact]
    public void Write_FilterThatMatchesNothing_Throws()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => _h.Write(_def, path, V(($"{Presets}[name=Nope].value", "1"))));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_IndexOutOfRange_Throws()
    {
        var path = _tmp.Write("arr.json", "{ \"list\": [1] }");
        Assert.Throws<InvalidOperationException>(() => _h.Write(_def, path, V(("list[4]", "5"))));
    }

    [Fact]
    public void Write_CompactFile_StaysCompact()
    {
        var path = _tmp.Write("c.json", "{\"a\":1,\"b\":\"x\"}");
        _h.Write(_def, path, V(("a", "2")));
        Assert.Equal("{\"a\":2,\"b\":\"x\"}", File.ReadAllText(path));
    }

    [Fact]
    public void Write_Utf8Bom_IsPreserved()
    {
        var path = _tmp.Write("b.json", "{\n  \"a\": 1\n}", new UTF8Encoding(true));
        _h.Write(_def, path, V(("a", "2")));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(2, (long)JsonNode.Parse(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3))!["a"]!);
    }

    [Fact]
    public void Write_NoBom_StaysWithoutBom()
    {
        var path = _tmp.Write("nb.json", "{\n  \"a\": 1\n}");
        _h.Write(_def, path, V(("a", "2")));
        Assert.Equal((byte)'{', File.ReadAllBytes(path)[0]);
    }

    [Fact]
    public void Write_FilterOnNumericField_UsesLooseEquality()
    {
        var path = _tmp.Write("n.json", "{ \"opts\": [ { \"id\": 1, \"v\": \"a\" }, { \"id\": 2, \"v\": \"b\" } ] }");
        _h.Write(_def, path, V(("opts[id=2.0].v", "z")));
        Assert.Equal("z", (string)Parse(path)["opts"]![1]!["v"]!);
    }

    [Theory]
    [InlineData("a.b.c", 3)]
    [InlineData("data[group_name=/graphics/presets].options[name=DLSS].value", 5)]
    [InlineData("['a.b'].c", 2)]
    [InlineData("[\"x]y\"].z", 2)]
    [InlineData("items[2].value", 3)]
    public void ParsePath_SegmentCounts(string path, int count)
    {
        Assert.Equal(count, JsonHandler.Parse(path).Count);
    }

    [Fact]
    public void ParsePath_SegmentKinds()
    {
        var segs = JsonHandler.Parse("data[group_name=/graphics/presets].options[3]['a.b']");
        Assert.Equal(new JsonHandler.PropSegment("data"), segs[0]);
        Assert.Equal(new JsonHandler.FilterSegment("group_name", "/graphics/presets"), segs[1]);
        Assert.Equal(new JsonHandler.PropSegment("options"), segs[2]);
        Assert.Equal(new JsonHandler.IndexSegment(3), segs[3]);
        Assert.Equal(new JsonHandler.PropSegment("a.b"), segs[4]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a[")]
    [InlineData("a[bad]")]
    public void ParsePath_Invalid_Throws(string path)
    {
        Assert.Throws<ConfigException>(() => JsonHandler.Parse(path));
    }

    [Fact]
    public void Flatten_JoinsNestedMapsWithDots()
    {
        var flat = _h.Flatten(new Dictionary<string, object?>
        {
            ["graphics"] = new Dictionary<string, object?> { ["quality"] = 3, ["hdr"] = true },
            [$"{Presets}[name=DLSS].value"] = "DLSS_Quality",
        });
        Assert.Equal("3", flat["graphics.quality"]);
        Assert.Equal("true", flat["graphics.hdr"]);
        Assert.Equal("DLSS_Quality", flat[$"{Presets}[name=DLSS].value"]);
    }
}
