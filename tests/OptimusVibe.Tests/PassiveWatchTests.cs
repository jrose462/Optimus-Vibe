using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;

namespace OptimusVibe.Tests;

/// <summary>Passive pass A: mapping's "what the game rewrites by itself" watched around normal launches.</summary>
[Collection("RunnerMutex")] // Runner.Run takes the process-wide "testgame" run mutex
public class PassiveWatchTests
{
    private const string Key = "steam:1285190";

    private sealed class FakeClock : TimeProvider
    {
        public long Ticks;
        public DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    private static FileChange Text(string path, params KeyChange[] keys) =>
        new(path, ProbeChangeKind.Changed, 10, 11, true, "a", "b", "ini", keys, 0);

    private static FileChange Binary(string path) => new(path, ProbeChangeKind.Changed, 10, 11, false, "a", "b", "", [], 0);

    private static ProbeDiff Diff(IReadOnlyList<FileChange> files, params RegistryChange[] registry) => new(files, registry, []);

    // ------------------------------------------------------------ intersection

    [Fact]
    public void Intersect_NeedsTwoSessions()
    {
        Assert.Null(PassiveWatch.Intersect([]));
        Assert.Null(PassiveWatch.Intersect([Diff([Binary("{Documents}\\G\\state.sav")])]));
    }

    [Fact]
    public void Intersect_KeepsWhatRecurs_AndDropsAOneOffChange()
    {
        var s1 = Diff([Text("{Documents}\\G\\user.ini", new KeyChange("[S]LastPlayed", "1", "2"), new KeyChange("[S]Quality", "3", "0")), Binary("{Documents}\\G\\state.sav")]);
        var s2 = Diff([Text("{Documents}\\G\\user.ini", new KeyChange("[S]LastPlayed", "2", "3")), Binary("{Documents}\\G\\state.sav"), Binary("{Documents}\\G\\once.sav")]);
        var s3 = Diff([Text("{Documents}\\G\\user.ini", new KeyChange("[S]LastPlayed", "3", "4"))]);

        var a = PassiveWatch.Intersect([s1, s2, s3])!;

        var ini = a.Files.Single(f => f.Path.EndsWith("user.ini"));
        Assert.Equal([new KeyChange("[S]LastPlayed", "3", "4")], ini.Keys); // newest values; the player's one-off Quality edit drops out
        Assert.Contains(a.Files, f => f.Path.EndsWith("state.sav"));     // changed in 2 of 3
        Assert.DoesNotContain(a.Files, f => f.Path.EndsWith("once.sav"));
    }

    [Fact]
    public void Intersect_DropsATextFileWhoseKeysNeverRecur()
    {
        var s1 = Diff([Text("{Documents}\\G\\user.ini", new KeyChange("[S]Shadows", "3", "1"))]);
        var s2 = Diff([Text("{Documents}\\G\\user.ini", new KeyChange("[S]Textures", "3", "1"))]);
        Assert.Empty(PassiveWatch.Intersect([s1, s2])!.Files);
    }

    [Fact]
    public void Intersect_LineChanges_RecurOnlyWithTheSameLine()
    {
        var s1 = Diff([Text("{Documents}\\G\\notes.txt", new KeyChange("(line)", null, "fixed"), new KeyChange("(line)", null, "t=1"))]);
        var s2 = Diff([Text("{Documents}\\G\\notes.txt", new KeyChange("(line)", null, "fixed"), new KeyChange("(line)", null, "t=2"))]);
        var file = Assert.Single(PassiveWatch.Intersect([s1, s2])!.Files);
        Assert.Equal([new KeyChange("(line)", null, "fixed")], file.Keys);
    }

    [Fact]
    public void Intersect_Registry_ByKeyAndName_CaseInsensitive_NewestValue()
    {
        var s1 = Diff([], new RegistryChange(@"HKCU\Software\G", "Time", "1", "2"), new RegistryChange(@"HKCU\Software\G", "Gfx", "3", "0"));
        var s2 = Diff([], new RegistryChange(@"hkcu\software\g", "time", "2", "3"));
        var a = PassiveWatch.Intersect([s1, s2])!;
        var r = Assert.Single(a.Registry);
        Assert.Equal("3", r.After);
    }

