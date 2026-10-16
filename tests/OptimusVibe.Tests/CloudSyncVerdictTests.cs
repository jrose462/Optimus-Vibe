using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Engine;
using OptimusVibe.Library;

namespace OptimusVibe.Tests;

public sealed class CloudSyncVerdictTests
{
    private static LauncherEvidence Steam(SyncVerdict v, SyncConfidence c = SyncConfidence.High, string line = "steam line") =>
        new() { Verdict = v, Confidence = c, Lines = [line] };

    private static ExternalChangeRecord Change(string? from = null, string? preset = null) => new()
    {
        Utc = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), Files = ["user"], FromDevice = from, Preset = preset,
    };

    private static ObservationSummary Obs(int clean = 0, params ExternalChangeRecord[] changes) =>
        new() { CleanLaunches = clean, Changes = changes };

    private static CloudSyncReport Combine(bool? declared, LauncherEvidence[] launchers, ObservationSummary? obs = null) =>
        CloudSyncVerdict.Combine("g", "desktop", declared, launchers, obs ?? Obs());

    [Fact]
    public void LauncherSyncsSettings_IsSyncedHigh()
    {
        var r = Combine(true, [Steam(SyncVerdict.Synced)]);
        Assert.Equal((SyncVerdict.Synced, SyncConfidence.High), (r.Detected, r.Confidence));
        Assert.Equal(true, r.Effective);
        Assert.False(r.SuggestOptOut);
        Assert.Null(r.Contradiction);
        Assert.Contains("steam line", r.Evidence);
    }

    [Fact]
    public void AnySyncingLauncherBeatsANotSyncingOne()
    {
        var r = Combine(true, [Steam(SyncVerdict.NotSynced), Steam(SyncVerdict.Synced, SyncConfidence.High, "ea line")]);
        Assert.Equal(SyncVerdict.Synced, r.Detected);
    }

    [Fact]
    public void StrongObservation_OverridesLauncherNotSynced_AndFlagsContradiction()
    {
        var r = Combine(false, [Steam(SyncVerdict.NotSynced)], Obs(2, Change("ally-x", "Performance")));
        Assert.Equal((SyncVerdict.Synced, SyncConfidence.High), (r.Detected, r.Confidence));
        Assert.Equal("Settings changed while closed and matched the ally-x Performance preset (" +
                     new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("MMM d") + ").", r.Evidence[0]);
        Assert.Equal("Research says per-machine, but settings arrived from your Ally X. Keep this game managed.", r.Contradiction);
        Assert.False(r.SuggestOptOut);
        Assert.Equal(true, r.Effective);
    }

    [Fact]
    public void LauncherNotSynced_StaysNotSynced_AndSuggestsOptOut()
    {
        var r = Combine(false, [Steam(SyncVerdict.NotSynced)], Obs(3));
        Assert.Equal((SyncVerdict.NotSynced, SyncConfidence.High), (r.Detected, r.Confidence));
        Assert.True(r.SuggestOptOut);
        Assert.Null(r.Contradiction);
    }

    [Fact]
    public void NonStrongExternalChange_LowersConfidence_AndBlocksOptOut()
    {
        var r = Combine(false, [Steam(SyncVerdict.NotSynced)], Obs(0, Change()));
        Assert.Equal(SyncVerdict.NotSynced, r.Detected);
        Assert.Equal(SyncConfidence.Medium, r.Confidence);
        Assert.True(r.HasExternalChanges);
        Assert.False(r.SuggestOptOut);
        Assert.Contains(r.Evidence, e => e.StartsWith("Settings changed 1 time while the game was closed"));
    }

    [Theory]
    [InlineData(0, SyncVerdict.Unknown, SyncConfidence.None)]
    [InlineData(4, SyncVerdict.Unknown, SyncConfidence.None)]
    [InlineData(5, SyncVerdict.NotSynced, SyncConfidence.Low)]
    [InlineData(9, SyncVerdict.NotSynced, SyncConfidence.Low)]
    [InlineData(10, SyncVerdict.NotSynced, SyncConfidence.Medium)]
    [InlineData(40, SyncVerdict.NotSynced, SyncConfidence.Medium)]
    public void NoLauncherAnswer_CleanLaunchesAlone(int clean, SyncVerdict verdict, SyncConfidence confidence)
    {
        var r = Combine(null, [LauncherEvidence.Unknown("Epic: can't inspect")], Obs(clean));
        Assert.Equal((verdict, confidence), (r.Detected, r.Confidence));
    }

    [Fact]
    public void NoLauncherAnswer_ExternalChanges_StayUnknown()
    {
        var r = Combine(null, [], Obs(8, Change()));
        Assert.Equal(SyncVerdict.Unknown, r.Detected);
        Assert.Null(r.Effective);
        Assert.False(r.SuggestOptOut);
    }

    [Fact]
    public void ObservedNotSyncedAtLowConfidence_DoesNotOverrideADeclaredTrue()
    {
        var r = Combine(true, [], Obs(6));
        Assert.Equal((SyncVerdict.NotSynced, SyncConfidence.Low), (r.Detected, r.Confidence));
        Assert.Null(r.Contradiction); // low confidence: not decent enough to call it a contradiction
        Assert.Equal(true, r.Effective);
        Assert.False(r.SuggestOptOut);
    }

    [Fact]
    public void DeclaredTrue_ButLauncherOnlySyncsSaves_IsAContradiction()
    {
        var r = Combine(true, [Steam(SyncVerdict.NotSynced)]);
        Assert.NotNull(r.Contradiction);
        Assert.Equal(false, r.Effective);
        Assert.True(r.SuggestOptOut);
    }

    [Fact]
    public void DeclaredFalse_NothingDetected_StillSuggestsOptOut_UnlessChangesWereSeen()
    {
        Assert.True(Combine(false, []).SuggestOptOut);
        Assert.False(Combine(false, [], Obs(0, Change())).SuggestOptOut);
        Assert.False(Combine(null, []).SuggestOptOut);
        Assert.False(Combine(true, []).SuggestOptOut);
    }

    [Fact]
    public void Evidence_MentionsResearchAndCleanLaunches()
    {
        var r = Combine(null, [], Obs(3));
        Assert.Contains(r.Evidence, e => e.Contains("untouched between 3 launches"));
        Assert.Contains(r.Evidence, e => e.StartsWith("Not researched yet"));
        Assert.Contains(Combine(false, []).Evidence, e => e.Contains("per machine"));
    }
}

