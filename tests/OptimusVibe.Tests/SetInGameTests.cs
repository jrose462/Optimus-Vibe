using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.Tests;

/// <summary>The "set in-game" flag: its two sources, the YAML shapes, the validator and the library file.</summary>
public sealed class SetInGameFlagTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly ConfigStore _store;

    public SetInGameFlagTests()
    {
        var root = _tmp.Dir("config");
        _store = new ConfigStore(new AppPaths { ConfigRoot = root, DataDir = _tmp.Dir("data") });
    }

    public void Dispose() => _tmp.Dispose();

    private GameDef Game(string yaml, string id = "forspoken")
    {
        _tmp.Write(Path.Combine("config", "games", id + ".yaml"), yaml);
        return _store.LoadGame(id);
    }

    private const string Flagged = """
        name: Forspoken
        store: steam
        steamAppId: 1680880
        mappable: false
        flagged:
          reason: the game rewrites its binary settings store on every boot
          since: "2026-10-20"
          evidence: verify run overridden twice; mapping run changed only system.save
          issue: 12
        """;

    [Fact]
    public void FlagDef_round_trips_through_the_game_yaml()
    {
        var game = Game(Flagged);
        Assert.NotNull(game.Flagged);
        Assert.Equal("the game rewrites its binary settings store on every boot", game.Flagged!.Reason);
        Assert.Equal("2026-10-20", game.Flagged.Since);
        Assert.Equal("verify run overridden twice; mapping run changed only system.save", game.Flagged.Evidence);
        Assert.Equal(12, game.Flagged.Issue);

        var text = Yaml_(game);
        Assert.Contains("flagged:", text);
        Assert.Contains("reason:", text);
        Assert.Contains("issue: 12", text);
    }

    private static string Yaml_(GameDef game)
    {
        var s = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(YamlDotNet.Serialization.DefaultValuesHandling.OmitNull).Build();
        return s.Serialize(game);
    }

    [Fact]
    public void A_game_without_flagged_has_none()
    {
        Assert.Null(Game("name: Plain\nstore: steam\nsteamAppId: 1\n", "plain").Flagged);
    }

    [Fact]
    public void Effective_is_either_source_and_the_game_file_wins()
    {
        var plain = Game("name: Plain\nstore: steam\nsteamAppId: 1\n", "plain");
        var lib = new LibraryFile();
        Assert.Null(GameFlags.Effective(plain, lib));
        Assert.Null(GameFlags.Effective(plain, (LibraryFile?)null));

        lib.SetInGame["PLAIN"] = new FlagDef { Reason = "user says so" }; // game ids match case-insensitively
        Assert.Equal("user says so", GameFlags.Effective(plain, lib)!.Reason);

        var flagged = Game(Flagged);
        lib.SetInGame["forspoken"] = new FlagDef { Reason = "user says so too" };
        Assert.Equal("the game rewrites its binary settings store on every boot", GameFlags.Effective(flagged, lib)!.Reason);
        Assert.Equal("the game rewrites its binary settings store on every boot", GameFlags.Effective(flagged, new LibraryFile())!.Reason);
    }

    // ---------------------------------------------------------------- library.yaml

    [Fact]
    public void SetInGame_writes_and_clears_a_flag_in_library_yaml()
    {
        var flag = GameFlags.Create("binary store", issue: 7, evidence: "2 overridden tests", today: new DateTime(2026, 10, 21));
        Assert.True(LibraryService.SetInGame(_store, "forspoken", flag));

        var lib = LibraryService.LoadLibrary(_store);
        var back = Assert.Single(lib.SetInGame);
        Assert.Equal("forspoken", back.Key);
        Assert.Equal("binary store", back.Value.Reason);
        Assert.Equal("2026-10-21", back.Value.Since);
        Assert.Equal("2 overridden tests", back.Value.Evidence);
        Assert.Equal(7, back.Value.Issue);
        Assert.Contains("setInGame:", File.ReadAllText(_store.Paths.LibraryFile));

        Assert.False(LibraryService.SetInGame(_store, "forspoken", flag), "the same flag again changes nothing");
        Assert.True(LibraryService.SetInGame(_store, "FORSPOKEN", GameFlags.Create("another reason")), "a different flag replaces it, whatever the case");
        Assert.Equal("another reason", Assert.Single(LibraryService.LoadLibrary(_store).SetInGame).Value.Reason);

        Assert.True(LibraryService.SetInGame(_store, "forspoken", null));
        Assert.Empty(LibraryService.LoadLibrary(_store).SetInGame);
        Assert.False(LibraryService.SetInGame(_store, "forspoken", null), "nothing left to clear");
    }

    [Fact]
    public void SetInGame_keeps_the_other_library_choices_and_needs_a_reason()
    {
        LibraryService.SetOptOut(_store, "steam:1", true);
        LibraryService.SetSteamLaunchOptionSkip(_store, "steam:2", true);
        LibraryService.SetInGame(_store, "b-game", GameFlags.Create("r"));
        LibraryService.SetInGame(_store, "a-game", GameFlags.Create("r"));

        var lib = LibraryService.LoadLibrary(_store);
        Assert.Equal(["steam:1"], lib.OptOut);
        Assert.Equal(["steam:2"], lib.SteamLaunchOptionsSkip);
        Assert.Equal(["a-game", "b-game"], lib.SetInGame.Keys.ToArray());

        Assert.Throws<ConfigException>(() => LibraryService.SetInGame(_store, "x", new FlagDef { Reason = " " }));
        Assert.Throws<ConfigException>(() => LibraryService.SetInGame(_store, " ", GameFlags.Create("r")));
    }

    [Fact]
    public void A_library_file_without_setInGame_loads_with_an_empty_map()
    {
        File.WriteAllText(_store.Paths.LibraryFile, "optOut: [steam:1]\n");
        var lib = LibraryService.LoadLibrary(_store);
        Assert.Empty(lib.SetInGame);
        Assert.Equal(["steam:1"], lib.OptOut);
    }

    [Fact]
    public void Library_entries_carry_the_effective_flag_and_stay_managed()
    {
        Game("""
            name: Forspoken
            store: steam
            steamAppId: 1680880
            presets:
              desktop:
                quality: {}
            """);
        LibraryService.SetInGame(_store, "forspoken", GameFlags.Create("binary store"));
        var detected = new DetectedGame { Store = "steam", StoreId = "1680880", Name = "Forspoken" };
        var desktop = new OptimusVibe.Devices.DeviceSelection("desktop", null, "test", "none");

        var entry = Assert.Single(LibraryService.Build(_store, desktop, [detected]));
        Assert.Equal(LibraryStatus.Managed, entry.Status); // stays under the Managed filter
        Assert.Equal("binary store", entry.SetInGame!.Reason);

        LibraryService.SetInGame(_store, "forspoken", null);
        Assert.Null(Assert.Single(LibraryService.Build(_store, desktop, [detected])).SetInGame);
    }

    // ---------------------------------------------------------------- words

    [Fact]
    public void Status_and_run_lines_name_the_reason_and_the_research()
    {
        var flag = new FlagDef { Reason = "the game rewrites its store." };
        Assert.Equal("Set in-game: the game rewrites its store. Optimus-Vibe still sets the display and graphics-driver settings.", GameFlags.StatusLine(flag));
        flag.Issue = 12;
        Assert.Equal("Set in-game: the game rewrites its store. Optimus-Vibe still sets the display and graphics-driver settings. Research: #12.", GameFlags.StatusLine(flag));
        Assert.Equal("Settings left to the game (set in-game: the game rewrites its store)", GameFlags.RunLine(flag));
        Assert.Contains(GameFlags.FallbackReason, GameFlags.StatusLine(new FlagDef()));
    }

    [Fact]
    public void Describe_says_set_in_game_before_anything_else()
    {
        var game = Game(Flagged);
        var text = LaunchPolicy.Describe(game, ProfileResolver.Empty("desktop"), null, game.Flagged);
        Assert.StartsWith("Set in-game: the game rewrites its binary settings store on every boot.", text);
        Assert.EndsWith("Research: #12.", text);
        // unflagged: the old words
        Assert.StartsWith("In-game steps only", LaunchPolicy.Describe(game, ProfileResolver.Empty("desktop"), null));
    }

    // ---------------------------------------------------------------- validator

    private ValidationReport Validate(string gameYaml)
    {
        _tmp.Write(Path.Combine("config", "devices.yaml"), """
            devices:
              desktop:
                match: { gpu: "NVIDIA*" }
                defaultPreset: quality
            """);
        _tmp.Write(Path.Combine("config", "games", "forspoken.yaml"), gameYaml);
        return Validator.Run(_store);
    }

    private const string Base = """
        name: Forspoken
        store: steam
        steamAppId: 1680880
        cloudSyncsSettings: true
        processes: [Forspoken.exe]
        presets:
          desktop:
            quality: { display: { width: 1920, height: 1080, refresh: 60 } }
        """;

    [Fact]
    public void Flagged_without_a_reason_is_an_error()
    {
        var r = Validate(Base + "\nflagged:\n  since: \"2026-10-20\"\n");
        Assert.Contains(r.Errors, e => e.Contains("flagged needs a reason"));

        r = Validate(Base + "\nflagged:\n  reason: \"  \"\n");
        Assert.Contains(r.Errors, e => e.Contains("flagged needs a reason"));
    }

    [Fact]
    public void Flagged_with_a_reason_is_valid_and_silences_the_mappable_warning()
    {
        var r = Validate(Base + "\nmappable: false\nflagged:\n  reason: opaque store\n  since: \"2026-10-20\"\n  issue: 3\n");
        Assert.Empty(r.Errors);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("flagged"));
    }

    [Fact]
    public void Mappable_false_without_flagged_warns_that_presets_are_still_written()
    {
        var r = Validate(Base + "\nmappable: false\n");
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Contains("mappable is false") && w.Contains("presets still get written") && w.Contains("add flagged:"));

        Assert.DoesNotContain(Validate(Base + "\nmappable: true\n").Warnings, w => w.Contains("flagged"));
    }

    [Fact]
    public void Flagged_issue_must_be_positive_and_since_a_date()
    {
        var r = Validate(Base + "\nflagged:\n  reason: x\n  issue: 0\n  since: yesterday\n");
        Assert.Contains(r.Errors, e => e.Contains("flagged.issue"));
        Assert.Contains(r.Warnings, w => w.Contains("flagged.since"));
    }
}

