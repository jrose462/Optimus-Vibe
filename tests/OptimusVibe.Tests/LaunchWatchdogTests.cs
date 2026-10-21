using OptimusVibe.Engine;
using OptimusVibe.Launch;
using OptimusVibe.Logging;

namespace OptimusVibe.Tests;

[Collection("RunnerMutex")]
public sealed class LaunchWatchdogTests
{
    /// <summary>A fake clock that advances one poll per sleep.</summary>
    private sealed class Clock
    {
        public DateTime Now = new(2026, 1, 1);
        public int Polls;
        public bool Sleep(TimeSpan t) { Now += t; Polls++; return false; }
    }

    private static WatchdogInputs Inputs(Clock c, Func<bool> game, TimeSpan? timeout = null, Func<bool>? launcher = null,
        Func<TimeSpan, string?>? early = null, Action<Func<bool>>? detected = null, List<(LaunchPhase, string)>? log = null) => new()
    {
        GameRunning = game,
        LauncherRunning = launcher ?? (() => false),
        EarlyFailure = early,
        OnGameDetected = detected,
        OnStatus = log is null ? null : (p, m) => log.Add((p, m)),
        StartupTimeout = timeout ?? TimeSpan.FromMinutes(3),
        PollInterval = TimeSpan.FromSeconds(2),
        Now = () => c.Now,
        Sleep = c.Sleep,
    };

    [Fact]
    public void LateGameExe_IsStillCaught_AndLauncherWaitIsReported()
    {
        Log.Quiet = true;
        var c = new Clock();
        var log = new List<(LaunchPhase, string)>();
        // EA-style: the launcher prompt sits for 5 minutes, then the game starts and runs for 10 polls
        var outcome = LaunchWatchdog.Watch(Inputs(c,
            game: () => c.Polls >= 150 && c.Polls < 160,
            timeout: TimeSpan.FromMinutes(10),
            launcher: () => c.Polls < 150, log: log));

        Assert.True(outcome.GameSeen);
        Assert.Equal(LaunchPhase.Exited, outcome.Phase);
        Assert.Equal([LaunchPhase.Launching, LaunchPhase.WaitingOnLauncher, LaunchPhase.GameRunning], log.Select(l => l.Item1).ToArray());
    }

    [Fact]
    public void NoGame_GivesUpAfterTheTimeout_WithAReason()
    {
        Log.Quiet = true;
        var c = new Clock();
        var outcome = LaunchWatchdog.Watch(Inputs(c, () => false, TimeSpan.FromSeconds(60)));
        Assert.False(outcome.GameSeen);
        Assert.Equal(LaunchPhase.GaveUp, outcome.Phase);
        Assert.Contains("never appeared", outcome.Reason);
        Assert.InRange(c.Polls, 29, 32);
    }

    [Fact]
    public void ARunningStarter_PreventsGivingUp()
    {
        Log.Quiet = true;
        var c = new Clock();
        var stub = new WatchdogInputs
        {
            GameRunning = () => c.Polls >= 200 && c.Polls < 203, StarterGone = () => false, StartupTimeout = TimeSpan.FromSeconds(60),
            PollInterval = TimeSpan.FromSeconds(2), Now = () => c.Now, Sleep = c.Sleep,
        };
        Assert.True(LaunchWatchdog.Watch(stub).GameSeen);
    }

    [Fact]
    public void EarlyFailure_EndsTheWaitImmediately()
    {
        Log.Quiet = true;
        var c = new Clock();
        var log = new List<(LaunchPhase, string)>();
        var outcome = LaunchWatchdog.Watch(Inputs(c, () => false, TimeSpan.FromMinutes(10),
            early: e => e >= TimeSpan.FromSeconds(20) ? LauncherLogs.BattleNetFailure : null, log: log));
        Assert.Equal(LaunchPhase.Failed, outcome.Phase);
        Assert.Equal(LauncherLogs.BattleNetFailure, outcome.Reason);
        Assert.InRange(c.Polls, 10, 11);
        Assert.Equal(LaunchPhase.Failed, log[^1].Item1);
    }

