using GameProfiles.Launch;

namespace GameProfiles.Tests;

public sealed class RunnerGuardTests
{
    [Theory]
    [InlineData("steam://rungameid/1091500", true)]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true", true)]
    [InlineData(@"shell:AppsFolder\Microsoft.Halo_8wekyb3d8bbwe!App", true)]
    [InlineData(@"SHELL:AppsFolder\X!App", true)]
    [InlineData(@"C:\Program Files (x86)\Battle.net\Battle.net.exe", false)]
    [InlineData("cmd.exe", false)]
    [InlineData(@"D:\Games\shell\game.exe", false)]
    public void IsShellTarget(string target, bool expected) => Assert.Equal(expected, Runner.IsShellTarget(target));

    [Fact]
    public void MutexName_IsLocalAndSafe()
    {
        Assert.Equal(@"Local\GameProfiles.run.cyberpunk2077", Runner.MutexName("Cyberpunk2077"));
        Assert.Equal(@"Local\GameProfiles.run.a_b_c", Runner.MutexName(@"a\b c"));
    }

    [Fact]
    public void RunGuard_IsExclusiveAcrossThreads_AndReleased()
    {
        var id = "guardtest-" + Guid.NewGuid().ToString("N");
        using (var held = HoldOnOtherThread(id))
        {
            Assert.Null(Runner.TryAcquireRunGuard(id));
        }
        using var mine = Runner.TryAcquireRunGuard(id);
        Assert.NotNull(mine);
    }

    [Fact]
    public void Run_WhileGameIsManaged_IsPassThrough()
    {
        using var fx = new EngineFixture();
        var ctx = fx.Load();
        using (HoldOnOtherThread(ctx.Game.Id))
        {
            var code = Runner.Run(ctx, ["cmd.exe", "/c", "exit 3"], prompt: false);
            Assert.Equal(3, code);
        }
        // nothing applied: the game's settings files were not written, no run state recorded
        Assert.False(File.Exists(fx.GameFile("user.ini")));
        Assert.False(File.Exists(fx.GameFile("engine.ini")));
        Assert.False(File.Exists(fx.Paths.StateFile));
    }

    [Fact]
    public void Run_PassThrough_UsesLaunchOptionWhenNoCommand()
    {
        using var fx = new EngineFixture();
        var ctx = fx.Load();
        using (HoldOnOtherThread(ctx.Game.Id))
        {
            var code = Runner.Run(ctx, [], prompt: false, new RunOptions { Launch = "cmd.exe", LaunchArgs = ["/c", "exit 4"] });
            Assert.Equal(4, code);
        }
        Assert.False(File.Exists(fx.GameFile("user.ini")));
    }

    [Fact]
    public void LaunchPlain_ReturnsExitCode()
    {
        Assert.Equal(5, Runner.LaunchPlain(["cmd.exe", "/c", "exit 5"]));
        Assert.Equal(0, Runner.LaunchPlain([]));
        Assert.Equal(2, Runner.LaunchPlain([@"C:\does\not\exist\nope.exe"]));
    }

    [Fact]
    public void ProcessWatch_FindsOwnExeFolder_AndHandlesAccessDenied()
    {
        // every process is probed (system ones deny access) without throwing
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var path = ProcessWatch.ExePath(self);
        Assert.NotNull(path);
        Assert.True(ProcessWatch.IsUnder(path!, Path.GetDirectoryName(path!)!));
        Assert.False(ProcessWatch.IsUnder(@"C:\GamesX\a.exe", @"C:\Games"));
        _ = ProcessWatch.PidsUnder(Path.GetDirectoryName(path!)!); // excludes our own pid by design
    }

    /// <summary>Hold the game's run mutex on another thread (an "outer" run) until disposed.</summary>
    private static IDisposable HoldOnOtherThread(string gameId)
    {
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        bool ok = false;
        var t = new Thread(() =>
        {
            using var guard = Runner.TryAcquireRunGuard(gameId);
            ok = guard is not null;
            acquired.Set();
            release.Wait();
        }) { IsBackground = true };
        t.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(ok, "outer guard not acquired");
        return new Releaser(() =>
        {
            release.Set();
            t.Join(TimeSpan.FromSeconds(10));
            acquired.Dispose();
            release.Dispose();
        });
    }

    private sealed class Releaser(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
