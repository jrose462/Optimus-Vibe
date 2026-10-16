using GameProfiles.Config;
using GameProfiles.Engine;
using GameProfiles.Handlers;

namespace GameProfiles.Tests;

public sealed class ManualSavesTests : IDisposable
{
    private readonly EngineFixture _f = new();
    private const string G = EngineFixture.GameId;

    public ManualSavesTests()
    {
        _f.WriteGameFile("user.ini", "[S]\r\nShadows=2\r\nTextures=3\r\nExtraKey=9\r\n");
        _f.WriteGameFile("engine.ini", "[E]\r\nLod=2\r\n");
        File.WriteAllBytes(_f.GameFile("blob.bin"), [7, 8, 9]);
    }

    public void Dispose() => _f.Dispose();

    private static Dictionary<string, object?> Block(object? v) => Assert.IsType<Dictionary<string, object?>>(v);

    [Fact]
    public void SaveCurrent_RecordsUnionOfKnownKeys_AndSnapshotsUnmappedFiles()
    {
        var ctx = _f.Load("desktop"); // performance (device default)
        var r = ManualSaves.SaveCurrent(ctx, "  My Save ", sync: false);

        Assert.Equal("my-save", r.Save.Slug);
        Assert.Equal("My Save", r.Save.Name);
        Assert.Equal("recommended:performance", r.Save.BasedOn);
        Assert.Null(r.Sync);
        Assert.Empty(r.Skipped);
        Assert.Equal(["blob"], r.Snapshots);

        // user: Shadows/Textures (base, performance) + Fps (quality, battery) are known; Fps is missing in the file
        // so it is skipped; ExtraKey is not mentioned by any preset so it is not recorded.
        Assert.Equal(3, r.KeysSaved);
        var loaded = _f.Store.LoadSave(G, "desktop", "my-save");
        var s = Block(Block(loaded.Body["user"])["S"]);
        Assert.Equal(new Dictionary<string, object?> { ["Shadows"] = "2", ["Textures"] = "3" }, s);
        Assert.Equal("2", Block(Block(loaded.Body["engine"])["E"])["Lod"]);
        Assert.False(loaded.Body.ContainsKey("blob"));
        Assert.Equal([7, 8, 9], File.ReadAllBytes(Path.Combine(loaded.Dir, "blob.snap")));

        // The new save resolves and is the active preset.
        Assert.Equal(PresetRef.Manual("my-save"), _f.Selections.Get("desktop", G));
        var again = _f.Load("desktop");
        Assert.Equal(PresetRef.Manual("my-save"), again.Preset);
        Assert.NotNull(again.Files.Single(f => f.Id == "blob").SnapshotFile);
    }

    [Fact]
    public void SaveCurrent_KnownKeysFromOtherDevicesPresetsAreIncluded()
    {
        var game = _f.Store.LoadGame(G);
        var user = game.Files.Single(f => f.Id == "user");
        var keys = GameEngine.KnownKeys(game, user, new IniHandler());
        Assert.Equal(new HashSet<string> { "[S]Shadows", "[S]Textures", "[S]Fps" }, keys);
        Assert.Empty(GameEngine.KnownKeys(game, game.Files.Single(f => f.Id == "blob"), new RawFileHandler()));
    }

    [Fact]
    public void SaveCurrent_RecordsDisplayAndDriverFromActivePreset()
    {
        var ctx = _f.Load("desktop", PresetRef.Recommended("quality"));
        var r = ManualSaves.SaveCurrent(ctx, "Q", sync: false);
        var display = Block(r.Save.Body["display"]);
        Assert.Equal(2560, display["width"]);
        Assert.Equal(165, display["refresh"]);
        Assert.Equal("120", Block(r.Save.Body["nvidia"])["FrameRateLimiter"]);
        Assert.False(r.Save.Body.ContainsKey("amd"));

        var p = _f.Load("desktop", PresetRef.Manual(r.Save.Slug)).Profile;
        Assert.Equal("2560x1440@165Hz", p.Display!.ToString());
        Assert.Equal("120", p.Nvidia["FrameRateLimiter"]);
    }

    [Fact]
    public void SaveCurrent_MissingFile_IsSkipped()
    {
        File.Delete(_f.GameFile("engine.ini"));
        var r = ManualSaves.SaveCurrent(_f.Load("desktop"), "x", sync: false);
        Assert.Contains(r.Skipped, s => s.StartsWith("engine:"));
        Assert.False(r.Save.Body.ContainsKey("engine"));
    }

    [Fact]
    public void SaveCurrent_NoKnownKeysPresent_NoBlock()
    {
        _f.WriteGameFile("user.ini", "[Other]\r\nX=1\r\n");
        var r = ManualSaves.SaveCurrent(_f.Load("desktop"), "x", sync: false);
        Assert.False(r.Save.Body.ContainsKey("user"));
        Assert.Equal(1, r.KeysSaved); // engine Lod only
    }

