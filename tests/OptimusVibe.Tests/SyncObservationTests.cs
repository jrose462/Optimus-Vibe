using OptimusVibe.CloudSync;
using OptimusVibe.Launch;

namespace OptimusVibe.Tests;

[Collection("RunnerMutex")] // Runner.Run holds the process-wide "testgame" mutex, like RunnerGuardTests
public sealed class SyncObservationTests : IDisposable
{
    private const string Yaml = """
        name: Test Game
        store: other
        files:
          - id: user
            handler: ini
            path: "{T}/user.ini"
          - id: blob
            handler: file
            path: "{T}/blob.bin"
        presets:
          desktop:
            quality:
              user: { S: { A: 1, B: 1, C: 1, D: 1, E: 1 } }
          ally-x:
            performance:
              label: Performance
              user: { S: { A: 3, B: 3, C: 3, D: 3, E: 3 } }
        """;

    private readonly EngineFixture _f = new(Yaml);

    public SyncObservationTests() => _f.WriteGameFile("user.ini", Ini(1, 1, 1, 1, 1));

    public void Dispose() => _f.Dispose();

    private static string Ini(params int[] values) =>
        "[S]\r\n" + string.Join("", values.Select((v, i) => $"{(char)('A' + i)}={v}\r\n"));

    private ObservationSummary Summary() =>
        SyncObservationStore.For(_f.Paths).Summary(EngineFixture.GameId, "desktop");

    [Fact]
    public void FirstRun_HasNoBaseline_NothingRecorded()
    {
        var ctx = _f.Load();
        Assert.Null(SyncObserver.BeforeRun(ctx));
        Assert.Equal(0, Summary().CleanLaunches);
        Assert.False(File.Exists(SyncObservationStore.For(_f.Paths).Path));
    }