    [Fact]
    public void Intersect_OfQuietSessions_IsEmptyNotNull()
    {
        var a = PassiveWatch.Intersect([Diff([]), Diff([])]);
        Assert.NotNull(a);
        Assert.True(a.IsEmpty);
    }

    // ------------------------------------------------------------ watch: store, cap, timing, limits

    private static ProbeSnapshot Snap(params (string Token, string Hash)[] files)
    {
        var s = new ProbeSnapshot();
        foreach (var (token, hash) in files) s.Files[token] = new ProbeFile(token, 1, DateTime.UnixEpoch, hash, null);
        return s;
    }

    /// <summary>One watched session: the "before" and "after" snapshots each take <paramref name="seconds"/>.</summary>
    private static PassiveRun? Session(string dataDir, FakeClock clock, int n, double seconds = 0.5, string? skip = null, bool truncated = false)
    {
        var calls = 0;
        ProbeSnapshot Take()
        {
            clock.Advance(seconds);
            var s = Snap(("{Documents}\\G\\state.sav", $"h{n}-{calls++}"));
            s.Truncated = truncated;
            return s;
        }
        var watch = PassiveWatch.Begin(dataDir, Key, Take, time: clock);
        clock.Now = clock.Now.AddHours(1);
        return watch?.End(skip);
    }

    [Fact]
    public void Watch_KeepsTheNewestThreeSessions_AndTheyMakeAPassA()
    {
        using var t = new TempDir();
        var clock = new FakeClock();
        Assert.Null(ProbeStore.PassiveA(t.Path, Key));
        for (var i = 0; i < 5; i++) Assert.NotNull(Session(t.Path, clock, i));

        var entry = ProbeStore.GetPassive(t.Path, Key)!;
        Assert.Equal(PassiveWatch.MaxRuns, entry.Runs.Count);
        Assert.Equal(0, entry.Strikes);
        Assert.True(entry.Runs[0].Utc < entry.Runs[^1].Utc);

        var a = ProbeStore.PassiveA(t.Path, "STEAM:1285190")!;
        Assert.Equal(3, a.Launches);
        Assert.Equal("{Documents}\\G\\state.sav", Assert.Single(a.Diff.Files).Path);
    }

    [Fact]
    public void Watch_SkippedSession_KeepsNothing()
    {
        using var t = new TempDir();
        Assert.Null(Session(t.Path, new FakeClock(), 1, skip: "the game process was never seen"));
        Assert.Null(ProbeStore.GetPassive(t.Path, Key));
    }

    [Fact]
    public void Watch_SlowSnapshotsInTwoSessions_StopWatchingTheGame()
    {
        using var t = new TempDir();
        var clock = new FakeClock();
        Assert.NotNull(Session(t.Path, clock, 1, seconds: 6)); // kept, but a strike
        Assert.Equal(1, ProbeStore.GetPassive(t.Path, Key)!.Strikes);
        Assert.NotNull(Session(t.Path, clock, 2, seconds: 1));
        Assert.Null(Session(t.Path, clock, 3, seconds: 1, skip: "the run was cancelled") ); // nothing kept, before was fast: no strike
        Assert.Equal(1, ProbeStore.GetPassive(t.Path, Key)!.Strikes);
        Assert.NotNull(Session(t.Path, clock, 4, seconds: 5.5));

        var entry = ProbeStore.GetPassive(t.Path, Key)!;
        Assert.True(entry.Disabled);
        Assert.Null(Session(t.Path, clock, 5)); // no longer watched
        Assert.Equal(3, ProbeStore.GetPassive(t.Path, Key)!.Runs.Count);
    }

