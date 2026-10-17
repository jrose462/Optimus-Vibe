using OptimusVibe.Config;
using OptimusVibe.Library;

namespace OptimusVibe.Tests;

public sealed class SteamLaunchOptionsComposeTests
{
    private const string Exe = @"C:\Tools\Optimus-Vibe\OptimusVibe.exe";
    private const string W = "\"" + Exe + "\" run cyberpunk2077 -- %command%";
    private const string OldApp = "\"C:\\Old Place\\GameProfilesApp.exe\" run cyberpunk2077 -- %command%";
    private const string Cli = "\"C:\\Tools\\GameProfiles\\gameprofiles.exe\" run cyberpunk2077 --preset battery --no-prompt -- %command%";

    [Theory]
    [InlineData(null, W)]
    [InlineData("", W)]
    [InlineData("   ", W)]
    [InlineData("-dx12", W + " -dx12")]
    [InlineData("-dx12 -skipStartScreen", W + " -dx12 -skipStartScreen")]
    [InlineData("%command% -dx12", W + " -dx12")]
    [InlineData("FOO=1 %command%", "FOO=1 " + W)]
    [InlineData("FOO=1 %command% -dx12", "FOO=1 " + W + " -dx12")]
    [InlineData(W, W)]
    [InlineData(W + " -dx12", W + " -dx12")]
    [InlineData(OldApp, W)]
    [InlineData("\"D:\\Tools\\optimus-vibe.exe\" run cyberpunk2077 --preset battery -- %command%", W)]
    [InlineData(OldApp + " -dx12", W + " -dx12")]
    [InlineData("FOO=1 " + OldApp + " -dx12", "FOO=1 " + W + " -dx12")]
    [InlineData(Cli, W)]
    [InlineData(@"C:\Tools\OptimusVibe.exe run other -- %command%", W)]
    [InlineData("\"C:\\Old\\GameProfilesApp.exe\" run cyberpunk2077", W)]
    public void Compose(string? existing, string expected)
    {
        Assert.Equal(expected, SteamLaunchOptions.Compose(existing, Exe, "cyberpunk2077"));
        Assert.Equal(expected, SteamLaunchOptions.Compose(expected, Exe, "cyberpunk2077")); // idempotent
    }

    [Fact]
    public void Compose_QuotedExeArgument_AndGameIdWithSpace()
    {
        Assert.Equal("\"C:\\A B\\OptimusVibe.exe\" run \"my game\" -- %command%",
            SteamLaunchOptions.Compose(null, "\"C:\\A B\\OptimusVibe.exe\"", "my game"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("-dx12", "-dx12")]
    [InlineData(" %command% -dx12 ", "%command% -dx12")] // not ours: untouched apart from trimming
    [InlineData("FOO=1 %command%", "FOO=1 %command%")]
    [InlineData(W, null)]
    [InlineData(W + " -dx12", "-dx12")]
    [InlineData("FOO=1 " + W, "FOO=1 %command%")]
    [InlineData("FOO=1 " + W + " -dx12", "FOO=1 %command% -dx12")]
    [InlineData(OldApp, null)]
    [InlineData(OldApp + "   -dx12", "-dx12")]
    [InlineData(Cli + " -dx12", "-dx12")]
    [InlineData(@"c:\tools\OptimusVibe.exe RUN x -- %COMMAND% -vulkan", "-vulkan")]
    [InlineData("\"C:\\Old\\GameProfilesApp.exe\" run cyberpunk2077", null)]
    public void Strip(string? existing, string? expected) => Assert.Equal(expected, SteamLaunchOptions.Strip(existing));

    [Theory]
    [InlineData("-dx12")]
    [InlineData("FOO=1 %command% -dx12")]
    [InlineData(null)]
    public void Strip_UndoesCompose(string? original)
    {
        var composed = SteamLaunchOptions.Compose(original, Exe, "g");
        Assert.True(SteamLaunchOptions.IsWrapped(composed));
        Assert.Equal(original, SteamLaunchOptions.Strip(composed));
    }

    [Fact]
    public void WrappedDetails()
    {
        Assert.Contains(SteamLaunchOptions.Marker, W);
        Assert.True(SteamLaunchOptions.IsWrapped(Cli));
        Assert.False(SteamLaunchOptions.IsWrapped("-dx12 %command%"));
        Assert.False(SteamLaunchOptions.IsWrapped("\"C:\\x\\otherapp.exe\" run y -- %command%"));
        Assert.Equal("cyberpunk2077", SteamLaunchOptions.WrappedGameId("FOO=1 " + OldApp));
        Assert.Equal(@"C:\Old Place\GameProfilesApp.exe", SteamLaunchOptions.WrappedExe(OldApp));
        Assert.Null(SteamLaunchOptions.WrappedGameId("-dx12"));
    }
}

/// <summary>Plan/Apply against a fake Steam folder — never the real localconfig.vdf.</summary>
public sealed class SteamLaunchOptionsFileTests : IDisposable
{
    private const string Exe = @"C:\Tools\Optimus-Vibe\OptimusVibe.exe";
    private readonly FakeSteam _steam = new();
    private readonly TempDir _backups = new();
    private readonly string _account;

    public SteamLaunchOptionsFileTests()
    {
        _account = Path.Combine(_steam.Root, "userdata", FakeSteam.RecentAccount);
        WriteLocalConfig(Fixtures.Text("localconfig.vdf", "\n"));
        // an older account that must be ignored
        var other = Path.Combine(_steam.Root, "userdata", "1234", "config");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "localconfig.vdf"), "\"UserLocalConfigStore\"\n{\n}\n");
    }

