using OptimusVibe.Config;

namespace OptimusVibe.Tests;

public sealed class ProfileResolverTests
{
    internal static Dictionary<string, object?> M(params (string Key, object? Value)[] kv)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    private static GameDef Game(
        (string Name, Dictionary<string, object?> Body)[] profiles,
        params (string Device, string Preset, Dictionary<string, object?> Body)[] presets)
    {
        var game = new GameDef
        {
            Id = "testgame",
            Files =
            [
                new FileDef { Id = "user", Handler = "ini" },
                new FileDef { Id = "Engine", Handler = "ini" },
            ],
            Profiles = profiles.ToDictionary(p => p.Name, p => p.Body),
        };
        foreach (var (device, preset, body) in presets)
        {
            if (!game.Presets.TryGetValue(device, out var map)) game.Presets[device] = map = new();
            map[preset] = body;
        }
        return game;
    }

    private static readonly DeviceDef Desktop = new()
    {
        Display = new DisplayModeDef { Width = 2560, Height = 1440, Refresh = 165 },
    };

    /// <summary>Shared bases under profiles:, device presets extend them.</summary>
    private static GameDef Standard() => Game(
        [
            ("base", M(("user", M(("Section", M(("Shadows", "3"), ("Textures", "3"), ("Fps", "0"))))))),
            ("handheld", M(("extends", "base"), ("user", M(("Section", M(("Shadows", "1"), ("Fps", "60"))))))),
        ],
        ("ally-x", "performance", M(("extends", "handheld"),
            ("display", M(("refresh", "120"))),
            ("amd", M(("Chill", M(("min", "55"), ("max", "60"))), ("AFMF", false))),
            ("user", M(("Section", M(("Textures", "2"))))))),
        ("desktop", "quality", M(("extends", "base"), ("display", "inherit"), ("nvidia", M(("FrameRateLimiter", 165))))));

    /// <summary>Resolve a raw body for device "d".</summary>
    private static ResolvedProfile ResolveBody(Dictionary<string, object?> body, DeviceDef? device = null, GameDef? game = null) =>
        ProfileResolver.Resolve(game ?? Game([]), "d", device, body, PresetRef.Recommended("x"));

    private static Dictionary<string, object?> Section(ResolvedProfile p) =>
        (Dictionary<string, object?>)p.FileBlocks["user"]["Section"]!;

    // ------------------------------------------------------------------ flatten / extends

    [Fact]
    public void FlattenPreset_MergesExtendsChainIntoProfiles_ChildWins()
    {
        var flat = ProfileResolver.FlattenPreset(Standard(), "ally-x", "performance");
        Assert.False(flat.ContainsKey("extends"));
        var section = (Dictionary<string, object?>)((Dictionary<string, object?>)flat["user"]!)["Section"]!;
        Assert.Equal("1", section["Shadows"]);   // handheld
        Assert.Equal("2", section["Textures"]);  // preset
        Assert.Equal("60", section["Fps"]);      // handheld
    }

    [Fact]
    public void FlattenPreset_DoesNotMutateGameDefinition()
    {
        var game = Standard();
        ProfileResolver.FlattenPreset(game, "ally-x", "performance");
        ProfileResolver.ResolvePreset(game, "ally-x", null, "performance");
        var baseSection = (Dictionary<string, object?>)((Dictionary<string, object?>)game.Profiles["base"]["user"]!)["Section"]!;
        Assert.Equal("3", baseSection["Shadows"]);
        Assert.Equal("3", baseSection["Textures"]);
        Assert.Equal("handheld", game.Presets["ally-x"]["performance"]["extends"]);
    }

    [Theory]
    [InlineData("ally-x", "nope")]
    [InlineData("laptop", "performance")]
    public void FlattenPreset_UnknownPresetOrDevice_Throws(string device, string preset)
    {
        var ex = Assert.Throws<ConfigException>(() => ProfileResolver.FlattenPreset(Standard(), device, preset));
        Assert.Contains(preset, ex.Message);
    }

