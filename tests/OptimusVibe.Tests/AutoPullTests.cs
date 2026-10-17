using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

public sealed class AutoPullTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly AppPaths _paths;
    private DateTime _now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    public AutoPullTests() => _paths = new AppPaths { ConfigRoot = _tmp.Dir("repo", "config"), DataDir = _tmp.Dir("data") };

    public void Dispose() => _tmp.Dispose();

    private sealed class Fake : ISyncProvider
    {
        public int Pulls;
        public bool Ready = true, Ok = true;
        public Action? OnPull;
        public ManualResetEventSlim? Block;
        public string Name => "fake";
        public string Describe() => "fake";
        public bool IsReady(out string reason) { reason = "not signed in"; return Ready; }
        public GitResult CommitAndSync(string message, params string[] paths) => new(true, "");
        public GitResult Pull()
        {
            Interlocked.Increment(ref Pulls);
            Block?.Wait(TimeSpan.FromSeconds(10));
            OnPull?.Invoke();
            return new(Ok, Ok ? "pulled" : "offline");
        }
    }

    private PullOutcome Run(Fake f, int minAgeMin = 5, double timeoutSec = 5, bool force = false) =>
        AutoPull.Run(f, _paths, TimeSpan.FromMinutes(minAgeMin), TimeSpan.FromSeconds(timeoutSec), () => _now, force);

    private void WriteGame(string id, string text)
    {
        Directory.CreateDirectory(_paths.GamesDir);
        File.WriteAllText(_paths.GameFile(id), text);
    }

    [Fact]
    public void Pulls_RecordsTheTime_AndReportsNewAndChangedGames()
    {
        WriteGame("old", "a: 1");
        WriteGame("same", "a: 1");
        var f = new Fake { OnPull = () => { WriteGame("old", "a: 2"); WriteGame("new", "a: 1"); } };

        var r = Run(f);

        Assert.Equal(PullStatus.Pulled, r.Status);
        Assert.Equal(["new", "old"], r.ChangedGames);
        Assert.Equal(_now, AutoPull.LastSuccess(_paths.DataDir));
    }

    [Fact]
    public void ARecentPull_IsSkipped_UntilItIsOldEnough_OrForced()
    {
        var f = new Fake();
        Run(f);
        _now += TimeSpan.FromMinutes(1);
        Assert.Equal(PullStatus.Skipped, Run(f, minAgeMin: 2).Status);
        Assert.Equal(1, f.Pulls);
        Assert.Equal(PullStatus.Pulled, Run(f, minAgeMin: 2, force: true).Status);
        _now += TimeSpan.FromMinutes(3);
        Assert.Equal(PullStatus.Pulled, Run(f, minAgeMin: 2).Status);
        Assert.Equal(3, f.Pulls);
    }

    [Fact]
    public void AFailedPull_IsReported_AndDoesNotCountAsRecent()
    {
        var f = new Fake { Ok = false };
        var r = Run(f);
        Assert.Equal(PullStatus.Failed, r.Status);
        Assert.Contains("offline", r.Message);
        Assert.Null(AutoPull.LastSuccess(_paths.DataDir));
        f.Ok = true;
        Assert.Equal(PullStatus.Pulled, Run(f).Status);
    }

    [Fact]
    public void ASlowPull_StopsBeingWaitedFor()
    {
        using var gate = new ManualResetEventSlim();
        var f = new Fake { Block = gate };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var r = Run(f, timeoutSec: 0.3);

        Assert.Equal(PullStatus.TimedOut, r.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
        gate.Set(); // let the background pull finish; it records its own success
        SpinWait.SpinUntil(() => AutoPull.LastSuccess(_paths.DataDir) is not null, 5000);
        Assert.NotNull(AutoPull.LastSuccess(_paths.DataDir));
    }

    [Fact]
    public void AProviderThatIsNotReady_IsSkipped_AndAThrowingOneNeverThrows()
    {
        Assert.Equal(PullStatus.Skipped, Run(new Fake { Ready = false }).Status);
        var boom = new Fake { OnPull = () => throw new InvalidOperationException("x") };
        Assert.Equal(PullStatus.Failed, Run(boom).Status);
    }
}
