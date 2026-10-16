using GameProfiles.Config;

namespace GameProfiles.Tests;

public sealed class ProfileResolverTests
{
    private static Dictionary<string, object?> M(params (string Key, object? Value)[] kv)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    private static GameDef Game(params (string Name, Dictionary<string, object?> Body)[] profiles) => new()
    {
        Id = "testgame",
        Files =
        [
            new FileDef { Id = "user", Handler = "ini" },
            new FileDef { Id = "Engine", Handler = "ini" },
        ],
        Profiles = profiles.ToDictionary(p => p.Name, p => p.Body),
    };

    private static readonly DeviceDef Desktop = new()
    {
        Display = new DisplayModeDef { Width = 2560, Height = 1440, Refresh = 165 },
    };

    private static GameDef Standard() => Game(
        ("base", M(("user", M(("Section", M(("Shadows", "3"), ("Textures", "3"), ("Fps", "0"))))))),
        ("handheld", M(("extends", "base"), ("user", M(("Section", M(("Shadows", "1"), ("Fps", "60"))))))),
        ("ally-x", M(("extends", "handheld"),
            ("display", M(("refresh", "120"))),
            ("amd", M(("Chill", M(("min", "55"), ("max", "60"))), ("AFMF", false))),
            ("user", M(("Section", M(("Textures", "2"))))))),
        ("desktop", M(("extends", "base"), ("display", "inherit"), ("nvidia", M(("FrameRateLimiter", 165)))))
    );

    private static Dictionary<string, object?> Section(ResolvedProfile p) =>
        (Dictionary<string, object?>)p.FileBlocks["user"]["Section"]!;

    [Fact]
    public void Flatten_MergesExtendsChain_ChildWins()
    {
        var flat = ProfileResolver.Flatten(Standard(), "ally-x")!;
        Assert.False(flat.ContainsKey("extends"));
        var section = (Dictionary<string, object?>)((Dictionary<string, object?>)flat["user"]!)["Section"]!;
        Assert.Equal("1", section["Shadows"]);   // handheld
        Assert.Equal("2", section["Textures"]);  // ally-x
        Assert.Equal("60", section["Fps"]);      // handheld
    }

    [Fact]
    public void Flatten_DoesNotMutateGameDefinition()
    {
        var game = Standard();
        ProfileResolver.Flatten(game, "ally-x");
        ProfileResolver.Resolve(game, "ally-x", null, M(("user", M(("Section", M(("Shadows", "0")))))));
        var baseSection = (Dictionary<string, object?>)((Dictionary<string, object?>)game.Profiles["base"]["user"]!)["Section"]!;
        Assert.Equal("3", baseSection["Shadows"]);
        Assert.Equal("3", baseSection["Textures"]);
    }

    [Fact]
    public void Flatten_UnknownProfile_ReturnsNull() => Assert.Null(ProfileResolver.Flatten(Standard(), "nope"));

