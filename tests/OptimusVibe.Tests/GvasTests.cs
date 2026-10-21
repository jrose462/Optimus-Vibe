using System.Text;
using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Handlers;

namespace OptimusVibe.Tests;

/// <summary>Builds small synthetic GVAS saves (never a real game file) following the public GVAS layout.</summary>
internal sealed class GvasBuilder
{
    private readonly MemoryStream _ms = new();

    public static GvasBuilder Start(string saveClass = "/Script/Test.SaveObject")
    {
        var b = new GvasBuilder();
        b.Raw("GVAS"u8.ToArray());
        b.I32(2); b.I32(522);
        b.U16(4); b.U16(26); b.U16(2); b.I32(0); b.Str("++UE4+Release-4.26");
        b.I32(3); b.I32(1); b.Raw(new byte[16]); b.I32(7); // one custom version
        b.Str(saveClass);
        return b;
    }

    public GvasBuilder Raw(byte[] x) { _ms.Write(x); return this; }
    public GvasBuilder U8(byte x) { _ms.WriteByte(x); return this; }
    public GvasBuilder U16(ushort x) => Raw(BitConverter.GetBytes(x));
    public GvasBuilder I32(int x) => Raw(BitConverter.GetBytes(x));
    public GvasBuilder I64(long x) => Raw(BitConverter.GetBytes(x));
    public GvasBuilder Str(string s)
    {
        if (s.Length == 0) return I32(0);
        if (s.All(c => c < 0x100)) { I32(s.Length + 1); Raw(Encoding.Latin1.GetBytes(s)); return U8(0); }
        I32(-(s.Length + 1)); Raw(Encoding.Unicode.GetBytes(s)); return U8(0).U8(0);
    }

    private GvasBuilder Head(string name, string type, long size) => Str(name).Str(type).I64(size);

    public GvasBuilder Int(string n, int v) => Head(n, "IntProperty", 4).U8(0).I32(v);
    public GvasBuilder UInt(string n, uint v) => Head(n, "UInt32Property", 4).U8(0).Raw(BitConverter.GetBytes(v));
    public GvasBuilder Float(string n, float v) => Head(n, "FloatProperty", 4).U8(0).Raw(BitConverter.GetBytes(v));
    public GvasBuilder Double(string n, double v) => Head(n, "DoubleProperty", 8).U8(0).Raw(BitConverter.GetBytes(v));
    public GvasBuilder Bool(string n, bool v) => Head(n, "BoolProperty", 0).U8(v ? (byte)1 : (byte)0).U8(0);
    public GvasBuilder StrProp(string n, string v) => Head(n, "StrProperty", PayloadLen(v)).U8(0).Str(v);
    public GvasBuilder NameProp(string n, string v) => Head(n, "NameProperty", PayloadLen(v)).U8(0).Str(v);
    public GvasBuilder Enum(string n, string enumType, string v) => Head(n, "EnumProperty", PayloadLen(v)).Str(enumType).U8(0).Str(v);
    public GvasBuilder Byte(string n, byte v) => Head(n, "ByteProperty", 1).Str("None").U8(0).U8(v);
    public GvasBuilder IntPoint(string n, int x, int y) => Head(n, "StructProperty", 8).Str("IntPoint").Raw(new byte[16]).U8(0).I32(x).I32(y);
    public GvasBuilder Map(string n, byte[] payload) =>
        Head(n, "MapProperty", payload.Length).Str("StrProperty").Str("IntProperty").U8(0).Raw(payload);

    /// <summary>A generic struct: a nested property list ending in None.</summary>
    public GvasBuilder Struct(string n, string typeName, Action<GvasBuilder> body)
    {
        var inner = new GvasBuilder();
        body(inner);
        inner.Str("None");
        var bytes = inner._ms.ToArray();
        return Head(n, "StructProperty", bytes.Length).Str(typeName).Raw(new byte[16]).U8(0).Raw(bytes);
    }

    public GvasBuilder IntArray(string n, params int[] values)
    {
        Head(n, "ArrayProperty", 4 + values.Length * 4).Str("IntProperty").U8(0).I32(values.Length);
        foreach (var v in values) I32(v);
        return this;
    }

