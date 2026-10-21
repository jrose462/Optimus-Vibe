using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Launch;

namespace OptimusVibe.Tests;

public sealed class AutoSnapshotTests : IDisposable
{
    private const string G = EngineFixture.GameId;
    private readonly List<EngineFixture> _fixtures = new();

    public void Dispose() => _fixtures.ForEach(f => f.Dispose());

    private static string Yaml(string? mode = "perPreset", string? snapshotsFrom = null)
    {
        var y = EngineFixture.GameYaml.ReplaceLineEndings("\n");
        if (mode is not null)
            y = y.Replace("    handler: file\n    path: \"{T}/blob.bin\"", $"    handler: file\n    path: \"{{T}}/blob.bin\"\n    autoSnapshot: {mode}");
        if (snapshotsFrom is not null)
            y = y.Replace("    performance:\n      extends: base\n", $"    performance:\n      extends: base\n      snapshotsFrom: {snapshotsFrom}\n");
        return y;
    }

    private EngineFixture Fixture(string? mode = "perPreset", string? snapshotsFrom = null) => Prepare(new EngineFixture(Yaml(mode, snapshotsFrom)));

    private EngineFixture Prepare(EngineFixture f)
    {
        _fixtures.Add(f);
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(f.GameFile("blob.bin"), [1, 1, 1]);
        return f;
    }

    private static PresetRef Perf => PresetRef.Recommended("performance");

    // ---------------------------------------------------------------- store