    [Fact]
    public void SaveCurrent_MakeActiveFalse_LeavesSelection()
    {
        _f.Selections.Set("desktop", G, PresetRef.Recommended("quality"));
        ManualSaves.SaveCurrent(_f.Load("desktop"), "x", makeActive: false, sync: false);
        Assert.Equal(PresetRef.Recommended("quality"), _f.Selections.Get("desktop", G));
    }

    [Fact]
    public void SaveCurrent_EmptyName_GetsDefaultName()
    {
        var r = ManualSaves.SaveCurrent(_f.Load("desktop"), "   ", sync: false);
        Assert.StartsWith("Saved ", r.Save.Name);
        Assert.StartsWith("saved-", r.Save.Slug);
    }

    [Fact]
    public void SaveCurrent_SameNameTwice_GetsNewSlug()
    {
        var a = ManualSaves.SaveCurrent(_f.Load("desktop"), "Dup", sync: false);
        var b = ManualSaves.SaveCurrent(_f.Load("desktop"), "Dup", sync: false);
        Assert.Equal("dup", a.Save.Slug);
        Assert.Equal("dup-2", b.Save.Slug);
    }

    [Fact]
    public void SaveCurrent_Update_KeepsSlugCreatedAndBasedOn()
    {
        var first = ManualSaves.SaveCurrent(_f.Load("desktop"), "Original", notes: "n1", sync: false);
        var created = _f.Store.LoadSave(G, "desktop", "original").Created;

        _f.WriteGameFile("user.ini", "[S]\r\nShadows=0\r\nTextures=0\r\n");
        File.Delete(_f.GameFile("blob.bin"));
        var ctx = _f.Load("desktop", PresetRef.Recommended("quality"));
        var upd = ManualSaves.SaveCurrent(ctx, "", updateSlug: first.Save.Slug, sync: false);

        Assert.Equal("original", upd.Save.Slug);
        Assert.Equal("Original", upd.Save.Name); // empty name keeps the old one
        var loaded = _f.Store.LoadSave(G, "desktop", "original");
        Assert.Equal(created, loaded.Created);
        Assert.Equal("recommended:performance", loaded.BasedOn); // not changed to quality
        Assert.Equal("n1", loaded.Notes);
        Assert.True(loaded.Updated >= loaded.Created);
        Assert.Equal("0", Block(Block(loaded.Body["user"])["S"])["Shadows"]);
        Assert.False(File.Exists(Path.Combine(loaded.Dir, "blob.snap"))); // stale snapshot removed
        Assert.Single(_f.Store.ListSaves(G, "desktop"));
    }

    [Fact]
    public void SaveCurrent_Update_Rename()
    {
        var first = ManualSaves.SaveCurrent(_f.Load("desktop"), "Old", sync: false);
        var upd = ManualSaves.SaveCurrent(_f.Load("desktop"), "New name", updateSlug: first.Save.Slug, sync: false);
        Assert.Equal("old", upd.Save.Slug);
        Assert.Equal("New name", _f.Store.LoadSave(G, "desktop", "old").Name);
    }

    [Fact]
    public void SaveCurrent_Update_Unknown_Throws() =>
        Assert.Throws<ConfigException>(() => ManualSaves.SaveCurrent(_f.Load("desktop"), "x", updateSlug: "nope", sync: false));

    [Fact]
    public void SaveCurrent_FromManualSave_InheritsBasedOn()
    {
        var first = ManualSaves.SaveCurrent(_f.Load("desktop"), "First", sync: false);
        var ctx = _f.Load("desktop"); // now the manual save is selected
        Assert.Equal(PresetRef.Manual("first"), ctx.Preset);
        var second = ManualSaves.SaveCurrent(ctx, "Second", sync: false);
        Assert.Equal(first.Save.BasedOn, second.Save.BasedOn);
        Assert.Equal("recommended:performance", second.Save.BasedOn);
    }

    [Fact]
    public void SaveCurrent_SyncTrue_AutoSyncOff_DoesNothing()
    {
        var r = ManualSaves.SaveCurrent(_f.Load("desktop"), "x", sync: true);
        Assert.Null(r.Sync);
    }

