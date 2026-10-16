using GameProfiles.Config;

namespace GameProfiles.Tests;

public sealed class ValuesTests
{
    [Theory]
    [InlineData("1", "1.000000", true)]
    [InlineData("0.5", "0.500000", true)]
    [InlineData("60", "60.0", true)]
    [InlineData("1e3", "1000", true)]
    [InlineData(" 3 ", "3", true)]
    [InlineData("True", "true", true)]
    [InlineData("FALSE", "false", true)]
    [InlineData("true", "false", false)]
    [InlineData("High", "High", true)]
    [InlineData("High", "high", false)]
    [InlineData("1", "2", false)]
    [InlineData("1", "1.0001", false)]
    [InlineData("1", "true", false)]
    [InlineData("", "", true)]
    [InlineData("", "0", false)]
    [InlineData("1920x1080", "1920x1080", true)]
    public void Equivalent(string a, string b, bool expected)
    {
        Assert.Equal(expected, Values.Equivalent(a, b));
        Assert.Equal(expected, Values.Equivalent(b, a));
    }

    [Fact]
    public void Equivalent_Nulls()
    {
        Assert.True(Values.Equivalent(null, null));
        Assert.False(Values.Equivalent(null, ""));
        Assert.False(Values.Equivalent("x", null));
    }

    [Fact]
    public void ToScalar_UsesInvariantFormatting()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", Values.ToScalar(1.5));
            Assert.Equal("true", Values.ToScalar(true));
            Assert.Equal("false", Values.ToScalar(false));
            Assert.Equal("", Values.ToScalar(null));
            Assert.Equal("42", Values.ToScalar(42));
            Assert.Equal("abc", Values.ToScalar("abc"));
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }
}
