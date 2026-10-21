using OptimusVibe.Config;
using OptimusVibe.Engine;

namespace OptimusVibe.Tests;

public sealed class SnapshotsFromTests : IDisposable
{
    private readonly List<EngineFixture> _fixtures = new();

    public void Dispose() => _fixtures.ForEach(f => f.Dispose());

    private EngineFixture Fixture(string snapshotsFrom = "borrowed", string? yaml = null)
    {
        var f = new EngineFixture(yaml ?? EngineFixture.GameYaml.ReplaceLineEndings("\n").Replace(
            "    performance:\n      extends: base\n      user: { S: { Shadows: 1 } }",
            $"    performance:\n      extends: base\n      snapshotsFrom: {snapshotsFrom}\n      user: {{ S: {{ Shadows: 1 }} }}"));
        _fixtures.Add(f);
        return f;
    }

    private static Dictionary<string, object?> NoBody() => new();

    [Fact]
    public void YamlParse_KeepsSnapshotsFromOnPreset_AndResolves()
    {
        var f = Fixture();
        var game = f.Store.LoadGame(EngineFixture.GameId);
        Assert.Equal("borrowed", game.Presets["desktop"]["performance"]["snapshotsFrom"]);
        var resolved = ProfileResolver.ResolvePreset(game, "desktop", f.Devices.Devices["desktop"], "performance");
        Assert.Equal("borrowed", resolved.SnapshotsFrom);
        Assert.DoesNotContain("snapshotsFrom", resolved.FileBlocks.Keys);
        Assert.Contains("user", resolved.FileBlocks.Keys);
        Assert.Null(ProfileResolver.ResolvePreset(game, "desktop", f.Devices.Devices["desktop"], "quality").SnapshotsFrom);
    }

    [Fact]
    public void RecommendedPreset_WithExistingSave_GetsSnapshots_AndKeepsKeyedTargets()
    {
        var f = Fixture();
        var save = f.WriteSave("desktop", "Borrowed", NoBody(), new() { ["blob"] = [1, 2, 3] });
        Assert.Equal("borrowed", save.Slug);

        var ctx = f.Load("desktop", PresetRef.Recommended("performance"));
        Assert.Null(ctx.Save);
        Assert.Equal("borrowed", ctx.SnapshotsSave?.Slug);
        Assert.Equal(Path.Combine(save.Dir, "blob.snap"), ctx.Files.Single(x => x.Id == "blob").SnapshotFile);
        Assert.Null(ctx.Files.Single(x => x.Id == "user").SnapshotFile); // no user.snap in the save
        Assert.Equal("1", ctx.Files.Single(x => x.Id == "user").Targets["[S]Shadows"]);
        Assert.Contains(GameEngine.Plan(ctx), p => p.File.Id == "blob" && p.RestoreSnapshot);
    }

    [Fact]
    public void RecommendedPreset_MissingSave_AppliesWithoutSnapshots_AndDoesNotThrow()
    {
        var f = Fixture("not-there");
        var ctx = f.Load("desktop", PresetRef.Recommended("performance"));
        Assert.Null(ctx.SnapshotsSave);
        Assert.All(ctx.Files, x => Assert.Null(x.SnapshotFile));
        Assert.Equal("1", ctx.Files.Single(x => x.Id == "user").Targets["[S]Shadows"]);
    }

    [Fact]
    public void RecommendedPreset_SaveOnOtherDevice_IsNotUsed()
    {
        var f = Fixture();
        f.WriteSave("ally-x", "Borrowed", NoBody(), new() { ["blob"] = [9] });
        var ctx = f.Load("desktop", PresetRef.Recommended("performance"));
        Assert.Null(ctx.SnapshotsSave);
    }

    [Fact]
    public void RecommendedPreset_PathLikeSlug_IsIgnored()
    {
        var f = Fixture("\"../x\"");
        var ctx = f.Load("desktop", PresetRef.Recommended("performance"));
        Assert.Null(ctx.SnapshotsSave);
    }

    [Fact]
    public void PresetWithoutSnapshotsFrom_HasNoSnapshots()
    {
        var f = Fixture();
        f.WriteSave("desktop", "Borrowed", NoBody(), new() { ["blob"] = [1] });
        var ctx = f.Load("desktop", PresetRef.Recommended("quality"));
        Assert.Null(ctx.SnapshotsSave);
        Assert.All(ctx.Files, x => Assert.Null(x.SnapshotFile));
    }

    [Fact]
    public void ManualSaveSelection_StillUsesItsOwnSnapshots()
    {
        var f = Fixture();
        f.WriteSave("desktop", "Borrowed", NoBody(), new() { ["blob"] = [1] });
        var own = f.WriteSave("desktop", "Own", NoBody(), new() { ["blob"] = [2] });
        var ctx = f.Load("desktop", PresetRef.Manual(own.Slug));
        Assert.Equal(own.Slug, ctx.Save?.Slug);
        Assert.Equal(own.Slug, ctx.SnapshotsSave?.Slug);
        Assert.Equal(Path.Combine(own.Dir, "blob.snap"), ctx.Files.Single(x => x.Id == "blob").SnapshotFile);
        Assert.Null(ctx.Profile.SnapshotsFrom);
    }

    [Fact]
    public void Validator_AcceptsField_AndDoesNotTreatItAsFileId()
    {
        var f = Fixture();
        var r = Validator.Run(f.Store);
        Assert.DoesNotContain(r.Errors, e => e.Contains("snapshotsFrom") || e.Contains("not a file id"));
        Assert.Contains(r.Warnings, w => w.Contains("snapshotsFrom 'borrowed'") && w.Contains("not a manual save"));

        f.WriteSave("desktop", "Borrowed", NoBody(), new() { ["blob"] = [1] });
        var again = Validator.Run(f.Store);
        Assert.DoesNotContain(again.Warnings, w => w.Contains("snapshotsFrom"));
    }

    [Fact]
    public void Validator_BadValue_IsError()
    {
        var bad = Validator.Run(Fixture("\"a/b\"").Store);
        Assert.Contains(bad.Errors, e => e.Contains("snapshotsFrom must be"));
        var empty = Validator.Run(Fixture("\"\"").Store);
        Assert.Contains(empty.Errors, e => e.Contains("snapshotsFrom must be"));
    }

    [Fact]
    public void Validator_NoFileHandler_WarnsOnly()
    {
        var yaml = """
            name: Test Game
            store: other
            processes: [Test.exe]
            files:
              - id: user
                handler: ini
                path: "{T}/user.ini"
            presets:
              desktop:
                performance:
                  snapshotsFrom: borrowed
                  user: { S: { Shadows: 1 } }
            """.ReplaceLineEndings("\n");
        var r = Validator.Run(Fixture(yaml: yaml).Store);
        Assert.DoesNotContain(r.Errors, e => e.Contains("snapshotsFrom"));
        Assert.Contains(r.Warnings, w => w.Contains("no 'handler: file'"));
    }

    [Fact]
    public void Validator_SnapshotsFromInProfile_IsError()
    {
        var yaml = EngineFixture.GameYaml.ReplaceLineEndings("\n").Replace("  base:\n", "  base:\n    snapshotsFrom: x\n");
        var r = Validator.Run(Fixture(yaml: yaml).Store);
        Assert.Contains(r.Errors, e => e.Contains("profiles.base") && e.Contains("snapshotsFrom"));
    }

    [Fact]
    public void FileIdNamedSnapshotsFrom_IsReserved()
    {
        Assert.Contains("snapshotsFrom", ProfileResolver.ReservedKeys);
    }
}