    [Fact]
    public void Cancel_StopsTheWait()
    {
        Log.Quiet = true;
        using var cts = new CancellationTokenSource();
        var c = new Clock();
        var inputs = new WatchdogInputs
        {
            GameRunning = () => false, StartupTimeout = TimeSpan.FromHours(1), PollInterval = TimeSpan.FromSeconds(2), Now = () => c.Now,
            Sleep = t => { c.Now += t; if (++c.Polls == 3) cts.Cancel(); return false; },
        };
        var outcome = LaunchWatchdog.Watch(inputs, cts.Token);
        Assert.Equal(LaunchPhase.Cancelled, outcome.Phase);
        Assert.Equal(3, c.Polls);
    }

    [Fact]
    public void OnGameDetected_RunsOnce_AndItsFailureDoesNotStopTheWatch()
    {
        Log.Quiet = true;
        var c = new Clock();
        var calls = 0;
        var outcome = LaunchWatchdog.Watch(Inputs(c, () => c.Polls is >= 2 and < 6, detected: _ => { calls++; throw new IOException("boom"); }));
        Assert.Equal(1, calls);
        Assert.Equal(LaunchPhase.Exited, outcome.Phase);
    }

    [Fact]
    public void ProbeThatThrows_IsTreatedAsNotRunning()
    {
        Log.Quiet = true;
        var c = new Clock();
        var outcome = LaunchWatchdog.Watch(Inputs(c, () => throw new InvalidOperationException("x"), TimeSpan.FromSeconds(10)));
        Assert.Equal(LaunchPhase.GaveUp, outcome.Phase);
    }

    // ---------------------------------------------------------------- launcher logs

    [Fact]
    public void BattleNetProbe_NeedsLaunchBinaryAfterTheMark()
    {
        using var tmp = new TempDir();
        var dir = tmp.Dir("logs");
        var log = Path.Combine(dir, "battle.net-1.log");
        File.WriteAllText(log, "old LaunchBinary line from an earlier game\n");
        var marks = LauncherLogs.Mark(dir);

        Assert.Null(LauncherLogs.BattleNetProbe(dir, marks, TimeSpan.FromSeconds(5))); // too early: no verdict
        Assert.Equal(LauncherLogs.BattleNetFailure, LauncherLogs.BattleNetProbe(dir, marks, TimeSpan.FromSeconds(25))); // only old lines
        File.AppendAllText(log, "game selected\nLaunchBinary Fen\n");
        Assert.Null(LauncherLogs.BattleNetProbe(dir, marks, TimeSpan.FromSeconds(25)));
    }

    [Fact]
    public void BattleNetProbe_NoLogs_NoVerdict()
    {
        using var tmp = new TempDir();
        Assert.Null(LauncherLogs.BattleNetProbe(tmp.Dir("none"), new Dictionary<string, long>(), TimeSpan.FromMinutes(5)));
        Assert.Null(LauncherLogs.BattleNetProbe(tmp.File("missing"), new Dictionary<string, long>(), TimeSpan.FromMinutes(5)));
    }

    // ---------------------------------------------------------------- apply after process start

