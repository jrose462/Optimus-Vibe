using OptimusVibe.Config;

namespace OptimusVibe.Tests;

public sealed class PresetRefTests
{
    [Theory]
    [InlineData("recommended:battery", PresetKind.Recommended, "battery")]
    [InlineData("preset:battery", PresetKind.Recommended, "battery")]
    [InlineData("r:battery", PresetKind.Recommended, "battery")]
    [InlineData("Recommended:Quality", PresetKind.Recommended, "Quality")]
    [InlineData("manual:steady-40", PresetKind.Manual, "steady-40")]
    [InlineData("save:steady-40", PresetKind.Manual, "steady-40")]
    [InlineData("m:steady-40", PresetKind.Manual, "steady-40")]
    [InlineData("MANUAL:steady-40", PresetKind.Manual, "steady-40")]
    [InlineData(" manual : steady-40 ", PresetKind.Manual, "steady-40")]
    [InlineData("performance", PresetKind.Recommended, "performance")]
    [InlineData("  performance  ", PresetKind.Recommended, "performance")]
    [InlineData("manual:a:b", PresetKind.Manual, "a:b")]
    public void Parse(string value, PresetKind kind, string name)
    {
        var r = PresetRef.Parse(value);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(name, r.Name);
    }

    [Theory]
    [InlineData("bogus:x")]
    [InlineData("auto:quality")]
    [InlineData(":quality")]
    public void Parse_UnknownKind_Throws(string value)
    {
        var ex = Assert.Throws<ConfigException>(() => PresetRef.Parse(value));
        Assert.Contains("recommended:", ex.Message);
    }

    [Fact]
    public void ToString_UsesCanonicalKind()
    {
        Assert.Equal("recommended:battery", PresetRef.Recommended("battery").ToString());
        Assert.Equal("manual:steady-40", PresetRef.Manual("steady-40").ToString());
        Assert.Equal("recommended:x", PresetRef.Parse("r:x").ToString());
        Assert.Equal("manual:x", PresetRef.Parse("save:x").ToString());
    }

    [Theory]
    [InlineData("recommended:quality")]
    [InlineData("manual:my-save-2")]
    public void RoundTrip(string value) => Assert.Equal(value, PresetRef.Parse(value).ToString());

    [Fact]
    public void ValueEquality()
    {
        Assert.Equal(PresetRef.Manual("a"), PresetRef.Parse("manual:a"));
        Assert.NotEqual(PresetRef.Manual("a"), PresetRef.Recommended("a"));
        Assert.True(PresetRef.Recommended("q") == PresetRef.Parse("q"));
    }
}