/// <summary>Verify streaks (two overridden Test preset results in a row) and the mapping run suggestion.</summary>
public sealed class SetInGameSuggestionTests : IDisposable
{
    private readonly TempDir _data = new();

    public void Dispose() => _data.Dispose();

    private static RunResult Verify(RunVerdict verdict, string game = "forspoken", string device = "desktop", bool seen = true) => new()
    {
        Utc = DateTime.UtcNow, Game = game, Device = device, Preset = "recommended:quality", PresetLabel = "Quality", Mode = RunResult.VerifyMode,
        AppliedKeys = 10, ProcessSeen = seen,
        Drift = verdict == RunVerdict.Overridden ? [new RunDrift { FileId = "user", Key = "K", Applied = "1", Now = "2" }] : [],
    };

    [Fact]
    public void NextStreak_counts_overrides_and_resets_on_held()
    {
        Assert.Equal(1, RunResults.NextStreak(0, RunVerdict.Overridden));
        Assert.Equal(2, RunResults.NextStreak(1, RunVerdict.Overridden));
        Assert.Equal(0, RunResults.NextStreak(5, RunVerdict.Held));
        Assert.Equal(3, RunResults.NextStreak(3, RunVerdict.Unknown)); // no clear answer: neither confirms nor breaks it
    }