    private string LocalConfig => SteamLaunchOptions.LocalConfigPath(_account);

    private void WriteLocalConfig(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalConfig)!);
        File.WriteAllText(LocalConfig, text);
    }

    public void Dispose()
    {
        _steam.Dispose();
        _backups.Dispose();
    }

    [Fact]
    public void AccountDir_IsMostRecentLogin()
    {
        Assert.Equal(_account, SteamLaunchOptions.AccountDir(_steam.Root));
    }

    [Fact]
    public void Get_ReadsLaunchOptions()
    {
        Assert.Equal("-skipStartScreen", SteamLaunchOptions.Get(1091500, _account));
        Assert.Equal("", SteamLaunchOptions.Get(570, _account));
        Assert.Null(SteamLaunchOptions.Get(42, _account));
        var all = SteamLaunchOptions.GetAll(_account);
        Assert.Equal(2, all.Count);
        Assert.True(SteamLaunchOptions.IsWrapped(all[1245620]));
    }

    [Fact]
    public void Plan_EnablesDisablesAndSkipsNoOps()
    {
        var changes = SteamLaunchOptions.Plan(
            [(1091500, "cyberpunk2077"), (570, "dota2"), (999, "newgame")],
            [1245620, 42, 570],
            Exe, _account);

        Assert.Equal(4, changes.Count);
        var cp = changes.Single(c => c.AppId == 1091500);
        Assert.Equal("-skipStartScreen", cp.Before);
        Assert.Equal($"\"{Exe}\" run cyberpunk2077 -- %command% -skipStartScreen", cp.After);
        var dota = changes.Single(c => c.AppId == 570); // in both lists: enable wins
        Assert.Null(dota.Before);
        Assert.Equal($"\"{Exe}\" run dota2 -- %command%", dota.After);
        Assert.Equal($"\"{Exe}\" run newgame -- %command%", changes.Single(c => c.AppId == 999).After);
        var elden = changes.Single(c => c.AppId == 1245620);
        Assert.Equal("eldenring", elden.GameId);
        Assert.Null(elden.After);

        // already wrapped with this exe → nothing to do
        var again = SteamLaunchOptions.Plan([(1245620, "eldenring")], [], @"C:\Old Place\GameProfilesApp.exe", _account);
        Assert.Empty(again);
    }

    [Fact]
    public void Apply_RefusedWhileSteamRuns()
    {
        var before = File.ReadAllText(LocalConfig);
        var changes = SteamLaunchOptions.Plan([(1091500, "cyberpunk2077")], [], Exe, _account);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SteamLaunchOptions.Apply(changes, _account, isSteamRunning: () => true, backupDir: _backups.Path));
        Assert.Contains("Steam is running", ex.Message);
        Assert.Equal(before, File.ReadAllText(LocalConfig));
        Assert.Empty(Directory.GetFiles(_backups.Path));
    }

    [Fact]
    public void Apply_WritesBacksUpAndKeepsEverythingElse()
    {
        var original = File.ReadAllText(LocalConfig);
        var changes = SteamLaunchOptions.Plan([(1091500, "cyberpunk2077"), (999, "newgame")], [1245620], Exe, _account);
        var backup = SteamLaunchOptions.Apply(changes, _account, isSteamRunning: () => false, backupDir: _backups.Path);

        Assert.NotNull(backup);
        Assert.StartsWith(Path.Combine(_backups.Path, "localconfig-"), backup);
        Assert.Equal(original, File.ReadAllText(backup!));

        Assert.Equal($"\"{Exe}\" run cyberpunk2077 -- %command% -skipStartScreen", SteamLaunchOptions.Get(1091500, _account));
        Assert.Equal($"\"{Exe}\" run newgame -- %command%", SteamLaunchOptions.Get(999, _account));
        Assert.Equal("", SteamLaunchOptions.Get(1245620, _account));

        var text = File.ReadAllText(LocalConfig);
        Assert.DoesNotContain("\r\n", text);
        Assert.Contains("\"Path\"\t\t\"D:\\\\SteamLibrary\\\\steamapps\\\\common\"", text);
        Assert.Contains("\"PersonaName\"\t\t\"Player \\\"One\\\"\"", text);
        Assert.Equal(original.Split('\n').Length + 4, text.Split('\n').Length); // only the new app block added
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(LocalConfig)!, "*.tmp"));

        Assert.Empty(SteamLaunchOptions.Plan([(1091500, "cyberpunk2077"), (999, "newgame")], [1245620], Exe, _account));
    }

    [Fact]
    public void Apply_SubsetOfChanges_LeavesTheOthersUntouched()
    {
        var changes = SteamLaunchOptions.Plan([(1091500, "cyberpunk2077"), (999, "newgame")], [1245620], Exe, _account);
        Assert.Equal(3, changes.Count);
        var picked = changes.Where(c => c.AppId == 999).ToList();

        SteamLaunchOptions.Apply(picked, _account, isSteamRunning: () => false, backupDir: _backups.Path);

        Assert.Equal($"\"{Exe}\" run newgame -- %command%", SteamLaunchOptions.Get(999, _account));
        Assert.Equal("-skipStartScreen", SteamLaunchOptions.Get(1091500, _account)); // not picked: unchanged
        Assert.True(SteamLaunchOptions.IsWrapped(SteamLaunchOptions.Get(1245620, _account))); // not picked: still wrapped
        // the unpicked changes are still pending
        Assert.Equal([1091500, 1245620], SteamLaunchOptions.Plan([(1091500, "cyberpunk2077"), (999, "newgame")], [1245620], Exe, _account)
            .Select(c => c.AppId).Order());
    }

    [Fact]
    public void Apply_KeepsLastTenBackups()
    {
        for (int i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(_backups.Path, $"localconfig-2000010{i % 10}-00000{i / 10}-000.vdf"), "old");
        File.WriteAllText(Path.Combine(_backups.Path, "unrelated.txt"), "x");

        var backup = SteamLaunchOptions.Apply(
            SteamLaunchOptions.Plan([(570, "dota2")], [], Exe, _account), _account, () => false, _backups.Path);

        var kept = Directory.GetFiles(_backups.Path, "localconfig-*.vdf");
        Assert.Equal(SteamLaunchOptions.BackupsKept, kept.Length);
        Assert.Contains(backup, kept);
        Assert.True(File.Exists(Path.Combine(_backups.Path, "unrelated.txt")));
    }

    [Fact]
    public void Apply_CapitalAppsKey_IsReusedNotDuplicated()
    {
        WriteLocalConfig(Fixtures.Text("localconfig.vdf", "\n").Replace("\"apps\"", "\"Apps\""));
        SteamLaunchOptions.Apply(SteamLaunchOptions.Plan([(999, "newgame")], [], Exe, _account), _account, () => false, _backups.Path);
        var text = File.ReadAllText(LocalConfig);
        Assert.Contains("\"Apps\"", text);
        Assert.DoesNotContain("\"apps\"", text);
        Assert.Equal($"\"{Exe}\" run newgame -- %command%", SteamLaunchOptions.Get(999, _account));
    }

    [Fact]
    public void Apply_CreatesAppsWhenMissing_AndKeepsCrLf()
    {
        WriteLocalConfig("\"UserLocalConfigStore\"\r\n{\r\n\t\"friends\"\r\n\t{\r\n\t}\r\n}\r\n");
        SteamLaunchOptions.Apply(SteamLaunchOptions.Plan([(10, "g")], [], Exe, _account), _account, () => false, _backups.Path);
        Assert.Equal($"\"{Exe}\" run g -- %command%", SteamLaunchOptions.Get(10, _account));
        var text = File.ReadAllText(LocalConfig);
        Assert.Contains("\t\"Software\"\r\n\t{\r\n\t\t\"Valve\"\r\n\t\t{\r\n\t\t\t\"Steam\"\r\n\t\t\t{\r\n\t\t\t\t\"apps\"\r\n", text);
    }

    [Fact]
    public void Apply_NothingToDo_WritesNothing()
    {
        Assert.Null(SteamLaunchOptions.Apply([], _account, () => true, _backups.Path));
        Assert.Empty(Directory.GetFiles(_backups.Path));
    }
}

