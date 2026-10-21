using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;
using OptimusVibe.Outbox;

namespace OptimusVibe.Tests;

/// <summary>
/// The "set in-game" flag inside the organic lab: where a suggestion comes from (<see cref="GameHealth.FlagEvidence"/>), that what led to a
/// flag never suggests it again once lifted (<see cref="RunResultEntry.EvidenceSinceUtc"/>, stamped by a flagged run and by "Manage settings
/// again"), and that a flagged game's waiting automatic requests are withdrawn.
/// </summary>
public sealed class FlagIntegrationTests : IDisposable
{
    private const string Preset = "recommended:performance";
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private readonly TempDir _data = new();

    public void Dispose() => _data.Dispose();

    private static HealthInputs In() => new()
    {
        GameId = "g", Device = "desktop", HasConfig = true, HasPresets = true, LabReady = true, CanLaunch = true, CurrentPreset = Preset,
    };

    private static RunResult Run(int day, int drifted = 0, bool verify = false, int? sec = 1800)
    {
        var r = new RunResult
        {
            Utc = T0.AddDays(day), Game = "g", Device = "desktop", Preset = Preset, Mode = verify ? RunResult.VerifyMode : RunResult.NormalMode,
            AppliedKeys = 10, ProcessSeen = true, DurationSec = verify ? null : sec, UserChanged = verify ? false : null,
            Capture = verify ? null : drifted > 0 ? CaptureAnswer.DontSave : CaptureAnswer.NotAsked,
        };
        for (var i = 0; i < drifted; i++) r.Drift.Add(new RunDrift { FileId = "gfx", Key = $"Quality{i}", Applied = "High", Now = "Low" });
        return r;
    }

    /// <summary>A launch the passive check rates overridden on its own: most of the preset drifted in a short, unsaved session.</summary>
    private static RunResult Overridden(int day) => Run(day, drifted: 6, sec: 120);

    private static ProbeRecord BinaryOnlyMapping(int day) =>
        new() { Utc = T0.AddDays(day), SuggestSetInGame = true, OpaqueFiles = ["{LocalAppData}/Game/system.save"] };

    // ---------------------------------------------------------------- where the suggestion comes from

    [Fact]
    public void FlagEvidence_FromTests_KeepsTheLastOverriddenTest()
    {
        var test = Run(1, drifted: 4, verify: true);
        var f = GameHealth.FlagEvidence(In() with { OverriddenStreak = 2, LastVerify = test })!;
        Assert.Equal(FlagSource.Verify, f.Source);
        Assert.Same(test, f.Run);
        Assert.Equal(GameFlags.VerifySuggestionReason(2), f.Reason);
        Assert.StartsWith("Test preset: the game changed 4 of 10 setting(s) in the last test", f.Summary);
        Assert.Contains("run: verify", f.RunText);
    }

    [Fact]
    public void FlagEvidence_FromLaunches_KeepsTheNewestLaunchTheGameReset()
    {
        var newest = Overridden(1);
        var f = GameHealth.FlagEvidence(In() with { Recent = [Overridden(0), newest, Run(2, sec: 20)] })!;
        Assert.Equal(FlagSource.Passive, f.Source);
        Assert.Same(newest, f.Run);
        Assert.Equal(GameFlags.PassiveSuggestionReason(2), f.Reason);
        Assert.StartsWith("Normal launches: the game changed 6 of 10 setting(s) in the last launch", f.Summary);
        Assert.Contains("run: normal", f.RunText);
    }

    [Fact]
    public void FlagEvidence_FromAMappingRun_HasNoRun_AndKeepsTheReason()
    {
        var mapping = BinaryOnlyMapping(0);
        mapping.SuggestReason = "only system.save changed";
        var f = GameHealth.FlagEvidence(In() with { Mapping = mapping })!;
        Assert.Equal(FlagSource.Mapping, f.Source);
        Assert.Null(f.Run);
        Assert.Null(f.RunText);
        Assert.Equal("only system.save changed", f.Summary);
    }

    [Fact]
    public void FlagSuggestion_IsTheEvidencesReason_AndNothingWhenFlagged()
    {
        var inputs = In() with { OverriddenStreak = 2 };
        Assert.Equal(GameHealth.FlagEvidence(inputs)!.Reason, GameHealth.FlagSuggestion(inputs));
        Assert.Null(GameHealth.FlagEvidence(inputs with { Flag = GameFlags.Create("x") }));
        Assert.Null(GameHealth.FlagEvidence(In()));
    }