    private const string Yaml = """
        name: Test Game
        store: other
        cloudSyncsSettings: true
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
    public void Reapply_RestoresOnlyWhatTheLauncherSyncRewrote()
    {
        Log.Quiet = true;
        using var fx = new EngineFixture(Yaml);
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n");
        fx.WriteGameFile("late.ini", "[L]\nQuality=2\n");
        var ctx = fx.Load();
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        Assert.Contains("Shadows=1", fx.ReadGameFile("user.ini"));

        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n"); // EA CloudSync puts the old file back
        var details = new Runner.RunDetails();
        Runner.ReapplyAfterStart(ctx, applied, () => true, TimeSpan.Zero, details, _ => { });

        Assert.Contains("Shadows=1", fx.ReadGameFile("user.ini"));
        Assert.Equal(["user"], details.Reapplied);
        Assert.Empty(details.Late);
        Assert.Empty(GameEngine.FindDrift(ctx, applied));
    }

    [Fact]
    public void Reapply_NothingDrifted_WritesNothing()
    {
        Log.Quiet = true;
        using var fx = new EngineFixture(Yaml);
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n");
        fx.WriteGameFile("late.ini", "[L]\nQuality=0\n");
        var ctx = fx.Load();
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        var details = new Runner.RunDetails();
        Runner.ReapplyAfterStart(ctx, applied, () => true, TimeSpan.Zero, details, _ => { });
        Assert.Empty(details.Reapplied);
    }

    [Fact]
    public void Reapply_FileAppearingAfterBoot_IsAppliedAndMarkedLate()
    {
        Log.Quiet = true;
        using var fx = new EngineFixture(Yaml);
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n"); // late.ini does not exist yet: first-run game
        var ctx = fx.Load();
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        Assert.False(applied.Targets.ContainsKey("late"));

        var sleeps = 0;
        var details = new Runner.RunDetails();
        Runner.ReapplyAfterStart(ctx, applied, () => true, TimeSpan.Zero, details, _ =>
        {
            if (++sleeps == 3) fx.WriteGameFile("late.ini", "[L]\nQuality=0\n"); // the game writes it a few seconds in
        }, TimeSpan.FromSeconds(30));

        Assert.Contains("Quality=2", fx.ReadGameFile("late.ini"));
        Assert.Equal(["late"], details.Late);
        Assert.True(applied.Targets.ContainsKey("late"));
    }

    [Fact]
    public void Reapply_GivesUpWaitingForAFileThatNeverAppears()
    {
        Log.Quiet = true;
        using var fx = new EngineFixture(Yaml);
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n");
        var ctx = fx.Load();
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        var sleeps = 0;
        Runner.ReapplyAfterStart(ctx, applied, () => true, TimeSpan.Zero, new Runner.RunDetails(), _ => sleeps++, TimeSpan.FromSeconds(10));
        Assert.InRange(sleeps, 4, 6);
        Assert.False(File.Exists(fx.GameFile("late.ini")));
    }

    [Fact]
    public void Reapply_StopsWhenTheGameHasExited()
    {
        Log.Quiet = true;
        using var fx = new EngineFixture(Yaml);
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n");
        fx.WriteGameFile("late.ini", "[L]\nQuality=2\n");
        var ctx = fx.Load();
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n");
        Runner.ReapplyAfterStart(ctx, applied, () => false, TimeSpan.Zero, new Runner.RunDetails(), _ => { });
        Assert.Contains("Shadows=3", fx.ReadGameFile("user.ini"));
    }

    [Fact]
    public void Reapply_CancelledOrExitedDuringTheSettleWindow_StopsWithoutWriting()
    {
        Log.Quiet = true;
        using var fx = new EngineFixture(Yaml);
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n");
        fx.WriteGameFile("late.ini", "[L]\nQuality=2\n");
        var ctx = fx.Load();
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        fx.WriteGameFile("user.ini", "[S]\nShadows=3\n");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var details = new Runner.RunDetails();
        Runner.ReapplyAfterStart(ctx, applied, () => true, TimeSpan.FromMinutes(5), details, null, null, cts.Token); // default sleep returns at once
        Assert.Contains("Shadows=3", fx.ReadGameFile("user.ini"));
        Assert.Empty(details.Reapplied);

        var started = DateTime.UtcNow;
        Runner.ReapplyAfterStart(ctx, applied, () => false, TimeSpan.FromMinutes(5), details); // the game is gone: no 5 minute sleep
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}