    [Fact]
    public void SaveCurrent_FileIdCaseMismatchInPreset_StillRecordsKeys()
    {
        using var f = new EngineFixture(EngineFixture.GameYaml.Replace("user: { S: { Shadows: 1 } }", "User: { S: { Shadows: 1 } }")
            .Replace("user: { S: { Shadows: 3, Textures: 3 } }", "User: { S: { Shadows: 3, Textures: 3 } }")
            .Replace("user: { S: { Fps: 0 } }", "User: { S: { Fps: 0 } }")
            .Replace("user: { S: { Fps: 40 } }", "User: { S: { Fps: 40 } }"));
        f.WriteGameFile("user.ini", "[S]\r\nShadows=2\r\n");
        var ctx = f.Load("desktop");
        Assert.Equal("1", ctx.Files.Single(x => x.Id == "user").Targets["[S]Shadows"]); // resolver accepts 'User'
        var r = ManualSaves.SaveCurrent(ctx, "x", sync: false);
        Assert.DoesNotContain("user", r.Snapshots);
        Assert.True(r.Save.Body.ContainsKey("user"));
    }

    // ------------------------------------------------------------------ delete / rename

    [Fact]
    public void Delete_ClearsMatchingSelection()
    {
        var r = ManualSaves.SaveCurrent(_f.Load("desktop"), "Gone", sync: false);
        Assert.Equal(PresetRef.Manual("gone"), _f.Selections.Get("desktop", G));
        Assert.Null(ManualSaves.Delete(_f.Store, G, "desktop", r.Save.Slug, sync: false));
        Assert.False(Directory.Exists(r.Save.Dir));
        Assert.Null(_f.Selections.Get("desktop", G));
        Assert.Equal(PresetRef.Recommended("performance"), _f.Load("desktop").Preset);
    }

    [Fact]
    public void Delete_LeavesOtherSelectionsAlone()
    {
        var a = ManualSaves.SaveCurrent(_f.Load("desktop"), "A", makeActive: false, sync: false);
        ManualSaves.SaveCurrent(_f.Load("desktop"), "B", sync: false);
        _f.Selections.Set("ally-x", G, PresetRef.Manual("a"));
        ManualSaves.Delete(_f.Store, G, "desktop", a.Save.Slug, sync: false);
        Assert.Equal(PresetRef.Manual("b"), _f.Selections.Get("desktop", G));
        Assert.Equal(PresetRef.Manual("a"), _f.Selections.Get("ally-x", G)); // other device untouched
    }

    [Fact]
    public void Delete_RecommendedSelectionWithSameName_NotCleared()
    {
        var a = ManualSaves.SaveCurrent(_f.Load("desktop"), "quality", makeActive: false, sync: false);
        _f.Selections.Set("desktop", G, PresetRef.Recommended("quality"));
        ManualSaves.Delete(_f.Store, G, "desktop", a.Save.Slug, sync: false);
        Assert.Equal(PresetRef.Recommended("quality"), _f.Selections.Get("desktop", G));
    }

    [Fact]
    public void Rename_KeepsSlugAndSnapshots()
    {
        var r = ManualSaves.SaveCurrent(_f.Load("desktop"), "Before", sync: false);
        Assert.Null(ManualSaves.Rename(_f.Store, G, "desktop", r.Save.Slug, "  After  ", sync: false));
        var loaded = _f.Store.LoadSave(G, "desktop", "before");
        Assert.Equal("After", loaded.Name);
        Assert.True(File.Exists(Path.Combine(loaded.Dir, "blob.snap")));
        Assert.True(loaded.Body.ContainsKey("user"));
    }

    [Fact]
    public void SyncChange_AutoSyncOff_IsNull() => Assert.Null(ManualSaves.SyncChange(_f.Paths, "msg"));

    [Fact]
    public void SyncChange_NotARepo_IsNull()
    {
        AppPaths.SaveSettings(_f.Paths.DataDir, new AppSettings { AutoSync = true });
        Assert.Null(ManualSaves.SyncChange(_f.Paths, "msg"));
    }

    [Fact]
    public void SaveCurrent_AutoSyncOn_CommitsToConfigRepo()
    {
        if (!GitSyncTests.GitAvailable) return; // git not on PATH: nothing to test
        GitSyncTests.InitRepo(_f.Paths.RepoRoot);
        AppPaths.SaveSettings(_f.Paths.DataDir, new AppSettings { AutoSync = true });

        var r = ManualSaves.SaveCurrent(_f.Load("desktop"), "Synced", sync: true);
        Assert.NotNull(r.Sync);
        Assert.True(r.Sync!.Ok, r.Sync.Message);
        Assert.Contains("no remote", r.Sync.Message);
        var log = GitSyncTests.Git(_f.Paths.RepoRoot, "log", "--name-only", "--format=%s");
        Assert.Contains("chore(saves): add testgame/desktop 'Synced'", log);
        Assert.Contains("config/saves/testgame/desktop/synced/save.yaml", log);
        Assert.DoesNotContain("games/testgame.yaml", log); // only saves/ is staged
    }
}