    public GvasBuilder StructArray(string n, string typeName, params Action<GvasBuilder>[] elems)
    {
        var body = new GvasBuilder();
        foreach (var e in elems) { e(body); body.Str("None"); }
        var bytes = body._ms.ToArray();
        // payload = count + element header (name, type, size, struct type, guid, flag) + elements
        var header = new GvasBuilder().Str(n).Str("StructProperty").I64(bytes.Length).Str(typeName).Raw(new byte[16]).U8(0)._ms.ToArray();
        Head(n, "ArrayProperty", 4 + header.Length + bytes.Length).Str("StructProperty").U8(0).I32(elems.Length);
        return Raw(header).Raw(bytes);
    }

    private static long PayloadLen(string s) => s.Length == 0 ? 4 : s.All(c => c < 0x100) ? 4 + s.Length + 1 : 4 + (s.Length + 1) * 2;

    public byte[] Finish() => new GvasBuilder().Raw(_ms.ToArray()).Str("None").I32(0)._ms.ToArray();

    /// <summary>A save in the shape of a typical game settings object.</summary>
    public static byte[] Sample() => Start()
        .UInt("ResolutionSizeX", 2560).UInt("ResolutionSizeY", 1440)
        .Int("OverallScalabilityQuality", -1)
        .Bool("bIsVSyncEnabled", false)
        .Float("Gamma", 2.3f).Double("Exposure", 0.25)
        .StrProp("Locale", "en-US").NameProp("Profile", "Default")
        .Enum("Version", "ESettingsVersion", "ESettingsVersion::Latest")
        .Byte("Mode", 2)
        .IntPoint("Window", 1920, 1080)
        .Struct("Quality", "QualityBlock", s => s.Int("Shadows", 3).Bool("Enabled", true))
        .IntArray("Slots", 10, 20, 30)
        .StructArray("Binds", "Bind", e => e.NameProp("Action", "Jump").Bool("bShift", false), e => e.NameProp("Action", "Fire").Bool("bShift", true))
        .StrProp("Wide", "café 世界")
        .Map("Unknown", [0, 0, 0, 0, 0, 0, 0, 0])
        .Finish();
}

public sealed class GvasTests
{
    private static GvasFile Load() => GvasFile.Parse(GvasBuilder.Sample());

    [Fact]
    public void RoundTrip_IsByteIdentical()
    {
        var bytes = GvasBuilder.Sample();
        Assert.Equal(bytes, GvasFile.Parse(bytes).ToBytes());
    }

    [Fact]
    public void Header_IsParsed()
    {
        var f = Load();
        Assert.Equal("/Script/Test.SaveObject", f.SaveClass);
        Assert.StartsWith("4.26.2", f.EngineVersion);
        Assert.Equal(2, f.SaveGameVersion);
    }

    [Theory]
    [InlineData("ResolutionSizeX", "2560")]
    [InlineData("OverallScalabilityQuality", "-1")]
    [InlineData("bIsVSyncEnabled", "false")]
    [InlineData("Gamma", "2.3")]
    [InlineData("Exposure", "0.25")]
    [InlineData("Locale", "en-US")]
    [InlineData("Profile", "Default")]
    [InlineData("Version", "ESettingsVersion::Latest")]
    [InlineData("Mode", "2")]
    [InlineData("Window.X", "1920")]
    [InlineData("Window.Y", "1080")]
    [InlineData("Quality.Shadows", "3")]
    [InlineData("Quality.Enabled", "true")]
    [InlineData("Slots[1]", "20")]
    [InlineData("Binds[1].Action", "Fire")]
    [InlineData("Binds[1].bShift", "true")]
    [InlineData("Wide", "café 世界")]
    public void Get_ReadsEveryScalarKind(string path, string expected) => Assert.Equal(expected, Load().Get(path));

    [Fact]
    public void Get_UnknownIsNull_AndOpaqueIsDescribed()
    {
        var f = Load();
        Assert.Null(f.Get("Nope"));
        Assert.Null(f.Get("Window.Z"));
        Assert.Null(f.Get("Slots[9]"));
        Assert.Equal("<8 bytes>", f.Get("Unknown"));
    }