    [Fact]
    public void CleanLaunches_AreCounted()
    {
        var ctx = _f.Load();
        SyncObserver.AfterRun(ctx);
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(SyncObserver.BeforeRun(ctx));
            SyncObserver.AfterRun(ctx);
        }
        var s = Summary();
        Assert.Equal(3, s.CleanLaunches);
        Assert.False(s.HasChanges);
    }

    [Fact]
    public void ExternalChange_NotMatchingAnyPreset_IsRecordedButNotStrong()
    {
        var ctx = _f.Load();
        SyncObserver.AfterRun(ctx);
        _f.WriteGameFile("user.ini", Ini(7, 8, 9, 1, 2));

        var change = SyncObserver.BeforeRun(ctx);
        Assert.NotNull(change);
        Assert.Equal(["user"], change!.Files);
        Assert.False(change.Strong);
        var s = Summary();
        Assert.Single(s.Changes);
        Assert.Null(s.LatestStrong);
        Assert.Equal(0, s.CleanLaunches);

        // the changed state is the new baseline: the next launch is clean
        Assert.Null(SyncObserver.BeforeRun(ctx));
        Assert.Equal(1, Summary().CleanLaunches);
    }

    [Fact]
    public void ChangeMatchingAnotherDevicesPreset_IsStrong()
    {
        var ctx = _f.Load();
        SyncObserver.AfterRun(ctx);
        _f.WriteGameFile("user.ini", Ini(3, 3, 3, 3, 3)); // what the Ally X Performance preset says

        var change = SyncObserver.BeforeRun(ctx)!;
        Assert.True(change.Strong);
        Assert.Equal("ally-x", change.FromDevice);
        Assert.Equal("Performance", change.Preset);
        Assert.Equal((5, 5), (change.Compared, change.Matched));
        Assert.Equal("ally-x", Summary().LatestStrong!.FromDevice);
    }

    [Fact]
    public void Match_NeedsEightyPercentOfKeys()
    {
        var ctx = _f.Load();
        SyncObserver.AfterRun(ctx);
        _f.WriteGameFile("user.ini", Ini(3, 3, 3, 3, 9)); // 4 of 5 = 80%
        Assert.True(SyncObserver.BeforeRun(ctx)!.Strong);

        SyncObserver.AfterRun(ctx);
        _f.WriteGameFile("user.ini", Ini(3, 3, 3, 9, 9)); // 3 of 5 = 60%
        Assert.False(SyncObserver.BeforeRun(ctx)!.Strong);
    }

    [Fact]
    public void Match_NeedsAtLeastThreeKeys()
    {
        using var f = new EngineFixture("""
            name: Test Game
            store: other
            files:
              - id: user
                handler: ini
                path: "{T}/user.ini"
            presets:
              desktop:
                quality: { user: { S: { A: 1, B: 1 } } }
              ally-x:
                performance: { user: { S: { A: 3, B: 3 } } }
            """);
        f.WriteGameFile("user.ini", "[S]\r\nA=1\r\nB=1\r\n");
        var ctx = f.Load();
        SyncObserver.AfterRun(ctx);
        f.WriteGameFile("user.ini", "[S]\r\nA=3\r\nB=3\r\n");
        var change = SyncObserver.BeforeRun(ctx)!;
        Assert.False(change.Strong); // 2/2 keys, but too few to prove anything
    }

    [Fact]
    public void ValuesSharedWithTheDevicesOwnPreset_ProveNothing()
    {
        using var f = new EngineFixture("""
            name: Test Game
            store: other
            files:
              - id: user
                handler: ini
                path: "{T}/user.ini"
            presets:
              desktop:
                quality: { user: { S: { A: 3, B: 3, C: 3, D: 3 } } }
              ally-x:
                performance: { user: { S: { A: 3, B: 3, C: 3, D: 3 } } }
            """);
        f.WriteGameFile("user.ini", "[S]\r\nA=1\r\nB=1\r\nC=1\r\nD=1\r\n");
        var ctx = f.Load();
        SyncObserver.AfterRun(ctx);
        f.WriteGameFile("user.ini", "[S]\r\nA=3\r\nB=3\r\nC=3\r\nD=3\r\n");
        Assert.False(SyncObserver.BeforeRun(ctx)!.Strong);
    }

    [Fact]
    public void WholeFileSnapshotOfAnotherDevicesManualSave_IsStrong()
    {
        File.WriteAllBytes(_f.GameFile("blob.bin"), [1, 2, 3]);
        var ctx = _f.Load();
        SyncObserver.AfterRun(ctx);

        _f.WriteSave("ally-x", "Steady 40", new Dictionary<string, object?>(), new Dictionary<string, byte[]> { ["blob"] = [9, 9, 9] });
        File.WriteAllBytes(_f.GameFile("blob.bin"), [9, 9, 9]);

        var change = SyncObserver.BeforeRun(ctx)!;
        Assert.Equal(["blob"], change.Files);
        Assert.True(change.Strong);
        Assert.Equal("ally-x", change.FromDevice);
        Assert.Equal("Steady 40", change.Preset);
    }

    [Fact]
    public void Observing_NeverThrows_EvenWhenTheDataDirIsUnusable()
    {
        var blocker = _f.Tmp.Write("not-a-dir", "x");
        var ctx = _f.Load();
        var broken = new OptimusVibe.Engine.GameContext
        {
            Paths = new OptimusVibe.Config.AppPaths { ConfigRoot = ctx.Paths.ConfigRoot, DataDir = blocker },
            Store = ctx.Store, Game = ctx.Game, Device = ctx.Device, Profile = ctx.Profile, Files = ctx.Files,
        };
        SyncObserver.AfterRun(broken);
        Assert.Null(SyncObserver.BeforeRun(broken));
    }

    [Fact]
    public void RunnerRun_RecordsFingerprints_AndCountsCleanLaunches()
    {
        var ctx = _f.Load();
        string[] cmd = [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c", "exit", "0"];

        Assert.Equal(0, Runner.Run(ctx, cmd, prompt: false));
        Assert.True(File.Exists(SyncObservationStore.For(_f.Paths).Path));
        Assert.Equal(0, Summary().CleanLaunches); // first run only sets the baseline

        // The first run left the file as the baseline; run again with nothing touched in between.
        Assert.Equal(0, Runner.Run(_f.Load(), cmd, prompt: false));
        Assert.Equal(1, Summary().CleanLaunches);

        // A file changed behind our back between two runs is noticed at the next launch.
        _f.WriteGameFile("user.ini", Ini(3, 3, 3, 3, 3));
        Assert.Equal(0, Runner.Run(_f.Load(), cmd, prompt: false));
        Assert.True(Summary().LatestStrong is { FromDevice: "ally-x" });
    }
}
