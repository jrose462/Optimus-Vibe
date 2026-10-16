using GameProfiles.Config;
using GameProfiles.Devices;
using GameProfiles.Library;

namespace GameProfiles.Tests;

public sealed class LibraryServiceTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly ConfigStore _store;
    private static readonly DeviceSelection Desktop = new("desktop", null, "test", "none");
    private static readonly DeviceSelection Ally = new("ally-x", null, "test", "none");

    public LibraryServiceTests()
    {
        var config = _tmp.Dir("config");
        _store = new ConfigStore(new AppPaths { ConfigRoot = config, DataDir = _tmp.Dir("data") });

        Game("borderlands-4", """
            name: Borderlands 4
            steamAppId: 1285190
            cloudSyncsSettings: false
            presets:
              desktop:
                quality: {}
                performance: {}
            """);
        Game("diablo-4", """
            name: Diablo IV
            store: battlenet
            storeIds:
              battlenet: Fen
            presets:
              desktop:
                quality: {}
              ally-x:
                battery: {}
            """);
        Game("jedi-survivor", """
            name: "STAR WARS Jedi: Survivor"
            store: steam
            presets:
              ally-x:
                performance: {}
            """);
        Game("cyberpunk2077", """
            name: Cyberpunk 2077
            steamAppId: 1091500
            presets:
              desktop:
                quality: {}
            """);
        Game("doom", """
            name: DOOM
            steamAppId: 379720
            presets:
              desktop:
                quality: {}
            """);
    }

    private void Game(string id, string yaml) => _tmp.Write(Path.Combine("config", "games", id + ".yaml"), yaml);

    private static DetectedGame D(string store, string id, string name, params string[] exes) => new()
    {
        Store = store, StoreId = id, Name = name, InstallDir = $@"C:\Games\{name}", Exes = exes,
    };

    private static readonly IReadOnlyList<DetectedGame> Detected =
    [
        D("steam", "1285190", "Borderlands® 4", "Borderlands4.exe"),
        D("battlenet", "Fen", "Diablo IV", "Diablo IV.exe"),
        D("steam", "1774580", "STAR WARS Jedi: Survivor™", "JediSurvivor.exe"), // name match (config has no steamAppId)
        D("epic", "Fortnite", "Fortnite", "FortniteLauncher.exe"),
        D("steam", "2280", "DOOM (1993)"),
        D("steam", "9999", "Doom"), // config DOOM has steamAppId 379720 → a different app, no name match
    ];

    [Fact]
    public void NormalizeName_StripsSymbolsAndPunctuation()
    {
        Assert.Equal("borderlands4", LibraryService.NormalizeName("Borderlands® 4"));
        Assert.Equal("starwarsjedisurvivor", LibraryService.NormalizeName("STAR WARS Jedi: Survivor™"));
        Assert.Equal("marvelsmidnightsuns", LibraryService.NormalizeName("Marvel's Midnight-Suns©"));
        Assert.Equal("diabloiv", LibraryService.NormalizeName("Diablo IV"));
        Assert.NotEqual(LibraryService.NormalizeName("Diablo IV"), LibraryService.NormalizeName("Diablo 4"));
        Assert.Equal("", LibraryService.NormalizeName(null));
    }

    [Fact]
    public void Build_MatchesAndAssignsStatus_Desktop()
    {
        var entries = LibraryService.Build(_store, Desktop, Detected).ToDictionary(e => e.Key);

        Assert.Equal(LibraryStatus.Managed, entries["steam:1285190"].Status);
        Assert.Equal("borderlands-4", entries["steam:1285190"].Game!.Id);
        Assert.Equal("Borderlands 4", entries["steam:1285190"].Name); // config name wins
        Assert.True(entries["steam:1285190"].SuggestOptOut);

        Assert.Equal(LibraryStatus.Managed, entries["battlenet:Fen"].Status);
        Assert.Equal("diablo-4", entries["battlenet:Fen"].Game!.Id);

        // Configured by name, but no desktop presets → NeedsPresets.
        Assert.Equal("jedi-survivor", entries["steam:1774580"].Game!.Id);
        Assert.Equal(LibraryStatus.NeedsPresets, entries["steam:1774580"].Status);

        Assert.Null(entries["epic:Fortnite"].Game);
        Assert.Equal(LibraryStatus.NeedsPresets, entries["epic:Fortnite"].Status);
        Assert.Null(entries["steam:9999"].Game);
        Assert.Null(entries["steam:2280"].Game);

        Assert.Equal(LibraryStatus.NotInstalled, entries["steam:1091500"].Status);
        Assert.Equal("cyberpunk2077", entries["steam:1091500"].Game!.Id);
        Assert.Null(entries["steam:1091500"].Detected);
        Assert.Equal(LibraryStatus.NotInstalled, entries["steam:379720"].Status);
        Assert.Equal(8, entries.Count);
    }

    [Fact]
    public void Build_StatusDependsOnDevice()
    {
        var entries = LibraryService.Build(_store, Ally, Detected).ToDictionary(e => e.Key);
        Assert.Equal(LibraryStatus.NeedsPresets, entries["steam:1285190"].Status);
        Assert.Equal(LibraryStatus.Managed, entries["battlenet:Fen"].Status);
        Assert.Equal(LibraryStatus.Managed, entries["steam:1774580"].Status);
    }

    [Fact]
    public void Build_IsSortedByName()
    {
        var names = LibraryService.Build(_store, Desktop, Detected).Select(e => e.Name).ToList();
        Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), names);
    }

    [Fact]
    public void OptOut_RoundTrip_AndStatus()
    {
        Assert.Empty(LibraryService.LoadLibrary(_store).OptOut);
        LibraryService.SetOptOut(_store, "steam:1285190", true);
        LibraryService.SetOptOut(_store, "STEAM:1285190", true); // idempotent, case-insensitive
        LibraryService.SetOptOut(_store, "epic:Fortnite", true);
        Assert.StartsWith("#", File.ReadAllText(_store.Paths.LibraryFile));
        Assert.Equal(["epic:Fortnite", "steam:1285190"], LibraryService.LoadLibrary(_store).OptOut);

        var entries = LibraryService.Build(_store, Desktop, Detected).ToDictionary(e => e.Key);
        Assert.Equal(LibraryStatus.OptedOut, entries["steam:1285190"].Status);
        Assert.True(entries["steam:1285190"].OptedOut);
        Assert.False(entries["steam:1285190"].SuggestOptOut);

        LibraryService.SetOptOut(_store, "steam:1285190", false);
        Assert.Equal(["epic:Fortnite"], LibraryService.LoadLibrary(_store).OptOut);
        Assert.Equal(LibraryStatus.Managed, LibraryService.Build(_store, Desktop, Detected).Single(e => e.Key == "steam:1285190").Status);
    }

    [Fact]
    public void SetOptOut_KeepsExistingHeaderComment()
    {
        _tmp.Write(Path.Combine("config", "library.yaml"), "# my header\n# second line\noptOut: []\n");
        LibraryService.SetOptOut(_store, "gog:1", true);
        var text = File.ReadAllText(_store.Paths.LibraryFile);
        Assert.StartsWith("# my header\n# second line\n", text);
        Assert.Equal(["gog:1"], LibraryService.LoadLibrary(_store).OptOut);
    }

    [Fact]
    public void QueueRequests_AddsNeedsPresets_Once_SkipsOptedOut()
    {
        LibraryService.SetOptOut(_store, "steam:9999", true);
        var entries = LibraryService.Build(_store, Desktop, Detected);

        var added = LibraryService.QueueRequests(_store, entries, "desktop");
        // NeedsPresets on desktop: jedi (configured, no desktop presets), Fortnite, DOOM (1993); Doom 9999 is opted out.
        Assert.Equal(3, added);
        var text = File.ReadAllText(_store.Paths.RequestsFile);
        Assert.StartsWith("#", text);
        Assert.Contains("game-settings-recommender", text);

        var reqs = LibraryService.LoadRequests(_store).Requests;
        Assert.Equal(["epic:Fortnite", "steam:1774580", "steam:2280"], reqs.Select(r => $"{r.Store}:{r.StoreId}").Order());
        var fn = reqs.Single(r => r.StoreId == "Fortnite");
        Assert.Equal("Fortnite", fn.Name);
        Assert.Equal(@"C:\Games\Fortnite", fn.InstallDir);
        Assert.Equal(["FortniteLauncher.exe"], fn.Exes);
        Assert.Equal("desktop", fn.Device);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), fn.Requested);

        Assert.Equal(0, LibraryService.QueueRequests(_store, entries, "desktop")); // already queued
        Assert.Equal(3, LibraryService.LoadRequests(_store).Requests.Count);
    }

    [Fact]
    public void QueueRequests_PrunesConfiguredAndOptedOut()
    {
        var entries = LibraryService.Build(_store, Desktop, Detected);
        LibraryService.QueueRequests(_store, entries, "desktop");

        // Fortnite gets a config with desktop presets; DOOM (1993) gets opted out.
        Game("fortnite", """
            name: Fortnite
            store: epic
            storeIds:
              epic: Fortnite
            presets:
              desktop:
                performance: {}
            """);
        LibraryService.SetOptOut(_store, "steam:2280", true);

        var added = LibraryService.QueueRequests(_store, LibraryService.Build(_store, Desktop, Detected), "desktop");
        Assert.Equal(0, added);
        Assert.Equal(["steam:1774580", "steam:9999"], LibraryService.LoadRequests(_store).Requests.Select(r => $"{r.Store}:{r.StoreId}").Order());
    }

    [Fact]
    public void QueueRequests_NothingToDo_DoesNotCreateFile()
    {
        var entries = LibraryService.Build(_store, Desktop, [D("steam", "1285190", "Borderlands 4")]);
        Assert.Equal(0, LibraryService.QueueRequests(_store, entries, "desktop"));
        Assert.False(File.Exists(_store.Paths.RequestsFile));
    }

    [Fact]
    public void FindInstall_PrefersIdsThenOwnStore()
    {
        var bl4 = _store.LoadGame("borderlands-4");
        var jedi = _store.LoadGame("jedi-survivor");
        var cp = _store.LoadGame("cyberpunk2077");
        var detected = new List<DetectedGame>(Detected)
        {
            D("epic", "jedi", "Star Wars Jedi - Survivor"),
            D("epic", "bl4", "Borderlands 4"),
        };

        Assert.Equal("steam:1285190", LibraryService.FindInstall(bl4, detected)!.Key); // app id beats name
        Assert.Equal("steam:1774580", LibraryService.FindInstall(jedi, detected)!.Key); // own store wins name ties
        Assert.Null(LibraryService.FindInstall(cp, detected));
    }

    [Fact]
    public void ConfiguredKey_Fallbacks()
    {
        Assert.Equal("steam:1", LibraryService.ConfiguredKey(new GameDef { Id = "a", SteamAppId = 1 }));
        Assert.Equal("battlenet:Fen", LibraryService.ConfiguredKey(new GameDef { Id = "b", Store = "battlenet", StoreIds = { ["epic"] = "x", ["battlenet"] = "Fen" } }));
        Assert.Equal("epic:x", LibraryService.ConfiguredKey(new GameDef { Id = "c", Store = "gog", StoreIds = { ["epic"] = "x" } }));
        Assert.Equal("config:d", LibraryService.ConfiguredKey(new GameDef { Id = "d" }));
    }

    [Fact]
    public void BrokenGameConfig_IsSkipped()
    {
        Game("broken", "name: [unclosed");
        var entries = LibraryService.Build(_store, Desktop, Detected);
        Assert.DoesNotContain(entries, e => e.Game?.Id == "broken");
    }

    public void Dispose() => _tmp.Dispose();
}
