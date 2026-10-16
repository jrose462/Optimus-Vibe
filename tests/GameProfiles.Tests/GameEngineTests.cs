using GameProfiles.Config;
using GameProfiles.Engine;

namespace GameProfiles.Tests;

public sealed class GameEngineTests : IDisposable
{
    private readonly EngineFixture _f = new();
    private const string G = EngineFixture.GameId;

    public GameEngineTests()
    {
        _f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        _f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(_f.GameFile("blob.bin"), [1]);
    }

    public void Dispose() => _f.Dispose();

    private static Dictionary<string, object?> S(string section, params (string K, object? V)[] kv)
    {
        var inner = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) inner[k] = v;
        return new Dictionary<string, object?> { [section] = inner };
    }

    [Fact]
    public void Plan_RecommendedPreset_OnlyChangedKeys()
    {
        var plans = GameEngine.Plan(_f.Load("desktop", PresetRef.Recommended("performance")));
        var user = plans.Single(p => p.File.Id == "user");
        Assert.True(user.HasWork);
        Assert.False(user.RestoreSnapshot);
        var change = Assert.Single(user.Changes);
        Assert.Equal(new KeyChange("[S]Shadows", "9", "1"), change);

        var engine = plans.Single(p => p.File.Id == "engine");
        Assert.Null(engine.Skip);
        Assert.Empty(engine.Changes); // already Lod=1
        Assert.False(engine.HasWork);

        Assert.Equal("no settings in this preset", plans.Single(p => p.File.Id == "blob").Skip);
    }

    [Fact]
    public void Plan_MissingFile_SkippedUnlessCreateIfMissing()
    {
        File.Delete(_f.GameFile("user.ini"));
        var plans = GameEngine.Plan(_f.Load("desktop", PresetRef.Recommended("performance")));
        Assert.StartsWith("not found", plans.Single(p => p.File.Id == "user").Skip);

        using var f2 = new EngineFixture(EngineFixture.GameYaml.Replace("path: \"{T}/user.ini\"", "path: \"{T}/user.ini\"\n    createIfMissing: true"));
        var created = GameEngine.Plan(f2.Load("desktop", PresetRef.Recommended("performance"))).Single(p => p.File.Id == "user");
        Assert.Null(created.Skip);
        Assert.Equal(2, created.Changes.Count);
        Assert.All(created.Changes, c => Assert.Null(c.Current));
    }

    [Fact]
    public void Plan_UnresolvedPath_IsSkippedWithError()
    {
        using var f = new EngineFixture(EngineFixture.GameYaml.Replace("\"{T}/engine.ini\"", "\"{T}/missing*/engine.ini\""));
        var plan = GameEngine.Plan(f.Load("desktop")).Single(p => p.File.Id == "engine");
        Assert.Contains("no file matches", plan.Skip);
        Assert.False(plan.HasWork);
    }

    [Fact]
    public void Apply_WritesTargets_AndBacksUp()
    {
        var ctx = _f.Load("desktop", PresetRef.Recommended("performance"));
        var state = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        Assert.Contains("Shadows=1", _f.ReadGameFile("user.ini"));
        Assert.Contains("Textures=3", _f.ReadGameFile("user.ini"));
        Assert.Equal("1", state.Targets["user"]["[S]Shadows"]);
        Assert.Equal("1", state.Targets["engine"]["[E]Lod"]);
        Assert.False(state.Targets.ContainsKey("blob"));

        var backups = new BackupStore(_f.Paths.BackupsDir).List(G);
        Assert.Contains(BackupStore.Original, backups);
        Assert.Equal(2, backups.Count);

        // Second apply: nothing to do.
        Assert.DoesNotContain(GameEngine.Plan(ctx), p => p.HasWork);
    }

    [Fact]
    public void Apply_ManualSave_RestoresSnapshotThenWritesKeys()
    {
        var snap = "[S]\r\nShadows=5\r\nTextures=1\r\nFromSnap=yes\r\n"u8.ToArray();
        var save = _f.WriteSave("desktop", "Snappy",
            new() { ["user"] = S("S", ("Shadows", "0")) },
            new() { ["user"] = snap, ["blob"] = [4, 2] });

        var ctx = _f.Load("desktop", PresetRef.Manual(save.Slug));
        var plans = GameEngine.Plan(ctx);
        var user = plans.Single(p => p.File.Id == "user");
        Assert.True(user.RestoreSnapshot);
        Assert.Equal(["[S]Shadows"], user.Changes.Select(c => c.Key)); // with a snapshot every target is rewritten
        var blob = plans.Single(p => p.File.Id == "blob");
        Assert.True(blob.RestoreSnapshot);
        Assert.Empty(blob.Changes);
        Assert.Equal("no settings in this preset", plans.Single(p => p.File.Id == "engine").Skip);

        var state = GameEngine.Apply(ctx, plans);
        var text = _f.ReadGameFile("user.ini");
        Assert.Contains("Shadows=0", text);   // key written after the snapshot
        Assert.Contains("Textures=1", text);  // from the snapshot
        Assert.Contains("FromSnap=yes", text);
        Assert.Equal([4, 2], File.ReadAllBytes(_f.GameFile("blob.bin")));
        Assert.Equal("[E]\r\nLod=1\r\n", _f.ReadGameFile("engine.ini"));
        Assert.True(state.SnapshotHashes.ContainsKey("blob"));
        Assert.False(state.SnapshotHashes.ContainsKey("user")); // tracked by keys instead
    }

    [Fact]
    public void Apply_ManualSave_SnapshotRecreatesMissingFile()
    {
        File.Delete(_f.GameFile("blob.bin"));
        var save = _f.WriteSave("desktop", "Blob", new(), new() { ["blob"] = [3, 3] });
        var ctx = _f.Load("desktop", PresetRef.Manual(save.Slug));
        var plan = GameEngine.Plan(ctx).Single(p => p.File.Id == "blob");
        Assert.Null(plan.Skip);
        GameEngine.Apply(ctx, [plan]);
        Assert.Equal([3, 3], File.ReadAllBytes(_f.GameFile("blob.bin")));
    }

    [Fact]
    public void Apply_ReadOnlyAfterApply_SetsAttribute()
    {
        using var f = new EngineFixture(EngineFixture.GameYaml.Replace("path: \"{T}/user.ini\"", "path: \"{T}/user.ini\"\n    readOnlyAfterApply: true"));
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\n");
        var ctx = f.Load("desktop", PresetRef.Recommended("performance"));
        GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        Assert.True(File.GetAttributes(f.GameFile("user.ini")).HasFlag(FileAttributes.ReadOnly));

        // Re-applying works through the read-only flag.
        File.SetAttributes(f.GameFile("user.ini"), FileAttributes.ReadOnly);
        var ctx2 = f.Load("desktop", PresetRef.Recommended("quality"));
        GameEngine.Apply(ctx2, GameEngine.Plan(ctx2));
        Assert.Contains("Fps=0", f.ReadGameFile("user.ini"));
    }

    [Fact]
    public void FindDrift_KeyChangedInGame()
    {
        var ctx = _f.Load("desktop", PresetRef.Recommended("performance"));
        var state = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        Assert.Empty(GameEngine.FindDrift(ctx, state));

        _f.WriteGameFile("user.ini", "[S]\r\nShadows=1.000000\r\nTextures=4\r\n");
        var drift = Assert.Single(GameEngine.FindDrift(ctx, state));
        Assert.Equal(new Drift("user", "[S]Textures", "3", "4"), drift);
    }

    [Fact]
    public void FindDrift_KeyRemoved_ReportsNull()
    {
        var ctx = _f.Load("desktop", PresetRef.Recommended("performance"));
        var state = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        _f.WriteGameFile("engine.ini", "[E]\r\n");
        var drift = Assert.Single(GameEngine.FindDrift(ctx, state));
        Assert.Equal(new Drift("engine", "[E]Lod", "1", null), drift);
    }

    [Fact]
    public void FindDrift_WholeFileSnapshot()
    {
        var save = _f.WriteSave("desktop", "Blob", new(), new() { ["blob"] = [5] });
        var ctx = _f.Load("desktop", PresetRef.Manual(save.Slug));
        var state = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        Assert.Empty(GameEngine.FindDrift(ctx, state));

        File.WriteAllBytes(_f.GameFile("blob.bin"), [6]);
        var drift = Assert.Single(GameEngine.FindDrift(ctx, state));
        Assert.Equal("blob", drift.FileId);
        Assert.Equal("(whole file)", drift.Key);
    }

    [Fact]
    public void FindDrift_EmptyState_NoDrift()
    {
        var ctx = _f.Load("desktop");
        Assert.Empty(GameEngine.FindDrift(ctx, new AppliedState()));
    }
}
