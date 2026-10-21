using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;
using OptimusVibe.Logging;

namespace OptimusVibe.Tests;

/// <summary>
/// The rules of the passive verdict and the run store that keep run history honest: only cloud-sync rewrites point at sync, whole files and
/// unasked drift never count as the game's own, a run survives a wrapper killed at the capture prompt, every run knows its build, and the
/// file holds up to several writers.
/// </summary>
[Collection("RunnerMutex")] // Runner.Run takes the process-wide "testgame" run mutex
public sealed class RunHistoryRulesTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public RunHistoryRulesTests() => Log.Quiet = true;

    public void Dispose() => _tmp.Dispose();

    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static RunResult Normal(int drifted = 1, CaptureAnswer? capture = CaptureAnswer.DontSave, int minute = 0, int? sec = 600, string now = "Low",
        string? build = null)
    {
        var r = new RunResult
        {
            Utc = T0.AddMinutes(minute), Game = "g", Device = "desktop", Preset = "recommended:performance", Mode = RunResult.NormalMode,
            AppliedKeys = 10, ProcessSeen = true, DurationSec = sec, Capture = capture, Build = build,
        };
        for (var i = 0; i < drifted; i++) r.Drift.Add(new RunDrift { FileId = "gfx", Key = $"Quality{i}", Applied = "High", Now = now });
        return r;
    }

    private static RunResult WholeFile(int minute)
    {
        var r = new RunResult
        {
            Utc = T0.AddMinutes(minute), Game = "g", Device = "desktop", Mode = RunResult.NormalMode, SnapshotFiles = 2, AppliedKeys = 8,
            ProcessSeen = true, DurationSec = 1800, Capture = CaptureAnswer.DontSave,
        };
        r.Drift.Add(new RunDrift { FileId = "save", Key = RunDrift.WholeFile });
        return r;
    }

    // ---------------------------------------------------------------- sync suspect only for cloud-sync rewrites

    [Fact]
    public void AfterProcessStartGame_RewritingTheSameValue_IsOverridden_NotSyncSuspect()
    {
        // applyTiming afterProcessStart: every file is written after the game is seen, so every file is in Reapplied, none in SyncReapplied
        RunResult Run(int minute)
        {
            var r = Normal(minute: minute);
            r.Reapplied = ["gfx"];
            return r;
        }

        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Run(0), null));
        Assert.Equal(PassiveVerdict.Overridden, RunResults.Passive(Run(1), Run(0)));

        RunResults.Record(_tmp.Path, Run(0));
        RunResults.Record(_tmp.Path, Run(1));
        RunResults.Record(_tmp.Path, Run(2));
        Assert.Equal(2, RunResults.PassiveOverriddenStreak(_tmp.Path, "g", "desktop"));
        Assert.True(HealthInputs.FromRuns(_tmp.Path, "g", "desktop").SuggestsFlagFromRuns);
    }

    [Fact]
    public void ALateCreatedFileTheGameRewrites_IsNotBlamedOnSync()
    {
        var r = Normal(minute: 1);
        r.Reapplied = ["gfx"];
        r.LateFiles = ["gfx"];
        Assert.NotEqual(PassiveVerdict.SyncSuspect, RunResults.Passive(r, null));
        Assert.Equal(PassiveVerdict.Overridden, RunResults.Passive(r, Normal(minute: 0)));
    }

    private const string ReapplyYaml = """
        name: Test Game
        store: other
        processes: [Test.exe]
        files:
          - id: user
            handler: ini
            path: "{T}/user.ini"
          - id: late
            handler: ini
            path: "{T}/late.ini"
        presets:
          desktop:
            p:
              user: { S: { Shadows: 1 } }
              late: { L: { Quality: 2 } }
        """;

    [Fact]
    public void Reapply_OnlyARewriteOfWhatThePreLaunchApplyWrote_CountsAsSync()
    {
        using var fx = new EngineFixture(ReapplyYaml);
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n");
        fx.WriteGameFile("late.ini", "[L]\nQuality=0\n");
        var ctx = fx.Load();

        // afterProcessStart: nothing written before launch, so the first write after start is not sync
        var first = new Runner.RunDetails();
        Runner.ReapplyAfterStart(ctx, new AppliedState(), () => true, TimeSpan.Zero, first, _ => { });
        Assert.Equal(["late", "user"], first.Reapplied.Order());
        Assert.Empty(first.SyncReapplied);

        // before (or both): written before launch, then a launcher's sync put the old file back
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n");
        var synced = new Runner.RunDetails();
        Runner.ReapplyAfterStart(ctx, applied, () => true, TimeSpan.Zero, synced, _ => { });
        Assert.Equal(["user"], synced.SyncReapplied);

        var (_, result) = Runner.MeasureResult(ctx, applied, verify: false, processSeen: true, synced);
        Assert.Equal(["user"], result!.SyncReapplied);
    }

    [Fact]
    public void Reapply_AFileMissingBeforeLaunch_IsLate_NotSync()
    {
        using var fx = new EngineFixture(ReapplyYaml);
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n"); // late.ini does not exist yet
        var ctx = fx.Load();
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        var sleeps = 0;
        var details = new Runner.RunDetails();
        Runner.ReapplyAfterStart(ctx, applied, () => true, TimeSpan.Zero, details, _ =>
        {
            if (++sleeps == 2) fx.WriteGameFile("late.ini", "[L]\nQuality=0\n");
        }, TimeSpan.FromSeconds(30));

        Assert.Contains("late", details.Reapplied);
        Assert.DoesNotContain("late", details.SyncReapplied);
    }

    // ---------------------------------------------------------------- whole files never recur

    [Fact]
    public void TwoWholeFileDriftsInARow_AreNotTheSameValue()
    {
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(WholeFile(1), WholeFile(0)));

        RunResults.Record(_tmp.Path, WholeFile(0));
        RunResults.Record(_tmp.Path, WholeFile(1));
        RunResults.Record(_tmp.Path, WholeFile(2));
        Assert.Equal(0, RunResults.PassiveOverriddenStreak(_tmp.Path, "g", "desktop"));

        // a keyed setting in the same runs still can recur
        var keyed = WholeFile(4);
        keyed.Drift.Add(new RunDrift { FileId = "gfx", Key = "Quality", Applied = "High", Now = "Low" });
        var keyedBefore = WholeFile(3);
        keyedBefore.Drift.Add(new RunDrift { FileId = "gfx", Key = "Quality", Applied = "High", Now = "Low" });
        Assert.Equal(PassiveVerdict.Overridden, RunResults.Passive(keyed, keyedBefore));
    }

    // ---------------------------------------------------------------- only asked-and-declined drift counts

    [Theory]
    [InlineData(CaptureAnswer.Off)]
    [InlineData(CaptureAnswer.DontAskAgain)]
    [InlineData(CaptureAnswer.NotAsked)]
    [InlineData(null)]
    public void DriftThePlayerWasNotAskedAbout_NeverCountsAgainstThePreset(CaptureAnswer? answer)
    {
        // same value twice
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(capture: answer, minute: 1), Normal(capture: answer, minute: 0)));
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(capture: answer, minute: 1), Normal(capture: CaptureAnswer.DontSave, minute: 0)));
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(capture: CaptureAnswer.DontSave, minute: 1), Normal(capture: answer, minute: 0)));
        // most of the preset in a short session
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(6, capture: answer, sec: 120), null));
        // held is still held: no drift says the game kept everything whatever the player did
        Assert.Equal(PassiveVerdict.Held, RunResults.Passive(Normal(0, capture: answer), null));

        for (var i = 0; i < 3; i++) RunResults.Record(_tmp.Path, Normal(capture: answer ?? CaptureAnswer.NotAsked, minute: i));
        Assert.Equal(0, RunResults.PassiveOverriddenStreak(_tmp.Path, "g", "desktop"));
    }

    [Fact]
    public void AskedAndDeclined_StillCounts()
    {
        Assert.Equal(PassiveVerdict.Overridden, RunResults.Passive(Normal(minute: 1), Normal(minute: 0)));
        Assert.Equal(PassiveVerdict.Overridden, RunResults.Passive(Normal(6, sec: 120), null));
    }

    // ---------------------------------------------------------------- stored before the prompt

    [Fact]
    public void APendingRun_IsStoredWithoutAStreakStep_AndTheAnswerTakesIt()
    {
        RunResults.Record(_tmp.Path, Normal(minute: 0));
        var pending = Normal(capture: null, minute: 1);
        RunResults.Record(_tmp.Path, pending);

        // the wrapper could be killed here: the run is there, nothing counted yet
        var entry = RunResults.Entry(_tmp.Path, "g", "desktop")!;
        Assert.Equal(2, entry.Recent.Count);
        Assert.Null(entry.Last!.Capture);
        Assert.Equal(0, entry.PassiveOverriddenStreak);

        RunResults.SetCapture(_tmp.Path, pending, CaptureAnswer.DontSave);

        entry = RunResults.Entry(_tmp.Path, "g", "desktop")!;
        Assert.Equal(CaptureAnswer.DontSave, entry.Recent[^1].Capture);
        Assert.Equal(CaptureAnswer.DontSave, entry.Last!.Capture);
        Assert.Equal(1, entry.PassiveOverriddenStreak);
        Assert.Equal(2, entry.Recent.Count);

        RunResults.SetCapture(_tmp.Path, pending, CaptureAnswer.DontSave); // already final: no second step
        Assert.Equal(1, RunResults.PassiveOverriddenStreak(_tmp.Path, "g", "desktop"));
    }

    [Fact]
    public void SetCapture_ForARunThatWasNeverStored_StoresIt()
    {
        RunResults.SetCapture(_tmp.Path, Normal(capture: null, minute: 3), CaptureAnswer.Saved);
        var stored = Assert.Single(RunResults.Recent(_tmp.Path, "g", "desktop"));
        Assert.Equal(CaptureAnswer.Saved, stored.Capture);
    }

    [Fact]
    public void Runner_StorePendingThenFinish_KeepsTheRunIfTheAnswerNeverComes()
    {
        using var f = new EngineFixture();
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(f.GameFile("blob.bin"), [1]);
        var ctx = f.Load("desktop", PresetRef.Recommended("performance"));
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        f.WriteGameFile("user.ini", "[S]\r\nShadows=1\r\nTextures=4\r\n");
        var details = new Runner.RunDetails { StartedUtc = T0, GameSeenUtc = T0, EndedUtc = T0.AddMinutes(20) };

        var (drift, result) = Runner.MeasureResult(ctx, applied, verify: false, processSeen: true, details);
        Assert.NotEmpty(drift);
        Runner.StoreResult(ctx, result, capture: null);

        var stored = RunResults.Get(f.Paths.DataDir, EngineFixture.GameId, "desktop")!; // killed at the prompt: still recorded
        Assert.Null(stored.Capture);
        Assert.Equal(20 * 60, stored.DurationSec);

        Runner.FinishResult(ctx, result, CaptureAnswer.DontAskAgain);
        Assert.Equal(CaptureAnswer.DontAskAgain, RunResults.Get(f.Paths.DataDir, EngineFixture.GameId, "desktop")!.Capture);
        Assert.Single(RunResults.Recent(f.Paths.DataDir, EngineFixture.GameId, "desktop"));
    }

    // ---------------------------------------------------------------- every run knows its build

    [Fact]
    public void RunBuild_ANonSteamGameWithNoInstallFolder_UsesItsDetectedInstall_LikeTheLab()
    {
        var install = _tmp.Dir("EpicGame");
        FakeFiles.Touch(Path.Combine(install, "EpicGame.exe"), 700);
        var game = new GameDef { Id = "epicgame", Name = "Epic Game", Store = "epic" };
        var detected = new DetectedGame { Store = "epic", StoreId = "Fox", Name = "Epic Game", InstallDir = install };

        var build = Runner.RunBuild(game, installDir: null, _ => detected);

        Assert.StartsWith(GameBuild.ExePrefix, build);
        Assert.Equal(GameBuild.Of(detected), build); // the same string the presets mark and the library scan record
        Assert.Equal(build, Runner.RunBuild(game, install, _ => throw new InvalidOperationException("not needed")));
        Assert.Null(Runner.RunBuild(game, null, _ => null));
        Assert.Null(Runner.RunBuild(game, null, _ => throw new InvalidOperationException("scan failed"))); // never throws
    }

    [Fact]
    public void RunnerRun_ANonSteamGameLaunchedFromItsYaml_RecordsTheDetectedBuild()
    {
        using var f = new EngineFixture(EngineFixture.GameYaml.Replace("processes: [Test.exe]", "processes: []"));
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(f.GameFile("blob.bin"), [1]);
        var install = _tmp.Dir("Installed");
        FakeFiles.Touch(Path.Combine(install, "Game.exe"), 900);
        var detected = new DetectedGame { Store = "gog", StoreId = "1", Name = "Test Game", InstallDir = install };
        string[] cmd = [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c", "exit", "0"];

        Runner.Run(f.Load(), cmd, prompt: false, new RunOptions { DisplayApply = (_, _) => null, FindInstall = _ => detected });

        var stored = Assert.Single(RunResults.Recent(f.Paths.DataDir, EngineFixture.GameId, "desktop"));
        Assert.Equal(GameBuild.Of(detected), stored.Build);
    }

    [Fact]
    public void RunnerRun_FillsInTheBuildOfPresetsThatArrivedWithoutOne()
    {
        using var f = new EngineFixture(EngineFixture.GameYaml.Replace("processes: [Test.exe]", "processes: []"));
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(f.GameFile("blob.bin"), [1]);
        var install = _tmp.Dir("Installed2");
        FakeFiles.Touch(Path.Combine(install, "Game.exe"), 901);
        var detected = new DetectedGame { Store = "gog", StoreId = "1", Name = "Test Game", InstallDir = install };
        var lab = new LabStateStore(f.Paths.DataDir);
        lab.MarkPresets(EngineFixture.GameId, T0, build: null);
        string[] cmd = [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c", "exit", "0"];

        Runner.Run(f.Load(), cmd, prompt: false, new RunOptions { DisplayApply = (_, _) => null, FindInstall = _ => detected });

        Assert.Equal(GameBuild.Of(detected), lab.Load().PresetsFor(EngineFixture.GameId)!.BuildId);
    }

    [Fact]
    public void HealthInputs_AHeldRunOfAFingerprintOnlyGame_ClearsGameUpdated()
    {
        var mark = new PresetsMark { MadeUtc = T0, BuildId = "exe:game.exe:123:20261001000000" };
        var h = new HealthInputs { GameId = "g", Device = "desktop", Presets = mark, CurrentBuild = "exe:game.exe:456:20261015000000" };
        Assert.True(h.GameUpdated);

        var held = h with { Recent = [Normal(0, minute: 60, build: "exe:game.exe:456:20261015000000")] };
        Assert.Equal("exe:game.exe:456:20261015000000", held.BaselineBuild);
        Assert.False(held.GameUpdated);
    }

    [Fact]
    public void HealthInputs_BaselinePrefersABuildOfTheInstalledKind()
    {
        // presets on steam:100; a held run whose manifest read failed recorded a fingerprint; the game is now steam:101
        var h = new HealthInputs
        {
            GameId = "g", Device = "desktop", Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:100" }, CurrentBuild = "steam:101",
            Recent = [Normal(0, minute: 60, build: "exe:game.exe:1:20261001000000")],
        };
        Assert.Equal("steam:100", h.BaselineBuild);
        Assert.True(h.GameUpdated);
        Assert.True(h.GameUpdatedByStore);

        // only another kind known: still that one
        Assert.Equal("exe:game.exe:1:20261001000000", (h with { Presets = null }).BaselineBuild);
        // unknown now: newest of any kind
        Assert.Equal("exe:game.exe:1:20261001000000", (h with { CurrentBuild = null }).BaselineBuild);
    }

    // ---------------------------------------------------------------- several writers

    [Fact]
    public void Record_WritesThroughItsOwnTempFile_NotOneAnotherWriterHolds()
    {
        RunResults.Record(_tmp.Path, Normal(minute: 0));
        var shared = RunResults.FileFor(_tmp.Path) + ".tmp"; // the fixed name every writer used before: another process mid-write
        using (new FileStream(shared, FileMode.Create, FileAccess.Write, FileShare.None))
            RunResults.Record(_tmp.Path, Normal(minute: 1));

        Assert.Equal(2, RunResults.Recent(_tmp.Path, "g", "desktop").Count);
        Assert.Equal([shared], Directory.GetFiles(_tmp.Path, "*.tmp")); // its own temp file is gone
    }

    [Fact]
    public void Record_FromManyThreads_LosesNoUpdate()
    {
        Parallel.For(0, 16, i =>
        {
            var r = Normal(minute: i);
            r.Game = $"g{i % 4}";
            RunResults.Record(_tmp.Path, r);
        });

        Assert.Equal(16, Enumerable.Range(0, 4).Sum(g => RunResults.Recent(_tmp.Path, $"g{g}", "desktop").Count));
    }
}