public sealed class SteamManagedTargetsTests
{
    [Fact]
    public void ManagedTargets_UsesPresetsForDeviceAndOptOuts()
    {
        using var fx = new EngineFixture();
        void Game(string id, int appId, string device) => File.WriteAllText(Path.Combine(fx.Paths.GamesDir, id + ".yaml"),
            $"name: {id}\nsteamAppId: {appId}\npresets:\n  {device}:\n    quality:\n      label: Q\n");
        Game("alpha", 111, "desktop");
        Game("beta", 222, "desktop");     // opted out by key
        Game("gamma", 333, "ally-x");     // no presets for desktop → untouched
        Game("delta", 444, "desktop");    // opted out by game id
        File.WriteAllText(fx.Paths.LibraryFile, "optOut:\n  - steam:222\n  - STEAM:555\n  - delta\n  - battlenet:Fen\n");

        var (enable, disable) = SteamLaunchOptions.ManagedTargets(fx.Store, "desktop");

        Assert.Equal(new[] { (111, "alpha") }, enable.ToArray());
        Assert.Equal(new[] { 222, 444, 555 }, disable.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ManagedTargets_ExcludesSkippedFromEnableAndDisable()
    {
        using var fx = new EngineFixture();
        void Game(string id, int appId) => File.WriteAllText(Path.Combine(fx.Paths.GamesDir, id + ".yaml"),
            $"name: {id}\nsteamAppId: {appId}\npresets:\n  desktop:\n    quality:\n      label: Q\n");
        Game("alpha", 111);   // managed
        Game("beta", 222);    // managed but skipped: never enabled
        Game("gamma", 333);   // opted out and skipped: never disabled either
        Game("delta", 444);   // opted out: disabled
        Game("eps", 555);     // skipped by game id
        File.WriteAllText(fx.Paths.LibraryFile,
            "optOut:\n  - steam:333\n  - steam:444\n  - steam:666\n  - steam:777\nsteamLaunchOptionsSkip:\n  - steam:222\n  - STEAM:333\n  - eps\n  - steam:777\n");

        var (enable, disable) = SteamLaunchOptions.ManagedTargets(fx.Store, "desktop");

        Assert.Equal(new[] { (111, "alpha") }, enable.ToArray());
        Assert.Equal(new[] { 444, 666 }, disable.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ManagedTargets_NoLibraryFile()
    {
        using var fx = new EngineFixture();
        File.WriteAllText(Path.Combine(fx.Paths.GamesDir, "alpha.yaml"), "name: A\nsteamAppId: 111\npresets:\n  desktop:\n    quality:\n      label: Q\n");
        var (enable, disable) = SteamLaunchOptions.ManagedTargets(fx.Store, "desktop");
        Assert.Single(enable);
        Assert.Empty(disable);
    }
}