    [Fact]
    public void Flatten_Cycle_Throws()
    {
        var game = Game(("a", M(("extends", "b"))), ("b", M(("extends", "c"))), ("c", M(("extends", "a"))));
        var ex = Assert.Throws<ConfigException>(() => ProfileResolver.Flatten(game, "a"));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public void Flatten_SelfExtends_Throws()
    {
        var game = Game(("a", M(("extends", "a"))));
        Assert.Throws<ConfigException>(() => ProfileResolver.Flatten(game, "a"));
    }

    [Fact]
    public void Flatten_UnknownExtends_Throws()
    {
        var game = Game(("a", M(("extends", "ghost"))));
        var ex = Assert.Throws<ConfigException>(() => ProfileResolver.Flatten(game, "a"));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void Resolve_CaptureOverlayWins_AndNullRemovesKey()
    {
        var capture = M(("user", M(("Section", M(("Shadows", "0"), ("Fps", null), ("NewKey", "x"))))));
        var p = ProfileResolver.Resolve(Standard(), "ally-x", null, capture);
        Assert.True(p.HasProfile);
        Assert.True(p.HasCapture);
        var s = Section(p);
        Assert.Equal("0", s["Shadows"]);
        Assert.Equal("2", s["Textures"]);
        Assert.Equal("x", s["NewKey"]);
        Assert.False(s.ContainsKey("Fps"));
    }

    [Fact]
    public void Resolve_NullInChildProfile_RemovesInheritedKey()
    {
        var game = Game(
            ("base", M(("user", M(("S", M(("A", "1"), ("B", "2"))))))),
            ("dev", M(("extends", "base"), ("user", M(("S", M(("B", null))))))));
        var p = ProfileResolver.Resolve(game, "dev", null, new());
        var s = (Dictionary<string, object?>)p.FileBlocks["user"]["S"]!;
        Assert.Equal("1", s["A"]);
        Assert.False(s.ContainsKey("B"));
    }

    [Fact]
    public void Resolve_NoProfile_UsesCaptureOnly()
    {
        var p = ProfileResolver.Resolve(Standard(), "laptop", null, M(("user", M(("S", M(("A", "1")))))));
        Assert.False(p.HasProfile);
        Assert.True(p.HasCapture);
        Assert.Null(p.Display);
        Assert.Equal("1", ((Dictionary<string, object?>)p.FileBlocks["user"]["S"]!)["A"]);
    }

    [Fact]
    public void Resolve_NoProfileNoCapture_IsEmpty()
    {
        var p = ProfileResolver.Resolve(Standard(), "laptop", null, new());
        Assert.False(p.HasProfile);
        Assert.False(p.HasCapture);
        Assert.Empty(p.FileBlocks);
        Assert.Empty(p.Nvidia);
        Assert.Empty(p.Amd);
    }

    [Theory]
    [InlineData("inherit")]
    [InlineData("Device")]
    public void Resolve_DisplayInherit_UsesDeviceDefault(string value)
    {
        var game = Game(("desktop", M(("display", value))));
        var p = ProfileResolver.Resolve(game, "desktop", Desktop, new());
        Assert.Same(Desktop.Display, p.Display);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("keep")]
    public void Resolve_DisplayNone_IsNull(string value)
    {
        var game = Game(("desktop", M(("display", value))));
        Assert.Null(ProfileResolver.Resolve(game, "desktop", Desktop, new()).Display);
    }

    [Fact]
    public void Resolve_DisplayOmitted_IsNull()
    {
        var game = Game(("desktop", M(("user", M(("S", M(("A", "1"))))))));
        Assert.Null(ProfileResolver.Resolve(game, "desktop", Desktop, new()).Display);
    }

    [Fact]
    public void Resolve_DisplayMap_MergedWithDeviceDefault()
    {
        var p = ProfileResolver.Resolve(Standard(), "ally-x", Desktop, new());
        Assert.Equal(2560, p.Display!.Width);
        Assert.Equal(1440, p.Display.Height);
        Assert.Equal(120, p.Display.Refresh);
    }

    [Fact]
    public void Resolve_DisplayMap_WithoutDeviceDefault_LeavesOthersNull()
    {
        var game = Game(("d", M(("display", M(("width", 1280), ("height", "800"))))));
        var p = ProfileResolver.Resolve(game, "d", new DeviceDef(), new());
        Assert.Equal(1280, p.Display!.Width);
        Assert.Equal(800, p.Display.Height);
        Assert.Null(p.Display.Refresh);
    }

    [Fact]
    public void Resolve_DisplayInvalid_Throws()
    {
        var game = Game(("d", M(("display", "fullscreen"))));
        Assert.Throws<ConfigException>(() => ProfileResolver.Resolve(game, "d", Desktop, new()));
    }

    [Fact]
    public void Resolve_DisplayCaptureOverridesProfile()
    {
        var p = ProfileResolver.Resolve(Standard(), "desktop", Desktop, M(("display", "none")));
        Assert.Null(p.Display);
    }

    [Fact]
    public void Resolve_VendorBlocks()
    {
        var ally = ProfileResolver.Resolve(Standard(), "ally-x", null, new());
        Assert.Equal("min=55,max=60", ally.Amd["Chill"]);
        Assert.Equal("false", ally.Amd["afmf"]); // case-insensitive lookup
        Assert.Empty(ally.Nvidia);

        var desk = ProfileResolver.Resolve(Standard(), "desktop", Desktop, new());
        Assert.Equal("165", desk.Nvidia["FrameRateLimiter"]);
        Assert.Empty(desk.Amd);
        Assert.False(desk.FileBlocks.ContainsKey("nvidia"));
    }

    [Fact]
    public void Resolve_UnknownFileId_Throws()
    {
        var game = Game(("d", M(("graphics", M(("A", "1"))))));
        var ex = Assert.Throws<ConfigException>(() => ProfileResolver.Resolve(game, "d", null, new()));
        Assert.Contains("graphics", ex.Message);
    }

    [Fact]
    public void Resolve_UnknownFileIdInCapture_Throws()
    {
        Assert.Throws<ConfigException>(() =>
            ProfileResolver.Resolve(Standard(), "ally-x", null, M(("bogus", M(("A", "1"))))));
    }

    [Fact]
    public void Resolve_FileIdMatchedCaseInsensitively_UsesCanonicalId()
    {
        var game = Game(("d", M(("engine", M(("S", M(("A", "1"))))))));
        var p = ProfileResolver.Resolve(game, "d", null, new());
        Assert.True(p.FileBlocks.ContainsKey("Engine"));
    }

    [Fact]
    public void DeepMerge_ReplacesScalarWithMap_AndMapWithScalar()
    {
        var target = M(("a", "1"), ("b", M(("x", "1"))));
        ProfileResolver.DeepMerge(target, M(("a", M(("y", "2"))), ("b", "flat")));
        Assert.Equal("2", ((Dictionary<string, object?>)target["a"]!)["y"]);
        Assert.Equal("flat", target["b"]);
    }

    [Fact]
    public void DeepMerge_CopiesOverlayMaps()
    {
        var inner = M(("x", "1"));
        var target = new Dictionary<string, object?>();
        ProfileResolver.DeepMerge(target, M(("a", inner)));
        Assert.NotSame(inner, target["a"]);
        ((Dictionary<string, object?>)target["a"]!)["x"] = "changed";
        Assert.Equal("1", inner["x"]);
    }
}
