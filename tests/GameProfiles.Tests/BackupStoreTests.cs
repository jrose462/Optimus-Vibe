using System.Text.RegularExpressions;
using GameProfiles.Config;
using GameProfiles.Engine;

namespace GameProfiles.Tests;

public sealed class BackupStoreTests : IDisposable
{
    private readonly EngineFixture _f = new();
    private const string G = EngineFixture.GameId;

    public BackupStoreTests()
    {
        _f.WriteGameFile("user.ini", "[S]\r\nShadows=1\r\n");
        _f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
    }

    public void Dispose() => _f.Dispose();

    private string GameBackups => Path.Combine(_f.Paths.BackupsDir, G);

    private IEnumerable<ResolvedFile> Files => _f.Load("desktop").Files;

    [Fact]
    public void Backup_WritesOriginalOnce_AndSkipsUnchanged()
    {
        var store = new BackupStore(_f.Paths.BackupsDir);
        var first = store.Backup(G, Files);
        Assert.NotNull(first);
        Assert.True(File.Exists(Path.Combine(GameBackups, BackupStore.Original, "manifest.json")));
        Assert.Null(store.Backup(G, Files)); // nothing changed

        _f.WriteGameFile("user.ini", "[S]\r\nShadows=2\r\n");
        var origUser = File.ReadAllText(Path.Combine(GameBackups, BackupStore.Original, "user.bin"));
        Assert.Contains("Shadows=1", origUser);
    }

    [Fact]
    public void Backup_NameCollision_GetsNumericSuffix()
    {
        var store = new BackupStore(_f.Paths.BackupsDir);
        // Occupy every timestamp the backup could pick in the next few seconds (dirs without a manifest are not backups).
        var now = DateTime.Now;
        for (int s = -1; s <= 10; s++)
            Directory.CreateDirectory(Path.Combine(GameBackups, now.AddSeconds(s).ToString("yyyyMMdd-HHmmss")));

        var name = store.Backup(G, Files);
        Assert.NotNull(name);
        Assert.Matches(new Regex(@"^\d{8}-\d{6}-2$"), name);

        // Same second again with different content: -3
        _f.WriteGameFile("user.ini", "[S]\r\nShadows=3\r\n");
        var stamp = name![..15];
        var next = store.Backup(G, Files)!;
        if (next.StartsWith(stamp)) Assert.Equal(stamp + "-3", next);
        else Assert.Matches(new Regex(@"^\d{8}-\d{6}-2$"), next);
    }

    [Fact]
    public void List_NewestFirst_OriginalLast_AndPrunes()
    {
        var store = new BackupStore(_f.Paths.BackupsDir, keep: 2);
        for (int i = 0; i < 4; i++)
        {
            _f.WriteGameFile("user.ini", $"[S]\r\nShadows={i}\r\n");
            Assert.NotNull(store.Backup(G, Files));
        }
        var list = store.List(G);
        Assert.Equal(BackupStore.Original, list[^1]);
        Assert.Equal(3, list.Count); // 2 kept + original
        Assert.True(string.CompareOrdinal(list[0], list[1]) > 0);
    }

    [Fact]
    public void Restore_PutsFilesBack_AndRemovesFilesThatDidNotExist()
    {
        var store = new BackupStore(_f.Paths.BackupsDir);
        var name = store.Backup(G, Files)!; // blob.bin does not exist yet
        _f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\n");
        File.WriteAllBytes(_f.GameFile("blob.bin"), [1]);

        store.Restore(G, name);
        Assert.Contains("Shadows=1", _f.ReadGameFile("user.ini"));
        Assert.False(File.Exists(_f.GameFile("blob.bin")));
    }

    [Fact]
    public void Restore_Unknown_Throws() =>
        Assert.Throws<InvalidOperationException>(() => new BackupStore(_f.Paths.BackupsDir).Restore(G, "nope"));
}