public sealed class CloudSyncIntegrationTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeSteam _steam = new();
    private readonly ConfigStore _store;
    private readonly string _docs;
    private readonly PathResolver _resolver;
    private static readonly DeviceSelection Desktop = new("desktop", null, "test", "none");

    private const string GameYaml = """
        name: Borderlands 4
        store: steam
        steamAppId: 1285190
        cloudSyncsSettings: false
        processes: [Game.exe]
        files:
          - id: gus
            handler: ini
            path: "{Documents}/My Games/Borderlands 4/Saved/Config/Windows/GameUserSettings.ini"
        presets:
          desktop:
            quality: { gus: { S: { A: 1, B: 1, C: 1 } } }
          ally-x:
            performance: { gus: { S: { A: 3, B: 3, C: 3 } } }
        """;

    public CloudSyncIntegrationTests()
    {
        var config = _tmp.Dir("config");
        _tmp.Write(Path.Combine("config", "games", "borderlands-4.yaml"), GameYaml);
        _store = new ConfigStore(new AppPaths { ConfigRoot = config, DataDir = _tmp.Dir("data") });
        _docs = _tmp.Dir("Documents");
        _resolver = new PathResolver(_steam.Locator(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Documents"] = _docs });
        _tmp.Write(Path.Combine("Documents", "My Games", "Borderlands 4", "Saved", "Config", "Windows", "GameUserSettings.ini"), "[S]\r\nA=1\r\nB=1\r\nC=1\r\n");
    }

    public void Dispose()
    {
        _steam.Dispose();
        _tmp.Dispose();
    }

    private CloudSyncChecker Checker() => new(_store, Desktop, _steam.Locator(), _resolver, eaLogsDir: _tmp.Dir("NoEa"));

    private void RemoteCache(params string[] names) =>
        File.WriteAllText(Path.Combine(_steam.UserData(FakeSteam.RecentAccount, 1285190), "remotecache.vdf"),
            SteamCloudTests.Cache(names.Select(n => (n, 2)).ToArray()));

    private static readonly DetectedGame Detected = new() { Store = "steam", StoreId = "1285190", Name = "Borderlands 4" };

    private LibraryEntry Entry(CloudSyncChecker? checker) => LibraryService.Build(_store, Desktop, [Detected], checker).Single();

    [Fact]
    public void Checker_SteamSavesOnly_NotSynced()
    {
        RemoteCache(SteamCloudTests.Saves);
        var r = Checker().Check(_store.LoadGame("borderlands-4"));
        Assert.Equal((SyncVerdict.NotSynced, SyncConfidence.High), (r.Detected, r.Confidence));
        Assert.Contains("Steam Cloud syncs 4 .sav files but not GameUserSettings.ini.", r.Evidence);
        Assert.True(r.SuggestOptOut);
    }

    [Fact]
    public void Checker_SteamSyncsTheIni_SyncedAndContradictsTheDeclaredFalse()
    {
        RemoteCache("My Games/Borderlands 4/Saved/Config/Windows/GameUserSettings.ini");
        var r = Checker().Check(_store.LoadGame("borderlands-4"));
        Assert.Equal(SyncVerdict.Synced, r.Detected);
        Assert.NotNull(r.Contradiction);
        Assert.False(r.SuggestOptOut);
    }

    [Fact]
    public void Checker_NoUserdata_SteamOnlyGameIsLowNotSynced()
    {
        var r = Checker().Check(_store.LoadGame("borderlands-4"));
        Assert.Contains("No Steam Cloud data for this game.", r.Evidence);
        Assert.Equal((SyncVerdict.NotSynced, SyncConfidence.Low), (r.Detected, r.Confidence));
    }

    [Fact]
    public void Checker_BattleNetGame_SaysItCannotInspect()
    {
        _tmp.Write(Path.Combine("config", "games", "diablo-4.yaml"), """
            name: Diablo IV
            store: battlenet
            storeIds: { battlenet: Fen }
            files:
              - id: prefs
                handler: ini
                path: "{Documents}/Diablo IV/Prefs.ini"
            """);
        var r = Checker().Check(_store.LoadGame("diablo-4"));
        Assert.Equal(SyncVerdict.Unknown, r.Detected);
        Assert.Contains(r.Evidence, e => e.StartsWith("Battle.net") && e.Contains("can't be inspected"));
    }

    [Fact]
    public void Checker_EaGame_UsesTheEaLog()
    {
        var logs = _tmp.Dir("Ea");
        var sav = Path.Combine(_docs, "Respawn", "GameUserSettings.sav");
        File.WriteAllText(Path.Combine(logs, "EADesktop.log"), EaCloudLogTests.FileLine("2026-08-20T20:25:11.126Z", sav));
        _tmp.Write(Path.Combine("config", "games", "jedi.yaml"), """
            name: Jedi
            store: steam
            steamAppId: 1774580
            storeIds: { ea: "1234" }
            cloudSyncsSettings: true
            files:
              - id: usersav
                handler: file
                path: "{Documents}/Respawn/GameUserSettings.sav"
            """);
        var r = new CloudSyncChecker(_store, Desktop, _steam.Locator(), _resolver, logs).Check(_store.LoadGame("jedi"));
        Assert.Equal((SyncVerdict.Synced, SyncConfidence.High), (r.Detected, r.Confidence));
        Assert.Contains(r.Evidence, e => e.StartsWith("EA app cloud-syncs GameUserSettings.sav"));
        Assert.Contains("No Steam Cloud data for this game.", r.Evidence);
    }

    [Fact]
    public void SuggestOptOut_WithoutAReport_FallsBackToTheDeclaredValue()
    {
        var entry = Entry(null);
        Assert.Null(entry.CloudSync);
        Assert.True(entry.SuggestOptOut);
    }

    [Fact]
    public void SuggestOptOut_FollowsTheReport()
    {
        RemoteCache(SteamCloudTests.Saves);
        Assert.True(Entry(Checker()).SuggestOptOut);

        RemoteCache("My Games/Borderlands 4/Saved/Config/Windows/GameUserSettings.ini");
        var synced = Entry(Checker());
        Assert.False(synced.SuggestOptOut); // declared false, but the launcher syncs the settings file
        Assert.NotNull(synced.CloudSync!.Contradiction);
    }

    [Fact]
    public void SuggestOptOut_FalseWhenOptedOutAlready_OrWhenChangesWereObserved()
    {
        RemoteCache(SteamCloudTests.Saves);
        LibraryService.SetOptOut(_store, "steam:1285190", true);
        Assert.False(Entry(Checker()).SuggestOptOut);
        LibraryService.SetOptOut(_store, "steam:1285190", false);
        Assert.True(Entry(Checker()).SuggestOptOut);

        // a change observed while the game was closed
        var obs = new SyncObservationStore(Path.Combine(_store.Paths.DataDir, "sync-observations.json"));
        var file = new SyncObservationFile();
        file.Games["borderlands-4"] = new() { ["desktop"] = new DeviceObservation { Changes = [new ExternalChangeRecord { Utc = DateTime.UtcNow, Files = ["gus"] }] } };
        obs.Save(file);
        Assert.False(Entry(Checker()).SuggestOptOut);
    }

    [Fact]
    public void LibraryEntryWithoutAGame_NeverSuggestsOptOut()
    {
        var e = new LibraryEntry { Key = "steam:1", Name = "X", Status = LibraryStatus.NeedsPresets };
        Assert.False(e.SuggestOptOut);
    }

    [Fact]
    public void Validator_WarnsWhenCloudSyncsSettingsIsUnknown()
    {
        _tmp.Write(Path.Combine("config", "devices.yaml"), "devices:\n  desktop:\n    match: { gpu: \"NVIDIA*\" }\n");
        _tmp.Write(Path.Combine("config", "games", "unknown.yaml"), """
            name: Unknown
            store: steam
            steamAppId: 5
            processes: [U.exe]
            presets:
              desktop:
                quality: {}
            """);
        var report = Validator.Run(_store);
        Assert.Contains(report.Warnings, w => w.Contains("games/unknown.yaml") && w.Contains("cloudSyncsSettings is unknown") && w.Contains("cloud-check"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("borderlands-4.yaml") && w.Contains("cloudSyncsSettings"));
    }
}