    [Fact]
    public void Two_overridden_tests_in_a_row_suggest_the_flag()
    {
        RunResults.Record(_data.Path, Verify(RunVerdict.Overridden));
        Assert.Equal(1, RunResults.OverriddenStreak(_data.Path, "forspoken", "desktop"));
        Assert.False(GameFlags.SuggestsFromVerify(1));

        RunResults.Record(_data.Path, Verify(RunVerdict.Overridden));
        Assert.Equal(2, RunResults.OverriddenStreak(_data.Path, "forspoken", "desktop"));
        Assert.True(GameFlags.SuggestsFromVerify(2));
        Assert.Equal("the game overrode the preset in 2 tests in a row", GameFlags.VerifySuggestionReason(2));
    }

    [Fact]
    public void A_held_test_or_a_normal_run_changes_the_streak_as_it_should()
    {
        RunResults.Record(_data.Path, Verify(RunVerdict.Overridden));
        // an unclear verify (the game never appeared) leaves it alone
        var unclear = Verify(RunVerdict.Overridden);
        unclear.ProcessSeen = false;
        RunResults.Record(_data.Path, unclear);
        Assert.Equal(1, RunResults.OverriddenStreak(_data.Path, "forspoken", "desktop"));

        // a normal run is not a test
        var normal = Verify(RunVerdict.Overridden);
        normal.Mode = RunResult.NormalMode;
        RunResults.Record(_data.Path, normal);
        Assert.Equal(1, RunResults.OverriddenStreak(_data.Path, "forspoken", "desktop"));

        RunResults.Record(_data.Path, Verify(RunVerdict.Held));
        Assert.Equal(0, RunResults.OverriddenStreak(_data.Path, "forspoken", "desktop"));
    }