    [Fact]
    public void Store_RoundTrip_AndUnchangedIsSkipped()
    {
        using var tmp = new TempDir();
        var store = new AutoSnapshotStore(tmp.Dir("auto"));
        Assert.Null(store.Get(G, "desktop", "performance", "blob"));
        Assert.True(store.Save(G, "desktop", "performance", "blob", [1, 2, 3], "first session"));
        var meta = store.Get(G, "desktop", "performance", "blob")!;
        Assert.Equal("first session", meta.SessionNote);
        Assert.Equal("performance", meta.Preset);
        Assert.Equal(BackupStore.Hash([[1, 2, 3]]), meta.Sha256);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(store.SnapPath(G, "desktop", "performance", "blob")));
        Assert.False(store.Save(G, "desktop", "performance", "blob", [1, 2, 3])); // same contents
        Assert.True(store.Save(G, "desktop", "performance", "blob", [9]));
        Assert.Equal([9], File.ReadAllBytes(store.SnapPath(G, "desktop", "performance", "blob")));
    }

    [Fact]
    public void Store_WritesAtomically_LeavesNoTempFiles()
    {
        using var tmp = new TempDir();
        var store = new AutoSnapshotStore(tmp.Dir("auto"));
        store.Save(G, "desktop", "performance", "blob", [1]);
        store.Save(G, "desktop", "performance", "blob", [2]);
        Assert.Empty(Directory.EnumerateFiles(store.RootDir, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(2, Directory.EnumerateFiles(store.RootDir, "*", SearchOption.AllDirectories).Count()); // .snap + .json
    }

    [Fact]
    public void Store_ManualKeyIsPathSafe_AndForgetScopes()
    {
        using var tmp = new TempDir();
        var store = new AutoSnapshotStore(tmp.Dir("auto"));
        store.Save(G, "desktop", "manual:steady", "blob", [1]);
        store.Save(G, "desktop", "performance", "blob", [2]);
        store.Save(G, "ally-x", "performance", "blob", [3]);
        Assert.Equal(3, store.List(G).Count);
        Assert.Equal(2, store.List(G, "desktop").Count);
        Assert.Equal(1, store.Forget(G, "desktop", "manual:steady"));
        Assert.Single(store.List(G, "desktop"));
        Assert.Equal(1, store.Forget(G, "desktop"));
        Assert.Empty(store.List(G, "desktop"));
        Assert.Single(store.List(G, "ally-x")); // other device untouched
    }

    // ---------------------------------------------------------------- keys

    [Fact]
    public void Keys_PerPresetUsesPreset_PerDeviceUsesDeviceKey()
    {
        var pp = new FileDef { Id = "x", AutoSnapshot = "perPreset" };
        var pd = new FileDef { Id = "x", AutoSnapshot = "PERDEVICE" };
        Assert.Equal("performance", AutoSnapshots.KeyFor(pp, Perf));
        Assert.Equal("manual:steady", AutoSnapshots.KeyFor(pp, PresetRef.Manual("steady")));
        Assert.Null(AutoSnapshots.KeyFor(pp, null));
        Assert.Equal("_device", AutoSnapshots.KeyFor(pd, Perf));
        Assert.Equal("_device", AutoSnapshots.KeyFor(pd, null));
        Assert.Null(AutoSnapshots.KeyFor(new FileDef { Id = "x" }, Perf));
        Assert.Null(AutoSnapshots.KeyFor(new FileDef { Id = "x", AutoSnapshot = "bogus" }, Perf));
    }

    [Fact]
    public void PerDevice_SharesOneCopyAcrossPresets_PerPresetDoesNot()
    {
        var dev = Fixture("perDevice");
        AutoSnapshots.CaptureAfterSession(dev.Load("desktop", Perf), true);
        var store = AutoSnapshots.StoreFor(dev.Paths);
        Assert.NotNull(store.Get(G, "desktop", "_device", "blob"));
        Assert.Equal(store.SnapPath(G, "desktop", "_device", "blob"),
            dev.Load("desktop", PresetRef.Recommended("quality")).Files.Single(x => x.Id == "blob").SnapshotFile);

        var per = Fixture("perPreset");
        AutoSnapshots.CaptureAfterSession(per.Load("desktop", Perf), true);
        Assert.NotNull(per.Load("desktop", Perf).Files.Single(x => x.Id == "blob").SnapshotFile);
        Assert.Null(per.Load("desktop", PresetRef.Recommended("quality")).Files.Single(x => x.Id == "blob").SnapshotFile);
    }

    // ---------------------------------------------------------------- restore before launch + precedence

    [Fact]
    public void Restore_SetsSnapshotFileAndHash_AndWritesTheStoredCopy()
    {
        var f = Fixture();
        AutoSnapshots.StoreFor(f.Paths).Save(G, "desktop", "performance", "blob", [7, 7]);
        var ctx = f.Load("desktop", Perf);
        var blob = ctx.Files.Single(x => x.Id == "blob");
        Assert.NotNull(blob.SnapshotFile);
        Assert.NotNull(blob.AutoCopy);
        Assert.Contains(GameEngine.Plan(ctx), p => p.File.Id == "blob" && p.RestoreSnapshot);

        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        Assert.Equal([7, 7], File.ReadAllBytes(f.GameFile("blob.bin")));
        Assert.Equal(BackupStore.Hash([[7, 7]]), applied.SnapshotHashes["blob"]);
        Assert.Equal("restore auto copy (Performance, " + blob.AutoCopy!.Utc.ToLocalTime().ToString("yyyy-MM-dd") + ")",
            AutoSnapshots.RestoreText(blob, ctx.Preset, ctx.Profile));
    }

    [Fact]
    public void Restore_ReappliesAfterALauncherSyncReplacedIt()
    {
        var f = Fixture();
        AutoSnapshots.StoreFor(f.Paths).Save(G, "desktop", "performance", "blob", [7, 7]);
        var ctx = f.Load("desktop", Perf);
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        Assert.DoesNotContain(GameEngine.PlanReapply(ctx, applied), p => p.File.Id == "blob" && p.RestoreSnapshot); // intact

        File.WriteAllBytes(f.GameFile("blob.bin"), [5]); // cloud sync from the other device
        Assert.Contains(GameEngine.PlanReapply(ctx, applied), p => p.File.Id == "blob" && p.RestoreSnapshot);
    }

    [Fact]
    public void NoStoredCopy_MeansNothingToRestore()
    {
        var f = Fixture();
        var ctx = f.Load("desktop", Perf);
        var blob = ctx.Files.Single(x => x.Id == "blob");
        Assert.Null(blob.SnapshotFile);
        Assert.Equal("performance", blob.AutoKey);
        Assert.DoesNotContain(GameEngine.Plan(ctx), p => p.File.Id == "blob" && p.RestoreSnapshot);
    }

    [Fact]
    public void Precedence_ManualSaveBeatsAuto()
    {
        var f = Fixture();
        var own = f.WriteSave("desktop", "Own", new(), new() { ["blob"] = [2] });
        AutoSnapshots.StoreFor(f.Paths).Save(G, "desktop", "manual:" + own.Slug, "blob", [9]);
        var ctx = f.Load("desktop", PresetRef.Manual(own.Slug));
        var blob = ctx.Files.Single(x => x.Id == "blob");
        Assert.Equal(Path.Combine(own.Dir, "blob.snap"), blob.SnapshotFile);
        Assert.Null(blob.AutoCopy);
    }

    [Fact]
    public void Precedence_SnapshotsFromBeatsAuto()
    {
        var f = Fixture(snapshotsFrom: "borrowed");
        AutoSnapshots.StoreFor(f.Paths).Save(G, "desktop", "performance", "blob", [9]);
        var save = f.WriteSave("desktop", "Borrowed", new(), new() { ["blob"] = [3] });
        var blob = f.Load("desktop", Perf).Files.Single(x => x.Id == "blob");
        Assert.Equal(Path.Combine(save.Dir, "blob.snap"), blob.SnapshotFile);
        Assert.Null(blob.AutoCopy);
    }

    [Fact]
    public void Precedence_SnapshotsFromNamingAMissingSave_StillSkipsAuto()
    {
        var f = Fixture(snapshotsFrom: "not-there");
        AutoSnapshots.StoreFor(f.Paths).Save(G, "desktop", "performance", "blob", [9]);
        Assert.Null(f.Load("desktop", Perf).Files.Single(x => x.Id == "blob").SnapshotFile);
    }

    [Fact]
    public void FileWithoutAutoSnapshot_IsUnaffected()
    {
        var f = Fixture(mode: null);
        AutoSnapshots.StoreFor(f.Paths).Save(G, "desktop", "performance", "blob", [9]);
        var blob = f.Load("desktop", Perf).Files.Single(x => x.Id == "blob");
        Assert.Null(blob.SnapshotFile);
        Assert.Null(blob.AutoKey);
        Assert.Null(AutoSnapshots.StatusLine(f.Load("desktop", Perf)));
    }

    // ---------------------------------------------------------------- capture after the session

    [Fact]
    public void Capture_FirstSessionIsNoted_ThenUnchangedIsSkipped_ThenChangeIsKept()
    {
        var f = Fixture();
        var ctx = f.Load("desktop", Perf);
        Assert.Equal(["blob"], AutoSnapshots.CaptureAfterSession(ctx, processSeen: true));
        var store = AutoSnapshots.StoreFor(f.Paths);
        Assert.Equal("first session", store.Get(G, "desktop", "performance", "blob")!.SessionNote);
        Assert.Equal([1, 1, 1], File.ReadAllBytes(store.SnapPath(G, "desktop", "performance", "blob")));

        Assert.Empty(AutoSnapshots.CaptureAfterSession(ctx, true)); // unchanged

        File.WriteAllBytes(f.GameFile("blob.bin"), [4, 4]);
        Assert.Equal(["blob"], AutoSnapshots.CaptureAfterSession(ctx, true));
        var meta = store.Get(G, "desktop", "performance", "blob")!;
        Assert.Null(meta.SessionNote);
        Assert.Equal([4, 4], File.ReadAllBytes(store.SnapPath(G, "desktop", "performance", "blob")));
    }

    [Fact]
    public void Capture_NotWhenGameNotSeen_OrCancelled()
    {
        var f = Fixture();
        var ctx = f.Load("desktop", Perf);
        Assert.Empty(AutoSnapshots.CaptureAfterSession(ctx, processSeen: false));
        Assert.Empty(AutoSnapshots.CaptureAfterSession(ctx, processSeen: true, cancelled: true));
        Assert.Empty(AutoSnapshots.StoreFor(f.Paths).List(G));
    }

    [Fact]
    public void Capture_SkipsAMissingFile()
    {
        var f = Fixture();
        File.Delete(f.GameFile("blob.bin"));
        Assert.Empty(AutoSnapshots.CaptureAfterSession(f.Load("desktop", Perf), true));
    }

    // ---------------------------------------------------------------- capture prompt + results

    private sealed class FakePrompt : ICapturePrompt
    {
        public List<CaptureRequest> Asked { get; } = new();

        public CaptureDecision Ask(CaptureRequest request)
        {
            Asked.Add(request);
            return new CaptureDecision(CaptureChoice.DontSave);
        }
    }

    private (GameContext Ctx, AppliedState Applied) PlayAndChange(EngineFixture f, bool keyed)
    {
        AutoSnapshots.StoreFor(f.Paths).Save(G, "desktop", "performance", "blob", [7, 7]);
        var ctx = f.Load("desktop", Perf);
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        File.WriteAllBytes(f.GameFile("blob.bin"), [8, 8, 8]); // the player tuned the binary file in-game
        if (keyed) f.WriteGameFile("user.ini", "[S]\r\nShadows=1.000000\r\nTextures=4\r\n");
        return (ctx, applied);
    }

    [Fact]
    public void Prompt_WholeFileDriftOnAutoSnapshotFile_DoesNotAsk()
    {
        var f = Fixture();
        var (ctx, applied) = PlayAndChange(f, keyed: false);
        Assert.Contains(GameEngine.FindDrift(ctx, applied), d => d.FileId == "blob");
        var prompt = new FakePrompt();
        Assert.Null(Runner.OfferCapture(ctx, applied, prompt));
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public void Prompt_KeyedDriftStillAsks_WithoutTheWholeFileLine()
    {
        var f = Fixture();
        var (ctx, applied) = PlayAndChange(f, keyed: true);
        var prompt = new FakePrompt();
        Runner.OfferCapture(ctx, applied, prompt);
        var ask = Assert.Single(prompt.Asked);
        Assert.Equal(1, ask.TotalChanges);
        Assert.DoesNotContain(ask.Lines, l => l.Contains("whole file"));
    }

    [Fact]
    public void Prompt_WholeFileDriftWithoutAutoSnapshot_StillAsks()
    {
        var f = Fixture(mode: null);
        var save = f.WriteSave("desktop", "Borrowed", new(), new() { ["blob"] = [7, 7] });
        var ctx = f.Load("desktop", PresetRef.Manual(save.Slug));
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        File.WriteAllBytes(f.GameFile("blob.bin"), [8]);
        var prompt = new FakePrompt();
        Runner.OfferCapture(ctx, applied, prompt);
        Assert.Single(prompt.Asked);
    }

    [Fact]
    public void RecordResult_NormalRunIgnoresExpectedDrift_VerifyReportsItAsBefore()
    {
        var f = Fixture();
        var (ctx, applied) = PlayAndChange(f, keyed: false);
        Assert.Empty(Runner.RecordResult(ctx, applied, verify: false, processSeen: true));
        Assert.Contains(Runner.RecordResult(ctx, applied, verify: true, processSeen: true), d => d.FileId == "blob");
    }

    // ---------------------------------------------------------------- status, forget, validator

    [Fact]
    public void Status_FirstLaunchThenKeptCopy_AndForget()
    {
        var f = Fixture();
        var ctx = f.Load("desktop", Perf);
        Assert.StartsWith("First launch with this preset:", AutoSnapshots.StatusLine(ctx));
        Assert.False(AutoSnapshots.HasCopy(ctx));

        AutoSnapshots.CaptureAfterSession(ctx, true);
        Assert.StartsWith("Settings file kept per device: Performance copy from ", AutoSnapshots.StatusLine(ctx));
        Assert.True(AutoSnapshots.HasCopy(ctx));

        Assert.Equal(1, AutoSnapshots.ForgetActive(ctx));
        Assert.False(AutoSnapshots.HasCopy(f.Load("desktop", Perf)));
        Assert.Null(f.Load("desktop", Perf).Files.Single(x => x.Id == "blob").SnapshotFile);
    }

    [Fact]
    public void Forget_OnlyTouchesTheActivePreset()
    {
        var f = Fixture();
        var store = AutoSnapshots.StoreFor(f.Paths);
        store.Save(G, "desktop", "performance", "blob", [1]);
        store.Save(G, "desktop", "quality", "blob", [2]);
        AutoSnapshots.ForgetActive(f.Load("desktop", Perf));
        Assert.Null(store.Get(G, "desktop", "performance", "blob"));
        Assert.NotNull(store.Get(G, "desktop", "quality", "blob"));
    }

    [Fact]
    public void CliForget_ByPresetArg_AndAllForTheDevice()
    {
        var f = Fixture();
        var store = AutoSnapshots.StoreFor(f.Paths);
        store.Save(G, "desktop", "performance", "blob", [1]);
        store.Save(G, "desktop", "manual:steady", "blob", [2]);
        store.Save(G, "desktop", "_device", "blob", [3]);
        Assert.Equal("performance", AutoSnapshots.KeyFromArg("recommended:performance"));
        Assert.Equal("manual:steady", AutoSnapshots.KeyFromArg("manual:steady"));
        Assert.Equal("_device", AutoSnapshots.KeyFromArg("_device"));
        Assert.Equal(1, store.Forget(G, "desktop", AutoSnapshots.KeyFromArg("manual:steady")));
        Assert.Equal(1, store.Forget(G, "desktop", AutoSnapshots.KeyFromArg("performance")));
        Assert.Single(store.List(G, "desktop"));
        Assert.Equal(1, store.Forget(G, "desktop"));
        Assert.Empty(store.List(G));
    }

    [Fact]
    public void Validator_BadValueIsError_KeyedHandlerWarns_FileHandlerIsQuiet()
    {
        var bad = Validator.Run(Fixture("sometimes").Store);
        Assert.Contains(bad.Errors, e => e.Contains("autoSnapshot must be"));

        var ok = Validator.Run(Fixture("perDevice").Store);
        Assert.DoesNotContain(ok.Errors, e => e.Contains("autoSnapshot"));
        Assert.DoesNotContain(ok.Warnings, w => w.Contains("autoSnapshot"));

        var yaml = Yaml(mode: null).Replace("path: \"{T}/user.ini\"", "path: \"{T}/user.ini\"\n    autoSnapshot: perPreset");
        var keyed = Prepare(new EngineFixture(yaml));
        Assert.Contains(Validator.Run(keyed.Store).Warnings, w => w.Contains("autoSnapshot") && w.Contains("ini"));
    }

    [Fact]
    public void OldConfig_WithoutAutoSnapshot_LoadsAsOff()
    {
        var f = Fixture(mode: null);
        Assert.All(f.Store.LoadGame(G).Files, x => Assert.Null(x.AutoSnapshot));
    }
}
