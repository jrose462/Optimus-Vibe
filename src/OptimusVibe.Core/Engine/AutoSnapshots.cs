using System.Text;
using System.Text.Json;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Engine;

/// <summary>What is known about one stored copy (<c>&lt;fileId&gt;.json</c> next to the <c>.snap</c>).</summary>
public sealed class AutoSnapshotMeta
{
    public string Sha256 { get; set; } = "";
    public DateTime Utc { get; set; }

    /// <summary>The preset key the copy belongs to ("performance", "manual:steady", "_device").</summary>
    public string Preset { get; set; } = "";

    /// <summary>"first session" for the copy captured when none was stored yet; otherwise null.</summary>
    public string? SessionNote { get; set; }
}

/// <summary>One stored copy as listed by <see cref="AutoSnapshotStore.List"/>.</summary>
public sealed record AutoSnapshotEntry(string Game, string Device, string Key, string FileId, AutoSnapshotMeta Meta, string SnapPath);

/// <summary>
/// This device's own copies of files a launcher cloud-syncs between devices (<c>autoSnapshot</c> on a files: entry):
/// <c>auto-snapshots\&lt;game&gt;\&lt;device&gt;\&lt;presetKey&gt;\&lt;fileId&gt;.snap</c> + <c>.json</c> in the local data dir.
/// Never synced to the config repo and not part of the backups rotation. Writes are atomic (temp file + move).
/// </summary>
public sealed class AutoSnapshotStore(string rootDir)
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string RootDir => rootDir;

    public string KeyDir(string game, string device, string key) =>
        Path.Combine(rootDir, Segment(game), Segment(device), Segment(key));

    public string SnapPath(string game, string device, string key, string fileId) =>
        Path.Combine(KeyDir(game, device, key), Segment(fileId) + ".snap");

    private string MetaPath(string game, string device, string key, string fileId) =>
        Path.Combine(KeyDir(game, device, key), Segment(fileId) + ".json");

    /// <summary>A path-safe folder/file name: "manual:slug" becomes "manual-slug" (a colon is not valid in a Windows file name).</summary>
    internal static string Segment(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim())
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '-' : c);
        var r = sb.ToString().TrimEnd('.', ' ');
        return r.Length == 0 || r is "." or ".." ? "_" : r;
    }

    /// <summary>The stored copy's meta, or null when there is no copy (a copy without readable meta gets one worked out from the file).</summary>
    public AutoSnapshotMeta? Get(string game, string device, string key, string fileId)
    {
        var snap = SnapPath(game, device, key, fileId);
        if (!File.Exists(snap)) return null;
        try
        {
            var meta = MetaPath(game, device, key, fileId);
            if (File.Exists(meta) && JsonSerializer.Deserialize<AutoSnapshotMeta>(File.ReadAllText(meta), Opts) is { Sha256.Length: > 0 } m)
                return m;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ignoring unreadable meta for {snap}: {ex.Message}");
        }
        try
        {
            return new AutoSnapshotMeta { Sha256 = BackupStore.Hash([File.ReadAllBytes(snap)]), Utc = File.GetLastWriteTimeUtc(snap), Preset = key };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Store a copy. Returns false (writing nothing) when the stored copy already has the same contents.</summary>
    public bool Save(string game, string device, string key, string fileId, byte[] data, string? sessionNote = null, DateTime? utc = null)
    {
        var hash = BackupStore.Hash([data]);
        if (Get(game, device, key, fileId) is { } existing && existing.Sha256 == hash) return false;
        var dir = KeyDir(game, device, key);
        Directory.CreateDirectory(dir);
        var snap = SnapPath(game, device, key, fileId);
        WriteAtomic(snap, data);
        var meta = new AutoSnapshotMeta { Sha256 = hash, Utc = utc ?? DateTime.UtcNow, Preset = key, SessionNote = sessionNote };
        WriteAtomic(MetaPath(game, device, key, fileId), new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(meta, Opts)));
        return true;
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Every stored copy of a game, optionally only one device's.</summary>
    public List<AutoSnapshotEntry> List(string game, string? device = null)
    {
        var result = new List<AutoSnapshotEntry>();
        var gameDir = Path.Combine(rootDir, Segment(game));
        if (!Directory.Exists(gameDir)) return result;
        foreach (var devDir in Directory.EnumerateDirectories(gameDir))
        {
            var dev = Path.GetFileName(devDir);
            if (device is not null && !dev.Equals(Segment(device), StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var keyDir in Directory.EnumerateDirectories(devDir))
                foreach (var snap in Directory.EnumerateFiles(keyDir, "*.snap"))
                {
                    var key = Path.GetFileName(keyDir);
                    var fileId = Path.GetFileNameWithoutExtension(snap);
                    var meta = Get(game, dev, key, fileId);
                    if (meta is not null) result.Add(new AutoSnapshotEntry(game, dev, key, fileId, meta, snap));
                }
        }
        return result.OrderBy(e => e.Device, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.FileId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Delete the stored copies for one preset key (all of a device's keys when <paramref name="key"/> is null), optionally one file only. Returns how many copies went.</summary>
    public int Forget(string game, string device, string? key = null, string? fileId = null)
    {
        int removed = 0;
        foreach (var e in List(game, device))
        {
            if (key is not null && !e.Key.Equals(Segment(key), StringComparison.OrdinalIgnoreCase)) continue;
            if (fileId is not null && !e.FileId.Equals(Segment(fileId), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Delete(e.SnapPath);
                var meta = Path.ChangeExtension(e.SnapPath, ".json");
                if (File.Exists(meta)) File.Delete(meta);
                removed++;
                var dir = Path.GetDirectoryName(e.SnapPath)!;
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not delete {e.SnapPath}: {ex.Message}");
            }
        }
        return removed;
    }
}

/// <summary>The engine side of <c>autoSnapshot</c>: which key a file uses, restoring before launch (via GameContext), keeping a copy after.</summary>
public static class AutoSnapshots
{
    public const string PerPreset = "perPreset";
    public const string PerDevice = "perDevice";
    public const string DeviceKey = "_device";
    public static readonly string[] Modes = [PerPreset, PerDevice];

    /// <summary>The normalised mode of a file ("perPreset" / "perDevice"), or null when off or unrecognised.</summary>
    public static string? ModeOf(FileDef def) =>
        Modes.FirstOrDefault(m => m.Equals(def.AutoSnapshot?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Store key: the active preset ("performance", "manual:steady") for perPreset, "_device" for perDevice; null when off (or no preset).</summary>
    public static string? KeyFor(FileDef def, PresetRef? preset) => ModeOf(def) switch
    {
        PerDevice => DeviceKey,
        PerPreset when preset is not null => preset.Kind == PresetKind.Manual ? "manual:" + preset.Name : preset.Name,
        _ => null,
    };

    public static AutoSnapshotStore StoreFor(AppPaths paths) => new(paths.AutoSnapshotsDir);

    /// <summary>
    /// A normal run's whole-file change on an autoSnapshot file is expected (the player tuned it in-game, and it is kept automatically),
    /// so it neither prompts for a manual save nor counts as the game overriding the preset.
    /// </summary>
    public static bool IsExpectedDrift(GameContext ctx, Drift d) =>
        d.Applied is null && d.Key == "(whole file)"
        && ctx.Files.FirstOrDefault(f => f.Id.Equals(d.FileId, StringComparison.OrdinalIgnoreCase)) is { } f
        && ModeOf(f.Def) is not null;

    /// <summary>
    /// After a session: keep each autoSnapshot file's current contents for the active key (unchanged contents are skipped). Only when
    /// the game process was seen and the run was not cancelled; the caller never calls it for probe runs. Never throws.
    /// </summary>
    public static List<string> CaptureAfterSession(GameContext ctx, bool processSeen, bool cancelled = false)
    {
        var kept = new List<string>();
        if (!processSeen || cancelled) return kept;
        var store = StoreFor(ctx.Paths);
        foreach (var f in ctx.Files)
        {
            var key = KeyFor(f.Def, ctx.Preset);
            if (key is null || f.Path is null) continue;
            try
            {
                if (!f.Handler.Exists(f.Path)) continue;
                var data = f.Handler.Snapshot(f.Path);
                if (data is null) continue;
                var first = store.Get(ctx.Game.Id, ctx.Device.Name, key, f.Id) is null;
                if (!store.Save(ctx.Game.Id, ctx.Device.Name, key, f.Id, data, first ? "first session" : null)) continue;
                kept.Add(f.Id);
                Log.Info($"{f.Id}: kept this device's copy for {Describe(f.Def, ctx.Preset, ctx.Profile)} (auto)");
            }
            catch (Exception ex)
            {
                Log.Warn($"{f.Id}: could not keep this device's copy: {ex.Message}");
            }
        }
        return kept;
    }

    /// <summary>A CLI --preset value as a store key: "performance" / "recommended:performance" -> "performance"; "manual:x" and "_device" stay.</summary>
    public static string KeyFromArg(string value)
    {
        if (value == DeviceKey) return value;
        var r = PresetRef.Parse(value);
        return r.Kind == PresetKind.Manual ? "manual:" + r.Name : r.Name;
    }

    /// <summary>"restore auto copy (Performance, 2026-10-20)" for plans and inspect; null when the file restores nothing automatic.</summary>
    public static string? RestoreText(ResolvedFile f, PresetRef? preset, ResolvedProfile? profile = null) =>
        f.AutoCopy is { } m ? $"restore auto copy ({Describe(f.Def, preset, profile)}, {m.Utc.ToLocalTime():yyyy-MM-dd})" : null;

    /// <summary>"Performance" / "Steady 40" / "this device" for messages and the game page.</summary>
    public static string Describe(FileDef def, PresetRef? preset, ResolvedProfile? profile = null) =>
        ModeOf(def) == PerDevice ? "this device" : profile?.Label ?? preset?.Name ?? "the preset";

    /// <summary>
    /// The game page's line for a game with an autoSnapshot file: the kept copy for the active preset, or what to expect on the first launch.
    /// Null when the game has no such file (or no active preset for perPreset files).
    /// </summary>
    public static string? StatusLine(GameContext ctx)
    {
        var f = ctx.Files.FirstOrDefault(x => x.AutoKey is not null);
        if (f is null) return null;
        var perDevice = ModeOf(f.Def) == PerDevice;
        var meta = StoreFor(ctx.Paths).Get(ctx.Game.Id, ctx.Device.Name, f.AutoKey!, f.Id);
        if (meta is null)
            return perDevice
                ? "First launch on this device: set it up in-game; Optimus-Vibe keeps it for next time."
                : "First launch with this preset: set it up in-game; Optimus-Vibe keeps it for next time.";
        var date = meta.Utc.ToLocalTime().ToString("MMM d, yyyy");
        return perDevice
            ? $"Settings file kept per device: copy from {date}"
            : $"Settings file kept per device: {Describe(f.Def, ctx.Preset, ctx.Profile)} copy from {date}";
    }

    /// <summary>True when the active preset has a kept copy the player could forget.</summary>
    public static bool HasCopy(GameContext ctx) =>
        ctx.Files.Any(f => f.AutoKey is not null && StoreFor(ctx.Paths).Get(ctx.Game.Id, ctx.Device.Name, f.AutoKey, f.Id) is not null);

    /// <summary>Delete this device's kept copies for the active preset (every autoSnapshot file). Returns how many went.</summary>
    public static int ForgetActive(GameContext ctx)
    {
        var store = StoreFor(ctx.Paths);
        return ctx.Files.Where(f => f.AutoKey is not null).Sum(f => store.Forget(ctx.Game.Id, ctx.Device.Name, f.AutoKey, f.Id));
    }
}