    [Fact]
    public void The_streak_is_per_game_and_device_and_can_be_reset()
    {
        RunResults.Record(_data.Path, Verify(RunVerdict.Overridden));
        RunResults.Record(_data.Path, Verify(RunVerdict.Overridden));
        RunResults.Record(_data.Path, Verify(RunVerdict.Overridden, device: "ally-x"));
        Assert.Equal(2, RunResults.OverriddenStreak(_data.Path, "forspoken", "desktop"));
        Assert.Equal(1, RunResults.OverriddenStreak(_data.Path, "forspoken", "ally-x"));
        Assert.Equal(0, RunResults.OverriddenStreak(_data.Path, "other", "desktop"));

        RunResults.ResetStreak(_data.Path, "forspoken", "desktop");
        Assert.Equal(0, RunResults.OverriddenStreak(_data.Path, "forspoken", "desktop"));
        Assert.NotNull(RunResults.Get(_data.Path, "forspoken", "desktop", verifyOnly: true)); // the results themselves stay
        Assert.Equal(1, RunResults.OverriddenStreak(_data.Path, "forspoken", "ally-x"));
        RunResults.ResetStreak(_data.Path, "never", "seen"); // no entry: nothing happens
    }

    [Fact]
    public void A_results_file_from_an_older_version_has_a_zero_streak()
    {
        File.WriteAllText(RunResults.FileFor(_data.Path),
            """{"games":{"forspoken":{"desktop":{"last":{"game":"forspoken","device":"desktop","mode":"verify"}}}}}""");
        Assert.Equal(0, RunResults.OverriddenStreak(_data.Path, "forspoken", "desktop"));
    }

    // ---------------------------------------------------------------- mapping run

    private static (LabProfile P, ProbeSession S) Make()
    {
        var p = new LabProfile();
        return (p, new ProbeSession(p.Target(), p.Options()));
    }

    [Fact]
    public void A_mapping_run_whose_pass_b_only_changed_binary_files_suggests_the_flag()
    {
        var (p, s) = Make();
        using var _ = p;
        p.WriteBytes(p.Documents, @"Borderlands4\system.save", [0, 0, 1]);
        var s0 = s.Snapshot();
        var s1 = s.Snapshot();
        p.WriteBytes(p.Documents, @"Borderlands4\system.save", [0, 0, 7]);
        var s2 = s.Snapshot();

        var ev = s.ToEvidence(ProbeSession.Diff(s0, s1), ProbeSession.Diff(s1, s2));
        Assert.True(ev.SuggestsSetInGame);
        Assert.Contains("system.save", ev.SuggestionReason);
        Assert.StartsWith("the game keeps its settings only in a binary file (system.save)", ev.SuggestionReason);

        var record = ProbeStore.FromEvidence(ev);
        Assert.True(record.SuggestSetInGame);
        Assert.Equal(ev.SuggestionReason, record.SuggestReason);
    }