    [Theory]
    [InlineData("ResolutionSizeX", "1600", "1600")]
    [InlineData("OverallScalabilityQuality", "2", "2")]
    [InlineData("bIsVSyncEnabled", "True", "true")]
    [InlineData("Gamma", "2.1", "2.1")]
    [InlineData("Exposure", "1.5", "1.5")]
    [InlineData("Locale", "fr-FR and a much longer value", "fr-FR and a much longer value")]
    [InlineData("Profile", "Other", "Other")]
    [InlineData("Version", "ESettingsVersion::Old", "ESettingsVersion::Old")]
    [InlineData("Mode", "1", "1")]
    [InlineData("Window.X", "3840", "3840")]
    [InlineData("Quality.Shadows", "0", "0")]
    [InlineData("Slots[2]", "99", "99")]
    [InlineData("Binds[0].bShift", "true", "true")]
    [InlineData("Wide", "世", "世")]
    public void Set_EachType_ChangesOnlyThatValue(string path, string value, string expected)
    {
        var f = Load();
        var before = f.Enumerate().ToDictionary(e => e.Path, e => e.Value);
        f.Set(path, value);
        var bytes = f.ToBytes();
        var again = GvasFile.Parse(bytes);
        Assert.Equal(bytes, again.ToBytes());
        var after = again.Enumerate().ToDictionary(e => e.Path, e => e.Value);
        Assert.Equal(before.Keys, after.Keys);
        var changed = after.Where(kv => before[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
        Assert.Equal([path], changed);
        Assert.Equal(expected, again.Get(path));
    }

    [Fact]
    public void Set_SameLengthValue_ChangesExactlyThoseBytes()
    {
        var original = GvasBuilder.Sample();
        var f = GvasFile.Parse(original);
        f.Set("ResolutionSizeX", "1600");
        var edited = f.ToBytes();
        Assert.Equal(original.Length, edited.Length);
        var diff = Enumerable.Range(0, original.Length).Count(i => original[i] != edited[i]);
        Assert.InRange(diff, 1, 2); // 2560 (0x0A00) -> 1600 (0x0640): two bytes of one uint
    }

    [Fact]
    public void Set_LongerString_UpdatesSizesOfEnclosingStructsAndArrays()
    {
        var bytes = GvasBuilder.Start()
            .StructArray("Binds", "Bind", e => e.NameProp("Action", "Jump"), e => e.NameProp("Action", "Fire"))
            .Struct("Outer", "OuterBlock", s => s.Struct("Inner", "InnerBlock", i => i.StrProp("Name", "a")))
            .Finish();
        var f = GvasFile.Parse(bytes);
        f.Set("Binds[0].Action", "A_much_longer_action_name");
        f.Set("Outer.Inner.Name", "longer value");
        var again = GvasFile.Parse(f.ToBytes());
        Assert.Equal("A_much_longer_action_name", again.Get("Binds[0].Action"));
        Assert.Equal("Fire", again.Get("Binds[1].Action"));
        Assert.Equal("longer value", again.Get("Outer.Inner.Name"));
        Assert.Equal(f.ToBytes(), again.ToBytes());
    }

    [Fact]
    public void Set_AddsAMissingProperty_OnlyWithATypeHint()
    {
        var f = Load();
        var ex = Assert.Throws<GvasException>(() => f.Set("ShadowQuality", "1"));
        Assert.Contains("ShadowQuality:int", ex.Message);

        f.Set("ShadowQuality:int", "1");
        f.Set("bIsRayTracingEnabled:bool", "false");
        f.Set("Scale:float", "0.5");
        f.Set("Quality.Extra:int", "7");
        var again = GvasFile.Parse(f.ToBytes());
        Assert.Equal("1", again.Get("ShadowQuality"));
        Assert.Equal("false", again.Get("bIsRayTracingEnabled"));
        Assert.Equal("0.5", again.Get("Scale"));
        Assert.Equal("7", again.Get("Quality.Extra"));
        Assert.Equal(f.ToBytes(), again.ToBytes());
        f.Set("ShadowQuality:int", "2"); // now exists: plain update
        Assert.Equal("2", f.Get("ShadowQuality"));
    }

    [Fact]
    public void Set_TypeHintMustMatchExistingType()
    {
        var f = Load();
        Assert.Throws<GvasException>(() => f.Set("ResolutionSizeX:int", "1"));
        f.Set("ResolutionSizeX:uint", "1"); // right type
        Assert.Throws<GvasException>(() => f.Set("X:bogus", "1"));
    }

    [Theory]
    [InlineData("ResolutionSizeX", "-1")]                    // unsigned
    [InlineData("OverallScalabilityQuality", "99999999999")] // overflow
    [InlineData("OverallScalabilityQuality", "abc")]
    [InlineData("bIsVSyncEnabled", "maybe")]
    [InlineData("Gamma", "NaN")]
    [InlineData("Mode", "300")]
    [InlineData("Slots", "1")]                               // array container
    [InlineData("Quality", "1")]                             // struct container
    [InlineData("Unknown", "1")]                             // undecoded map
    [InlineData("Nope", "1")]                                // missing, no hint
    [InlineData("Window.Z", "1")]
    [InlineData("Slots[9]", "1")]
    public void Set_RefusesBadOrUnsupportedWrites_AndChangesNothing(string path, string value)
    {
        var original = GvasBuilder.Sample();
        var f = GvasFile.Parse(original);
        Assert.Throws<GvasException>(() => f.Set(path, value));
        Assert.Equal(original, f.ToBytes());
    }

    [Fact]
    public void Parse_RejectsNonGvasAndTruncatedData()
    {
        Assert.Throws<GvasException>(() => GvasFile.Parse([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        var bytes = GvasBuilder.Sample();
        Assert.Throws<GvasException>(() => GvasFile.Parse(bytes[..(bytes.Length / 2)]));
        Assert.False(GvasFile.LooksLikeGvas("not a save at all"u8.ToArray()));
    }

    [Fact]
    public void Enumerate_ListsPathsTypesAndWritability()
    {
        var e = Load().Enumerate().ToDictionary(x => x.Path);
        Assert.Equal("UInt32Property", e["ResolutionSizeX"].Type);
        Assert.True(e["ResolutionSizeX"].Writable);
        Assert.False(e["Slots"].Writable);
        Assert.False(e["Unknown"].Writable);
        Assert.True(e["Binds[0].Action"].Writable);
    }
}

public sealed class GvasHandlerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly GvasHandler _h = new();
    public void Dispose() => _tmp.Dispose();

    private static FileDef Def() => new() { Id = "sav", Handler = "gvas" };

    private string Sav(byte[]? bytes = null)
    {
        var p = Path.Combine(_tmp.Dir("g"), "GameUserSettings.sav");
        File.WriteAllBytes(p, bytes ?? GvasBuilder.Sample());
        return p;
    }

    [Fact]
    public void Registered_AndNamed()
    {
        Assert.IsType<GvasHandler>(SettingsHandler.For("gvas"));
        Assert.Contains("gvas", SettingsHandler.Names);
    }

    [Fact]
    public void Read_ReturnsValues_NullForMissing_IgnoresHints()
    {
        var r = _h.Read(Def(), Sav(), ["ResolutionSizeX", "Window.X", "ShadowQuality:int", "Nope"]);
        Assert.Equal("2560", r["ResolutionSizeX"]);
        Assert.Equal("1920", r["Window.X"]);
        Assert.Null(r["ShadowQuality:int"]);
        Assert.Null(r["Nope"]);
    }

    [Fact]
    public void Read_MissingOrNonGvasFile_IsAllNull_NotAnError()
    {
        Assert.Null(_h.Read(Def(), _tmp.File("missing.sav"), ["A"])["A"]);
        Assert.Null(_h.Read(Def(), Sav([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]), ["A"])["A"]);
    }

    [Fact]
    public void Write_ChangesAndAdds_ThenReadsBack_AndLeavesNoTempFile()
    {
        var path = Sav();
        _h.Write(Def(), path, new Dictionary<string, string>
        {
            ["ResolutionSizeX"] = "1600", ["ResolutionSizeY"] = "900", ["Quality.Shadows"] = "0", ["ViewDistanceQuality:int"] = "1", ["Gamma"] = "2.1",
        });
        var r = _h.Read(Def(), path, ["ResolutionSizeX", "ResolutionSizeY", "Quality.Shadows", "ViewDistanceQuality", "Gamma", "Locale", "Window.Y"]);
        Assert.Equal("1600", r["ResolutionSizeX"]);
        Assert.Equal("900", r["ResolutionSizeY"]);
        Assert.Equal("0", r["Quality.Shadows"]);
        Assert.Equal("1", r["ViewDistanceQuality"]);
        Assert.Equal("2.1", r["Gamma"]);
        Assert.Equal("en-US", r["Locale"]);   // untouched
        Assert.Equal("1080", r["Window.Y"]);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.ov-tmp"));
        Assert.Equal(File.ReadAllBytes(path), GvasFile.Parse(File.ReadAllBytes(path)).ToBytes());
    }

    [Fact]
    public void Write_UnsupportedKey_ThrowsAndLeavesFileUntouched()
    {
        var path = Sav();
        var before = File.ReadAllBytes(path);
        var ex = Assert.Throws<ConfigException>(() =>
            _h.Write(Def(), path, new Dictionary<string, string> { ["ResolutionSizeX"] = "1600", ["Unknown"] = "5" }));
        Assert.Contains("not decoded", ex.Message);
        Assert.Throws<ConfigException>(() => _h.Write(Def(), path, new Dictionary<string, string> { ["Slots"] = "5" }));
        Assert.Throws<ConfigException>(() => _h.Write(Def(), path, new Dictionary<string, string> { ["Brand:new"] = "5" }));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_NoChange_DoesNotTouchTheFile()
    {
        var path = Sav();
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        _h.Write(Def(), path, new Dictionary<string, string> { ["ResolutionSizeX"] = "2560", ["Gamma"] = "2.3" });
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Write_MissingFile_IsAClearError()
    {
        var ex = Assert.Throws<ConfigException>(() => _h.Write(Def(), _tmp.File("nope.sav"), new Dictionary<string, string> { ["A:int"] = "1" }));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void Write_ClearsReadOnly()
    {
        var path = Sav();
        File.SetAttributes(path, FileAttributes.ReadOnly);
        _h.Write(Def(), path, new Dictionary<string, string> { ["ResolutionSizeX"] = "800" });
        Assert.Equal("800", _h.Read(Def(), path, ["ResolutionSizeX"])["ResolutionSizeX"]);
    }

    [Fact]
    public void Describe_ListsKeys_AndCollapsesLongArraysUnlessFull()
    {
        var b = GvasBuilder.Start().Int("A", 1).IntArray("Big", Enumerable.Range(0, 20).ToArray()).Finish();
        var path = Sav(b);
        var brief = _h.Describe(path, false)!;
        Assert.Contains("A [IntProperty] = 1", brief);
        Assert.DoesNotContain("Big[3]", brief);
        Assert.Contains("20 elements not listed", brief);
        Assert.Contains("Big[3]", _h.Describe(path, true)!);
        Assert.Contains("not a readable GVAS save", _h.Describe(Sav([9, 9, 9, 9, 9, 9, 9, 9, 9]), false));
    }

    [Fact]
    public void Flatten_KeepsDottedAndHintedKeys()
    {
        var flat = _h.Flatten(new Dictionary<string, object?>
        {
            ["ShadowQuality:int"] = 1,
            ["Window"] = new Dictionary<string, object?> { ["X"] = 1280 },
        });
        Assert.Equal("1", flat["ShadowQuality:int"]);
        Assert.Equal("1280", flat["Window.X"]);
    }
}

/// <summary>Engine behaviour for a gvas file that also has autoSnapshot: copy restored first, preset keys written on top.</summary>
public sealed class GvasEngineTests : IDisposable
{
    private readonly EngineFixture _f;
    public void Dispose() => _f.Dispose();

    public GvasEngineTests()
    {
        var yaml = """
            name: Test Game
            store: other
            processes: [Test.exe]
            files:
              - id: usersav
                handler: gvas
                path: "{T}/GameUserSettings.sav"
                autoSnapshot: perPreset
            presets:
              desktop:
                performance:
                  usersav:
                    ResolutionSizeX: 1600
                    ResolutionSizeY: 900
                    ShadowQuality:int: 0
                    Window:
                      X: 1280
            """;
        _f = new EngineFixture(yaml);
    }

    [Fact]
    public void Validator_AcceptsGvasKeys_WithoutAutoSnapshotWarning()
    {
        var report = Validator.Run(_f.Store);
        Assert.DoesNotContain(report.Errors, e => e.Contains("usersav") && !e.Contains("unknown path token")); // {T} is the fixture's test token
        Assert.DoesNotContain(report.Warnings, w => w.Contains("autoSnapshot on a"));
    }

    [Fact]
    public void Apply_RestoresDeviceCopyThenWritesPresetKeysOnTop()
    {
        // the live file: what cloud sync / another preset left there
        File.WriteAllBytes(_f.GameFile("GameUserSettings.sav"), GvasBuilder.Sample());
        // this device's kept copy of the preset: carries a value the preset knows nothing about
        var copy = GvasFile.Parse(GvasBuilder.Sample());
        copy.Set("Locale", "kept-from-copy");
        copy.Set("ResolutionSizeX", "2048");
        AutoSnapshots.StoreFor(_f.Paths).Save(EngineFixture.GameId, "desktop", "performance", "usersav", copy.ToBytes());

        var ctx = _f.Load("desktop", PresetRef.Recommended("performance"));
        var plan = GameEngine.Plan(ctx).Single();
        Assert.True(plan.RestoreSnapshot);
        Assert.Equal(4, plan.Changes.Count); // every key is written after the restore

        GameEngine.Apply(ctx, [plan]);
        var after = GvasFile.Parse(File.ReadAllBytes(_f.GameFile("GameUserSettings.sav")));
        Assert.Equal("kept-from-copy", after.Get("Locale"));   // from the copy
        Assert.Equal("1600", after.Get("ResolutionSizeX"));    // preset wins over the copy
        Assert.Equal("900", after.Get("ResolutionSizeY"));
        Assert.Equal("0", after.Get("ShadowQuality"));         // inserted (absent in both)
        Assert.Equal("1280", after.Get("Window.X"));
    }

    [Fact]
    public void Apply_WithoutACopy_WritesTheKeys_AndDriftIsDetected()
    {
        File.WriteAllBytes(_f.GameFile("GameUserSettings.sav"), GvasBuilder.Sample());
        var ctx = _f.Load("desktop", PresetRef.Recommended("performance"));
        var plans = GameEngine.Plan(ctx);
        Assert.False(plans.Single().RestoreSnapshot);
        var applied = GameEngine.Apply(ctx, plans);
        Assert.Empty(GameEngine.FindDrift(ctx, applied));

        // the game changes a preset key afterwards
        var g = GvasFile.Parse(File.ReadAllBytes(_f.GameFile("GameUserSettings.sav")));
        g.Set("ResolutionSizeX", "3840");
        File.WriteAllBytes(_f.GameFile("GameUserSettings.sav"), g.ToBytes());
        var drift = Assert.Single(GameEngine.FindDrift(ctx, applied));
        Assert.Equal("ResolutionSizeX", drift.Key);
        Assert.Equal("3840", drift.Now);

        // re-planning only lists what is off now
        var replan = GameEngine.Plan(_f.Load("desktop", PresetRef.Recommended("performance")))[0];
        Assert.Equal(["ResolutionSizeX"], replan.Changes.Select(c => c.Key));
    }
}

public sealed class GvasValidatorTests : IDisposable
{
    private readonly EngineFixture _f = new("""
        name: Test Game
        store: other
        processes: [Test.exe]
        files:
          - id: usersav
            handler: gvas
            path: "{T}/s.sav"
        presets:
          desktop:
            performance:
              usersav:
                ShadowQuality:wat: 0
        """);
    public void Dispose() => _f.Dispose();

    [Fact]
    public void Validator_ReportsAnUnknownTypeHint()
    {
        var report = Validator.Run(_f.Store);
        Assert.Contains(report.Errors, e => e.Contains("Unknown type hint ':wat'"));
    }
}