    [Fact]
    public void Watch_ASnapshotThatHitsTheBounds_IsNotKept_AndCountsAsAStrike()
    {
        using var t = new TempDir();
        var clock = new FakeClock();
        Assert.Null(Session(t.Path, clock, 1, truncated: true));
        var entry = ProbeStore.GetPassive(t.Path, Key)!;
        Assert.Empty(entry.Runs);
        Assert.Equal(1, entry.Strikes);
    }

    [Fact]
    public void Watch_NeverThrows()
    {
        using var t = new TempDir();
        Assert.Null(PassiveWatch.Begin(t.Path, Key, () => throw new IOException("disk gone")));

        var first = true;
        var watch = PassiveWatch.Begin(t.Path, Key, () =>
        {
            if (first) { first = false; return Snap(); }
            throw new UnauthorizedAccessException("denied");
        });
        Assert.NotNull(watch);
        Assert.Null(watch.End());
    }

    [Fact]
    public void PassiveA_LeavesOutSessionsOlderThanTheMaxAge()
    {
        using var t = new TempDir();
        var clock = new FakeClock();
        Session(t.Path, clock, 1);
        Session(t.Path, clock, 2);
        var now = clock.Now.UtcDateTime;
        Assert.NotNull(ProbeStore.PassiveA(t.Path, Key, TimeSpan.FromDays(90), now));
        Assert.Null(ProbeStore.PassiveA(t.Path, Key, TimeSpan.FromDays(90), now.AddDays(91)));
    }

    [Fact]
    public void Store_KeepsMappingRunsAndPassiveSideBySide()
    {
        using var t = new TempDir();
        var clock = new FakeClock();
        Session(t.Path, clock, 1);
        ProbeStore.Record(t.Path, Key, new ProbeRecord { Utc = DateTime.UtcNow, Evidence = "mapped" });
        Session(t.Path, clock, 2);
        Assert.Equal("mapped", ProbeStore.Get(t.Path, Key)!.Evidence);
        Assert.Equal(2, ProbeStore.GetPassive(t.Path, Key)!.Runs.Count);

        // a file from before passive pass A existed still loads
        File.WriteAllText(ProbeStore.FileFor(t.Path), """{ "games": { "steam:1": { "evidence": "old" } } }""");
        Assert.Equal("old", ProbeStore.Get(t.Path, "steam:1")!.Evidence);
        Assert.Null(ProbeStore.GetPassive(t.Path, "steam:1"));
    }

    // ------------------------------------------------------------ real snapshots and the evidence

