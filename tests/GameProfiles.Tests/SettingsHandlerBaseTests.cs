using System.Text;
using GameProfiles.Config;
using GameProfiles.Handlers;

namespace GameProfiles.Tests;

public sealed class SettingsHandlerBaseTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Snapshot_ReturnsBytes_OrNullWhenMissing()
    {
        var h = new IniHandler();
        var path = _tmp.Write("a.ini", "[S]\nA=1\n", new UTF8Encoding(true));
        Assert.Equal(File.ReadAllBytes(path), h.Snapshot(path));
        Assert.Null(h.Snapshot(_tmp.File("missing.ini")));
    }

    [Fact]
    public void RestoreSnapshot_RestoresExactBytes_EvenWhenReadOnly()
    {
        var h = new JsonHandler();
        var path = _tmp.Write("a.json", "{ \"a\": 1 }");
        var snap = h.Snapshot(path)!;
        h.Write(new FileDef(), path, new Dictionary<string, string> { ["a"] = "2" });
        h.SetReadOnly(path, true);

        h.RestoreSnapshot(path, snap);
        Assert.Equal(snap, File.ReadAllBytes(path));
    }

    [Fact]
    public void RestoreSnapshot_Null_DeletesFile_EvenWhenReadOnly()
    {
        var h = new KeyValueTextHandler();
        var path = _tmp.Write("a.txt", "x=1\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        h.RestoreSnapshot(path, null);
        Assert.False(File.Exists(path));
        h.RestoreSnapshot(path, null); // missing file: no-op
    }

    [Fact]
    public void RestoreSnapshot_CreatesMissingDirectories()
    {
        var h = new RawFileHandler();
        var path = _tmp.File("deep", "er", "file.bin");
        byte[] data = [0, 1, 2, 255];
        h.RestoreSnapshot(path, data);
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void SetReadOnly_TogglesAttribute_AndIgnoresMissingFile()
    {
        var h = new IniHandler();
        var path = _tmp.Write("ro.ini", "[S]\n");
        h.SetReadOnly(path, true);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        h.SetReadOnly(path, false);
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        h.SetReadOnly(_tmp.File("none.ini"), true);
    }

    [Fact]
    public void Exists_ChecksFile()
    {
        var h = new XmlHandler();
        Assert.False(h.Exists(_tmp.File("x.xml")));
        Assert.True(h.Exists(_tmp.Write("x.xml", "<a/>")));
    }

    [Fact]
    public void BaseFlatten_JoinsNestedKeys_AndFormatsScalars()
    {
        SettingsHandler h = new JsonHandler(); // uses the base implementation
        var flat = h.Flatten(new Dictionary<string, object?>
        {
            ["a"] = new Dictionary<string, object?>
            {
                ["b"] = new Dictionary<string, object?> { ["c"] = 1.5 },
                ["flag"] = true,
                ["none"] = null,
            },
            ["top"] = 7,
        });
        Assert.Equal(4, flat.Count);
        Assert.Equal("1.5", flat["a.b.c"]);
        Assert.Equal("true", flat["a.flag"]);
        Assert.Equal("", flat["a.none"]);
        Assert.Equal("7", flat["top"]);
    }

    [Fact]
    public void BaseUnflatten_KeepsFlatKeys()
    {
        SettingsHandler h = new JsonHandler();
        var u = h.Unflatten(new Dictionary<string, string> { ["a.b"] = "1", ["c"] = "x" });
        Assert.Equal("1", u["a.b"]);
        Assert.Equal("x", u["c"]);
        Assert.Equal(new Dictionary<string, string> { ["a.b"] = "1", ["c"] = "x" }, h.Flatten(u));
    }

    [Theory]
    [InlineData("ini", typeof(IniHandler))]
    [InlineData("JSON", typeof(JsonHandler))]
    [InlineData("xml", typeof(XmlHandler))]
    [InlineData("registry", typeof(RegistryHandler))]
    [InlineData("keyvalue", typeof(KeyValueTextHandler))]
    [InlineData("file", typeof(RawFileHandler))]
    public void For_ReturnsHandler(string name, Type type)
    {
        var h = SettingsHandler.For(name);
        Assert.IsType(type, h);
        Assert.Equal(name.ToLowerInvariant(), h.Name);
        Assert.Contains(h.Name, SettingsHandler.Names);
    }

    [Fact]
    public void For_Unknown_Throws() => Assert.Throws<ConfigException>(() => SettingsHandler.For("yaml"));

    [Fact]
    public void IsFile_FalseOnlyForRegistry()
    {
        Assert.All(SettingsHandler.Names, n => Assert.Equal(n != "registry", SettingsHandler.For(n).IsFile));
    }
}

public sealed class TextFileTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public void LoadSave_RoundTripsBytes(string encName)
    {
        Encoding enc = encName switch
        {
            "utf8" => new UTF8Encoding(false),
            "utf8bom" => new UTF8Encoding(true),
            "utf16le" => new UnicodeEncoding(false, true),
            _ => new UnicodeEncoding(true, true),
        };
        var text = "[S]\r\nName=ÃœnÃ¯cÃ¸dÃ© âœ“\r\nB=2\r\n";
        var path = _tmp.Write("t.txt", text, enc);
        var before = File.ReadAllBytes(path);
        var f = TextFile.Load(path);
        Assert.Equal(["[S]", "Name=ÃœnÃ¯cÃ¸dÃ© âœ“", "B=2"], f.Lines);
        Assert.Equal("\r\n", f.NewLine);
        Assert.True(f.TrailingNewLine);
        f.Save(path);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Load_DetectsLfAndNoTrailingNewline()
    {
        var f = TextFile.Load(_tmp.Write("t.txt", "a\nb"));
        Assert.Equal("\n", f.NewLine);
        Assert.False(f.TrailingNewLine);
        Assert.Equal(["a", "b"], f.Lines);
    }

    [Fact]
    public void Load_MissingFile_DefaultsToUtf8CrlfEmpty()
    {
        var f = TextFile.Load(_tmp.File("none.txt"));
        Assert.Empty(f.Lines);
        Assert.Equal("\r\n", f.NewLine);
        Assert.Empty(f.Encoding.GetPreamble());
    }

    [Fact]
    public void EmptyExistingFile_WriteKey_HasNoLeadingBlankLine()
    {
        var path = _tmp.Write("empty.cfg", "");
        new KeyValueTextHandler().Write(new FileDef(), path, new Dictionary<string, string> { ["a"] = "1" });
        Assert.Equal("a=1", File.ReadAllText(path).Trim('\r', '\n'));
        Assert.False(File.ReadAllText(path).StartsWith('\n') || File.ReadAllText(path).StartsWith('\r'));
    }
}
