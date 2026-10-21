using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;

namespace OptimusVibe.Tests;

/// <summary>run-results.json, the verdicts, the sentences, run flag parsing and the "Verify run" evidence section.</summary>
public sealed class RunResultsTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static RunResult Result(int drifted, bool verify = true, bool seen = true, int keys = 14, int snaps = 0, string game = "g", string device = "desktop")
    {
        var r = new RunResult
        {
            Utc = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), Game = game, Device = device, Preset = "recommended:performance",
            PresetLabel = "Performance", Mode = verify ? RunResult.VerifyMode : RunResult.NormalMode, AppliedKeys = keys, SnapshotFiles = snaps,
            ProcessSeen = seen, UserChanged = verify ? false : null,
        };
        for (var i = 0; i < drifted; i++) r.Drift.Add(new RunDrift { FileId = "gfx", Key = $"Key{i}", Applied = "1", Now = "2" });
        return r;
    }

    // ---------------------------------------------------------------- store

    [Fact]
    public void Record_ThenGet_RoundTripsPerGameAndDevice()
    {
        RunResults.Record(_tmp.Path, Result(2, game: "a", device: "desktop"));
        RunResults.Record(_tmp.Path, Result(0, game: "a", device: "ally-x"));
        RunResults.Record(_tmp.Path, Result(5, game: "b", device: "desktop"));

        var a = RunResults.Get(_tmp.Path, "a", "desktop")!;
        Assert.Equal(2, a.Drift.Count);
        Assert.Equal("gfx", a.Drift[0].FileId);
        Assert.Equal("Performance", a.PresetLabel);
        Assert.Equal(14, a.AppliedKeys);
        Assert.Empty(RunResults.Get(_tmp.Path, "a", "ally-x")!.Drift);
        Assert.Equal(5, RunResults.Get(_tmp.Path, "B", "desktop")!.Drift.Count); // game ids match case-insensitively
        Assert.Null(RunResults.Get(_tmp.Path, "c", "desktop"));
        Assert.Null(RunResults.Get(_tmp.Path, "a", "laptop"));
    }

    [Fact]
    public void Record_KeepsTheLastVerifyRun_NextToTheLastRun()
    {
        RunResults.Record(_tmp.Path, Result(3, verify: true));
        RunResults.Record(_tmp.Path, Result(0, verify: false));

        Assert.False(RunResults.Get(_tmp.Path, "g", "desktop")!.IsVerify);              // the last run is the normal one
        Assert.Equal(3, RunResults.Get(_tmp.Path, "g", "desktop", verifyOnly: true)!.Drift.Count);
        Assert.Null(RunResults.Get(_tmp.Path, "never-tested", "desktop", verifyOnly: true));
    }

    [Fact]
    public void Record_WritesAtomically_AndSerializesNoComputedProperties()
    {
        RunResults.Record(_tmp.Path, Result(1));
        Assert.False(File.Exists(RunResults.FileFor(_tmp.Path) + ".tmp"));
        var json = File.ReadAllText(RunResults.FileFor(_tmp.Path));
        Assert.Contains("\"appliedKeys\": 14", json);
        Assert.Contains("\"mode\": \"verify\"", json);
        Assert.Contains("\"userChanged\": false", json);
        Assert.DoesNotContain("isVerify", json);
        Assert.DoesNotContain("isWholeFile", json);
    }

    [Fact]
    public void CorruptOrMissingFile_IsIgnored()
    {
        Assert.Null(RunResults.Get(Path.Combine(_tmp.Path, "nothing-here"), "g", "desktop"));
        File.WriteAllText(RunResults.FileFor(_tmp.Path), "{ not json");
        Assert.Null(RunResults.Get(_tmp.Path, "g", "desktop"));
        RunResults.Record(_tmp.Path, Result(1)); // and a new result replaces the broken file
        Assert.NotNull(RunResults.Get(_tmp.Path, "g", "desktop"));
    }

    // ---------------------------------------------------------------- verdict

    [Fact]
    public void Verdict_NoDrift_IsHeld()
    {
        Assert.Equal(RunVerdict.Held, RunResults.Verdict(Result(0, verify: true)));
        Assert.Equal(RunVerdict.Held, RunResults.Verdict(Result(0, verify: false)));
    }

    [Fact]
    public void Verdict_DriftInAVerifyRun_IsOverridden_InKeysOrSnapshotFiles()
    {
        Assert.Equal(RunVerdict.Overridden, RunResults.Verdict(Result(10)));

        var snap = Result(0, keys: 0, snaps: 1);
        snap.Drift.Add(new RunDrift { FileId = "save", Key = RunDrift.WholeFile });
        Assert.Equal(RunVerdict.Overridden, RunResults.Verdict(snap));
    }

    [Fact]
    public void Verdict_IsUnknown_WhenTheGameNeverAppeared_NothingWasApplied_OrANormalRunChanged()
    {
        Assert.Equal(RunVerdict.Unknown, RunResults.Verdict(Result(0, seen: false)));
        Assert.Equal(RunVerdict.Unknown, RunResults.Verdict(Result(4, seen: false)));
        Assert.Equal(RunVerdict.Unknown, RunResults.Verdict(Result(0, keys: 0)));
        Assert.Equal(RunVerdict.Unknown, RunResults.Verdict(Result(4, verify: false))); // the player may have changed them
    }

    [Fact]
    public void DriftRatio_AndHint_NeedMoreThanHalf_OfANormalRun()
    {
        Assert.Equal(0.5, RunResults.DriftRatio(Result(7)));
        Assert.False(RunResults.NeedsHint(Result(7, verify: false)));   // exactly half: not "more than half"
        Assert.True(RunResults.NeedsHint(Result(8, verify: false)));
        Assert.False(RunResults.NeedsHint(Result(14, verify: true)));   // a verify run has its own card
        Assert.False(RunResults.NeedsHint(Result(14, verify: false, seen: false)));
        Assert.False(RunResults.NeedsHint(null));
        Assert.Equal(0, RunResults.DriftRatio(Result(0, keys: 0)));
        Assert.True(RunResults.NeedsHint(WholeFileDrift())); // a changed snapshot file is 1 of 1
    }

    private static RunResult WholeFileDrift()
    {
        var r = Result(0, verify: false, keys: 0, snaps: 1);
        r.Drift.Add(new RunDrift { FileId = "save", Key = RunDrift.WholeFile });
        return r;
    }

    // ---------------------------------------------------------------- words

    [Fact]
    public void Describe_SaysWhatHappened()
    {
        Assert.Equal("The game kept all 14 settings.", RunResults.Describe(Result(0)));
        Assert.Equal("The game kept the setting.", RunResults.Describe(Result(0, keys: 1)));
        Assert.Equal("The game changed 2 of 14 settings: Key0, Key1.", RunResults.Describe(Result(2)));
        Assert.Equal("The game changed 7 of 14 settings: Key0, Key1, Key2 and 4 more.", RunResults.Describe(Result(7), maxNames: 3));
        Assert.Contains("never started", RunResults.Describe(Result(0, seen: false)));
        Assert.Contains("wrote no settings", RunResults.Describe(Result(0, keys: 0)));
        Assert.Contains("isn't clear", RunResults.Describe(Result(3, verify: false)));
        Assert.Equal("Last launch: the game changed 10 of 14 preset settings. Test preset?", RunResults.HintLine(Result(10, verify: false)));
        Assert.Equal("save (whole file).", RunResults.Names(WholeFileDrift()));
    }

    [Fact]
    public void EvidenceText_ListsEveryChangedKey_AndTheContext()
    {
        var text = RunResults.EvidenceText(Result(2));
        Assert.Contains("verdict: overridden", text);
        Assert.Contains("preset: Performance (recommended:performance)", text);
        Assert.Contains("player changed nothing: yes", text);
        Assert.Contains("preset wrote: 14 setting(s), 0 whole file(s)", text);
        Assert.Contains("gfx Key0: 1 -> 2", text);
        Assert.Contains("gfx Key1: 1 -> 2", text);
        Assert.Contains("verdict: held", RunResults.EvidenceText(Result(0)));
        Assert.Contains("... and 3 more", RunResults.EvidenceText(Result(5), maxLines: 2));
    }

    // ---------------------------------------------------------------- run flags

    [Fact]
    public void RunArgs_ParsesVerify_AndTheRestOfTheWrapperFlags()
    {
        var a = RunArgs.Parse(["run", "doom", "--verify", "--preset", "recommended:quality", "--no-prompt", "--config", @"C:\cfg", "--no-big-picture",
            "--launch", "steam://rungameid/1", "--launch-arg", "-x", "--install-dir", @"D:\g", "--", "game.exe", "-w"]);
        Assert.Equal("doom", a.Game);
        Assert.True(a.Verify);
        Assert.False(a.Prompt);
        Assert.True(a.NoBigPicture);
        Assert.Equal("recommended:quality", a.Preset);
        Assert.Equal(@"C:\cfg", a.Config);
        Assert.Equal("steam://rungameid/1", a.Launch);
        Assert.Equal(["-x"], a.LaunchArgs);
        Assert.Equal(["game.exe", "-w"], a.Command);
        var o = a.ToOptions();
        Assert.True(o.Verify);
        Assert.Equal(@"D:\g", o.InstallDir);
        Assert.Equal("steam://rungameid/1", o.Launch);
    }

    [Fact]
    public void RunArgs_VerifyIsOffByDefault_AndCaseInsensitive()
    {
        Assert.False(RunArgs.Parse(["run", "doom"]).Verify);
        Assert.True(RunArgs.Parse(["run", "--VERIFY", "doom"]).Verify);
        Assert.True(RunArgs.Parse(["run", "doom"]).Prompt);
        Assert.False(RunArgs.Parse(["run", "doom", "--", "--verify"]).Verify); // after "--" it belongs to the game
        Assert.Equal(["--verify"], RunArgs.Parse(["run", "doom", "--", "--verify"]).Command);
    }

    // ---------------------------------------------------------------- the runner records drift

    private sealed class FakePrompt : ICapturePrompt
    {
        public int Asked;

        public CaptureDecision Ask(CaptureRequest request)
        {
            Asked++;
            return new CaptureDecision(CaptureChoice.DontSave);
        }
    }

    [Fact]
    public void RecordResult_StoresTheDrift_ForEveryRun_AndOfferCaptureReusesIt()
    {
        using var fx = new EngineFixture();
        fx.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        fx.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        var ctx = fx.Load("desktop", PresetRef.Recommended("performance"));
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        fx.WriteGameFile("user.ini", "[S]\r\nShadows=2\r\nTextures=3\r\n"); // the game "changes" one value

        var drift = Runner.RecordResult(ctx, applied, verify: false, processSeen: true);

        var stored = RunResults.Get(fx.Paths.DataDir, EngineFixture.GameId, "desktop")!;
        Assert.Equal(RunResult.NormalMode, stored.Mode);
        Assert.Equal(applied.Targets.Values.Sum(t => t.Count), stored.AppliedKeys);
        Assert.Equal(drift.Count, stored.Drift.Count);
        Assert.Contains(stored.Drift, d => d.FileId == "user" && d.Key.EndsWith("Shadows"));
        Assert.Null(stored.UserChanged);
        Assert.Null(RunResults.Get(fx.Paths.DataDir, EngineFixture.GameId, "desktop", verifyOnly: true));

        // the capture prompt asks about exactly that list, even though the file now looks different
        fx.WriteGameFile("user.ini", "[S]\r\nShadows=1.000000\r\nTextures=3\r\n");
        var prompt = new FakePrompt();
        Runner.OfferCapture(ctx, applied, prompt, drift);
        Assert.Equal(1, prompt.Asked);
    }

    [Fact]
    public void RecordResult_InAVerifyRun_StoresItAsTheVerifyResult_WithAVerdict()
    {
        using var fx = new EngineFixture();
        fx.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        fx.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        var ctx = fx.Load("desktop", PresetRef.Recommended("performance"));
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));

        Runner.RecordResult(ctx, applied, verify: true, processSeen: true); // nothing touched the files
        var held = RunResults.Get(fx.Paths.DataDir, EngineFixture.GameId, "desktop", verifyOnly: true)!;
        Assert.Equal(RunVerdict.Held, RunResults.Verdict(held));
        Assert.False(held.UserChanged);

        fx.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        Runner.RecordResult(ctx, applied, verify: true, processSeen: true);
        var over = RunResults.Get(fx.Paths.DataDir, EngineFixture.GameId, "desktop", verifyOnly: true)!;
        Assert.Equal(RunVerdict.Overridden, RunResults.Verdict(over));

        Runner.RecordResult(ctx, applied, verify: true, processSeen: false);
        Assert.Equal(RunVerdict.Unknown, RunResults.Verdict(RunResults.Get(fx.Paths.DataDir, EngineFixture.GameId, "desktop", verifyOnly: true)!));
    }

    [Fact]
    public void RunOptions_VerifyIsOffByDefault() => Assert.False(new RunOptions().Verify);

    // ---------------------------------------------------------------- lab evidence

    [Fact]
    public void VerifyRunSection_IsRendered_Sanitized_AndOnlyWhenGiven()
    {
        using var p = new LabProfile("alice");
        var r = Result(1);
        r.Drift[0].Now = $@"{p.Documents}\Saves\x.cfg";
        r.Drift.Add(new RunDrift { FileId = "gfx", Key = "AuthToken", Applied = "a", Now = "supersecretvalue" });
        var text = RunResults.EvidenceText(r);

        var opts = new EvidenceOptions
        {
            Folders = p.Folders, Registry = new LabFakeRegistry(), UserProfile = p.Root, UserName = p.User, MachineName = "TESTPC",
            VerifyRun = _ => text,
        };
        var target = new LabTarget { Name = "Test Game", Store = "steam", StoreId = "1", InstallDir = p.Install };
        var bundle = new EvidenceCollector(opts).Collect(target);

        var section = Assert.Single(bundle.Sections, s => s.Kind == EvidenceKind.VerifyRun);
        Assert.Equal("### Verify run", section.Heading);
        Assert.Contains("verdict: overridden", section.Body);
        Assert.Contains("gfx Key0: 1 -> {Documents}", section.Body);   // tokenized path
        Assert.DoesNotContain("alice", section.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supersecretvalue", section.Body);
        Assert.Contains("### Verify run\n", bundle.Render().ReplaceLineEndings("\n"));
        Assert.Equal(1, bundle.Count(EvidenceKind.VerifyRun));

        var without = new EvidenceCollector(new EvidenceOptions { Folders = p.Folders, Registry = new LabFakeRegistry() }).Collect(target);
        Assert.Equal(0, without.Count(EvidenceKind.VerifyRun));
        Assert.DoesNotContain("### Verify run", without.Render());
    }
}
