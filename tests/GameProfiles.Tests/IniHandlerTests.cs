using System.Text;
using GameProfiles.Config;
using GameProfiles.Handlers;

namespace GameProfiles.Tests;

public sealed class IniHandlerTests : IDisposable
{
    private const string Fixture = "GameUserSettings.ini";
    private const string Gus = "/Script/Engine.GameUserSettings";
    private readonly TempDir _tmp = new();
    private readonly IniHandler _h = new();
    private readonly FileDef _def = new() { Id = "user", Handler = "ini" };

    public void Dispose() => _tmp.Dispose();

    private static Dictionary<string, string> V(params (string Key, string Value)[] kv) =>
        kv.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Read_ReturnsValues_CaseInsensitive_AndNullForMissing()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        var r = _h.Read(_def, path, [
            "[ScalabilityGroups]sg.ShadowQuality",
            "[/script/engine.gameusersettings]resolutionsizex",
            "[]Version",
            "[Internationalization]Culture",
            "[ScalabilityGroups]Missing",
            "[NoSuchSection]sg.ShadowQuality",
        ]);
        Assert.Equal("3", r["[ScalabilityGroups]sg.ShadowQuality"]);
        Assert.Equal("2560", r["[/script/engine.gameusersettings]resolutionsizex"]);
        Assert.Equal("5", r["[]Version"]);
        Assert.Equal("en", r["[Internationalization]Culture"]);
        Assert.Null(r["[ScalabilityGroups]Missing"]);
        Assert.Null(r["[NoSuchSection]sg.ShadowQuality"]);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNulls()
    {
        var r = _h.Read(_def, _tmp.File("nope.ini"), ["[A]b"]);
        Assert.Null(r["[A]b"]);
    }

    [Fact]
    public void Read_IgnoresCommentedKeys()
    {
        var path = _tmp.Write("c.ini", "[S]\r\n;Key=commented\r\n#Key=hash\r\nKey=real\r\n");
        Assert.Equal("real", _h.Read(_def, path, ["[S]Key"])["[S]Key"]);
    }

    public static IEnumerable<object[]> Encodings()
    {
        foreach (var nl in new[] { "\r\n", "\n" })
        {
            yield return [nl, "utf8"];
            yield return [nl, "utf8bom"];
            yield return [nl, "utf16le"];
        }
    }

    private static Encoding Enc(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false),
        "utf8bom" => new UTF8Encoding(true),
        "utf16le" => new UnicodeEncoding(false, true),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Write_ChangesOnlyTargetValues_ByteForByte(string newLine, string encodingName)
    {
        var enc = Enc(encodingName);
        var original = Fixtures.Text(Fixture, newLine);
        var path = _tmp.Write(Fixture, original, enc);

        _h.Write(_def, path, V(
            ("[ScalabilityGroups]sg.ShadowQuality", "1"),
            ($"[{Gus}]ResolutionSizeX", "1920"),
            ($"[{Gus}]FrameRateLimit", "60.000000")));

        var expected = original
            .Replace("sg.ShadowQuality=3", "sg.ShadowQuality=1")
            .Replace("\nResolutionSizeX=2560", "\nResolutionSizeX=1920")
            .Replace("FrameRateLimit=0.000000", "FrameRateLimit=60.000000");
        Assert.Equal(Fixtures.Encode(expected, enc), File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_Utf16LeBom_PreservesBomBytes()
    {
        var enc = new UnicodeEncoding(false, true);
        var path = _tmp.Write(Fixture, Fixtures.Text(Fixture), enc);
        _h.Write(_def, path, V(("[ScalabilityGroups]sg.ShadowQuality", "0")));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        Assert.Equal("0", _h.Read(_def, path, ["[ScalabilityGroups]sg.ShadowQuality"])["[ScalabilityGroups]sg.ShadowQuality"]);
    }

    [Fact]
    public void Write_Utf16LeWithoutBom_IsDetectedAndPreserved()
    {
        var enc = new UnicodeEncoding(false, false);
        var original = Fixtures.Text(Fixture);
        var path = _tmp.Write(Fixture, original, enc);
        _h.Write(_def, path, V(("[ScalabilityGroups]sg.TextureQuality", "1")));
        Assert.Equal(enc.GetBytes(original.Replace("sg.TextureQuality=3", "sg.TextureQuality=1")), File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_PreservesSpacingAroundEquals()
    {
        var path = _tmp.Write("s.ini", "[S]\nKey = old\nOther=1\n");
        _h.Write(_def, path, V(("[S]Key", "new")));
        Assert.Equal("[S]\nKey = new\nOther=1\n", File.ReadAllText(path));
    }

    [Fact]
    public void Write_InsertsMissingKey_AtEndOfExistingSection_BeforeBlankLine()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture, "\n");
        _h.Write(_def, path, V(("[ScalabilityGroups]sg.GlobalIlluminationQuality", "2")));

        var text = File.ReadAllText(path);
        Assert.Contains("sg.ShadingQuality=3\nsg.GlobalIlluminationQuality=2\n\n[/Script/Engine.GameUserSettings]", text);
        Assert.Equal("2", _h.Read(_def, path, ["[ScalabilityGroups]sg.GlobalIlluminationQuality"]).Values.Single());
    }

    [Fact]
    public void Write_InsertsMissingKey_InLastSection()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture, "\n");
        _h.Write(_def, path, V(("[Internationalization]Language", "de")));
        Assert.EndsWith("[Internationalization]\nCulture=en\nLanguage=de\n", File.ReadAllText(path));
    }