    // ---------------------------------------------------------------- what led to the flag doesn't suggest it again

    [Fact]
    public void EvidenceFromBeforeTheMark_DoesNotSuggestTheFlagAgain()
    {
        var since = T0.AddDays(5);
        var passive = In() with { Recent = [Overridden(0), Overridden(1)] };
        Assert.NotNull(GameHealth.FlagSuggestion(passive));
        Assert.Null(GameHealth.FlagSuggestion(passive with { EvidenceSinceUtc = since }));

        // the stored verify streak counts only through a test that is newer than the mark
        var tests = In() with { OverriddenStreak = 2, LastVerify = Run(1, drifted: 3, verify: true) };
        Assert.NotNull(GameHealth.FlagSuggestion(tests));
        Assert.Null(GameHealth.FlagSuggestion(tests with { EvidenceSinceUtc = since }));

        var mapped = In() with { Mapping = BinaryOnlyMapping(1) };
        Assert.NotNull(GameHealth.FlagSuggestion(mapped));
        Assert.Null(GameHealth.FlagSuggestion(mapped with { EvidenceSinceUtc = since }));
        Assert.NotNull(GameHealth.FlagSuggestion(mapped with { Mapping = BinaryOnlyMapping(6), EvidenceSinceUtc = since })); // a new mapping run counts
    }

    [Fact]
    public void AfterTheMark_TheHealthStartsOver_AndFreshLaunchesCount()
    {
        var old = In() with { Recent = [Overridden(0), Overridden(1)], EvidenceSinceUtc = T0.AddDays(5) };
        var h = GameHealth.Evaluate(old);
        Assert.Equal(HealthState.NotChecked, h.State);
        Assert.Equal(HealthAction.None, h.Suggested);

        var fresh = old with { Recent = [.. old.Recent, Overridden(6), Overridden(7)] };
        var again = GameHealth.Evaluate(fresh);
        Assert.Equal(HealthState.CantBeSetFromOutside, again.State);
        Assert.Equal(GameFlags.PassiveSuggestionReason(2), again.FlagReason);

        var held = old with { Recent = [.. old.Recent, Run(6), Run(7), Run(8)] };
        Assert.Equal(HealthState.Working, GameHealth.Evaluate(held).State);
    }

    [Fact]
    public void ManagingSettingsAgain_StampsTheMark_EvenWithoutHistory()
    {
        RunResults.Record(_data.Path, Overridden(0));
        RunResults.Record(_data.Path, Overridden(1));
        RunResults.ResetStreak(_data.Path, "g", "desktop", T0.AddDays(3));
        var inputs = HealthInputs.FromRuns(_data.Path, "g", "desktop");
        Assert.Equal(T0.AddDays(3), inputs.EvidenceSinceUtc);
        Assert.Equal(2, inputs.Recent.Count); // the history stays
        Assert.Null(GameHealth.FlagSuggestion(inputs with { HasConfig = true, HasPresets = true, CurrentPreset = Preset }));

        // a game mapped but never launched here: the entry is created so the mapping run stops counting too
        RunResults.ResetStreak(_data.Path, "mapped", "desktop", T0.AddDays(3));
        Assert.Equal(T0.AddDays(3), RunResults.Entry(_data.Path, "mapped", "desktop")!.EvidenceSinceUtc);
    }

    [Fact]
    public void MarkFlagged_StampsOnce_ClearsTheStreaks_AndMovesOnlyPastNewerRuns()
    {
        RunResults.Record(_data.Path, Run(0, drifted: 3, verify: true));
        RunResults.Record(_data.Path, Run(1, drifted: 3, verify: true));
        Assert.Equal(2, RunResults.OverriddenStreak(_data.Path, "g", "desktop"));

        RunResults.MarkFlagged(_data.Path, "g", "desktop", T0.AddDays(2));
        Assert.Equal(T0.AddDays(2), RunResults.Entry(_data.Path, "g", "desktop")!.EvidenceSinceUtc);
        Assert.Equal(0, RunResults.OverriddenStreak(_data.Path, "g", "desktop"));

        // a flagged run records nothing, so the next flagged run leaves the mark where it is
        RunResults.MarkFlagged(_data.Path, "g", "desktop", T0.AddDays(4));
        Assert.Equal(T0.AddDays(2), RunResults.Entry(_data.Path, "g", "desktop")!.EvidenceSinceUtc);

        // lifted, played, flagged again: the mark moves past the newer runs
        RunResults.Record(_data.Path, Overridden(5));
        RunResults.MarkFlagged(_data.Path, "g", "desktop", T0.AddDays(6));
        Assert.Equal(T0.AddDays(6), RunResults.Entry(_data.Path, "g", "desktop")!.EvidenceSinceUtc);

        RunResults.MarkFlagged(_data.Path, "", "desktop"); // never throws
    }

