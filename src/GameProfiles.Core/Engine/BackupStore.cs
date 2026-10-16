using System.Security.Cryptography;
using System.Text.Json;
using GameProfiles.Handlers;
using GameProfiles.Logging;

namespace GameProfiles.Engine;

/// <summary>
/// Local (per-machine) backups of every file a game config touches:
/// backups\&lt;game&gt;\original\ is written once and kept forever; timestamped copies are kept for the last few applies.
/// </summary>
public sealed class BackupStore(string backupsDir, int keep = 10)
{
    public const string Original = "original";

    private sealed record Entry(string FileId, string Handler, string Path, bool Existed, string? Blob);
    private sealed record Manifest(string Game, DateTime CreatedUtc, string Hash, List<Entry> Entries);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private string GameDir(string gameId) => Path.Combine(backupsDir, gameId);

    public IReadOnlyList<string> List(string gameId) =>
        Directory.Exists(GameDir(gameId))
            ? Directory.EnumerateDirectories(GameDir(gameId))
                .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                .Select(d => Path.GetFileName(d)!)
                .OrderByDescending(n => n == Original ? "" : n, StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>Back up the current state. Returns the backup name, or null when nothing changed since the last one.</summary>
    public string? Backup(string gameId, IEnumerable<ResolvedFile> files)
    {
        var snaps = files
            .Where(f => f.Path is not null)
            .Select(f => (File: f, Data: f.Handler.Snapshot(f.Path!)))
            .ToList();
        var hash = Hash(snaps.Select(s => s.Data));

        if (!Directory.Exists(Path.Combine(GameDir(gameId), Original)))
            Write(gameId, Original, snaps, hash);

        var latest = List(gameId).FirstOrDefault(n => n != Original);
        if (latest is not null && ReadManifest(gameId, latest)?.Hash == hash) return null;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var name = stamp;
        for (int n = 2; Directory.Exists(Path.Combine(GameDir(gameId), name)); n++) name = $"{stamp}-{n}";
        Write(gameId, name, snaps, hash);
        Prune(gameId);
        return name;
    }

    public void Restore(string gameId, string name)
    {
        var manifest = ReadManifest(gameId, name) ?? throw new InvalidOperationException($"No backup '{name}' for {gameId}");
        var dir = Path.Combine(GameDir(gameId), name);
        foreach (var e in manifest.Entries)
        {
            var handler = SettingsHandler.For(e.Handler);
            var data = e.Existed && e.Blob is not null ? File.ReadAllBytes(Path.Combine(dir, e.Blob)) : null;
            handler.RestoreSnapshot(e.Path, data);
            Log.Info($"  restored {e.FileId}: {e.Path}{(data is null ? " (removed — did not exist)" : "")}");
        }
    }

    private void Write(string gameId, string name, List<(ResolvedFile File, byte[]? Data)> snaps, string hash)
    {
        var dir = Path.Combine(GameDir(gameId), name);
        Directory.CreateDirectory(dir);
        var entries = new List<Entry>();
        foreach (var (file, data) in snaps)
        {
            string? blob = null;
            if (data is not null)
            {
                blob = file.Id + ".bin";
                File.WriteAllBytes(Path.Combine(dir, blob), data);
            }
            entries.Add(new Entry(file.Id, file.Handler.Name, file.Path!, data is not null, blob));
        }
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(new Manifest(gameId, DateTime.UtcNow, hash, entries), JsonOpts));
    }

    private Manifest? ReadManifest(string gameId, string name)
    {
        var file = Path.Combine(GameDir(gameId), name, "manifest.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file)) : null;
    }

    private void Prune(string gameId)
    {
        foreach (var old in List(gameId).Where(n => n != Original).Skip(keep))
            Directory.Delete(Path.Combine(GameDir(gameId), old), recursive: true);
    }

    internal static string Hash(IEnumerable<byte[]?> parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts)
        {
            sha.AppendData(p is null ? [0] : [1]);
            if (p is not null) sha.AppendData(p);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
