using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Logging;

namespace OptimusVibe.Tests;

/// <summary>
/// "Also check presets on my next launch" (<see cref="LabState.VerifyNext"/>): a manual opt-in per game on the game page. The player's own next
/// launch through the wrapper runs as a test of the preset (stored as a verify run, no capture prompt) and the opt-in is cleared afterwards.
/// </summary>
[Collection("RunnerMutex")] // the "testgame" run mutex is process-wide
public sealed class VerifyNextLaunchTests
{
    public VerifyNextLaunchTests() => Log.Quiet = true;

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

    private static readonly string[] Exit0 = [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c", "exit", "0"];

    private static int Run(EngineFixture fx, bool verify = false) =>
        Runner.Run(fx.Load("desktop", PresetRef.Recommended("performance")), Exit0, prompt: true,
            new RunOptions { Verify = verify, DisplayApply = (_, _) => null });

    [Fact]
    public void TheStore_TurnsItOnAndOff_PerGame()
    {
        using var tmp = new TempDir();
        var store = new LabStateStore(tmp.Path);
        Assert.False(store.VerifiesNext("g"));
        Assert.True(store.SetVerifyNext("g", true));
        Assert.True(store.VerifiesNext("g"));
        Assert.True(store.VerifiesNext("G")); // game ids ignore case
        Assert.False(store.VerifiesNext("h"));
        Assert.True(store.SetVerifyNext("g", false));
        Assert.False(store.VerifiesNext("g"));
    }

    [Fact]
    public void TheNextNormalLaunch_RunsAsATest_WithoutThePrompt_AndClearsIt()
    {
        using var fx = new EngineFixture(GameYaml);
        var store = new LabStateStore(fx.Paths.DataDir);
        store.SetVerifyNext(EngineFixture.GameId, true);

        // prompt: true, and the run is a normal one: only the opt-in makes it a test (a capture prompt here would block the test run)
        Assert.Equal(0, Run(fx));

        var entry = RunResults.Entry(fx.Paths.DataDir, EngineFixture.GameId, "desktop")!;
        Assert.NotNull(entry.LastVerify);
        Assert.True(entry.LastVerify!.IsVerify);
        Assert.Empty(entry.Recent); // not a normal launch: no passive verdict, no post-session card
        Assert.False(store.VerifiesNext(EngineFixture.GameId));

        // the launch after it is a normal one again
        Assert.Equal(0, Run(fx));
        Assert.Single(RunResults.Entry(fx.Paths.DataDir, EngineFixture.GameId, "desktop")!.Recent);
    }

    [Fact]
    public void ATestPresetRun_AlsoCountsAsTheCheck()
    {
        using var fx = new EngineFixture(GameYaml);
        var store = new LabStateStore(fx.Paths.DataDir);
        store.SetVerifyNext(EngineFixture.GameId, true);
        Assert.Equal(0, Run(fx, verify: true));
        Assert.False(store.VerifiesNext(EngineFixture.GameId));
    }

    [Fact]
    public void HealthInputs_ReadTheOptIn()
    {
        using var fx = new EngineFixture(GameYaml);
        new LabStateStore(fx.Paths.DataDir).SetVerifyNext(EngineFixture.GameId, true);
        Assert.True(HealthInputs.FromContext(fx.Load("desktop", PresetRef.Recommended("performance"))).VerifyNextLaunch);
    }
}
