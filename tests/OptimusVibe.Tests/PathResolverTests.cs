using OptimusVibe.Config;

namespace OptimusVibe.Tests;

public sealed class PathResolverTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeSteam _steam = new();

    public void Dispose()
    {
        _tmp.Dispose();
        _steam.Dispose();
    }

    private PathResolver Resolver(Dictionary<string, string>? overrides = null) => new(_steam.Locator(), overrides);

    [Fact]
    public void Overrides_ReplaceTokens_CaseInsensitively()
    {
        var r = Resolver(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Documents"] = @"X:\Docs" });
        Assert.Equal(@"X:\Docs\My Games\Game\GameUserSettings.ini", r.Expand(@"{documents}\My Games\Game\GameUserSettings.ini"));
    }

    [Fact]
    public void BuiltInTokens_AreCaseInsensitive()
    {
        // default comparer when caller passes no dictionary: built-in tokens still resolve
        var r = Resolver();
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\x",
            r.Expand(@"{LocalAppData}\x"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\x", r.Expand(@"{DOCUMENTS}\x"));
    }

    [Fact]
    public void SteamLibrary_ResolvesInstallDir()
    {
        var r = Resolver();
        Assert.Equal(
            Path.Combine(_steam.Library2, "steamapps", "common", "Cyberpunk 2077") + @"\engine\config\user.ini",
            r.Expand(@"{SteamLibrary:1091500}\engine\config\user.ini"));
    }

    [Fact]
    public void SteamRoot_And_SteamUserData()
    {
        var ud = _steam.UserData(FakeSteam.RecentAccount, FakeSteam.AppId);
        var r = Resolver();
        Assert.Equal(_steam.Root + @"\config", r.Expand(@"{SteamRoot}\config"));
        Assert.Equal(ud + @"\remote\settings.cfg", r.Expand(@"{SteamUserData:1091500}\remote\settings.cfg"));
    }

    [Fact]
    public void SteamLibrary_NotInstalled_Throws()
    {
        var ex = Assert.Throws<PathResolveException>(() => Resolver().Expand(@"{SteamLibrary:42}\x"));
        Assert.Contains("42", ex.Message);
    }

    [Fact]
    public void SteamLibrary_WithoutAppId_Throws()
    {
        Assert.Throws<PathResolveException>(() => Resolver().Expand(@"{SteamLibrary}\x"));
        Assert.Throws<PathResolveException>(() => Resolver().Expand(@"{SteamLibrary:abc}\x"));
    }

    [Fact]
    public void UnknownToken_Throws()
    {
        var ex = Assert.Throws<PathResolveException>(() => Resolver().Expand(@"{Bogus}\x"));
        Assert.Contains("Bogus", ex.Message);
    }

    [Fact]
    public void EnvironmentVariables_AreExpanded()
    {
        var name = "GP_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, @"Y:\EnvDir");
        try
        {
            Assert.Equal(@"Y:\EnvDir\sub\file.ini", Resolver().Expand($@"%{name}%\sub\file.ini"));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void TokensIn_ListsTokenNames()
    {
        Assert.Equal(["SteamLibrary", "Documents"], PathResolver.TokensIn(@"{SteamLibrary:1091500}\{Documents}\x"));
        Assert.Contains("SteamUserData", PathResolver.KnownTokens);
    }

    [Fact]
    public void ResolveFile_NoWildcard_ReturnsFullPath_EvenIfMissing()
    {
        var r = Resolver(new(StringComparer.OrdinalIgnoreCase) { ["Documents"] = _tmp.Path });
        Assert.Equal(Path.Combine(_tmp.Path, "a", "b.ini"), r.ResolveFile("{Documents}/a/b.ini"));
    }

    [Fact]
    public void ResolveFile_Wildcard_PicksNewestMatch()
    {
        var older = Path.Combine(_tmp.Dir("Saved", "1111aaaa"), "settings.ini");
        var newer = Path.Combine(_tmp.Dir("Saved", "2222bbbb"), "settings.ini");
        var newestButNoFile = _tmp.Dir("Saved", "3333cccc");
        File.WriteAllText(older, "a");
        File.WriteAllText(newer, "b");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
        Directory.SetLastWriteTimeUtc(newestButNoFile, DateTime.UtcNow);

        var r = Resolver(new(StringComparer.OrdinalIgnoreCase) { ["Documents"] = _tmp.Path });
        Assert.Equal(newer, r.ResolveFile("{Documents}/Saved/*/settings.ini"));
    }

    [Fact]
    public void ResolveFile_WildcardInFileName_PicksNewest()
    {
        var a = _tmp.Write("Settings_v1.txt", "a");
        var b = _tmp.Write("Settings_v2.txt", "b");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddHours(-5));
        var r = Resolver(new(StringComparer.OrdinalIgnoreCase) { ["Documents"] = _tmp.Path });
        Assert.Equal(a, r.ResolveFile(@"{Documents}\Settings_v?.txt"));
    }

    [Fact]
    public void ResolveFile_WildcardWithoutMatch_IsNull()
    {
        var r = Resolver(new(StringComparer.OrdinalIgnoreCase) { ["Documents"] = _tmp.Path });
        Assert.Null(r.ResolveFile(@"{Documents}\Nothing\*\x.ini"));
    }
}