    [Fact]
    public void A_text_or_registry_setting_in_pass_b_means_no_suggestion()
    {
        var (p, s) = Make();
        using var _ = p;
        p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", "[Display]\nQuality=3\n");
        p.WriteBytes(p.Documents, @"Borderlands4\system.save", [0, 0, 1]);
        var s0 = s.Snapshot();
        var s1 = s.Snapshot();
        p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", "[Display]\nQuality=0\n");
        p.WriteBytes(p.Documents, @"Borderlands4\system.save", [0, 0, 7]);
        var s2 = s.Snapshot();

        var ev = s.ToEvidence(ProbeSession.Diff(s0, s1), ProbeSession.Diff(s1, s2));
        Assert.Equal(1, ev.ReadableChanges);
        Assert.False(ev.SuggestsSetInGame);
        Assert.Null(ev.SuggestionReason);
        Assert.False(ProbeStore.FromEvidence(ev).SuggestSetInGame);
    }

    [Fact]
    public void No_pass_b_or_nothing_changed_means_no_suggestion()
    {
        var (p, s) = Make();
        using var _ = p;
        var empty = s.ToEvidence(new ProbeDiff([], [], []), null);
        Assert.False(empty.SuggestsSetInGame);
        var none = s.ToEvidence(new ProbeDiff([], [], []), new ProbeDiff([], [], []));
        Assert.False(none.SuggestsSetInGame);
    }
}

/// <summary>What a flagged game still gets from a run (display) and what it never gets (settings writes, results, prompts).</summary>
[Collection("RunnerMutex")] // the "testgame" run mutex is process-wide
public sealed class SetInGameRunnerTests
{
    // no `processes:`, so the run just waits for the launched command (cmd /c exit) instead of a game exe
    private const string GameYaml = """
        name: Test Game
        store: other
        files:
          - id: user
            handler: ini
            path: "{T}/user.ini"
            createIfMissing: true
          - id: blob
            handler: file
            path: "{T}/blob.bin"
            autoSnapshot: perPreset
        presets:
          desktop:
            performance:
              label: Fast
              display: { width: 1280, height: 720, refresh: 60 }
              user: { S: { Shadows: 1 } }
        """;

    private static readonly string[] Command = ["cmd.exe", "/c", "exit 3"];

    private sealed class Spy
    {
        public readonly List<(DisplayModeDef Mode, string? Target)> Calls = [];

        public RunOptions Options() => new() { DisplayApply = (mode, target) => { Calls.Add((mode, target)); return null; } };
    }

    [Fact]
    public void A_flagged_game_keeps_its_display_but_gets_no_settings_written()
    {
        using var fx = new EngineFixture(GameYaml);
        fx.WriteGameFile("blob.bin", "the game's own store");
        LibraryService.SetInGame(fx.Store, EngineFixture.GameId, GameFlags.Create("opaque store"));
        var ctx = fx.Load("desktop", PresetRef.Recommended("performance"));
        Assert.Equal("opaque store", ctx.InGame!.Reason);
        var spy = new Spy();

        var code = Runner.Run(ctx, Command, prompt: false, spy.Options());

        Assert.Equal(3, code);
        var (mode, _) = Assert.Single(spy.Calls);                         // display still switches
        Assert.Equal(1280, mode.Width);
        Assert.False(File.Exists(fx.GameFile("user.ini")));                // no preset write
        Assert.Equal("the game's own store", fx.ReadGameFile("blob.bin"));
        Assert.Null(RunResults.Get(fx.Paths.DataDir, EngineFixture.GameId, "desktop")); // no result, so no drift and no prompt
        Assert.False(Directory.Exists(fx.Paths.AutoSnapshotsDir) && Directory.EnumerateFileSystemEntries(fx.Paths.AutoSnapshotsDir).Any()); // no auto copy captured
        Assert.False(File.Exists(Path.Combine(fx.Paths.DataDir, "sync-observations.json")));
    }

    [Fact]
    public void The_same_game_unflagged_is_written_and_recorded()
    {
        using var fx = new EngineFixture(GameYaml);
        var ctx = fx.Load("desktop", PresetRef.Recommended("performance"));
        Assert.Null(ctx.InGame);
        var spy = new Spy();

        Runner.Run(ctx, Command, prompt: false, spy.Options());

        Assert.Single(spy.Calls);
        Assert.Contains("Shadows", fx.ReadGameFile("user.ini"));
        Assert.NotNull(RunResults.Get(fx.Paths.DataDir, EngineFixture.GameId, "desktop"));
    }

