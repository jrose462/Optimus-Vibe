using OptimusVibe.CloudSync;
using OptimusVibe.Config;

namespace OptimusVibe.Tests;

public sealed class SteamCloudTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _docs;
    private readonly PathResolver _resolver;

    // Synthetic file modeled on a real userdata\<account>\<appid>\remotecache.vdf (Auto-Cloud, root 2 = Documents).
    internal static string Cache(params (string Name, int Root)[] entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("\"1285190\"\n{\n\t\"ChangeNumber\"\t\t\"12\"\n\t\"OSType\"\t\t\"0\"\n");
        int i = 0;
        foreach (var (name, root) in entries)
        {
            sb.Append($"\t\"{name}\"\n\t{{\n\t\t\"root\"\t\t\"{root}\"\n\t\t\"size\"\t\t\"{1000 + i}\"\n");
            sb.Append("\t\t\"localtime\"\t\t\"1790990156\"\n\t\t\"time\"\t\t\"1790990155\"\n\t\t\"remotetime\"\t\t\"1790990155\"\n");
            sb.Append($"\t\t\"sha\"\t\t\"0ec0f51eaf4db3628fa1c869cd17ff6b700f37{i:00}\"\n\t\t\"syncstate\"\t\t\"1\"\n\t\t\"persiststate\"\t\t\"0\"\n\t\t\"platformstosync2\"\t\t\"-1\"\n\t}}\n");
            i++;
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    internal static readonly string[] Saves =
    [
        "My Games/Borderlands 4/Saved/SaveGames/7656/Profiles/client/profile.sav",
        "My Games/Borderlands 4/Saved/SaveGames/7656/Profiles/client/1.sav",
        "My Games/Borderlands 4/Saved/SaveGames/7656/Profiles/client/2.sav",
        "My Games/Borderlands 4/Saved/SaveGames/7656/Profiles/client/3.sav",
    ];

    public SteamCloudTests()
    {
        _docs = _tmp.Dir("Documents");
        _resolver = new PathResolver(new SteamLocator(_tmp.Dir("NoSteam")),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Documents"] = _docs });
    }

    public void Dispose() => _tmp.Dispose();

    private string Ini => Path.Combine(_docs, "My Games", "Borderlands 4", "Saved", "Config", "Windows", "GameUserSettings.ini");

    [Fact]
    public void Parse_ReadsEntriesAndSkipsHeaderKeys()
    {
        var entries = SteamCloud.Parse(Cache(Saves.Select(s => (s, 2)).ToArray()));
        Assert.Equal(4, entries.Count);
        Assert.All(entries, e => Assert.Equal(2, e.Root));
        Assert.Equal("My Games/Borderlands 4/Saved/SaveGames/7656/Profiles/client/profile.sav", entries[0].Name);
        Assert.Equal(1000, entries[0].Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790990155).UtcDateTime, entries[0].Time);
    }

    [Fact]
    public void Parse_EmptyOrHeaderOnly_NoEntries()
    {
        Assert.Empty(SteamCloud.Parse(""));
        Assert.Empty(SteamCloud.Parse("\"1285190\"\n{\n\t\"ChangeNumber\"\t\t\"1\"\n}\n"));
    }

    [Fact]
    public void Syncs_UsesRootFolderAndExactPath()
    {
        var entries = SteamCloud.Parse(Cache(Saves.Select(s => (s, 2)).ToArray()));
        var save = new SyncFile("sav", Path.Combine(_docs, "My Games", "Borderlands 4", "Saved", "SaveGames", "7656", "Profiles", "client", "1.sav"), true);
        Assert.True(SteamCloud.Syncs(entries, save, _resolver, 1285190));
        Assert.False(SteamCloud.Syncs(entries, new SyncFile("gus", Ini, true), _resolver, 1285190));
    }

    [Fact]
    public void Syncs_IsCaseAndSlashInsensitive_AndKnownRootMustMatch()
    {
        var entries = SteamCloud.Parse(Cache(("My Games/Borderlands 4/Saved/Config/Windows/GameUserSettings.ini", 2)));
        Assert.True(SteamCloud.Syncs(entries, new SyncFile("gus", Ini.ToUpperInvariant().Replace('\\', '/'), true), _resolver, 1285190));

        // Same relative name, but not under the Documents root the entry names.
        var other = new SyncFile("gus", @"C:\Elsewhere\My Games\Borderlands 4\Saved\Config\Windows\GameUserSettings.ini", true);
        Assert.False(SteamCloud.Syncs(entries, other, _resolver, 1285190));
    }

    [Fact]
    public void Syncs_UnknownRoot_FallsBackToPathSuffix()
    {
        var entries = SteamCloud.Parse(Cache(("Saved/Config/GameUserSettings.ini", 0)));
        Assert.True(SteamCloud.Syncs(entries, new SyncFile("gus", @"C:\Games\X\Saved\Config\GameUserSettings.ini", true), _resolver, 1));
        Assert.True(SteamCloud.Syncs(entries, new SyncFile("gus", "{Documents}/X/Saved/Config/GameUserSettings.ini", true), _resolver, 1));
        Assert.False(SteamCloud.Syncs(entries, new SyncFile("gus", @"C:\Games\X\Saved\Config\Other.ini", true), _resolver, 1));
        // a partial folder name is not a suffix match
        Assert.False(SteamCloud.Syncs(entries, new SyncFile("gus", @"C:\Games\XSaved\Config\GameUserSettings.ini", true), _resolver, 1));
    }

    [Fact]
    public void Evaluate_OnlySaves_NotSyncedWithEvidence()
    {
        var entries = SteamCloud.Parse(Cache(Saves.Select(s => (s, 2)).ToArray()));
        var ev = SteamCloud.Evaluate(entries, [new SyncFile("gus", Ini, true)], _resolver, 1285190, steamIsOnlyLauncher: true);
        Assert.Equal(SyncVerdict.NotSynced, ev.Verdict);
        Assert.Equal(SyncConfidence.High, ev.Confidence);
        Assert.Equal("Steam Cloud syncs 4 .sav files but not GameUserSettings.ini.", Assert.Single(ev.Lines));
    }

    [Fact]
    public void Evaluate_SettingsFileTracked_Synced()
    {
        var entries = SteamCloud.Parse(Cache(("My Games/Borderlands 4/Saved/Config/Windows/GameUserSettings.ini", 2), (Saves[0], 2)));
        var files = new[] { new SyncFile("gus", Ini, true), new SyncFile("other", Path.Combine(_docs, "x.cfg"), true) };
        var ev = SteamCloud.Evaluate(entries, files, _resolver, 1285190, true);
        Assert.Equal(SyncVerdict.Synced, ev.Verdict);
        Assert.Equal("Steam Cloud syncs GameUserSettings.ini but not x.cfg.", ev.Lines.Single());
    }

    [Fact]
    public void Evaluate_NoCloudData_UnknownUnlessSteamIsTheOnlyLauncher()
    {
        var files = new[] { new SyncFile("gus", Ini, true) };
        var only = SteamCloud.Evaluate(null, files, _resolver, 1, steamIsOnlyLauncher: true);
        Assert.Equal(SyncVerdict.NotSynced, only.Verdict);
        Assert.Equal(SyncConfidence.Low, only.Confidence);
        Assert.Equal("No Steam Cloud data for this game.", only.Lines.Single());

        var shared = SteamCloud.Evaluate([], files, _resolver, 1, steamIsOnlyLauncher: false);
        Assert.Equal(SyncVerdict.Unknown, shared.Verdict);
        Assert.Equal("No Steam Cloud data for this game.", shared.Lines.Single());
    }

    [Fact]
    public void Describe_GroupsByExtension()
    {
        var e = SteamCloud.Parse(Cache(("a/1.sav", 0), ("a/2.sav", 0), ("a/3.sav", 0), ("b/c.ini", 0)));
        Assert.Equal("3 .sav and 1 .ini files", SteamCloud.Describe(e));
        Assert.Equal("1 .sav file", SteamCloud.Describe(SteamCloud.Parse(Cache(("a/1.sav", 0)))));
    }

    [Fact]
    public void Read_FindsRemoteCacheOfMostRecentAccount_OrNull()
    {
        using var steam = new FakeSteam();
        var locator = steam.Locator();
        Assert.Null(SteamCloud.Read(locator, 1285190)); // no userdata folder at all

        var dir = steam.UserData(FakeSteam.RecentAccount, 1285190);
        Assert.Null(SteamCloud.Read(locator, 1285190)); // folder but no remotecache.vdf

        File.WriteAllText(Path.Combine(dir, "remotecache.vdf"), Cache(Saves.Select(s => (s, 2)).ToArray()));
        Assert.Equal(4, SteamCloud.Read(locator, 1285190)!.Count);
    }
}
