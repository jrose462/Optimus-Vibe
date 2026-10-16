using System.Text;
using GameProfiles.Config;
using GameProfiles.Handlers;

namespace GameProfiles.Tests;

public sealed class KeyValueTextHandlerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly KeyValueTextHandler _h = new();

    public void Dispose() => _tmp.Dispose();

    private static FileDef Def(string? format = null, string? template = null) =>
        new() { Id = "kv", Handler = "keyvalue", Format = format, Template = template };

    private static Dictionary<string, string> V(params (string Key, string Value)[] kv) =>
        kv.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Cvar_ReadAndWrite_ByteForByte()
    {
        var original = Fixtures.Text("Config.wtf");
        var path = _tmp.Write("Config.wtf", original);
        var def = Def("cvar");

        var r = _h.Read(def, path, ["gxMaxFPS", "GXRESOLUTION", "missing"]);
        Assert.Equal("60", r["gxMaxFPS"]);
        Assert.Equal("1920x1080", r["GXRESOLUTION"]);
        Assert.Null(r["missing"]);

        _h.Write(def, path, V(("gxMaxFPS", "144"), ("gxResolution", "2560x1440")));
        var expected = original.Replace("SET gxMaxFPS \"60\"", "SET gxMaxFPS \"144\"")
            .Replace("\"1920x1080\"", "\"2560x1440\"");
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path));
    }

    [Fact]
    public void Cvar_AppendsMissingKey()
    {
        var path = Fixtures.CopyTo(_tmp, "Config.wtf");
        _h.Write(Def("cvar"), path, V(("gxVSync", "0")));
        Assert.EndsWith("SET graphicsQuality \"7\"\r\nSET gxVSync \"0\"\r\n", File.ReadAllText(path));
        Assert.Equal("0", _h.Read(Def("cvar"), path, ["gxVSync"])["gxVSync"]);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void Quoted_DiabloStyle_ReadWritePreservesNewlines(string nl)
    {
        var original = Fixtures.Text("LocalPrefs.txt", nl);
        var path = _tmp.Write("LocalPrefs.txt", original, new UTF8Encoding(true));
        var def = Def("quoted");

        Assert.Equal("1920x1080", _h.Read(def, path, ["DisplayModeResolution"])["DisplayModeResolution"]);
        _h.Write(def, path, V(("DisplayModeResolution", "1280x800"), ("TextureQuality", "1"), ("NewKey", "abc")));

        var expected = original.Replace("\"1920x1080\"", "\"1280x800\"").Replace("TextureQuality \"2\"", "TextureQuality \"1\"")
                       + "NewKey \"abc\"" + nl;
        Assert.Equal(Fixtures.Encode(expected, new UTF8Encoding(true)), File.ReadAllBytes(path));
    }

    [Fact]
    public void Quoted_ToleratesExtraWhitespace()
    {
        var path = _tmp.Write("p.txt", "  Key\t  \"v\"  \n");
        Assert.Equal("v", _h.Read(Def("quoted"), path, ["Key"])["Key"]);
        _h.Write(Def("quoted"), path, V(("Key", "w")));
        Assert.Equal("  Key\t  \"w\"  \n", File.ReadAllText(path));
    }

    [Fact]
    public void Equals_IsDefaultFormat()
    {
        var path = _tmp.Write("e.cfg", "fps=60\n# comment\nvsync=1\n");
        Assert.Equal("{key}={value}", KeyValueTextHandler.TemplateFor(Def()));
        var def = Def();
        Assert.Equal("60", _h.Read(def, path, ["fps"])["fps"]);
        _h.Write(def, path, V(("fps", "30"), ("hdr", "0")));
        Assert.Equal("fps=30\n# comment\nvsync=1\nhdr=0\n", File.ReadAllText(path));
    }

    [Fact]
    public void EqualsSpaced_And_Colon()
    {
        var p1 = _tmp.Write("s.cfg", "fps = 60\n");
        _h.Write(Def("equals-spaced"), p1, V(("fps", "90"), ("new", "1")));
        Assert.Equal("fps = 90\nnew = 1\n", File.ReadAllText(p1));

        var p2 = _tmp.Write("c.yml", "fps: 60\n");
        _h.Write(Def("colon"), p2, V(("fps", "90")));
        Assert.Equal("fps: 90\n", File.ReadAllText(p2));
    }

    [Fact]
    public void CustomTemplate_OverridesFormat()
    {
        var path = _tmp.Write("t.cfg", "seta r_fps <- 60;\nseta r_mode <- 3;\n");
        var def = Def(format: "cvar", template: "seta {key} <- {value};");
        Assert.Equal("60", _h.Read(def, path, ["r_fps"])["r_fps"]);
        _h.Write(def, path, V(("r_fps", "120"), ("r_vsync", "0")));
        Assert.Equal("seta r_fps <- 120;\nseta r_mode <- 3;\nseta r_vsync <- 0;\n", File.ReadAllText(path));
    }

    [Fact]
    public void Write_MissingFile_CreatesIt()
    {
        var path = _tmp.File("new", "Config.wtf");
        _h.Write(Def("cvar"), path, V(("gxMaxFPS", "60")));
        Assert.Equal("SET gxMaxFPS \"60\"\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void UnknownFormat_Throws()
    {
        Assert.Throws<ConfigException>(() => KeyValueTextHandler.TemplateFor(Def("bogus")));
    }

    [Theory]
    [InlineData("{key} only")]
    [InlineData("only {value}")]
    public void TemplateWithoutPlaceholders_Throws(string template)
    {
        Assert.Throws<ConfigException>(() => KeyValueTextHandler.BuildRegex(template));
    }

    [Fact]
    public void Presets_AreCaseInsensitive()
    {
        Assert.Equal("SET {key} \"{value}\"", KeyValueTextHandler.TemplateFor(Def("CVAR")));
    }

    [Fact]
    public void Flatten_KeepsKeysVerbatim()
    {
        var flat = _h.Flatten(new Dictionary<string, object?> { ["gx.MaxFPS"] = 60, ["b"] = false });
        Assert.Equal("60", flat["gx.MaxFPS"]);
        Assert.Equal("false", flat["b"]);
    }
}

public sealed class RawFileHandlerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Write_WithValues_Throws()
    {
        var h = new RawFileHandler();
        var path = _tmp.Write("f.bin", "data");
        Assert.Throws<ConfigException>(() =>
            h.Write(new FileDef { Id = "f", Handler = "file" }, path, new Dictionary<string, string> { ["a"] = "1" }));
        Assert.Equal("data", File.ReadAllText(path));
    }

    [Fact]
    public void Write_Empty_IsNoOp_AndReadReturnsNulls()
    {
        var h = new RawFileHandler();
        var path = _tmp.Write("f.bin", "data");
        h.Write(new FileDef { Id = "f" }, path, new Dictionary<string, string>());
        Assert.Equal("data", File.ReadAllText(path));
        Assert.Null(h.Read(new FileDef(), path, ["x"])["x"]);
    }
}
