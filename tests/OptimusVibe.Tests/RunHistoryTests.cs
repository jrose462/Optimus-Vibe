using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.Tests;

/// <summary>
/// The run history groundwork of the organic game lab: the ring of normal runs, the capture answer and session length on every result, the
/// passive verdict and its streak, and the health inputs built from them.
/// </summary>
public sealed class RunHistoryTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static RunResult Normal(int drifted = 0, int? sec = 600, CaptureAnswer? capture = CaptureAnswer.NotAsked, int keys = 10, string now = "2",
        int minute = 0, bool seen = true, string? build = null)
    {
        var r = new RunResult
        {
            Utc = T0.AddMinutes(minute), Game = "g", Device = "desktop", Preset = "recommended:performance", Mode = RunResult.NormalMode,
            AppliedKeys = keys, ProcessSeen = seen, DurationSec = sec, Capture = capture, Build = build,
        };
        for (var i = 0; i < drifted; i++) r.Drift.Add(new RunDrift { FileId = "gfx", Key = $"Key{i}", Applied = "1", Now = now });
        return r;
    }

    private static RunResult Verify(int drifted, int minute = 0, string? build = null)
    {
        var r = Normal(drifted, minute: minute, build: build);
        r.Mode = RunResult.VerifyMode;
        r.UserChanged = false;
        r.Capture = CaptureAnswer.NotAsked;
        return r;
    }

    // ---------------------------------------------------------------- ring and back-compat

    [Fact]
    public void Record_KeepsTheLastNormalRuns_OldestFirst_CappedAtEight()
    {
        for (var i = 0; i < RunResults.RecentCap + 3; i++) RunResults.Record(_tmp.Path, Normal(minute: i));

        var recent = RunResults.Recent(_tmp.Path, "g", "desktop");
        Assert.Equal(RunResults.RecentCap, recent.Count);
        Assert.Equal(T0.AddMinutes(3), recent[0].Utc);
        Assert.Equal(T0.AddMinutes(RunResults.RecentCap + 2), recent[^1].Utc);
        Assert.Equal(recent[^1].Utc, RunResults.Get(_tmp.Path, "g", "desktop")!.Utc);
    }

    [Fact]
    public void Record_LeavesVerifyRunsOutOfTheRing()
    {
        RunResults.Record(_tmp.Path, Normal(minute: 0));
        RunResults.Record(_tmp.Path, Verify(0, minute: 1));

        Assert.Single(RunResults.Recent(_tmp.Path, "g", "desktop"));
        Assert.True(RunResults.Get(_tmp.Path, "g", "desktop")!.IsVerify);
        Assert.Empty(RunResults.Recent(_tmp.Path, "never-played", "desktop"));
        Assert.Null(RunResults.Entry(_tmp.Path, "never-played", "desktop"));
    }

    [Fact]
    public void OldFile_WithoutTheNewFields_StillLoads_AndGrowsARing()
    {
        File.WriteAllText(RunResults.FileFor(_tmp.Path), """
            {
              "games": {
                "g": {
                  "desktop": {
                    "last": { "utc": "2026-10-01T10:00:00Z", "game": "g", "device": "desktop", "mode": "normal", "appliedKeys": 4, "drift": [], "processSeen": true },
                    "lastVerify": { "utc": "2026-09-30T10:00:00Z", "game": "g", "device": "desktop", "mode": "verify", "appliedKeys": 4, "drift": [], "processSeen": true, "userChanged": false },
                    "overriddenStreak": 1
                  }
                }
              }
            }
            """);

        var e = RunResults.Entry(_tmp.Path, "g", "desktop")!;
        Assert.Empty(e.Recent);
        Assert.Equal(0, e.PassiveOverriddenStreak);
        Assert.Equal(1, e.OverriddenStreak);
        Assert.Null(e.Last!.Capture);
        Assert.Null(e.Last.DurationSec);
        Assert.Null(e.Last.Build);
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(e.Last, null)); // no session length: not counted as held

        RunResults.Record(_tmp.Path, Normal());
        e = RunResults.Entry(_tmp.Path, "g", "desktop")!;
        Assert.Single(e.Recent);
        Assert.Equal(1, e.OverriddenStreak); // the verify streak is untouched by normal runs
        Assert.NotNull(e.LastVerify);
    }

    [Fact]
    public void NewFields_AreStored_WithTheAnswerAsCamelCaseText()
    {
        RunResults.Record(_tmp.Path, Normal(2, sec: 1234, capture: CaptureAnswer.DontAskAgain, build: "steam:42"));

        var json = File.ReadAllText(RunResults.FileFor(_tmp.Path));
        Assert.Contains("\"capture\": \"dontAskAgain\"", json);
        Assert.Contains("\"durationSec\": 1234", json);
        Assert.Contains("\"build\": \"steam:42\"", json);
        Assert.Contains("\"recent\": [", json);
        var back = RunResults.Get(_tmp.Path, "g", "desktop")!;
        Assert.Equal(CaptureAnswer.DontAskAgain, back.Capture);
        Assert.Equal(1234, back.DurationSec);
        Assert.Equal("steam:42", back.Build);
    }

    // ---------------------------------------------------------------- passive verdict

    [Fact]
    public void Passive_Held_NeedsALongEnoughSession_WithNoDrift()
    {
        Assert.Equal(PassiveVerdict.Held, RunResults.Passive(Normal(sec: RunResults.PassiveHeldMinSec), null));
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(sec: RunResults.PassiveHeldMinSec - 1), null));
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(sec: null), null));
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(seen: false), null));
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(keys: 0), null));
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Verify(0), null)); // a test has its own verdict
    }

    [Fact]
    public void Passive_SameValueTwiceInARow_IsOverridden()
    {
        var first = Normal(1, capture: CaptureAnswer.DontSave);
        var second = Normal(1, capture: CaptureAnswer.DontSave);

        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(first, null));       // once proves nothing (the player may have done it)
        Assert.Equal(PassiveVerdict.Overridden, RunResults.Passive(second, first));
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(1, now: "7"), first)); // a different value is not the game's own
    }

    [Fact]
    public void Passive_ASavedCapture_IsTheUsersWork_AndBreaksTheRecurrence()
    {
        var saved = Normal(1, capture: CaptureAnswer.Saved);
        Assert.Equal(PassiveVerdict.UserSaved, RunResults.Passive(saved, null));
        Assert.Equal(PassiveVerdict.UserSaved, RunResults.Passive(Normal(1, capture: CaptureAnswer.Saved), Normal(1)));
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(1), saved)); // the previous drift was kept on purpose
    }

    [Fact]
    public void Passive_MostOfThePresetInAShortUnsavedSession_IsOverridden()
    {
        Assert.Equal(PassiveVerdict.Overridden, RunResults.Passive(Normal(5, sec: 120), null));   // half of 10, 2 minutes
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(4, sec: 120), null));      // less than half
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(5, sec: RunResults.ShortSessionSec), null)); // a long session
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(Normal(5, sec: null), null));     // length unknown
    }

    [Fact]
    public void Passive_DriftOnlyInFilesALauncherSyncRewrote_PointsAtSync()
    {
        var r = Normal(5, sec: 120);
        r.Reapplied = ["GFX"];
        Assert.Equal(PassiveVerdict.SyncSuspect, RunResults.Passive(r, null));

        r.Drift.Add(new RunDrift { FileId = "other", Key = "K", Applied = "1", Now = "2" });
        Assert.Equal(PassiveVerdict.Overridden, RunResults.Passive(r, null)); // not only re-applied files
    }

    [Fact]
    public void PassiveStreak_CountsOverrides_ResetsOnHeld_IgnoresSavedAndUnclear()
    {
        Assert.Equal(1, RunResults.NextPassiveStreak(0, PassiveVerdict.Overridden));
        Assert.Equal(0, RunResults.NextPassiveStreak(3, PassiveVerdict.Held));
        Assert.Equal(2, RunResults.NextPassiveStreak(2, PassiveVerdict.UserSaved));
        Assert.Equal(2, RunResults.NextPassiveStreak(2, PassiveVerdict.Unknown));
        Assert.Equal(2, RunResults.NextPassiveStreak(2, PassiveVerdict.SyncSuspect));

        RunResults.Record(_tmp.Path, Normal(1, minute: 0));
        RunResults.Record(_tmp.Path, Normal(1, minute: 1));               // same value again: 1
        RunResults.Record(_tmp.Path, Normal(1, minute: 2));               // and again: 2
        Assert.Equal(2, RunResults.PassiveOverriddenStreak(_tmp.Path, "g", "desktop"));
        Assert.True(GameFlags.SuggestsFromPassive(2));
        RunResults.Record(_tmp.Path, Normal(1, minute: 3, capture: CaptureAnswer.Saved));
        Assert.Equal(2, RunResults.PassiveOverriddenStreak(_tmp.Path, "g", "desktop"));
        RunResults.Record(_tmp.Path, Normal(0, minute: 4));
        Assert.Equal(0, RunResults.PassiveOverriddenStreak(_tmp.Path, "g", "desktop"));
        Assert.Equal(0, RunResults.OverriddenStreak(_tmp.Path, "g", "desktop")); // the verify streak never moves on normal runs
    }

    [Fact]
    public void ResetStreak_ClearsTheVerifyAndThePassiveStreak()
    {
        RunResults.Record(_tmp.Path, Verify(2));
        RunResults.Record(_tmp.Path, Normal(1, minute: 1));
        RunResults.Record(_tmp.Path, Normal(1, minute: 2));
        Assert.Equal(1, RunResults.OverriddenStreak(_tmp.Path, "g", "desktop"));
        Assert.Equal(1, RunResults.PassiveOverriddenStreak(_tmp.Path, "g", "desktop"));

        RunResults.ResetStreak(_tmp.Path, "g", "desktop");

        Assert.Equal(0, RunResults.OverriddenStreak(_tmp.Path, "g", "desktop"));
        Assert.Equal(0, RunResults.PassiveOverriddenStreak(_tmp.Path, "g", "desktop"));
        Assert.Equal(2, RunResults.Recent(_tmp.Path, "g", "desktop").Count); // the history stays
    }

    [Fact]
    public void PassiveHeldStreak_CountsTheNewestHeldRunsInARow()
    {
        Assert.Equal(0, RunResults.PassiveHeldStreak([]));
        Assert.Equal(3, RunResults.PassiveHeldStreak([Normal(1), Normal(0), Normal(0), Normal(0)]));
        Assert.Equal(0, RunResults.PassiveHeldStreak([Normal(0), Normal(0), Normal(0, sec: 10)]));
        Assert.Equal("the game changed the preset's settings after your last 2 launches", GameFlags.PassiveSuggestionReason(2));
    }

    // ---------------------------------------------------------------- the runner

    private sealed class FakePrompt(CaptureDecision decision) : ICapturePrompt
    {
        public int Asked;

        public CaptureDecision Ask(CaptureRequest request)
        {
            Asked++;
            return decision;
        }
    }

    private static (GameContext Ctx, AppliedState Applied) PlayAndTweak(EngineFixture f)
    {
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(f.GameFile("blob.bin"), [1]);
        var ctx = f.Load("desktop", PresetRef.Recommended("performance"));
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        f.WriteGameFile("user.ini", "[S]\r\nShadows=1.000000\r\nTextures=4\r\n");
        return (ctx, applied);
    }

    [Theory]
    [InlineData(CaptureChoice.DontSave, CaptureAnswer.DontSave)]
    [InlineData(CaptureChoice.DontAskAgain, CaptureAnswer.DontAskAgain)]
    [InlineData(CaptureChoice.Save, CaptureAnswer.Saved)]
    [InlineData(CaptureChoice.SaveAs, CaptureAnswer.Saved)]
    public void AskCapture_ReportsWhatThePlayerAnswered(CaptureChoice choice, CaptureAnswer expected)
    {
        using var f = new EngineFixture();
        var (ctx, applied) = PlayAndTweak(f);

        var outcome = Runner.AskCapture(ctx, applied, new FakePrompt(new CaptureDecision(choice, "Mine")));

        Assert.Equal(expected, outcome.Answer);
        Assert.Equal(expected == CaptureAnswer.Saved, outcome.Save is not null);
    }

    [Fact]
    public void AskCapture_NotAsked_WithoutDrift_AndOff_WhenTurnedOffForTheGame()
    {
        using var f = new EngineFixture();
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(f.GameFile("blob.bin"), [1]);
        var ctx = f.Load("desktop", PresetRef.Recommended("performance"));
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        var prompt = new FakePrompt(new CaptureDecision(CaptureChoice.Save));

        Assert.Equal(CaptureAnswer.NotAsked, Runner.AskCapture(ctx, applied, prompt).Answer);

        CapturePromptStore.SetSkipped(f.Paths.DataDir, EngineFixture.GameId, true);
        f.WriteGameFile("user.ini", "[S]\r\nShadows=1\r\nTextures=4\r\n");
        Assert.Equal(CaptureAnswer.Off, Runner.AskCapture(ctx, applied, prompt).Answer);
        Assert.Equal(0, prompt.Asked);
    }

    [Fact]
    public void AskCapture_AThrowingPrompt_IsNotAsked_AndNeverThrows()
    {
        using var f = new EngineFixture();
        var (ctx, applied) = PlayAndTweak(f);
        Assert.Equal(CaptureAnswer.NotAsked, Runner.AskCapture(ctx, applied, new ThrowingPrompt()).Answer);
    }

    private sealed class ThrowingPrompt : ICapturePrompt
    {
        public CaptureDecision Ask(CaptureRequest request) => throw new InvalidOperationException("no window");
    }

    [Fact]
    public void MeasureThenStore_RecordsTheAnswer_TheSessionLength_AndTheBuild()
    {
        using var f = new EngineFixture();
        var (ctx, applied) = PlayAndTweak(f);
        var seen = T0.AddMinutes(1);
        var details = new Runner.RunDetails { StartedUtc = T0, GameSeenUtc = seen, EndedUtc = seen.AddMinutes(25), Build = "steam:99" };

        var (drift, result) = Runner.MeasureResult(ctx, applied, verify: false, processSeen: true, details);
        Assert.NotEmpty(drift);
        Assert.Null(RunResults.Get(f.Paths.DataDir, EngineFixture.GameId, "desktop")); // nothing stored before the prompt was answered

        Runner.StoreResult(ctx, result, CaptureAnswer.DontSave);

        var stored = RunResults.Get(f.Paths.DataDir, EngineFixture.GameId, "desktop")!;
        Assert.Equal(CaptureAnswer.DontSave, stored.Capture);
        Assert.Equal(25 * 60, stored.DurationSec);
        Assert.Equal("steam:99", stored.Build);
        Assert.Single(RunResults.Recent(f.Paths.DataDir, EngineFixture.GameId, "desktop"));
    }

    [Fact]
    public void RunDetails_Duration_FromTheGameBeingSeen_ElseFromTheLaunch()
    {
        Assert.Equal(600, new Runner.RunDetails { StartedUtc = T0, GameSeenUtc = T0.AddSeconds(30), EndedUtc = T0.AddSeconds(630) }.DurationSec);
        Assert.Equal(630, new Runner.RunDetails { StartedUtc = T0, EndedUtc = T0.AddSeconds(630) }.DurationSec);
        Assert.Null(new Runner.RunDetails { StartedUtc = T0 }.DurationSec);
        Assert.Null(new Runner.RunDetails().DurationSec);
    }

    [Fact]
    public void MeasureResult_AGameThatNeverAppeared_HasNoSessionLength()
    {
        using var f = new EngineFixture();
        var (ctx, applied) = PlayAndTweak(f);
        var details = new Runner.RunDetails { StartedUtc = T0, EndedUtc = T0.AddMinutes(3) };

        var (_, result) = Runner.MeasureResult(ctx, applied, verify: false, processSeen: false, details);

        Assert.Null(result!.DurationSec);
    }

    [Fact]
    public void RunnerRun_StoresANormalRun_InTheRing_WithItsLength_AndNotAsked()
    {
        using var f = new EngineFixture(EngineFixture.GameYaml.Replace("processes: [Test.exe]", "processes: []"));
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(f.GameFile("blob.bin"), [1]);
        string[] cmd = [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c", "exit", "0"];

        Assert.Equal(0, Runner.Run(f.Load(), cmd, prompt: false, new RunOptions { DisplayApply = (_, _) => null }));

        var stored = Assert.Single(RunResults.Recent(f.Paths.DataDir, EngineFixture.GameId, "desktop"));
        Assert.Equal(CaptureAnswer.NotAsked, stored.Capture);
        Assert.NotNull(stored.DurationSec); // an exe the wrapper waited on: from the launch to its exit
        Assert.True(stored.DurationSec < 60);
        Assert.Equal(PassiveVerdict.Unknown, RunResults.Passive(stored, null)); // too short to count as held
    }

    // ---------------------------------------------------------------- health inputs

    [Fact]
    public void HealthInputs_FromRuns_ReadsTheHistoryAndBothStreaks()
    {
        RunResults.Record(_tmp.Path, Verify(3, minute: 0));
        RunResults.Record(_tmp.Path, Normal(1, minute: 1));
        RunResults.Record(_tmp.Path, Normal(1, minute: 2));

        var h = HealthInputs.FromRuns(_tmp.Path, "g", "desktop");

        Assert.Equal(2, h.Recent.Count);
        Assert.Equal(1, h.OverriddenStreak);
        Assert.Equal(1, h.PassiveOverriddenStreak);
        Assert.Equal(PassiveVerdict.Overridden, h.LastPassive);
        Assert.False(h.VerifyHeld);
        Assert.False(h.SuggestsFlagFromRuns);
        Assert.True((h with { PassiveOverriddenStreak = 2 }).SuggestsFlagFromRuns);
        Assert.True((h with { OverriddenStreak = 2 }).SuggestsFlagFromRuns);

        var none = HealthInputs.FromRuns(_tmp.Path, "other", "desktop");
        Assert.Empty(none.Recent);
        Assert.Null(none.Last);
        Assert.Equal(PassiveVerdict.Unknown, none.LastPassive);
        Assert.Equal(0, none.PassiveHeldStreak);
    }

    private static HealthInputs Inputs(params RunResult[] recent) => new() { GameId = "g", Device = "desktop", Recent = recent };

    [Fact]
    public void HealthInputs_GameUpdated_ComparesTheNewestKnownGoodBuild_WithTheInstalledOne()
    {
        var made = new PresetsMark { MadeUtc = T0, BuildId = "steam:100" };
        var h = Inputs() with { Presets = made, CurrentBuild = "steam:100" };
        Assert.Equal("steam:100", h.BaselineBuild);
        Assert.False(h.GameUpdated);

        Assert.True((h with { CurrentBuild = "steam:101" }).GameUpdated);
        Assert.True((h with { CurrentBuild = "steam:101" }).GameUpdatedByStore);
        Assert.False((h with { CurrentBuild = null }).GameUpdated);                  // unknown now: no claim
        Assert.False((h with { CurrentBuild = "exe:game.exe:1:2" }).GameUpdated);    // different kinds: no claim

        // a held test on the new build moves the baseline
        var verified = h with { CurrentBuild = "steam:101", LastVerify = Verify(0, minute: 30, build: "steam:101") };
        Assert.Equal("steam:101", verified.BaselineBuild);
        Assert.False(verified.GameUpdated);

        // an overridden test does not
        Assert.True((h with { CurrentBuild = "steam:101", LastVerify = Verify(2, minute: 30, build: "steam:101") }).GameUpdated);

        // a passively held normal run on the new build moves it too, a run that changed settings does not
        Assert.False((h with { CurrentBuild = "steam:101", Recent = [Normal(0, minute: 40, build: "steam:101")] }).GameUpdated);
        Assert.True((h with { CurrentBuild = "steam:101", Recent = [Normal(3, minute: 40, build: "steam:101")] }).GameUpdated);

        // no mark and nothing held: nothing to compare with
        Assert.Null(Inputs(Normal(3, build: "steam:1")).BaselineBuild);
    }

    [Fact]
    public void HealthInputs_AnExeFingerprintChange_IsAnUpdate_ButNotByTheStore()
    {
        var h = Inputs() with { Presets = new PresetsMark { MadeUtc = T0, BuildId = "exe:game.exe:10:20260101000000" }, CurrentBuild = "exe:game.exe:11:20261001000000" };
        Assert.True(h.GameUpdated);
        Assert.False(h.GameUpdatedByStore);
    }
}