    [Fact]
    public void Write_CreatesNewSection_AtEnd()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture, "\r\n");
        _h.Write(_def, path, V(("[/Script/Engine.RendererSettings]r.Streaming.PoolSize", "3000")));
        var text = File.ReadAllText(path);
        Assert.EndsWith("Culture=en\r\n\r\n[/Script/Engine.RendererSettings]\r\nr.Streaming.PoolSize=3000\r\n", text);
    }

    [Fact]
    public void Write_TwoNewKeysInSameNewSection_ShareOneHeader()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture, "\n");
        _h.Write(_def, path, V(("[New]A", "1"), ("[New]B", "2")));
        var text = File.ReadAllText(path);
        Assert.Single(text.Split('\n'), l => l == "[New]");
        Assert.EndsWith("[New]\nA=1\nB=2\n", text);
    }

    [Fact]
    public void Write_PreSectionKey_IsInsertedBeforeFirstSection()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture, "\n");
        _h.Write(_def, path, V(("[]Top", "yes")));
        Assert.StartsWith("; Unreal Engine user settings\nVersion=5\nTop=yes\n[ScalabilityGroups]\n", File.ReadAllText(path));
    }

    [Fact]
    public void Write_ExistingPreSectionKey_IsUpdated()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture, "\n");
        _h.Write(_def, path, V(("[]Version", "6")));
        Assert.StartsWith("; Unreal Engine user settings\nVersion=6\n[ScalabilityGroups]", File.ReadAllText(path));
    }

    [Fact]
    public void Write_PreservesComments()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture, "\r\n");
        _h.Write(_def, path, V(($"[{Gus}]AudioQualityLevel", "2"), ("[New]X", "1")));
        var text = File.ReadAllText(path);
        Assert.Contains("; Unreal Engine user settings\r\n", text);
        Assert.Contains("; resolution\r\n", text);
        Assert.Contains("# hash comment\r\nAudioQualityLevel=2\r\n", text);
    }

    [Fact]
    public void Write_DoesNotTouchCommentedOutKey_AddsRealKey()
    {
        var path = _tmp.Write("c.ini", "[S]\n;Key=commented\n");
        _h.Write(_def, path, V(("[S]Key", "1")));
        Assert.Equal("[S]\n;Key=commented\nKey=1\n", File.ReadAllText(path));
    }

    [Fact]
    public void Write_NoTrailingNewline_IsPreserved()
    {
        var path = _tmp.Write("n.ini", "[S]\r\nA=1");
        _h.Write(_def, path, V(("[S]A", "2")));
        Assert.Equal("[S]\r\nA=2", File.ReadAllText(path));
    }

    [Fact]
    public void Write_MissingFile_CreatesUtf8CrlfFile()
    {
        var path = _tmp.File("sub", "new.ini");
        _h.Write(_def, path, V(("[S]A", "1")));
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("[S]\r\nA=1\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Write_ReadOnlyFile_IsWritten()
    {
        var path = Fixtures.CopyTo(_tmp, Fixture);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        _h.Write(_def, path, V(("[ScalabilityGroups]sg.ShadowQuality", "0")));
        Assert.Equal("0", _h.Read(_def, path, ["[ScalabilityGroups]sg.ShadowQuality"]).Values.Single());
    }

    [Fact]
    public void Write_DuplicateKey_UpdatesFirstOccurrenceOnly()
    {
        var path = _tmp.Write("d.ini", "[S]\nA=1\nA=2\n");
        _h.Write(_def, path, V(("[S]A", "9")));
        Assert.Equal("[S]\nA=9\nA=2\n", File.ReadAllText(path));
    }

    [Fact]
    public void Flatten_NestsBySection_AndTopLevelScalarsArePreSection()
    {
        var block = new Dictionary<string, object?>
        {
            [Gus] = new Dictionary<string, object?> { ["ResolutionSizeX"] = 1920, ["bUseVSync"] = false },
            ["ScalabilityGroups"] = new Dictionary<string, object?> { ["sg.ShadowQuality"] = "1" },
            ["Version"] = "5",
        };
        var flat = _h.Flatten(block);
        Assert.Equal(4, flat.Count);
        Assert.Equal("1920", flat[$"[{Gus}]ResolutionSizeX"]);
        Assert.Equal("false", flat[$"[{Gus}]bUseVSync"]);
        Assert.Equal("1", flat["[ScalabilityGroups]sg.ShadowQuality"]);
        Assert.Equal("5", flat["[]Version"]);
    }

    [Fact]
    public void Unflatten_IsInverseOfFlatten()
    {
        var flat = new Dictionary<string, string>
        {
            [$"[{Gus}]ResolutionSizeX"] = "1920",
            [$"[{Gus}]ResolutionSizeY"] = "1080",
            ["[ScalabilityGroups]sg.ShadowQuality"] = "1",
            ["[]Version"] = "5",
        };
        var nested = _h.Unflatten(flat);
        Assert.Equal("5", nested["Version"]);
        var gus = Assert.IsType<Dictionary<string, object?>>(nested[Gus]);
        Assert.Equal("1920", gus["ResolutionSizeX"]);
        Assert.Equal("1080", gus["ResolutionSizeY"]);
        Assert.Equal(flat, _h.Flatten(nested));
    }

    [Theory]
    [InlineData("[Sec]Key", "Sec", "Key")]
    [InlineData("[]Key", "", "Key")]
    [InlineData("Key", "", "Key")]
    [InlineData("[/Script/Engine.GameUserSettings]sg.ShadowQuality", "/Script/Engine.GameUserSettings", "sg.ShadowQuality")]
    public void SplitKey_And_MakeKey(string flat, string section, string key)
    {
        Assert.Equal((section, key), IniHandler.SplitKey(flat));
        if (flat.StartsWith('[')) Assert.Equal(flat, IniHandler.MakeKey(section, key));
    }
}
