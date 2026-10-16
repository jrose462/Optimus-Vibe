using OptimusVibe.Config;
using OptimusVibe.Engine;

namespace OptimusVibe.Tests;

public sealed class SelectionStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private string FileIn => _tmp.File("nested", "dir", "selections.json");

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Get_NoFile_IsNull() => Assert.Null(new SelectionStore(FileIn).Get("desktop", "g"));

    [Fact]
    public void Set_Get_Clear()
    {
        var s = new SelectionStore(FileIn);
        s.Set("desktop", "g", PresetRef.Manual("steady-40"));
        Assert.True(File.Exists(FileIn)); // directory created
        Assert.Equal(PresetRef.Manual("steady-40"), s.Get("desktop", "g"));
        Assert.Equal(PresetRef.Manual("steady-40"), new SelectionStore(FileIn).Get("desktop", "g")); // persisted

        s.Set("desktop", "g", PresetRef.Recommended("battery"));
        Assert.Equal(PresetRef.Recommended("battery"), s.Get("desktop", "g"));

        s.Set("desktop", "g", null);
        Assert.Null(s.Get("desktop", "g"));
        s.Set("desktop", "g", null); // clearing again is fine
    }

    [Fact]
    public void Entries_AreIsolatedPerDeviceAndGame()
    {
        var s = new SelectionStore(FileIn);
        s.Set("desktop", "g", PresetRef.Recommended("quality"));
        s.Set("ally-x", "g", PresetRef.Recommended("battery"));
        s.Set("desktop", "h", PresetRef.Manual("x"));

        Assert.Equal(PresetRef.Recommended("quality"), s.Get("desktop", "g"));
        Assert.Equal(PresetRef.Recommended("battery"), s.Get("ally-x", "g"));
        Assert.Equal(PresetRef.Manual("x"), s.Get("desktop", "h"));
        Assert.Null(s.Get("ally-x", "h"));

        s.Set("desktop", "g", null);
        Assert.Null(s.Get("desktop", "g"));
        Assert.Equal(PresetRef.Recommended("battery"), s.Get("ally-x", "g"));
        Assert.Equal(PresetRef.Manual("x"), s.Get("desktop", "h"));
    }

    [Fact]
    public void StoredAsCanonicalStrings()
    {
        var s = new SelectionStore(FileIn);
        s.Set("desktop", "g", PresetRef.Manual("a"));
        Assert.Contains("\"desktop/g\": \"manual:a\"", File.ReadAllText(FileIn));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"desktop/g\": 5 }")]
    [InlineData("")]
    [InlineData("null")]
    public void CorruptFile_IsTolerated_AndOverwrittenOnSet(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileIn)!);
        File.WriteAllText(FileIn, content);
        var s = new SelectionStore(FileIn);
        Assert.Null(s.Get("desktop", "g"));
        s.Set("desktop", "g", PresetRef.Recommended("quality"));
        Assert.Equal(PresetRef.Recommended("quality"), s.Get("desktop", "g"));
    }

    [Fact]
    public void HandEditedShortForms_Parse()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileIn)!);
        File.WriteAllText(FileIn, """{ "desktop/g": "m:steady", "desktop/h": "battery" }""");
        var s = new SelectionStore(FileIn);
        Assert.Equal(PresetRef.Manual("steady"), s.Get("desktop", "g"));
        Assert.Equal(PresetRef.Recommended("battery"), s.Get("desktop", "h"));
    }
}