    [Fact]
    public void RealSnapshots_PassiveA_FeedsPassB_AndSaysWhereItCameFrom()
    {
        using var p = new LabProfile();
        using var data = new TempDir();
        var s = new ProbeSession(p.Target(), p.Options());
        var key = p.Target().Key;
        p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", "[Display]\nQuality=3\nLastPlayed=0\n");
        p.WriteBytes(p.Documents, @"Borderlands4\state.sav", [0, 0, 0]);

        // three normal launches: the preset writes Quality=3 before the "before" snapshot; the game bumps LastPlayed and state.sav every
        // time; in the second session the player also lowers Quality
        for (var i = 1; i <= 3; i++)
        {
            p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", $"[Display]\nQuality=3\nLastPlayed={i - 1}\n");
            var watch = PassiveWatch.Begin(data.Path, key, s.Snapshot)!;
            var quality = i == 2 ? 1 : 3;
            p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", $"[Display]\nQuality={quality}\nLastPlayed={i}\n");
            p.WriteBytes(p.Documents, @"Borderlands4\state.sav", [0, 0, (byte)i]);
            Assert.NotNull(watch.End());
        }
        var passive = ProbeStore.PassiveA(data.Path, key)!;
        var ini = passive.Diff.Files.Single(f => f.Path.EndsWith("GameUserSettings.ini"));
        Assert.Equal(["[Display]LastPlayed"], ini.Keys.Select(k => k.Key));  // the player's one-off Quality edit is not "rewritten every session"
        Assert.Contains(passive.Diff.Files, f => f.Path.EndsWith("state.sav"));

        // pass B (Map this game, one launch): the player changes the resolution
        var before = s.Snapshot();
        p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", "[Display]\nQuality=3\nLastPlayed=4\nResolutionSizeX=1280\n");
        p.WriteBytes(p.Documents, @"Borderlands4\state.sav", [0, 0, 4]);
        var ev = s.ToEvidence(passive.Diff, ProbeSession.Diff(before, s.Snapshot()), passive.Launches);

        Assert.Contains("Watched over 3 normal launches", ev.PassA);
        Assert.Contains("pass A: from 3 normal launches", ev.Header);
        Assert.Contains("[Display]ResolutionSizeX: (none) -> 1280", ev.PassB.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("LastPlayed: 3 -> 4", ev.PassB);
        Assert.Empty(ev.OpaqueFiles);         // state.sav changed in both: state, not settings
        Assert.False(ev.SuggestsSetInGame);
        Assert.DoesNotContain(p.Root, ev.Render());
    }

    [Fact]
    public void PassAOnly_NeverSuggestsSetInGame()
    {
        using var p = new LabProfile();
        var s = new ProbeSession(p.Target(), p.Options());
        var ev = s.ToEvidence(Diff([Binary("{Documents}\\Borderlands4\\gfx.sav")]), null, 3);
        Assert.False(ev.PassBRan);
        Assert.False(ev.SuggestsSetInGame);
        Assert.Contains("pass B was not run", ev.PassB);
    }

    // ------------------------------------------------------------ the runner

    private static readonly string[] Exit0 = [Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c", "exit", "0"];

    private static EngineFixture KeyedGame() =>
        new(EngineFixture.GameYaml.Replace("processes: [Test.exe]", "processes: []").Replace("store: other", "store: other\nstoreIds: { other: tg1 }"));

    private static RunOptions Watching(List<string> keys) => new()
    {
        DisplayApply = (_, _) => null,
        PassiveSnapshot = t => { keys.Add(t.Key); return () => Snap(("{Documents}\\T\\state.sav", Guid.NewGuid().ToString())); },
    };

    [Fact]
    public void Runner_WatchesANormalRun_UnderTheGamesStoreKey()
    {
        using var f = KeyedGame();
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        var keys = new List<string>();

        Assert.Equal(0, Runner.Run(f.Load(), Exit0, prompt: false, Watching(keys)));

        Assert.Equal(["other:tg1"], keys);
        var run = Assert.Single(ProbeStore.GetPassive(f.Paths.DataDir, "other:tg1")!.Runs);
        Assert.Equal("{Documents}\\T\\state.sav", Assert.Single(run.Diff.Files).Path);
    }

    [Fact]
    public void Runner_DoesNotWatch_WhenSessionChecksAreOff_OrTheGameHasNoStoreId()
    {
        using var f = KeyedGame();
        AppPaths.SaveSettings(f.Paths.DataDir, new AppSettings { GameLabWatchSessions = false });
        var keys = new List<string>();
        Runner.Run(f.Load(), Exit0, prompt: false, Watching(keys));
        Assert.Empty(keys);
        Assert.Null(ProbeStore.GetPassive(f.Paths.DataDir, "other:tg1"));

        using var plain = new EngineFixture(EngineFixture.GameYaml.Replace("processes: [Test.exe]", "processes: []"));
        Runner.Run(plain.Load(), Exit0, prompt: false, Watching(keys));
        Assert.Empty(keys);
    }

    [Fact]
    public void Runner_SkipReason()
    {
        var clean = new Runner.RunDetails();
        Assert.Null(Runner.PassiveSkipReason(processSeen: true, cancelled: false, clean));
        Assert.NotNull(Runner.PassiveSkipReason(processSeen: false, cancelled: false, clean));
        Assert.NotNull(Runner.PassiveSkipReason(processSeen: true, cancelled: true, clean));
        var rewritten = new Runner.RunDetails();
        rewritten.Reapplied.Add("user");
        Assert.NotNull(Runner.PassiveSkipReason(processSeen: true, cancelled: false, rewritten));
    }
}
