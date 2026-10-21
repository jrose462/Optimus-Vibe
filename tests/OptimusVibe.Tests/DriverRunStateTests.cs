using OptimusVibe.Config;
using OptimusVibe.Driver;
using OptimusVibe.Engine;
using OptimusVibe.Launch;

namespace OptimusVibe.Tests;

/// <summary>Run-state handling of machine-wide AMD driver settings: overlapping runs, failed restores, kills mid-apply.</summary>
[Collection("RunnerMutex")]
public sealed class DriverRunStateTests : IDisposable
{
    private const int DeadPid = 2_000_000_000;

    private const string Yaml = """
        name: Test Game
        store: other
        files:
          - id: user
            handler: ini
            path: "{T}/user.ini"
        presets:
          ally-x:
            battery:
              user: { S: { A: 3 } }
              amd: { AfmfEnabled: false }
        """;

    private readonly EngineFixture _fx = new(Yaml);

    private readonly AmdAdlxManagerTests.FakeAdlx _api = AmdAdlxManagerTests.Ally();

    public DriverRunStateTests()
    {
        _fx.WriteGameFile("user.ini", "[S]\r\nA=1\r\n");
        DriverSettings.Override = _ => new AmdAdlxManager(_api);
    }

    public void Dispose()
    {
        DriverSettings.Override = null;
        _fx.Dispose();
    }

    private GameContext Ctx() => _fx.Load("ally-x", PresetRef.Recommended("battery"));

    private int RunGame() => Runner.Run(Ctx(), [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c", "exit", "0"], prompt: false);

    private string OwnStateFile => _fx.Paths.RunStateFile(EngineFixture.GameId);

    private string MakeToken(params (string, string)[] kv) =>
        new AmdAdlxManager(_api).Apply(new GameDef { Name = "x" }, kv.ToDictionary(k => k.Item1, k => k.Item2))!;

    private string WriteState(string game, int pid, string token)
    {
        var path = Path.Combine(_fx.Paths.DataDir, $"state-{game}-{pid}.json");
        new RunState { WrapperPid = pid, StartedUtc = DateTime.UtcNow, Game = game, DriverVendor = "amd", DriverToken = token }.Save(path);
        return path;
    }

    [Fact]
    public void Normal_run_applies_and_restores_and_leaves_no_state_file()
    {
        Assert.Equal(0, RunGame());
        Assert.Equal(1, _api.Values[AmdSettingId.AfmfEnabled]);
        Assert.Equal(["AfmfEnabled=0", "AfmfEnabled=1"], _api.Writes);
        Assert.False(File.Exists(OwnStateFile));
    }

    [Fact]
    public void Overlapping_run_skips_the_driver_apply_and_keeps_the_other_runs_token()
    {
        // another live wrapper (this test process stands in for it) already owns a driver token
        var other = WriteState("othergame", Environment.ProcessId, "{\"V\":2,\"Snapshot\":{\"AfmfEnabled\":1}}");
        var before = File.ReadAllText(other);

        Assert.Equal(0, RunGame());

        Assert.Empty(_api.Writes); // game B did not touch the driver
        Assert.Equal(before, File.ReadAllText(other)); // and did not clobber A's token
        Assert.False(File.Exists(OwnStateFile));
    }

    [Fact]
    public void Failed_restore_keeps_the_token_and_the_next_start_retries_it()
    {
        _api.FailAfterWrites = 1; // the apply write works, the restore write-back fails
        Assert.Equal(0, RunGame());
        Assert.Equal(0, _api.Values[AmdSettingId.AfmfEnabled]); // still changed
        Assert.True(File.Exists(OwnStateFile));
        var kept = RunState.Load(OwnStateFile)!;
        Assert.NotNull(kept.DriverToken);

        // the wrapper is gone now; the driver works again at the next start
        kept.WrapperPid = DeadPid;
        kept.Save(OwnStateFile);
        _api.FailAfterWrites = int.MaxValue;
        RunState.RecoverStale(_fx.Paths.StateFile);

        Assert.Equal(1, _api.Values[AmdSettingId.AfmfEnabled]);
        Assert.False(File.Exists(OwnStateFile));
    }

    [Fact]
    public void Recovery_that_fails_keeps_the_file_and_blocks_a_new_apply_until_restored()
    {
        var token = MakeToken(("AfmfEnabled", "false"));
        var stale = WriteState("crashed", DeadPid, token);
        _api.Writes.Clear();
        _api.FailAfterWrites = 0; // driver refuses every write

        Assert.Equal(0, RunGame()); // RecoverStale fails; the new run must not snapshot the changed values as its baseline
        Assert.Empty(_api.Writes);
        Assert.True(File.Exists(stale));
        Assert.NotNull(RunState.Load(stale)!.DriverToken);

        _api.FailAfterWrites = int.MaxValue;
        Assert.Equal(0, RunGame()); // recovery works now (restores AFMF = on), then this run applies and restores normally
        Assert.False(File.Exists(stale));
        Assert.Equal(1, _api.Values[AmdSettingId.AfmfEnabled]);
    }

    [Fact]
    public void Token_is_on_disk_before_the_first_driver_write_so_a_kill_in_between_is_recoverable()
    {
        string? snapshotOnDisk = null;
        _api.OnWrite = _ => snapshotOnDisk ??= File.Exists(OwnStateFile) ? File.ReadAllText(OwnStateFile) : "";
        Assert.Equal(0, RunGame());
        Assert.False(string.IsNullOrEmpty(snapshotOnDisk));
        Assert.Contains("Snapshot", snapshotOnDisk);

        // simulate the kill: put that file back as a dead wrapper's leftover with the driver still changed
        _api.Values[AmdSettingId.AfmfEnabled] = 0;
        var left = RunState.Load(OwnStateFile) ?? System.Text.Json.JsonSerializer.Deserialize<RunState>(snapshotOnDisk!)!;
        left.WrapperPid = DeadPid;
        left.Save(OwnStateFile);
        RunState.RecoverStale(_fx.Paths.StateFile);
        Assert.Equal(1, _api.Values[AmdSettingId.AfmfEnabled]);
        Assert.False(File.Exists(OwnStateFile));
    }

    [Fact]
    public void FindHolder_distinguishes_live_runs_from_dead_leftovers_and_skips_its_own_file()
    {
        var token = "{\"V\":2,\"Snapshot\":{\"AfmfEnabled\":1}}";
        var dead = WriteState("crashed", DeadPid, token);
        var legacy = _fx.Paths.StateFile;

        Assert.NotNull(RunState.FindHolder(legacy, OwnStateFile, s => s.DriverToken is not null));
        Assert.Null(RunState.FindHolder(legacy, OwnStateFile, s => s.DriverToken is not null, liveOnly: true));
        Assert.Null(RunState.FindHolder(legacy, dead, s => s.DriverToken is not null)); // own file ignored
        Assert.Null(RunState.FindHolder(legacy, OwnStateFile, s => s.Display is not null));
    }
}

