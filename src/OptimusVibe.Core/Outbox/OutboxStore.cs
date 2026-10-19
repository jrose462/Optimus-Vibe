using System.Globalization;
using System.Text;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Outbox;

/// <summary>
/// The offline outbox on disk: <c>&lt;DataDir&gt;/outbox/&lt;createdTicks&gt;-&lt;id&gt;.json</c>, one file per item, written atomically
/// (temp file + move). Mutations (and the corrupt-file sweep) are serialized across threads AND processes (app + CLI) with the
/// named mutex <c>OptimusVibe.Outbox</c>. Reads never throw on a bad file: it is logged and moved to <c>outbox/corrupt/</c>.
/// </summary>
public sealed class OutboxStore
{
    public const string MutexName = "OptimusVibe.Outbox";

    public string Dir { get; }
    public string CorruptDir => Path.Combine(Dir, "corrupt");

    /// <param name="dataDir">Defaults to <see cref="AppPaths.DefaultDataDir"/>.</param>
    public OutboxStore(string? dataDir = null) => Dir = Path.Combine(dataDir ?? AppPaths.DefaultDataDir, "outbox");

    /// <summary>All readable items, oldest first.</summary>
    public IReadOnlyList<OutboxItem> List()
    {
        if (!Directory.Exists(Dir)) return [];
        return Locked(() =>
        {
            var items = new List<OutboxItem>();
            foreach (var file in Directory.EnumerateFiles(Dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var item = TryRead(file);
                if (item is not null) items.Add(item);
            }
            return (IReadOnlyList<OutboxItem>)items.OrderBy(i => i.CreatedUtc).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        }) ?? [];
    }

    public int Count => List().Count;

    public int CountOf(OutboxKind kind) => List().Count(i => i.Kind == kind);

    /// <summary>Stores a new item. Throws <see cref="IOException"/> when the disk refuses (callers should tell the user).</summary>
    public OutboxItem Add(OutboxItem item)
    {
        Locked(() =>
        {
            Directory.CreateDirectory(Dir);
            Write(PathFor(item), item);
            return 0;
        });
        Log.Info($"outbox: queued {item.Kind} {item.Id}");
        return item;
    }

    /// <summary>Rewrites an existing item (attempts / last error). Returns false when it no longer exists (e.g. another process sent it).</summary>
    public bool Update(OutboxItem item) =>
        Locked(() =>
        {
            var file = FindFile(item.Id);
            if (file is null) return false;
            Write(file, item);
            return true;
        });

    /// <summary>Deletes an item. Returns false when it was not there.</summary>
    public bool Remove(string id) =>
        Locked(() =>
        {
            var file = FindFile(id);
            if (file is null) return false;
            File.Delete(file);
            return true;
        });

    public OutboxItem? Get(string id) => List().FirstOrDefault(i => i.Id == id);

    // ------------------------------------------------------------ internals

    private string PathFor(OutboxItem item) =>
        Path.Combine(Dir, $"{item.CreatedUtc.ToUniversalTime().Ticks.ToString("D19", CultureInfo.InvariantCulture)}-{item.Id}.json");

    private string? FindFile(string id)
    {
        if (!Directory.Exists(Dir) || id.Any(c => !char.IsLetterOrDigit(c) && c != '-')) return null;
        return Directory.EnumerateFiles(Dir, "*-" + id + ".json").FirstOrDefault();
    }

    private static void Write(string path, OutboxItem item)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, item.ToJson(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private OutboxItem? TryRead(string file)
    {
        try
        {
            return OutboxItem.FromJson(File.ReadAllText(file, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException or NullReferenceException)
        {
            Quarantine(file, ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"outbox: can't read {Path.GetFileName(file)}: {ex.Message}");
            return null;
        }
    }

    private void Quarantine(string file, string reason)
    {
        Log.Warn($"outbox: {Path.GetFileName(file)} is unreadable ({reason}); moved to outbox/corrupt");
        try
        {
            Directory.CreateDirectory(CorruptDir);
            var target = Path.Combine(CorruptDir, Path.GetFileName(file));
            if (File.Exists(target)) target = Path.Combine(CorruptDir, Guid.NewGuid().ToString("N")[..8] + "-" + Path.GetFileName(file));
            File.Move(file, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"outbox: couldn't move the bad file: {ex.Message}");
        }
    }

    private static T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, MutexName);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { held = true; } // another process died holding it; we own it now
            // If it times out we go ahead anyway: every write is atomic, so the worst case is a stale attempt counter.
            return action();
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }
}
