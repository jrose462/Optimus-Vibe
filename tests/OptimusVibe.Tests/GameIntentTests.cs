using OptimusVibe.Config;

namespace OptimusVibe.Tests;

public sealed class GameIntentTests
{
    // LF whatever the checkout's line endings (CI checks out CRLF).
    private static readonly string Game = """
        id: g
        name: G
        # Claude's notes stay put
        intent:
          desktop: "EDIT ME (placeholder): e.g. max visuals"
          ally-x: "EDIT ME (placeholder): e.g. steady 60 fps"

        profiles:
          base: {}

        """.ReplaceLineEndings("\n");

    [Fact]
    public void ReplacesTheDeviceLine_KeepsEverythingElse()
    {
        var text = GameIntent.Set(Game, "ally-x", "Steady 60, quiet fans");
        Assert.Equal(Game.Replace("\"EDIT ME (placeholder): e.g. steady 60 fps\"", "\"Steady 60, quiet fans\""), text);
        Assert.Equal("Steady 60, quiet fans", Yaml.Parse<GameDef>(text).Intent["ally-x"]);
    }

    [Fact]
    public void AddsAMissingDevice_InTheBlock()
    {
        var text = GameIntent.Set(Game, "steam-deck", "Battery first");
        var game = Yaml.Parse<GameDef>(text);
        Assert.Equal("Battery first", game.Intent["steam-deck"]);
        Assert.Equal(3, game.Intent.Count);
        Assert.Contains("  ally-x: \"EDIT ME (placeholder): e.g. steady 60 fps\"\n  steam-deck: \"Battery first\"\n", text);
    }

    [Fact]
    public void AddsTheBlock_WhenMissing_OrEmptyInline()
    {
        foreach (var src in new[] { "id: g\nname: G\n", "id: g\nname: G\nintent: {}\nprofiles: {}\n" })
        {
            var game = Yaml.Parse<GameDef>(GameIntent.Set(src, "desktop", "Max visuals"));
            Assert.Equal("Max visuals", game.Intent["desktop"]);
        }
    }

    [Fact]
    public void ReplacesAMultiLineValue()
    {
        const string src = "id: g\nintent:\n  desktop: >-\n    long folded\n    text here\n  ally-x: \"x\"\nprofiles: {}\n";
        var text = GameIntent.Set(src, "desktop", "Short");
        Assert.Equal("id: g\nintent:\n  desktop: \"Short\"\n  ally-x: \"x\"\nprofiles: {}\n", text);
    }

    [Fact]
    public void QuotesAndFlattensInput_KeepsCrLf()
    {
        var text = GameIntent.Set(Game.Replace("\n", "\r\n"), "desktop", "  4K \"HDR\" \\ 120\nfps  ");
        Assert.Contains("  desktop: \"4K \\\"HDR\\\" \\\\ 120 fps\"\r\n", text);
        Assert.Equal("4K \"HDR\" \\ 120 fps", Yaml.Parse<GameDef>(text).Intent["desktop"]);
        Assert.DoesNotContain("\r\r", text);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void RefusesEmpty(string intent) => Assert.Throws<ArgumentException>(() => GameIntent.Set(Game, "desktop", intent));

    [Fact]
    public void RefusesBadDeviceNames() => Assert.Throws<ArgumentException>(() => GameIntent.Set(Game, "a: b", "x"));

    [Fact]
    public void CapsTheLength() => Assert.Equal(GameIntent.MaxLength, GameIntent.Clean(new string('a', 500)).Length);

    [Fact]
    public void SetInFile_WritesAndChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ov-intent-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, Game);
            Assert.Equal("Quiet on battery", GameIntent.SetInFile(path, "ally-x", " Quiet on battery "));
            Assert.Equal("Quiet on battery", Yaml.Load<GameDef>(path).Intent["ally-x"]);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }
}