    [Fact]
    public void A_flag_in_the_game_file_counts_too()
    {
        using var fx = new EngineFixture(GameYaml + "\nflagged:\n  reason: lab says so\n");
        var ctx = fx.Load("desktop", PresetRef.Recommended("performance"));
        Assert.Equal("lab says so", ctx.InGame!.Reason);

        Runner.Run(ctx, Command, prompt: false, new Spy().Options());
        Assert.False(File.Exists(fx.GameFile("user.ini")));
    }

    [Fact]
    public void A_flagged_test_preset_run_records_nothing_and_no_streak()
    {
        using var fx = new EngineFixture(GameYaml);
        LibraryService.SetInGame(fx.Store, EngineFixture.GameId, GameFlags.Create("opaque store"));
        var ctx = fx.Load("desktop", PresetRef.Recommended("performance"));

        Runner.Run(ctx, Command, prompt: false, new RunOptions { Verify = true, DisplayApply = (_, _) => null });

        Assert.Null(RunResults.Get(fx.Paths.DataDir, EngineFixture.GameId, "desktop", verifyOnly: true));
        Assert.Equal(0, RunResults.OverriddenStreak(fx.Paths.DataDir, EngineFixture.GameId, "desktop"));
    }

    [Fact]
    public void Plan_and_reapply_write_nothing_for_a_flagged_game()
    {
        using var fx = new EngineFixture(GameYaml);
        LibraryService.SetInGame(fx.Store, EngineFixture.GameId, GameFlags.Create("opaque store"));
        var ctx = fx.Load("desktop", PresetRef.Recommended("performance"));

        var plans = GameEngine.Plan(ctx);
        Assert.All(plans, p => { Assert.False(p.HasWork); Assert.Contains("set in-game", p.Skip); });

        var applied = new AppliedState();
        var details = new Runner.RunDetails();
        Runner.ReapplyAfterStart(ctx, applied, () => true, TimeSpan.Zero, details, _ => { });
        Assert.Empty(details.Reapplied);
        Assert.False(File.Exists(fx.GameFile("user.ini")));

        Assert.Empty(Runner.RecordResult(ctx, applied, verify: true, processSeen: true));
        Assert.Null(RunResults.Get(fx.Paths.DataDir, EngineFixture.GameId, "desktop"));
        Assert.Null(Runner.OfferCapture(ctx, applied));
    }

    [Fact]
    public void Clearing_the_flag_brings_the_writes_back()
    {
        using var fx = new EngineFixture(GameYaml);
        LibraryService.SetInGame(fx.Store, EngineFixture.GameId, GameFlags.Create("opaque store"));
        LibraryService.SetInGame(fx.Store, EngineFixture.GameId, null);
        var ctx = fx.Load("desktop", PresetRef.Recommended("performance"));
        Assert.Null(ctx.InGame);
        Assert.Contains(GameEngine.Plan(ctx), p => p.HasWork);
    }
}

/// <summary>The research-later issue: its own label and body, never the lab's.</summary>
public sealed class ResearchLaterTests
{
    private static GameLabContract.RequestInfo Info() => new(LabKind.Refresh, "forspoken", "Forspoken", "steam", "1680880", "desktop", "1.2.3");

    [Fact]
    public void The_body_has_the_reason_the_questions_and_no_lab_request_marker()
    {
        var body = GameLabContract.ResearchLaterBody(Info(), "the game rewrites its store", "<!-- optimus-vibe:lab-evidence v1 -->\n\n### Verify run\n```\nverdict: overridden\n```\n");
        Assert.StartsWith(GameLabContract.ResearchLaterMarker + "\n<!-- research-later {", body);
        Assert.DoesNotContain(GameLabContract.RequestMarker, body);                 // the lab workflow's gate keys on this marker
        Assert.Contains("**Game:** Forspoken (`steam:1680880`)", body);
        Assert.Contains("**Device:** desktop", body);
        Assert.Contains("the game rewrites its store", body);
        Assert.Contains("### Open questions", body);
        Assert.Equal(GameLabContract.ResearchLaterQuestions.Count, body.Split('\n').Count(l => l.StartsWith("- ")));
        Assert.True(body.IndexOf("### Open questions", StringComparison.Ordinal) < body.IndexOf("### Verify run", StringComparison.Ordinal));
        Assert.Equal("[research-later] Forspoken - set in-game", GameLabContract.ResearchLaterTitle(" Forspoken "));
        Assert.DoesNotContain(GameLabContract.LabelResearchLater, GameLabContract.Labels.Select(l => l.Name)); // the lab setup check is unchanged
    }

