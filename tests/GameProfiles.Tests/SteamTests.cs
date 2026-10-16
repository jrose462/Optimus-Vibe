using GameProfiles.Config;

namespace GameProfiles.Tests;

/// <summary>A fake Steam install: root library + a second library holding Cyberpunk (1091500).</summary>
public sealed class FakeSteam : IDisposable
{
    public const int AppId = 1091500;
    public const string RecentAccount = "39734273"; // 76561198000000001 - 76561197960265728
    private readonly TempDir _tmp = new();

    public string Root { get; }
    public string Library2 { get; }

    public FakeSteam(bool withLoginUsers = true)
    {
        Root = _tmp.Dir("Steam");
        Library2 = _tmp.Dir("SteamLibrary");
        var vdf = Fixtures.Text("libraryfolders.vdf")
            .Replace("__ROOT__", Root.Replace("\\", "\\\\"))
            .Replace("__LIB2__", Library2.Replace("\\", "\\\\"));
        _tmp.Write(Path.Combine("Steam", "steamapps", "libraryfolders.vdf"), vdf);
        _tmp.Write(Path.Combine("SteamLibrary", "steamapps", $"appmanifest_{AppId}.acf"), Fixtures.Text("appmanifest_1091500.acf"));
        Directory.CreateDirectory(Path.Combine(Library2, "steamapps", "common", "Cyberpunk 2077"));
        if (withLoginUsers)
            _tmp.Write(Path.Combine("Steam", "config", "loginusers.vdf"), Fixtures.Text("loginusers.vdf"));
    }

    public string UserData(string account, int appId) =>
        Directory.CreateDirectory(Path.Combine(Root, "userdata", account, appId.ToString())).FullName;

    public SteamLocator Locator() => new(Root);

    public void Dispose() => _tmp.Dispose();
}

public sealed class VdfTests
{
    [Fact]
    public void Parse_LibraryFolders()
    {
        var text = Fixtures.Text("libraryfolders.vdf").Replace("__ROOT__", "C:\\\\Program Files (x86)\\\\Steam").Replace("__LIB2__", "D:\\\\SteamLibrary");
        var root = Vdf.Parse(text).Child("libraryfolders")!;
        Assert.Equal(2, root.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", root.Child("0")!.Str("path"));
        Assert.Equal(@"D:\SteamLibrary", root.Child("1")!.Str("path"));
        Assert.Equal("", root.Child("0")!.Str("label"));
        Assert.Equal("70000000000", root.Child("1")!.Child("apps")!.Str("1091500"));
    }

    [Fact]
    public void Parse_AppManifest_CaseInsensitiveKeys()
    {
        var app = Vdf.Parse(Fixtures.Text("appmanifest_1091500.acf")).Child("appstate")!;
        Assert.Equal("Cyberpunk 2077", app.Str("installdir"));
        Assert.Equal("Cyberpunk 2077", app.Str("InstallDir"));
        Assert.Equal("english", app.Child("UserConfig")!.Str("language"));
        Assert.Null(app.Str("UserConfig"));   // object, not string
        Assert.Null(app.Child("installdir")); // string, not object
        Assert.Null(app.Str("missing"));
    }

    [Fact]
    public void Parse_EscapedQuotes_Comments_UnquotedTokens()
    {
        var users = Vdf.Parse(Fixtures.Text("loginusers.vdf")).Child("users")!;
        Assert.Equal("Player \"One\"", users.Child("76561198000000001")!.Str("PersonaName"));

        var d = Vdf.Parse("// header\nroot { key value // trailing\n nested { a \"b\" } }\n");
        Assert.Equal("value", d.Child("root")!.Str("key"));
        Assert.Equal("b", d.Child("root")!.Child("nested")!.Str("a"));
    }

    [Fact]
    public void Parse_Empty_And_Truncated()
    {
        Assert.Empty(Vdf.Parse(""));
        var d = Vdf.Parse("\"a\" { \"b\" \"c\"");
        Assert.Equal("c", d.Child("a")!.Str("b"));
    }
}

public sealed class SteamLocatorTests
{
    [Fact]
    public void Libraries_IncludesRootAndSecondLibraryOnce()
    {
        using var steam = new FakeSteam();
        var libs = steam.Locator().Libraries().ToList();
        Assert.Equal([steam.Root, steam.Library2], libs);
    }

    [Fact]
    public void InstallDir_FindsAppInSecondLibrary()
    {
        using var steam = new FakeSteam();
        Assert.Equal(Path.Combine(steam.Library2, "steamapps", "common", "Cyberpunk 2077"), steam.Locator().InstallDir(FakeSteam.AppId));
    }

    [Fact]
    public void InstallDir_UnknownApp_IsNull()
    {
        using var steam = new FakeSteam();
        Assert.Null(steam.Locator().InstallDir(12345));
    }

    [Fact]
    public void UserDataDir_UsesMostRecentAccount()
    {
        using var steam = new FakeSteam();
        var recent = steam.UserData(FakeSteam.RecentAccount, FakeSteam.AppId);
        var other = steam.UserData("39734274", FakeSteam.AppId);
        Directory.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-10));
        Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow); // newer, but not the most recent login
        Assert.Equal(recent, steam.Locator().UserDataDir(FakeSteam.AppId));
    }

    [Fact]
    public void UserDataDir_FallsBackToNewestFolder_WithoutLoginUsers()
    {
        using var steam = new FakeSteam(withLoginUsers: false);
        var older = steam.UserData("111", FakeSteam.AppId);
        var newer = steam.UserData("222", FakeSteam.AppId);
        Directory.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-10));
        Directory.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(newer, steam.Locator().UserDataDir(FakeSteam.AppId));
    }

    [Fact]
    public void UserDataDir_FallsBack_WhenRecentAccountHasNoFolderForApp()
    {
        using var steam = new FakeSteam();
        steam.UserData(FakeSteam.RecentAccount, 999);
        var other = steam.UserData("222", FakeSteam.AppId);
        Assert.Equal(other, steam.Locator().UserDataDir(FakeSteam.AppId));
    }

    [Fact]
    public void UserDataDir_NoUserdata_IsNull()
    {
        using var steam = new FakeSteam();
        Assert.Null(steam.Locator().UserDataDir(FakeSteam.AppId));
    }

    [Fact]
    public void MissingRoot_YieldsNothing()
    {
        using var tmp = new TempDir();
        var locator = new SteamLocator(tmp.File("no-steam"));
        Assert.Null(locator.InstallDir(FakeSteam.AppId));
        Assert.Null(locator.UserDataDir(FakeSteam.AppId));
        Assert.Single(locator.Libraries()); // root itself, no vdf
    }
}