    [Fact]
    public void AResultsFileWithoutTheMark_StillLoads_WithEverythingCounting()
    {
        File.WriteAllText(RunResults.FileFor(_data.Path),
            """{"games":{"g":{"desktop":{"overriddenStreak":2,"recent":[]}}}}""");
        var inputs = HealthInputs.FromRuns(_data.Path, "g", "desktop");
        Assert.Null(inputs.EvidenceSinceUtc);
        Assert.Equal(2, inputs.OverriddenStreak);
    }

    // ---------------------------------------------------------------- no automatic findings for a flagged game

    [Fact]
    public void Flagging_WithdrawsOnlyThatGamesAutomaticRequests()
    {
        var store = new OutboxStore(_data.Path);
        var info = new GameLabContract.RequestInfo(LabKind.Refresh, "g", "Game g", "steam", "g", "desktop", "1.0");
        var mine = store.Add(OutboxItem.ForLabRequest("o/r", "main", new PreparedLabRequest(info, ["e"]), automatic: true));
        var other = store.Add(OutboxItem.ForLabRequest("o/r", "main", new PreparedLabRequest(info with { GameId = "h", StoreId = "h" }, ["e"]), automatic: true));
        var byHand = store.Add(OutboxItem.ForLabRequest("o/r", "main", new PreparedLabRequest(info, ["e"])));

        Assert.Equal(1, AutoFindings.WithdrawQueued(store, "G"));
        Assert.Null(store.Get(mine.Id));
        Assert.NotNull(store.Get(other.Id));
        Assert.NotNull(store.Get(byHand.Id));
        Assert.Equal(0, AutoFindings.WithdrawQueued(store, "g"));
    }
}

/// <summary>A flagged run stamps the evidence mark, so lifting the flag later starts the game's health over.</summary>
[Collection("RunnerMutex")] // the "testgame" run mutex is process-wide
public sealed class FlagIntegrationRunnerTests
{
    private const string GameYaml = """
        name: Test Game
        store: other
        files:
          - id: user
            handler: ini
            path: "{T}/user.ini"
            createIfMissing: true
        presets:
          desktop:
            performance:
              label: Fast
              user: { S: { Shadows: 1 } }
        """;

    [Fact]
    public void AFlaggedRun_StampsTheMark_SoTheOldOverridesStopCountingOnceLifted()
    {
        using var fx = new EngineFixture(GameYaml);
        var before = DateTime.UtcNow.AddDays(-2);
        foreach (var day in new[] { 0, 1 })
        {
            var r = new RunResult
            {
                Utc = before.AddDays(day), Game = EngineFixture.GameId, Device = "desktop", Preset = PresetRef.Recommended("performance").ToString(),
                Mode = RunResult.NormalMode, AppliedKeys = 10, ProcessSeen = true, DurationSec = 120, Capture = CaptureAnswer.DontSave,
            };
            for (var i = 0; i < 6; i++) r.Drift.Add(new RunDrift { FileId = "user", Key = $"K{i}", Applied = "1", Now = "0" });
            RunResults.Record(fx.Paths.DataDir, r);
        }
        Assert.NotNull(GameHealth.FlagSuggestion(HealthInputs.FromContext(fx.Load("desktop", PresetRef.Recommended("performance")))));

        LibraryService.SetInGame(fx.Store, EngineFixture.GameId, GameFlags.Create("keeps resetting"));
        Runner.Run(fx.Load("desktop", PresetRef.Recommended("performance")), ["cmd.exe", "/c", "exit 0"], prompt: false,
            new RunOptions { DisplayApply = (_, _) => null });
        Assert.NotNull(RunResults.Entry(fx.Paths.DataDir, EngineFixture.GameId, "desktop")!.EvidenceSinceUtc);

        // lifted on the other device (only library.yaml changes): this one does not suggest the flag again from what led to it
        LibraryService.SetInGame(fx.Store, EngineFixture.GameId, null);
        var inputs = HealthInputs.FromContext(fx.Load("desktop", PresetRef.Recommended("performance")));
        Assert.Null(inputs.Flag);
        Assert.Null(GameHealth.FlagSuggestion(inputs));
        Assert.Equal(HealthState.NotChecked, GameHealth.Evaluate(inputs).State);
    }
}