    [Fact]
    public async Task Filing_labels_the_issue_research_later_only_and_keeps_the_evidence_sanitized()
    {
        using var p = new LabProfile();
        p.Write(p.Documents, @"My Games\Borderlands 4\GameUserSettings.ini", "[A]\nb=1\n");
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        var o = p.Options();
        var options = new EvidenceOptions
        {
            Folders = o.Folders, Registry = o.Registry, UserProfile = o.UserProfile, UserName = o.UserName, MachineName = o.MachineName,
            VerifyRun = _ => $"verdict: overridden\nuser dir {p.User}\n",
        };

        var filed = await client.FileResearchLaterAsync(p.Target(), "desktop", "the game overrode the preset in 2 tests in a row", new EvidenceCollector(options));

        var issue = Assert.Single(fake.Issues);
        Assert.Equal(filed.Number, issue.Number);
        Assert.Equal(["research-later"], issue.Labels);                              // NOT game-lab: the workflow must not start
        Assert.StartsWith("[research-later] ", issue.Title);
        Assert.Contains("the game overrode the preset in 2 tests in a row", issue.Body);
        Assert.Contains("### Verify run", issue.Body);
        Assert.Contains("### File: {Documents}\\My Games\\Borderlands 4\\GameUserSettings.ini", issue.Body);
        Assert.DoesNotContain(p.User, issue.Body);
        Assert.DoesNotContain(GameLabContract.RequestMarker, issue.Body);
        Assert.Contains("research-later", fake.LabelNames);
        Assert.False(filed.Reused);
    }

    [Fact]
    public async Task A_research_task_from_normal_launches_says_normal_launch_not_verify_run()
    {
        using var p = new LabProfile();
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        var o = p.Options();
        var options = new EvidenceOptions
        {
            Folders = o.Folders, Registry = o.Registry, UserProfile = o.UserProfile, UserName = o.UserName, MachineName = o.MachineName,
            LaunchRun = _ => $"run: normal\nuser dir {p.User}\n",
        };

        await client.FileResearchLaterAsync(p.Target(), "desktop", "the game reset the preset after 2 launches", new EvidenceCollector(options));

        var issue = Assert.Single(fake.Issues);
        Assert.Contains("### Normal launch", issue.Body);
        Assert.DoesNotContain("### Verify run", issue.Body);
        Assert.DoesNotContain(p.User, issue.Body);
    }

    [Fact]
    public async Task A_closed_issue_reads_as_finished_and_an_unknown_one_as_null()
    {
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        var issue = fake.Add("t", "b", "research-later");

        Assert.True(await client.IsIssueOpenAsync(issue.Number));
        issue.Open = false;
        Assert.False(await client.IsIssueOpenAsync(issue.Number));
        Assert.Null(await client.IsIssueOpenAsync(99));
    }

    [Fact]
    public async Task The_lab_router_never_auto_requests_a_flagged_game()
    {
        using var p = new LabProfile();
        using var data = new TempDir();
        var fake = new FakeLabGitHub();
        var client = new GameLabClient(fake.Api(), FakeLabGitHub.Repo);
        var entry = new LibraryEntry
        {
            Key = "steam:1", Name = "G", Status = LibraryStatus.NeedsPresets, SetInGame = new FlagDef { Reason = "x" },
            Detected = new DetectedGame { Store = "steam", StoreId = "1", Name = "G", InstallDir = p.Install, Exes = ["g.exe"] },
        };
        var filed = 0;

        var r = await LabRouter.RouteAsync([entry], labReady: true, autoRequest: true,
            (e, ct) => { filed++; return client.FileRequestAsync(LabKind.New, LabTarget.From(e), "desktop", new EvidenceCollector(p.Options()), ct); },
            e => e.Count, new LabStateStore(data.Path));

        Assert.Equal(0, filed);
        Assert.Equal(0, r.Filed);
        Assert.Empty(fake.Issues);
    }
}
