using System.Text;
using GameProfiles.Config;
using GameProfiles.Library;
using GameProfiles.Library.Scanners;

namespace GameProfiles.Tests;

/// <summary>In-memory registry: keys are case-insensitive "HKLM\..." paths.</summary>
internal sealed class FakeRegistry : IRegistryReader
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistry Set(string key, string name, string value)
    {
        if (!_keys.TryGetValue(key, out var values)) _keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
        values[name] = value;
        return this;
    }

    public IReadOnlyList<string> SubKeys(string key)
    {
        var prefix = key.TrimEnd('\\') + "\\";
        return _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? Value(string key, string name) =>
        _keys.TryGetValue(key, out var values) && values.TryGetValue(name, out var v) ? v : null;
}

internal static class FakeFiles
{
    public static string Touch(string path, int size = 16)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public static string Manifest(int appId, string name, string installDir, int stateFlags = 4) => $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"name"		"{{name}}"
        	"StateFlags"		"{{stateFlags}}"
        	"installdir"		"{{installDir}}"
        }
        """;
}

public sealed class SteamScannerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _root;
    private readonly string _lib2;

    public SteamScannerTests()
    {
        _root = _tmp.Dir("Steam");
        _lib2 = _tmp.Dir("Lib2");
        _tmp.Write(@"Steam\steamapps\libraryfolders.vdf", $$"""
            "libraryfolders"
            {
            	"0" { "path" "{{_root.Replace("\\", "\\\\")}}" }
            	"1" { "path" "{{_lib2.Replace("\\", "\\\\")}}" }
            }
            """);
    }

    private void App(string lib, int appId, string name, string dir, int flags = 4)
    {
        File.WriteAllText(Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf"), FakeFiles.Manifest(appId, name, dir, flags));
        Directory.CreateDirectory(Path.Combine(lib, "steamapps", "common", dir));
    }

    private List<DetectedGame> Scan() => new SteamScanner(new SteamLocator(_root)).Scan().ToList();

    [Fact]
    public void FindsGamesInAllLibraries_SkipsToolsAndUninstalled()
    {
        Directory.CreateDirectory(Path.Combine(_lib2, "steamapps"));
        App(_root, 1285190, "Borderlands® 4", "Borderlands 4");
        App(_lib2, 1774580, "STAR WARS Jedi: Survivor™", "Jedi Survivor", flags: 6); // installed + update pending
        App(_root, 228980, "Steamworks Common Redistributables", "Steamworks Shared");
        App(_root, 250820, "SteamVR", "SteamVR");
        App(_root, 1493710, "Proton Experimental", "Proton - Experimental");
        App(_root, 1070560, "Steam Linux Runtime 1.0 (scout)", "SteamLinuxRuntime");
        App(_root, 999, "Half Downloaded", "Half", flags: 1026); // update required, not fully installed
        App(_root, 555, "Some Redistributable Pack", "Redist");

        var games = Scan();
        Assert.Equal(["1285190", "1774580"], games.Select(g => g.StoreId).Order());
        var jedi = games.Single(g => g.StoreId == "1774580");
        Assert.Equal("steam", jedi.Store);
        Assert.Equal("steam:1774580", jedi.Key);
        Assert.Equal("STAR WARS Jedi: Survivor™", jedi.Name);
        Assert.Equal("steam://rungameid/1774580", jedi.LaunchUri);
        Assert.Equal(Path.Combine(_lib2, "steamapps", "common", "Jedi Survivor"), jedi.InstallDir);
    }

    [Fact]
    public void Exes_TopLevelAndBinaries_WithoutHelpers()
    {
        App(_root, 1285190, "Borderlands 4", "Borderlands 4");
        var dir = Path.Combine(_root, "steamapps", "common", "Borderlands 4");
        FakeFiles.Touch(Path.Combine(dir, "Borderlands4.exe"), 100);
        FakeFiles.Touch(Path.Combine(dir, "OakGame", "Binaries", "Win64", "Borderlands4.exe"), 5000);
        FakeFiles.Touch(Path.Combine(dir, "OakGame", "Binaries", "Win64", "Oak-Win64-Shipping.exe"), 9000);
        FakeFiles.Touch(Path.Combine(dir, "Engine", "Binaries", "Win64", "CrashReportClient.exe"));
        FakeFiles.Touch(Path.Combine(dir, "UnityCrashHandler64.exe"));
        FakeFiles.Touch(Path.Combine(dir, "vcredist_x64.exe"));
        FakeFiles.Touch(Path.Combine(dir, "EasyAntiCheat_EOS_Setup.exe"));
        FakeFiles.Touch(Path.Combine(dir, "__Installer", "directx", "DXSETUP.exe"));
        FakeFiles.Touch(Path.Combine(dir, "GameLauncher.exe"), 99999);

        var exes = Scan().Single().Exes;
        Assert.Equal(["Oak-Win64-Shipping.exe", "Borderlands4.exe", "GameLauncher.exe"], exes);
    }

    [Fact]
    public void Art_OldFlatLayout()
    {
        App(_root, 10, "Old", "Old");
        var cache = _tmp.Dir(@"Steam\appcache\librarycache");
        FakeFiles.Touch(Path.Combine(cache, "10_header.jpg"));
        var portrait = FakeFiles.Touch(Path.Combine(cache, "10_library_600x900.jpg"));
        FakeFiles.Touch(Path.Combine(cache, "100_library_600x900.jpg")); // other app with a longer id
        Assert.Equal(portrait, Scan().Single().ArtPath);
    }

    [Fact]
    public void Art_NewFolderLayout_DirectAndHashed()
    {
        App(_root, 1774580, "Jedi", "Jedi Survivor");
        App(_root, 1285190, "BL4", "Borderlands 4");
        App(_root, 4126040, "Aniimo", "Aniimo");
        App(_root, 42, "NoPortrait", "NoPortrait");
        var cache = _tmp.Dir(@"Steam\appcache\librarycache");
        FakeFiles.Touch(Path.Combine(cache, "1774580", "header.jpg"));
        var jedi = FakeFiles.Touch(Path.Combine(cache, "1774580", "library_600x900.jpg"));
        FakeFiles.Touch(Path.Combine(cache, "1285190", "986a88d1", "library_header.jpg"));
        var bl4 = FakeFiles.Touch(Path.Combine(cache, "1285190", "e68c1eea", "library_600x900.jpg"));
        var aniimo = FakeFiles.Touch(Path.Combine(cache, "4126040", "d57cd75a", "library_capsule.jpg"));
        FakeFiles.Touch(Path.Combine(cache, "4126040", "7094df90", "library_hero.jpg"));
        var header = FakeFiles.Touch(Path.Combine(cache, "42", "header.jpg"));

        var byId = Scan().ToDictionary(g => g.StoreId);
        Assert.Equal(jedi, byId["1774580"].ArtPath);
        Assert.Equal(bl4, byId["1285190"].ArtPath);
        Assert.Equal(aniimo, byId["4126040"].ArtPath);
        Assert.Equal(header, byId["42"].ArtPath);
    }

    [Fact]
    public void BrokenManifest_IsSkipped_NotThrown()
    {
        App(_root, 1, "Good", "Good");
        File.WriteAllText(Path.Combine(_root, "steamapps", "appmanifest_2.acf"), "\"AppState\" { \"appid\" \"notanumber\" }");
        Assert.Single(Scan());
    }

    [Fact]
    public void MissingSteam_YieldsNothing()
    {
        Assert.Empty(new SteamScanner(new SteamLocator(_tmp.File("nope"))).Scan());
    }

    public void Dispose() => _tmp.Dispose();
}

public sealed class EpicScannerTests : IDisposable
{
    private readonly TempDir _tmp = new();

    private void Item(string file, string json) => _tmp.Write(Path.Combine("Manifests", file), json);

    [Fact]
    public void ReadsGames_SkipsEnginesPluginsIncompleteAndDlc()
    {
        var hogwarts = _tmp.Dir("Games", "HogwartsLegacy");
        FakeFiles.Touch(Path.Combine(hogwarts, "HogwartsLegacy.exe"));
        var fortnite = _tmp.Dir("Games", "Fortnite");
        FakeFiles.Touch(Path.Combine(fortnite, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe"), 500);
        FakeFiles.Touch(Path.Combine(fortnite, "FortniteGame", "Binaries", "Win64", "FortniteLauncher.exe"), 10);
        string Loc(string p) => p.Replace("\\", "/");

        Item("a.item", $$"""
            { "bIsIncompleteInstall": false, "LaunchExecutable": "HogwartsLegacy.exe", "DisplayName": "Hogwarts Legacy",
              "InstallLocation": "{{Loc(hogwarts)}}", "CatalogNamespace": "e97659b5", "CatalogItemId": "864c7bc2",
              "AppName": "fa4240e5", "MainGameAppName": "fa4240e5", "AppCategories": ["public", "games", "applications"] }
            """);
        Item("b.item", $$"""
            { "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe", "DisplayName": "Fortnite",
              "InstallLocation": "{{Loc(fortnite)}}", "CatalogNamespace": "fn", "CatalogItemId": "4fe75bbc",
              "AppName": "Fortnite", "AppCategories": ["games", "applications"] }
            """);
        Item("engine.item", $$"""
            { "LaunchExecutable": "Engine/Binaries/Win64/UnrealEditor.exe", "DisplayName": "Unreal Engine 5.4",
              "InstallLocation": "{{Loc(hogwarts)}}", "AppName": "UE_5.4", "AppCategories": ["engines"] }
            """);
        Item("plugin.item", """{ "LaunchExecutable": "", "DisplayName": "Some Plugin", "AppName": "plug", "AppCategories": ["plugins"] }""");
        Item("partial.item", """{ "bIsIncompleteInstall": true, "LaunchExecutable": "x.exe", "DisplayName": "Partial", "AppName": "p" }""");
        Item("dlc.item", $$"""
            { "LaunchExecutable": "x.exe", "DisplayName": "DLC", "AppName": "dlc1", "MainGameAppName": "fa4240e5", "InstallLocation": "{{Loc(hogwarts)}}" }
            """);
        Item("broken.item", "{ not json");

        var games = new EpicScanner(_tmp.File("Manifests")).Scan().OrderBy(g => g.Name).ToList();
        Assert.Equal(["Fortnite", "Hogwarts Legacy"], games.Select(g => g.Name));
        var fn = games[0];
        Assert.Equal("epic:Fortnite", fn.Key);
        Assert.Equal("com.epicgames.launcher://apps/fn%3A4fe75bbc%3AFortnite?action=launch&silent=true", fn.LaunchUri);
        Assert.Equal(fortnite, fn.InstallDir);
        Assert.Equal("FortniteLauncher.exe", fn.Exes[0]); // the manifest's launch exe first
        Assert.Contains("FortniteClient-Win64-Shipping.exe", fn.Exes);
        Assert.Equal("fa4240e5", games[1].StoreId);
    }

    [Fact]
    public void MissingFolder_YieldsNothing() => Assert.Empty(new EpicScanner(_tmp.File("none")).Scan());

    public void Dispose() => _tmp.Dispose();
}

public sealed class GogScannerTests : IDisposable
{
    private readonly TempDir _tmp = new();

    [Fact]
    public void ReadsRegistry_LaunchesExeDirectly_SkipsDlcAndMissing()
    {
        var dir = _tmp.Dir("GOG Games", "The Witcher 3");
        var exe = FakeFiles.Touch(Path.Combine(dir, "bin", "x64", "witcher3.exe"));
        var reg = new FakeRegistry()
            .Set($@"{GogScanner.GamesKey}\1207664663", "gameName", "The Witcher 3: Wild Hunt")
            .Set($@"{GogScanner.GamesKey}\1207664663", "gameID", "1207664663")
            .Set($@"{GogScanner.GamesKey}\1207664663", "path", dir)
            .Set($@"{GogScanner.GamesKey}\1207664663", "exe", exe)
            .Set($@"{GogScanner.GamesKey}\1207664663", "launchParam", "-dx12 \"-name x\"")
            .Set($@"{GogScanner.GamesKey}\1640424747", "gameName", "Blood and Wine")
            .Set($@"{GogScanner.GamesKey}\1640424747", "dependsOn", "1207664663")
            .Set($@"{GogScanner.GamesKey}\1640424747", "path", dir)
            .Set($@"{GogScanner.GamesKey}\111", "gameName", "Gone")
            .Set($@"{GogScanner.GamesKey}\111", "path", _tmp.File("GOG Games", "Gone"));

        var g = Assert.Single(new GogScanner(reg).Scan());
        Assert.Equal("gog:1207664663", g.Key);
        Assert.Equal("The Witcher 3: Wild Hunt", g.Name);
        Assert.Equal(exe, g.LaunchExe);
        Assert.Equal(["-dx12", "-name x"], g.LaunchArgs);
        Assert.Null(g.LaunchUri);
        Assert.Equal("witcher3.exe", g.Exes[0]);
    }

    [Fact]
    public void RelativeExe_IsResolvedAgainstPath()
    {
        var dir = _tmp.Dir("Game");
        var reg = new FakeRegistry()
            .Set($@"{GogScanner.GamesKey}\5", "gameName", "Five")
            .Set($@"{GogScanner.GamesKey}\5", "path", dir)
            .Set($@"{GogScanner.GamesKey}\5", "exe", "five.exe");
        Assert.Equal(Path.Combine(dir, "five.exe"), Assert.Single(new GogScanner(reg).Scan()).LaunchExe);
    }

    [Fact]
    public void NoGog_YieldsNothing() => Assert.Empty(new GogScanner(new FakeRegistry()).Scan());

    public void Dispose() => _tmp.Dispose();
}

public sealed class BattleNetScannerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private const string Uninstall = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    // ---- tiny protobuf writer for fake product.db files
    private static byte[] Varint(ulong v)
    {
        var list = new List<byte>();
        do { var b = (byte)(v & 0x7F); v >>= 7; list.Add(v != 0 ? (byte)(b | 0x80) : b); } while (v != 0);
        return [.. list];
    }

    private static byte[] Bytes(int field, byte[] data) => [.. Varint((ulong)(field << 3 | 2)), .. Varint((ulong)data.Length), .. data];
    private static byte[] Str(int field, string s) => Bytes(field, Encoding.UTF8.GetBytes(s));
    private static byte[] Int(int field, ulong v) => [.. Varint((ulong)(field << 3)), .. Varint(v)];

    private static byte[] Install(string uid, string code, string path, bool installed) => Bytes(1,
    [
        .. Str(1, uid), .. Str(2, code), .. Bytes(3, [.. Str(1, path), .. Str(2, "us")]),
        .. Bytes(4, Bytes(1, [.. Int(1, installed ? 1UL : 0UL), .. Int(2, 1)])),
    ]);

    [Fact]
    public void ProductDb_And_Registry_Merge()
    {
        var d4 = _tmp.Dir("Diablo IV");
        FakeFiles.Touch(Path.Combine(d4, "Diablo IV.exe"), 1000);
        FakeFiles.Touch(Path.Combine(d4, "Diablo IV Launcher.exe"), 5000);
        FakeFiles.Touch(Path.Combine(d4, "BlizzardError", "BlizzardError.exe"));
        var ow = _tmp.Dir("Overwatch");
        FakeFiles.Touch(Path.Combine(ow, "_retail_", "Overwatch.exe"), 1000);
        var bnetDir = _tmp.Dir("Battle.net");
        var bnetExe = FakeFiles.Touch(Path.Combine(bnetDir, "Battle.net.exe"));
        var wow = _tmp.Dir("World of Warcraft");

        var db = _tmp.File("product.db");
        File.WriteAllBytes(db,
        [
            .. Install("agent", "agent", "C:/ProgramData/Battle.net/Agent", true),
            .. Install("battle.net", "bna", bnetDir.Replace('\\', '/'), true),
            .. Install("fenris", "fenris", d4.Replace('\\', '/'), true),
            .. Install("prometheus", "pro", ow.Replace('\\', '/'), true),
            .. Install("wow", "wow", wow.Replace('\\', '/'), false),
            .. Bytes(2, Str(1, "handshake")),
        ]);
        var reg = new FakeRegistry()
            .Set($@"{Uninstall}\Diablo IV", "DisplayName", "Diablo IV")
            .Set($@"{Uninstall}\Diablo IV", "Publisher", "Blizzard Entertainment")
            .Set($@"{Uninstall}\Diablo IV", "InstallLocation", d4)
            .Set($@"{Uninstall}\Diablo IV", "UninstallString", "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=fenris --displayname=\"Diablo IV\"")
            .Set($@"{Uninstall}\Battle.net", "DisplayName", "Battle.net")
            .Set($@"{Uninstall}\Battle.net", "Publisher", "Blizzard Entertainment")
            .Set($@"{Uninstall}\Battle.net", "UninstallString", "x --uid=battle.net");

        var games = new BattleNetScanner(reg, db, @"C:\nowhere\Battle.net.exe").Scan().OrderBy(g => g.StoreId).ToList();
        Assert.Equal(["battlenet:Fen", "battlenet:Pro"], games.Select(g => g.Key));
        var diablo = games[0];
        Assert.Equal("Diablo IV", diablo.Name);
        Assert.Equal(d4, diablo.InstallDir);
        Assert.Equal(bnetExe, diablo.LaunchExe);
        Assert.Equal(["--exec=launch Fen"], diablo.LaunchArgs);
        Assert.Equal(["Diablo IV.exe", "Diablo IV Launcher.exe"], diablo.Exes);
        Assert.Equal("Overwatch 2", games[1].Name); // table name when the registry has no entry
        Assert.Contains("Overwatch.exe", games[1].Exes);
    }

    [Fact]
    public void RegistryOnly_UsesUidFromUninstallString_AndDefaultExe()
    {
        var dir = _tmp.Dir("Hearthstone");
        var reg = new FakeRegistry()
            .Set($@"{Uninstall}\Hearthstone", "DisplayName", "Hearthstone")
            .Set($@"{Uninstall}\Hearthstone", "Publisher", "Blizzard Entertainment")
            .Set($@"{Uninstall}\Hearthstone", "InstallLocation", dir)
            .Set($@"{Uninstall}\Hearthstone", "UninstallString", "x --uid=hs_beta --displayname=\"Hearthstone\"")
            .Set($@"{Uninstall}\Other", "DisplayName", "Not Blizzard")
            .Set($@"{Uninstall}\Other", "Publisher", "Someone");

        var g = Assert.Single(new BattleNetScanner(reg, _tmp.File("missing.db"), @"C:\BN\Battle.net.exe").Scan());
        Assert.Equal("battlenet:WTCG", g.Key);
        Assert.Equal(@"C:\BN\Battle.net.exe", g.LaunchExe);
    }

    [Fact]
    public void CorruptProductDb_FallsBackToRegistry()
    {
        var db = _tmp.File("product.db");
        File.WriteAllBytes(db, [0x0A, 0xFF, 0xFF, 0xFF]);
        Assert.Empty(new BattleNetScanner(new FakeRegistry(), db, "x").Scan());
    }

    public void Dispose() => _tmp.Dispose();
}

public sealed class EaScannerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private const string Uninstall = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private static string InstallerData(string contentId, string title, string filePath) => $"""
        <?xml version='1.0' encoding='utf-8'?>
        <DiPManifest version="4.0">
          <contentIDs><contentID>{contentId}</contentID></contentIDs>
          <gameTitles><gameTitle locale="fr_FR">Titre</gameTitle><gameTitle locale="en_US">{title} (title)</gameTitle></gameTitles>
          <runtime>
            <launcher uid="1-1">
              <name locale="de_DE">Name DE</name>
              <name locale="en_US">{title}</name>
              <filePath>{filePath}</filePath>
              <parameters>-fullscreen</parameters>
              <trial>0</trial>
            </launcher>
          </runtime>
        </DiPManifest>
        """;

    [Fact]
    public void FindsGamesFromRegistryAndLibraryRoots_SkipsSteamCopies()
    {
        var lib = _tmp.Dir("EA Games");
        var bf = _tmp.Dir("EA Games", "Battlefield 2042");
        _tmp.Write(@"EA Games\Battlefield 2042\__Installer\installerdata.xml",
            InstallerData("196216", "Battlefield™ 2042", @"[HKEY_LOCAL_MACHINE\SOFTWARE\EA Games\Battlefield 2042\Install Dir]BF2042.exe"));
        FakeFiles.Touch(Path.Combine(bf, "BF2042.exe"));
        _tmp.Dir("EA Games", "NotAGame"); // no installerdata

        var jedi = _tmp.Dir("Steam", "steamapps", "common", "Jedi Survivor");
        _tmp.Write(@"Steam\steamapps\common\Jedi Survivor\__Installer\installerdata.xml", InstallerData("198300", "Jedi", "x.exe"));

        var nfs = _tmp.Dir("Other", "NFS");
        _tmp.Write(@"Other\NFS\installerdata.xml", InstallerData("", "Need for Speed™ Unbound", "NeedForSpeedUnbound.exe"));

        var reg = new FakeRegistry()
            .Set($@"{Uninstall}\{{B9CB}}", "DisplayName", "Jedi Survivor")
            .Set($@"{Uninstall}\{{B9CB}}", "Publisher", "Electronic Arts, Inc.")
            .Set($@"{Uninstall}\{{B9CB}}", "InstallLocation", jedi)
            .Set($@"{Uninstall}\{{EAAPP}}", "DisplayName", "EA app")
            .Set($@"{Uninstall}\{{EAAPP}}", "Publisher", "Electronic Arts")
            .Set(@"HKLM\SOFTWARE\WOW6432Node\EA Games\Need for Speed Unbound", "Install Dir", nfs + "\\")
            .Set(@"HKLM\SOFTWARE\WOW6432Node\Electronic Arts\EA Desktop", "InstallLocation", @"C:\Program Files\Electronic Arts\EA Desktop");

        var games = new EaScanner(reg, [lib], null).Scan().OrderBy(g => g.Name).ToList();
        Assert.Equal(["Battlefield™ 2042", "Need for Speed™ Unbound"], games.Select(g => g.Name));
        var bfGame = games[0];
        Assert.Equal("ea:196216", bfGame.Key);
        Assert.Equal(Path.Combine(bf, "BF2042.exe"), bfGame.LaunchExe);
        Assert.Equal(["-fullscreen"], bfGame.LaunchArgs);
        Assert.Equal("BF2042.exe", bfGame.Exes[0]);
        var nfsGame = games[1];
        Assert.Equal("ea:NFS", nfsGame.Key);     // no content id → folder name
        Assert.Null(nfsGame.LaunchExe);          // exe in installerdata does not exist on disk
    }

    [Fact]
    public void NothingInstalled_YieldsNothing() =>
        Assert.Empty(new EaScanner(new FakeRegistry(), [_tmp.File("none")], _tmp.File("none2")).Scan());

    public void Dispose() => _tmp.Dispose();
}

public sealed class XboxScannerTests : IDisposable
{
    private readonly TempDir _tmp = new();

    private static string Config(string identity, string publisher, string displayName, string exe) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Game configVersion="1">
          <Identity Name="{identity}" Publisher="{publisher}" Version="1.0.0.0" />
          <ExecutableList>
            <Executable Name="{exe}" Id="Game" TargetDeviceFamily="PC" />
          </ExecutableList>
          <ShellVisuals DefaultDisplayName="{displayName}" PublisherDisplayName="Pub" StoreLogo="StoreLogo.png" Square150x150Logo="Assets\Logo150.png" Square44x44Logo="Logo44.png" />
        </Game>
        """;

    [Fact]
    public void PublisherHash_MatchesKnownFamilyName() =>
        Assert.Equal("8wekyb3d8bbwe", XboxScanner.PublisherHash("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));

    [Fact]
    public void XboxGamesFolder_AndRegistryPackages()
    {
        var root = _tmp.Dir("XboxGames");
        var forza = _tmp.Dir("XboxGames", "Forza Horizon 5", "Content");
        _tmp.Write(@"XboxGames\Forza Horizon 5\Content\MicrosoftGame.config",
            Config("Microsoft.SunriseBaseGame", "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
                "ms-resource:AppDisplayName", "ForzaHorizon5.exe"));
        var logo = FakeFiles.Touch(Path.Combine(forza, "Assets", "Logo150.scale-200.png"));
        FakeFiles.Touch(Path.Combine(forza, "ForzaHorizon5.exe"));

        var custom = _tmp.Dir("Games", "Starfield", "Content");
        _tmp.Write(@"Games\Starfield\Content\MicrosoftGame.config",
            Config("BethesdaSoftworks.ProjectGold", "CN=whatever", "Starfield", "Starfield.exe"));
        _tmp.Write(@"Games\Starfield\Content\appxmanifest.xml", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications><Application Id="Game.App" Executable="Starfield.exe" /></Applications>
            </Package>
            """);
        var storeLogo = FakeFiles.Touch(Path.Combine(custom, "StoreLogo.png"));

        var notGame = _tmp.Dir("Apps", "Calculator");
        var reg = new FakeRegistry()
            .Set($@"{XboxScanner.PackagesKey}\BethesdaSoftworks.ProjectGold_1.9.0.0_x64__3275kfvn8vcwc", "PackageRootFolder", custom)
            .Set($@"{XboxScanner.PackagesKey}\Microsoft.WindowsCalculator_11.0_x64__8wekyb3d8bbwe", "PackageRootFolder", notGame);

        var games = new XboxScanner(reg, [root, _tmp.File("D-XboxGames")]).Scan().OrderBy(g => g.Name).ToList();
        Assert.Equal(["Forza Horizon 5", "Starfield"], games.Select(g => g.Name)); // ms-resource → folder name

        var fh5 = games[0];
        Assert.Equal("xbox:Microsoft.SunriseBaseGame_8wekyb3d8bbwe", fh5.Key);
        Assert.Equal(@"shell:AppsFolder\Microsoft.SunriseBaseGame_8wekyb3d8bbwe!Game", fh5.LaunchUri);
        Assert.Equal(logo, fh5.ArtPath);
        Assert.Equal(["ForzaHorizon5.exe"], fh5.Exes);

        var sf = games[1];
        Assert.Equal("BethesdaSoftworks.ProjectGold_3275kfvn8vcwc", sf.StoreId); // from the registry full name
        Assert.Equal(@"shell:AppsFolder\BethesdaSoftworks.ProjectGold_3275kfvn8vcwc!Game.App", sf.LaunchUri);
        Assert.Equal(storeLogo, sf.ArtPath);
    }

    [Fact]
    public void BrokenConfig_IsSkipped()
    {
        _tmp.Write(@"XboxGames\Broken\Content\MicrosoftGame.config", "<Game");
        Assert.Empty(new XboxScanner(new FakeRegistry(), [_tmp.File("XboxGames")]).Scan());
    }

    public void Dispose() => _tmp.Dispose();
}

public sealed class GameScannersTests
{
    private sealed class Fixed(string store, params DetectedGame[] games) : IGameScanner
    {
        public string Store => store;
        public IEnumerable<DetectedGame> Scan() => games;
    }

    private sealed class Throwing : IGameScanner
    {
        public string Store => "gog";
        public IEnumerable<DetectedGame> Scan() => throw new InvalidOperationException("boom");
    }

    private static DetectedGame G(string store, string id, string name) => new() { Store = store, StoreId = id, Name = name };

    [Fact]
    public void ScanAll_FiltersStores_Dedups_Sorts_SurvivesThrowingScanner()
    {
        IGameScanner[] scanners =
        [
            new Fixed("steam", G("steam", "2", "zeta"), G("steam", "1", "Alpha"), G("steam", "1", "Alpha dup")),
            new Fixed("epic", G("epic", "x", "Beta")),
            new Throwing(),
        ];
        var all = GameScanners.ScanAll(scanners, null);
        Assert.Equal(["Alpha", "Beta", "zeta"], all.Select(g => g.Name));

        var steamOnly = GameScanners.ScanAll(scanners, ["STEAM", "gog"]);
        Assert.Equal(["steam:1", "steam:2"], steamOnly.Select(g => g.Key));
    }

    [Fact]
    public void All_HasOneScannerPerStore() =>
        Assert.Equal(GameScanners.Stores, GameScanners.All().Select(s => s.Store));

    [Fact]
    public void SplitArgs_HandlesQuotes() =>
        Assert.Equal(["a", "b c", "", "d\"e"], ScanUtil.SplitArgs("a \"b c\" \"\" d\\\"e"));
}
