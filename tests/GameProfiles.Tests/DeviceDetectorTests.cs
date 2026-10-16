using GameProfiles.Config;
using GameProfiles.Devices;

namespace GameProfiles.Tests;

public sealed class DeviceDetectorTests
{
    private static readonly HardwareInfo DesktopHw = new()
    {
        Manufacturer = "ASUS",
        Model = "Example Board X1",
        Cpu = "AMD Ryzen 9 7950X3D 16-Core Processor",
        Gpus = ["NVIDIA GeForce RTX 4090", "AMD Radeon(TM) Graphics"],
    };

    private static readonly HardwareInfo AllyHw = new()
    {
        Manufacturer = "ASUSTeK COMPUTER INC.",
        Model = "ROG Xbox Ally X RC73XA",
        Cpu = "AMD Ryzen AI Z2 Extreme",
        Gpus = ["AMD Radeon(TM) 890M Graphics"],
    };

    private static DevicesFile Devices() => new()
    {
        Devices = new()
        {
            ["desktop"] = new DeviceDef { Match = new() { ["gpu"] = "NVIDIA*" } },
            ["ally-x"] = new DeviceDef { Match = new() { ["cpu"] = "*Z2 Extreme*", ["gpu"] = "AMD Radeon*" } },
            ["unmatched"] = new DeviceDef(),
        },
    };

    [Theory]
    [InlineData("NVIDIA*", "NVIDIA GeForce RTX 4090", true)]
    [InlineData("nvidia*", "NVIDIA GeForce RTX 4090", true)]
    [InlineData("*RTX 40?0", "NVIDIA GeForce RTX 4090", true)]
    [InlineData("*RTX 40?0", "NVIDIA GeForce RTX 40900", false)]
    [InlineData("RTX*", "NVIDIA GeForce RTX 4090", false)]
    [InlineData("AMD Radeon(TM) 890M Graphics", "AMD Radeon(TM) 890M Graphics", true)]
    [InlineData("ROG Ally RC71L.", "ROG Ally RC71LX", false)] // '.' is literal
    [InlineData("a+b", "a+b", true)]
    [InlineData("*", "", true)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void Wildcard(string pattern, string value, bool expected) =>
        Assert.Equal(expected, DeviceDetector.Wildcard(pattern, value));

    [Fact]
    public void Matches_AllRulesMustPass()
    {
        var rules = new Dictionary<string, string> { ["cpu"] = "*Z2 Extreme*", ["gpu"] = "AMD Radeon*" };
        Assert.True(DeviceDetector.Matches(rules, AllyHw));
        Assert.False(DeviceDetector.Matches(rules, DesktopHw)); // gpu matches the iGPU, cpu does not
    }

    [Fact]
    public void Matches_GpuMatchesAnyAdapter()
    {
        Assert.True(DeviceDetector.Matches(new() { ["gpu"] = "AMD Radeon*" }, DesktopHw));
    }

    [Fact]
    public void Matches_ModelManufacturer_CaseInsensitiveKeys()
    {
        Assert.True(DeviceDetector.Matches(new() { ["Model"] = "*Ally X*", ["MANUFACTURER"] = "ASUSTeK*" }, AllyHw));
        Assert.False(DeviceDetector.Matches(new() { ["model"] = "*Ally X*" }, DesktopHw));
    }

    [Fact]
    public void Matches_Machine_UsesMachineName()
    {
        Assert.True(DeviceDetector.Matches(new() { ["machine"] = Environment.MachineName }, DesktopHw));
        Assert.False(DeviceDetector.Matches(new() { ["hostname"] = "not-" + Environment.MachineName }, DesktopHw));
    }

    [Fact]
    public void Matches_UnknownKey_Throws() =>
        Assert.Throws<ConfigException>(() => DeviceDetector.Matches(new() { ["ram"] = "32GB" }, DesktopHw));

    [Fact]
    public void Matches_EmptyRules_True() => Assert.True(DeviceDetector.Matches(new(), DesktopHw));

    [Fact]
    public void Select_FirstMatchingDevice()
    {
        var d = DeviceDetector.Select(Devices(), DesktopHw, null);
        Assert.Equal("desktop", d.Name);
        Assert.Equal("nvidia", d.Driver);
        Assert.StartsWith("matched", d.Reason);
        Assert.Contains("gpu=NVIDIA*", d.Reason);

        var a = DeviceDetector.Select(Devices(), AllyHw, "");
        Assert.Equal("ally-x", a.Name);
        Assert.Equal("amd", a.Driver);
    }

    [Fact]
    public void Select_DeviceWithoutRules_NeverMatches()
    {
        var hw = new HardwareInfo { Gpus = ["Intel Arc A770"] };
        Assert.Throws<ConfigException>(() => DeviceDetector.Select(Devices(), hw, null));
    }

    [Fact]
    public void Select_Forced_WinsEvenIfUnknown()
    {
        var f = DeviceDetector.Select(Devices(), DesktopHw, "ally-x");
        Assert.Equal("ally-x", f.Name);
        Assert.Equal("forced", f.Reason);
        Assert.NotNull(f.Def);
        Assert.Equal("nvidia", f.Driver); // derived from the detected hardware

        var u = DeviceDetector.Select(Devices(), DesktopHw, "laptop");
        Assert.Equal("laptop", u.Name);
        Assert.Null(u.Def);
    }

    [Fact]
    public void Select_ExplicitDriver_Wins()
    {
        var devices = new DevicesFile
        {
            Devices = new() { ["desktop"] = new DeviceDef { Match = new() { ["gpu"] = "NVIDIA*" }, Driver = "NONE" } },
        };
        Assert.Equal("none", DeviceDetector.Select(devices, DesktopHw, null).Driver);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX", "amd")]
    [InlineData("Radeon 780M", "amd")]
    [InlineData("Intel(R) Arc(TM) Graphics", "none")]
    public void Driver_DerivedFromGpus(string gpu, string driver)
    {
        var devices = new DevicesFile { Devices = new() { ["any"] = new DeviceDef { Match = new() { ["gpu"] = "*" } } } };
        Assert.Equal(driver, DeviceDetector.Select(devices, new HardwareInfo { Gpus = [gpu] }, null).Driver);
    }

    [Theory]
    [InlineData("SudoMaker Virtual Display Adapter", true)]
    [InlineData("Microsoft Basic Display Adapter", true)]
    [InlineData("Parsec Virtual Display Adapter", true)]
    [InlineData("GlideX Display", true)]
    [InlineData("Microsoft Remote Display Adapter", true)]
    [InlineData("spacedesk Graphics Adapter", true)]
    [InlineData("IddSampleDriver Device", true)]
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    [InlineData("AMD Radeon(TM) 890M Graphics", false)]
    [InlineData("Intel(R) UHD Graphics 770", false)]
    public void IsVirtualAdapter(string name, bool expected) =>
        Assert.Equal(expected, DeviceDetector.IsVirtualAdapter(name));
}