    [Fact]
    public void FlattenBody_NoExtends_ReturnsCopy()
    {
        var body = M(("user", M(("S", M(("A", "1"))))));
        var flat = ProfileResolver.FlattenBody(Standard(), body, "x");
        Assert.NotSame(body, flat);
        Assert.Equal("1", ((Dictionary<string, object?>)((Dictionary<string, object?>)flat["user"]!)["S"]!)["A"]);
    }

    [Fact]
    public void FlattenBody_Cycle_Throws()
    {
        var game = Game([("a", M(("extends", "b"))), ("b", M(("extends", "c"))), ("c", M(("extends", "a")))]);
        var ex = Assert.Throws<ConfigException>(() => ProfileResolver.FlattenBody(game, M(("extends", "a")), "presets.d.x"));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public void FlattenBody_SelfExtendingProfile_Throws()
    {
        var game = Game([("a", M(("extends", "a")))]);
        var ex = Assert.Throws<ConfigException>(() => ProfileResolver.FlattenBody(game, M(("extends", "a")), "x"));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public void FlattenPreset_CycleThroughProfiles_Throws()
    {
        var game = Game([("a", M(("extends", "b"))), ("b", M(("extends", "a")))], ("d", "p", M(("extends", "a"))));
        Assert.Throws<ConfigException>(() => ProfileResolver.FlattenPreset(game, "d", "p"));
    }

    [Fact]
    public void FlattenBody_UnknownExtends_Throws()
    {
        var game = Game([("a", M(("extends", "ghost")))]);
        var ex = Assert.Throws<ConfigException>(() => ProfileResolver.FlattenBody(game, M(("extends", "a")), "x"));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void FlattenPreset_ExtendsAnotherPreset_IsUnknown()
    {
        // extends only reaches into profiles:, never into another preset.
        var game = Game([], ("d", "quality", M(("user", M(("S", M(("A", "1"))))))), ("d", "battery", M(("extends", "quality"))));
        var ex = Assert.Throws<ConfigException>(() => ProfileResolver.FlattenPreset(game, "d", "battery"));
        Assert.Contains("quality", ex.Message);
        Assert.Contains("profiles", ex.Message);
    }

    [Fact]
    public void Resolve_NullInChild_RemovesInheritedKey()
    {
        var game = Game(
            [("base", M(("user", M(("S", M(("A", "1"), ("B", "2")))))))],
            ("d", "p", M(("extends", "base"), ("user", M(("S", M(("B", null))))))));
        var p = ProfileResolver.ResolvePreset(game, "d", null, "p");
        var s = (Dictionary<string, object?>)p.FileBlocks["user"]["S"]!;
        Assert.Equal("1", s["A"]);
        Assert.False(s.ContainsKey("B"));
    }

    [Fact]
    public void ResolvePreset_FullChain()
    {
        var p = ProfileResolver.ResolvePreset(Standard(), "ally-x", null, "performance");
        Assert.Equal("ally-x", p.Device);
        Assert.Equal(PresetRef.Recommended("performance"), p.Source);
        Assert.False(p.IsEmpty);
        var s = Section(p);
        Assert.Equal("1", s["Shadows"]);
        Assert.Equal("2", s["Textures"]);
        Assert.Equal("60", s["Fps"]);
    }

    [Fact]
    public void Empty_HasNoSourceOrSettings()
    {
        var p = ProfileResolver.Empty("laptop");
        Assert.True(p.IsEmpty);
        Assert.Null(p.Source);
        Assert.Equal("laptop", p.Device);
        Assert.Null(p.Display);
        Assert.Empty(p.FileBlocks);
        Assert.Empty(p.Nvidia);
        Assert.Empty(p.Amd);
    }

    // ------------------------------------------------------------------ metadata keys

    [Fact]
    public void Resolve_MetadataKeys_AreNotFiles()
    {
        var p = ResolveBody(M(("label", "Steady 40"), ("description", "Locks 40 fps"), ("targetFps", 40),
            ("user", M(("S", M(("A", "1")))))));
        Assert.Equal("Steady 40", p.Label);
        Assert.Equal("Locks 40 fps", p.Description);
        Assert.Equal(40, p.TargetFps);
        Assert.Equal(["user"], p.FileBlocks.Keys);
    }

    [Theory]
    [InlineData("performance", "Performance")]
    [InlineData("steady-40", "Steady 40")]
    [InlineData("q", "Q")]
    public void Resolve_LabelDefaultsToTitleizedName(string name, string expected)
    {
        var p = ProfileResolver.Resolve(Game([]), "d", null, new(), PresetRef.Recommended(name));
        Assert.Equal(expected, p.Label);
        Assert.Null(p.Description);
        Assert.Null(p.TargetFps);
    }

    [Fact]
    public void Resolve_LabelFromBody_InheritedThroughExtends()
    {
        var game = Game([("base", M(("label", "Base label"), ("targetFps", "30")))], ("d", "p", M(("extends", "base"))));
        var p = ProfileResolver.ResolvePreset(game, "d", null, "p");
        Assert.Equal("Base label", p.Label);
        Assert.Equal(30, p.TargetFps);
    }

    [Fact]
    public void Resolve_ExplicitLabelArgument_Wins()
    {
        var p = ProfileResolver.Resolve(Game([]), "d", null, M(("label", "Body")), PresetRef.Manual("s"), label: "Arg", description: "Desc");
        Assert.Equal("Arg", p.Label);
        Assert.Equal("Desc", p.Description);
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData(" 120 ", 120)]
    public void Resolve_TargetFps_ParsesStrings(string raw, int expected) =>
        Assert.Equal(expected, ResolveBody(M(("targetFps", raw))).TargetFps);

    [Fact]
    public void Resolve_TargetFps_NotANumber_Throws() =>
        Assert.ThrowsAny<Exception>(() => ResolveBody(M(("targetFps", "sixty"))));

    [Fact]
    public void ResolveSave_UsesSaveNameAndNotes()
    {
        var save = new ManualSave
        {
            Slug = "steady-40", Name = "Steady 40", Notes = "my notes",
            Body = M(("extends", "base"), ("user", M(("Section", M(("Fps", "40")))))),
        };
        var p = ProfileResolver.ResolveSave(Standard(), "ally-x", null, save);
        Assert.Equal(PresetRef.Manual("steady-40"), p.Source);
        Assert.Equal("Steady 40", p.Label);
        Assert.Equal("my notes", p.Description);
        Assert.Equal("40", Section(p)["Fps"]);
        Assert.Equal("3", Section(p)["Shadows"]); // from base
    }

    // ------------------------------------------------------------------ display

    [Theory]
    [InlineData("inherit")]
    [InlineData("Device")]
    public void Resolve_DisplayInherit_UsesDeviceDefault(string value)
    {
        var p = ResolveBody(M(("display", value)), Desktop);
        Assert.Same(Desktop.Display, p.Display);
    }

    [Fact]
    public void ResolvePreset_DisplayInherit_FromDevice()
    {
        var p = ProfileResolver.ResolvePreset(Standard(), "desktop", Desktop, "quality");
        Assert.Equal("2560x1440@165Hz", p.Display!.ToString());
    }

    [Fact]
    public void Resolve_DisplayInherit_NoDevice_IsNull() => Assert.Null(ResolveBody(M(("display", "inherit"))).Display);

    [Theory]
    [InlineData("none")]
    [InlineData("keep")]
    public void Resolve_DisplayNone_IsNull(string value) => Assert.Null(ResolveBody(M(("display", value)), Desktop).Display);

    [Fact]
    public void Resolve_DisplayOmitted_IsNull() =>
        Assert.Null(ResolveBody(M(("user", M(("S", M(("A", "1")))))), Desktop).Display);

    [Fact]
    public void Resolve_DisplayMap_MergedWithDeviceDefault()
    {
        var p = ProfileResolver.ResolvePreset(Standard(), "ally-x", Desktop, "performance");
        Assert.Equal(2560, p.Display!.Width);
        Assert.Equal(1440, p.Display.Height);
        Assert.Equal(120, p.Display.Refresh);
    }

    [Fact]
    public void Resolve_DisplayMap_WithoutDeviceDefault_LeavesOthersNull()
    {
        var p = ResolveBody(M(("display", M(("width", 1280), ("height", "800")))), new DeviceDef());
        Assert.Equal(1280, p.Display!.Width);
        Assert.Equal(800, p.Display.Height);
        Assert.Null(p.Display.Refresh);
    }

    [Fact]
    public void Resolve_DisplayInvalid_Throws() =>
        Assert.Throws<ConfigException>(() => ResolveBody(M(("display", "fullscreen")), Desktop));

    [Fact]
    public void Resolve_PresetDisplayOverridesProfileDisplay()
    {
        var game = Game([("base", M(("display", "inherit")))], ("desktop", "p", M(("extends", "base"), ("display", "none"))));
        Assert.Null(ProfileResolver.ResolvePreset(game, "desktop", Desktop, "p").Display);
    }

    // ------------------------------------------------------------------ vendor blocks / file ids

    [Fact]
    public void Resolve_VendorBlocks()
    {
        var ally = ProfileResolver.ResolvePreset(Standard(), "ally-x", null, "performance");
        Assert.Equal("min=55,max=60", ally.Amd["Chill"]);
        Assert.Equal("false", ally.Amd["afmf"]); // case-insensitive lookup
        Assert.Empty(ally.Nvidia);

        var desk = ProfileResolver.ResolvePreset(Standard(), "desktop", Desktop, "quality");
        Assert.Equal("165", desk.Nvidia["FrameRateLimiter"]);
        Assert.Empty(desk.Amd);
        Assert.False(desk.FileBlocks.ContainsKey("nvidia"));
    }

    [Fact]
    public void Resolve_UnknownFileId_Throws()
    {
        var ex = Assert.Throws<ConfigException>(() => ResolveBody(M(("graphics", M(("A", "1"))))));
        Assert.Contains("graphics", ex.Message);
    }

    [Fact]
    public void Resolve_UnknownFileIdInProfile_Throws()
    {
        var game = Game([("base", M(("bogus", M(("A", "1")))))], ("d", "p", M(("extends", "base"))));
        Assert.Throws<ConfigException>(() => ProfileResolver.ResolvePreset(game, "d", null, "p"));
    }

    [Fact]
    public void Resolve_FileIdMatchedCaseInsensitively_UsesCanonicalId()
    {
        var p = ResolveBody(M(("engine", M(("S", M(("A", "1")))))));
        Assert.True(p.FileBlocks.ContainsKey("Engine"));
    }

    [Fact]
    public void ReservedKeys_IncludeMetadata()
    {
        foreach (var k in new[] { "extends", "display", "nvidia", "amd", "label", "description", "targetFps" })
            Assert.Contains(k, ProfileResolver.ReservedKeys);
    }

    // ------------------------------------------------------------------ preset names

    [Fact]
    public void PresetNames_ConventionalFirst_ThenOrdinal()
    {
        var game = Game([],
            ("d", "zeta", M()), ("d", "battery", M()), ("d", "alpha", M()), ("d", "quality", M()), ("d", "performance", M()),
            ("d", "Beta", M()));
        Assert.Equal(["quality", "performance", "battery", "Beta", "alpha", "zeta"], ProfileResolver.PresetNames(game, "d"));
    }

    [Fact]
    public void PresetNames_UnknownDevice_IsEmpty() => Assert.Empty(ProfileResolver.PresetNames(Standard(), "laptop"));

    // ------------------------------------------------------------------ deep merge

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
