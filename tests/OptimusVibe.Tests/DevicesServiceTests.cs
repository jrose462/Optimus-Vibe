using System.Text.RegularExpressions;
using OptimusVibe.Config;
using OptimusVibe.Devices;

namespace OptimusVibe.Tests;

public sealed class DevicesServiceTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly AppPaths _paths;
    private readonly DevicesService _svc;

    private const string Original = """
        # Device profiles. First match wins.
        #
        # match keys: model, cpu, gpu

        devices:
          # The handheld, matched on the CPU.
          ally-x:
            description: ROG Xbox Ally X
            match:
              cpu: "*Z2 Extreme*"
            driver: amd          # not the iGPU of the desktop
            defaultPreset: performance

          # Desktop box
          desktop:
            match:
              gpu: "NVIDIA GeForce RTX 4080*"
            driver: nvidia
            defaultPreset: quality
        """;

    public DevicesServiceTests()
    {
        var root = _tmp.Dir("config");
        _tmp.Write(Path.Combine("config", "devices.yaml"), Original);
        _paths = new AppPaths { ConfigRoot = root, DataDir = _tmp.Dir("data") };
        _svc = new DevicesService(_paths);
    }

    public void Dispose() => _tmp.Dispose();

    private string Text() => File.ReadAllText(_paths.DevicesFile).Replace("\r\n", "\n");
    private static Dictionary<string, string> Rules(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void Load_ReadsNamesAndOrder()
    {
        var all = _svc.Load();
        Assert.Equal(["ally-x", "desktop"], all.Select(d => d.Id));
        Assert.Equal("ally-x", all[0].DisplayName); // no name: falls back to the id
    }

    [Fact]
    public void Rename_ChangesOnlyTheNameLine_AndKeepsComments()
    {
        _svc.Rename("ally-x", "ROG Xbox Ally X");
        var text = Text();
        Assert.Contains("# Device profiles. First match wins.", text);
        Assert.Contains("# The handheld, matched on the CPU.", text);
        Assert.Contains("driver: amd          # not the iGPU of the desktop", text);
        Assert.Contains("  ally-x:\n    name: ROG Xbox Ally X\n    description: ROG Xbox Ally X", text);
        Assert.Equal("ROG Xbox Ally X", _svc.Load()[0].DisplayName);

        _svc.Rename("ally-x", "Handheld");   // second rename replaces the line, not adds another
        Assert.Single(Regex.Matches(Text(), @"^    name:", RegexOptions.Multiline));
        Assert.Equal("Handheld", _svc.Load()[0].DisplayName);
    }

    [Fact]
    public void Rename_QuotesAwkwardNames()
    {
        _svc.Rename("desktop", "Desk: main #1");
        Assert.Equal("Desk: main #1", _svc.Load()[1].DisplayName);
    }

    [Fact]
    public void Add_LessSpecific_GoesLast_AndKeepsHeader()
    {
        _svc.Add("laptop", "Work laptop", Rules(("model", "ThinkPad*")), null, "none", "performance", null);
        Assert.Equal(["ally-x", "desktop", "laptop"], _svc.Load().Select(d => d.Id));
        var text = Text();
        Assert.StartsWith("# Device profiles. First match wins.\n#\n# match keys: model, cpu, gpu\n", text);
        Assert.Contains("# Desktop box\n  desktop:", text);
        var laptop = _svc.Load()[2];
        Assert.Equal("Work laptop", laptop.Def.Name);
        Assert.Equal("ThinkPad*", laptop.Def.Match["model"]);
    }

    [Fact]
    public void Add_MoreSpecific_GoesFirst()
    {
        _svc.Add("desktop-office", "Office desktop", Rules(("gpu", "NVIDIA*"), ("machine", "OFFICE-PC")), new DisplayModeDef { Width = 1920, Height = 1080, Refresh = 60 },
            "nvidia", "quality", "desktop");
        var all = _svc.Load();
        Assert.Equal(["desktop-office", "ally-x", "desktop"], all.Select(d => d.Id));
        Assert.Equal(1920, all[0].Def.Display!.Width);
        Assert.Equal("desktop", all[0].Def.PresetsFrom);
        Assert.Contains("# The handheld, matched on the CPU.\n  ally-x:", Text());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Desktop")]
    [InlineData("two words")]
    [InlineData("-lead")]
    [InlineData("double--dash")]
    [InlineData("ally-x")]
    [InlineData("DESKTOP")]
    public void Add_RejectsBadOrDuplicateIds(string id) =>
        Assert.Throws<ConfigException>(() => _svc.Add(id, "X", Rules(("cpu", "x")), null, null, null, null));

    [Fact]
    public void Add_RejectsUnknownMatchKeyDriverAndPresetsFrom()
    {
        Assert.Throws<ConfigException>(() => _svc.Add("a", "A", Rules(("colour", "red")), null, null, null, null));
        Assert.Throws<ConfigException>(() => _svc.Add("a", "A", Rules(("cpu", "x")), null, "intel", null, null));
        Assert.Throws<ConfigException>(() => _svc.Add("a", "A", Rules(("cpu", "x")), null, null, null, "nope"));
        Assert.Throws<ConfigException>(() => _svc.Add("a", "", Rules(("cpu", "x")), null, null, null, null));
        Assert.Equal(2, _svc.Load().Count);
    }

    [Fact]
    public void Update_RewritesBlock_KeepsLeadComment_AndIdStays()
    {
        _svc.Update("desktop", "Big desktop", Rules(("gpu", "NVIDIA GeForce RTX 5090*")), null, "nvidia", "quality", null);
        var d = _svc.Load()[1];
        Assert.Equal("desktop", d.Id);
        Assert.Equal("Big desktop", d.Def.Name);
        Assert.Equal("NVIDIA GeForce RTX 5090*", d.Def.Match["gpu"]);
        Assert.Contains("# Desktop box\n  desktop:", Text());
    }

    [Fact]
    public void Update_RejectsPresetsFromCycle()
    {
        _svc.Update("desktop", "Desktop", Rules(("gpu", "NVIDIA*")), null, "nvidia", "quality", "ally-x");
        Assert.Throws<ConfigException>(() => _svc.Update("ally-x", "Ally", Rules(("cpu", "x")), null, "amd", null, "desktop"));
        Assert.Throws<ConfigException>(() => _svc.Update("ally-x", "Ally", Rules(("cpu", "x")), null, "amd", null, "ally-x"));
    }

    [Fact]
    public void Move_ReordersWithCommentsAttached()
    {
        _svc.Move("desktop", -1);
        Assert.Equal(["desktop", "ally-x"], _svc.Load().Select(d => d.Id));
        var text = Text();
        Assert.True(text.IndexOf("# Desktop box", StringComparison.Ordinal) < text.IndexOf("# The handheld", StringComparison.Ordinal));
        _svc.Move("desktop", -1); // already first
        Assert.Equal(["desktop", "ally-x"], _svc.Load().Select(d => d.Id));
        _svc.Move("desktop", 5);  // clamps to last
        Assert.Equal(["ally-x", "desktop"], _svc.Load().Select(d => d.Id));
    }

    [Fact]
    public void Delete_CurrentDevice_NeedsConfirmation()
    {
        var refused = _svc.Delete("desktop", "desktop", confirmed: false);
        Assert.False(refused.Deleted);
        Assert.Equal(2, _svc.Load().Count);
        var done = _svc.Delete("desktop", "desktop", confirmed: true);
        Assert.True(done.Deleted);
        Assert.Contains("stay in the repo", done.Message);
        Assert.Equal(["ally-x"], _svc.Load().Select(d => d.Id));
        Assert.Contains("# Device profiles", Text());
    }

    [Fact]
    public void Delete_OtherDevice_ClearsPresetsFromLinks()
    {
        _svc.Update("ally-x", "Ally", Rules(("cpu", "*Z2 Extreme*")), null, "amd", "performance", "desktop");
        var r = _svc.Delete("desktop", "ally-x", confirmed: false);
        Assert.True(r.Deleted);
        Assert.Null(_svc.Load().Single().Def.PresetsFrom);
        Assert.DoesNotContain("presetsFrom", Text());
    }

    [Fact]
    public void Delete_UnknownDevice_Throws() =>
        Assert.Throws<ConfigException>(() => _svc.Delete("nope", null, true));

    [Fact]
    public void CrLfFile_StaysCrLf()
    {
        File.WriteAllText(_paths.DevicesFile, Original.Replace("\n", "\r\n"));
        _svc.Rename("desktop", "Desk");
        var raw = File.ReadAllText(_paths.DevicesFile);
        Assert.DoesNotContain("\n", raw.Replace("\r\n", ""));
    }

    // ---------------------------------------------------------------- slug / ids

    [Theory]
    [InlineData("ROG Xbox Ally X", "rog-xbox-ally-x")]
    [InlineData("Desktop (RTX 4080)", "desktop-rtx-4080")]
    [InlineData("  --Hello__World!! ", "hello-world")]
    public void Slug(string name, string expected) => Assert.Equal(expected, DevicesService.Slug(name));

    [Fact]
    public void UniqueId_AppendsCounter()
    {
        Assert.Equal("desktop", DevicesService.UniqueId("desktop", []));
        Assert.Equal("desktop-2", DevicesService.UniqueId("desktop", ["desktop"]));
        Assert.Equal("desktop-3", DevicesService.UniqueId("desktop", ["desktop", "Desktop-2"]));
        Assert.Equal("device", DevicesService.UniqueId("", []));
    }

    // ---------------------------------------------------------------- suggestion from a (fake) machine

    private static readonly HardwareInfo AllyHw = new()
    {
        Manufacturer = "ASUSTeK COMPUTER INC.", Model = "ROG Xbox Ally X RC73XA", Cpu = "AMD Ryzen AI Z2 Extreme",
        Gpus = ["AMD Radeon(TM) 890M Graphics"],
    };

    private static readonly HardwareInfo DesktopHw = new()
    {
        Manufacturer = "ASUS", Model = "System Product Name", Cpu = "AMD Ryzen 7 9800X3D 8-Core Processor",
        Gpus = ["NVIDIA GeForce RTX 4080 SUPER", "AMD Radeon(TM) Graphics"],
    };

    [Fact]
    public void Suggest_Handheld_MatchesOnCpu()
    {
        var s = DevicesService.Suggest(AllyHw, new DisplayModeDef { Width = 1920, Height = 1080, Refresh = 120 }, "ALLY", new DevicesFile());
        Assert.True(s.IsHandheld);
        Assert.Equal("*Z2 Extreme*", s.Match["cpu"]);
        Assert.DoesNotContain("gpu", s.Match.Keys);
        Assert.DoesNotContain("machine", s.Match.Keys);
        Assert.Equal("amd", s.Driver);
        Assert.Equal("performance", s.DefaultPreset);
        Assert.Equal("ROG Xbox Ally X RC73XA", s.Name);
        Assert.Equal("rog-xbox-ally-x-rc73xa", s.Id);
        Assert.Equal(120, s.Display!.Refresh);
    }

    [Fact]
    public void Suggest_Desktop_MatchesOnDiscreteGpuModel()
    {
        var s = DevicesService.Suggest(DesktopHw, null, "PC", new DevicesFile());
        Assert.False(s.IsHandheld);
        Assert.Equal("NVIDIA GeForce RTX 4080*", s.Match["gpu"]); // the iGPU is not the match
        Assert.Equal("nvidia", s.Driver);
        Assert.Equal("quality", s.DefaultPreset);
        Assert.Equal("Desktop (RTX 4080)", s.Name);
        Assert.Equal("desktop-rtx-4080", s.Id);
        Assert.Null(s.Display);
    }

    [Fact]
    public void Suggest_AddsMachineName_WhenAnExistingDeviceAlreadyMatches()
    {
        var existing = new DevicesFile { Devices = new() { ["desktop"] = new DeviceDef { Match = new() { ["gpu"] = "NVIDIA*" } } } };
        var s = DevicesService.Suggest(DesktopHw, null, "OFFICE-PC", existing);
        Assert.Equal("OFFICE-PC", s.Match["machine"]);
        Assert.Equal("desktop", s.ExistingMatch);
        Assert.Equal("OFFICE-PC", s.MachineName);
    }

    [Fact]
    public void Suggest_IdIsUnique()
    {
        var existing = new DevicesFile { Devices = new() { ["desktop-rtx-4080"] = new DeviceDef { Match = new() { ["cpu"] = "none" } } } };
        Assert.Equal("desktop-rtx-4080-2", DevicesService.Suggest(DesktopHw, null, "PC", existing).Id);
    }

    [Fact]
    public void Suggest_SteamDeck_IsHandheldByModel()
    {
        var hw = new HardwareInfo { Manufacturer = "Valve", Model = "Jupiter", Cpu = "AMD Custom APU 0405", Gpus = ["AMD Custom GPU 0405"] };
        var s = DevicesService.Suggest(hw, null, "deck", new DevicesFile());
        Assert.True(s.IsHandheld);
        Assert.Equal("*Custom APU*", s.Match["cpu"]);
    }
}
