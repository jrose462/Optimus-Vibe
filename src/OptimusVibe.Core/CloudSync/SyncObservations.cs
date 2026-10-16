using System.Text.Json;
using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Handlers;
using OptimusVibe.Logging;

namespace OptimusVibe.CloudSync;

/// <summary>What was seen for one game on one device.</summary>
public sealed class DeviceObservation
{
    /// <summary>File id → fingerprint (BackupStore.Hash of the handler snapshot) after the last run.</summary>
    public Dictionary<string, string> Files { get; set; } = new();
    public DateTime? RecordedUtc { get; set; }

    /// <summary>Launches that began with every configured file exactly as the previous run left it.</summary>
    public int CleanLaunches { get; set; }

    public List<ExternalChangeRecord> Changes { get; set; } = new();
}

/// <summary>sync-observations.json in the per-machine data dir: game id → device → observation.</summary>
public sealed class SyncObservationFile
{
    public Dictionary<string, Dictionary<string, DeviceObservation>> Games { get; set; } = new();
}

/// <summary>Reads/writes the per-machine observation file. Never throws on a missing or corrupt file.</summary>
public sealed class SyncObservationStore(string path)
{
    public const int MaxChanges = 20;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Path { get; } = path;

    public static SyncObservationStore For(AppPaths paths) => new(System.IO.Path.Combine(paths.DataDir, "sync-observations.json"));

    public SyncObservationFile Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<SyncObservationFile>(File.ReadAllText(Path), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignoring unreadable {Path}: {ex.Message}");
        }
        return new();
    }

    public void Save(SyncObservationFile file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
        File.Move(tmp, Path, overwrite: true);
    }

    public ObservationSummary Summary(string gameId, string device)
    {
        var file = Load();
        return file.Games.TryGetValue(gameId, out var devices) && devices.TryGetValue(device, out var o)
            ? new ObservationSummary { CleanLaunches = o.CleanLaunches, Changes = o.Changes }
            : new ObservationSummary();
    }

    internal static DeviceObservation Entry(SyncObservationFile file, string gameId, string device)
    {
        if (!file.Games.TryGetValue(gameId, out var devices)) file.Games[gameId] = devices = new(StringComparer.OrdinalIgnoreCase);
        if (!devices.TryGetValue(device, out var o)) devices[device] = o = new DeviceObservation();
        return o;
    }
}

/// <summary>
/// Watches whether a game's settings files change while Optimus-Vibe is not running (a launcher cloud sync, or another
/// tool). <see cref="BeforeRun"/> compares each file with how the previous run left it, before settings are applied;
/// <see cref="AfterRun"/> records the fingerprints once the game has exited. Hooked into Runner.Run, which every launch
/// goes through (the app's Play button starts the same headless `run`, and so do Steam launch options). Everything is
/// caught and logged: observing never blocks or fails a launch.
/// </summary>
public static class SyncObserver
{
    /// <summary>At least this fraction of the compared keys must equal another device's preset to count as "arrived from there".</summary>
    public const double MatchRatio = 0.8;
    public const int MinKeys = 3;

    /// <summary>Compare with the last recorded state. Call before applying settings. Returns the change recorded, if any.</summary>
    public static ExternalChangeRecord? BeforeRun(GameContext ctx, DateTime? nowUtc = null)
    {
        try
        {
            var store = SyncObservationStore.For(ctx.Paths);
            var file = store.Load();
            var entry = SyncObservationStore.Entry(file, ctx.Game.Id, ctx.Device.Name);
            if (entry.Files.Count == 0) return null; // nothing recorded yet: no baseline to compare with

            var now = Fingerprints(ctx);
            var changed = now.Where(kv => entry.Files.TryGetValue(kv.Key, out var before) && before != kv.Value).Select(kv => kv.Key).ToList();
            if (changed.Count == 0)
            {
                entry.CleanLaunches++;
                foreach (var (id, hash) in now) entry.Files.TryAdd(id, hash); // files that only now resolve join the baseline
                store.Save(file);
                return null;
            }

            var record = new ExternalChangeRecord { Utc = nowUtc ?? DateTime.UtcNow, Files = changed };
            var match = FindArrival(ctx, record.Files);
            if (match is not null)
            {
                record.FromDevice = match.Device;
                record.Preset = match.Label;
                record.Compared = match.Compared;
                record.Matched = match.Matched;
            }
            entry.Changes.Add(record);
            if (entry.Changes.Count > SyncObservationStore.MaxChanges) entry.Changes.RemoveRange(0, entry.Changes.Count - SyncObservationStore.MaxChanges);
            foreach (var (id, hash) in now) entry.Files[id] = hash; // the new baseline
            store.Save(file);
            Log.Info($"Sync check {ctx.Game.Id}: settings changed while closed ({string.Join(", ", record.Files)})" +
                     (match is null ? "" : $"; matches {match.Device} {match.Label} ({match.Matched}/{match.Compared} keys)"));
            return record;
        }
        catch (Exception ex)
        {
            Log.Warn($"Sync check failed for {ctx.Game.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Record the current fingerprints as the baseline for the next launch. Call after the game exited (and after the capture prompt).</summary>
    public static void AfterRun(GameContext ctx, DateTime? nowUtc = null)
    {
        try
        {
            var store = SyncObservationStore.For(ctx.Paths);
            var file = store.Load();
            var entry = SyncObservationStore.Entry(file, ctx.Game.Id, ctx.Device.Name);
            entry.Files = Fingerprints(ctx);
            entry.RecordedUtc = nowUtc ?? DateTime.UtcNow;
            store.Save(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not record sync fingerprints for {ctx.Game.Id}: {ex.Message}");
        }
    }

    /// <summary>File id → SHA-256 of the handler's snapshot, for every file whose path resolved.</summary>
    public static Dictionary<string, string> Fingerprints(GameContext ctx)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in ctx.Files)
        {
            if (f.Path is null) continue;
            try { result[f.Id] = BackupStore.Hash([f.Handler.Snapshot(f.Path)]); }
            catch (Exception ex) { Log.Warn($"Sync check: could not read {f.Id}: {ex.Message}"); }
        }
        return result;
    }

    private sealed record Arrival(string Device, string Label, int Compared, int Matched);

    /// <summary>
    /// Do the changed files now hold another device's preset (or manual save)? Key-based presets need at least
    /// <see cref="MinKeys"/> compared keys with a <see cref="MatchRatio"/> match; a whole-file snapshot must be identical.
    /// The other device must fit better than this device's own presets, so values shared by every preset prove nothing.
    /// </summary>
    private static Arrival? FindArrival(GameContext ctx, IReadOnlyList<string> changedFileIds)
    {
        var game = ctx.Game;
        var devices = new HashSet<string>(game.Presets.Keys, StringComparer.OrdinalIgnoreCase);
        var savesDir = System.IO.Path.Combine(ctx.Paths.SavesDir, game.Id);
        if (Directory.Exists(savesDir))
            foreach (var d in Directory.EnumerateDirectories(savesDir))
                devices.Add(System.IO.Path.GetFileName(d));

        Arrival? best = null;
        double bestRatio = 0;
        double ownBest = 0;
        foreach (var device in devices)
        {
            bool own = device.Equals(ctx.Device.Name, StringComparison.OrdinalIgnoreCase);
            foreach (var (label, profile, save) in Candidates(ctx, game, device))
            {
                var (compared, matched, exact) = Score(ctx, profile, save, changedFileIds);
                if (compared == 0) continue;
                double ratio = (double)matched / compared;
                if (own) { ownBest = Math.Max(ownBest, ratio); continue; }
                bool qualifies = exact || (compared >= MinKeys && ratio >= MatchRatio);
                if (qualifies && (ratio > bestRatio || (ratio == bestRatio && best is null)))
                {
                    best = new Arrival(device, label, compared, matched);
                    bestRatio = ratio;
                }
            }
        }
        return best is not null && bestRatio > ownBest ? best : null;
    }

    private static IEnumerable<(string Label, ResolvedProfile Profile, ManualSave? Save)> Candidates(GameContext ctx, GameDef game, string device)
    {
        foreach (var name in ProfileResolver.PresetNames(game, device))
        {
            ResolvedProfile? p = null;
            try { p = ProfileResolver.ResolvePreset(game, device, null, name); }
            catch (Exception ex) { Log.Warn($"Sync check: skipped preset {device}/{name}: {ex.Message}"); }
            if (p is not null) yield return (p.Label ?? Capitalize(name), p, null);
        }
        IReadOnlyList<ManualSave> saves = [];
        try { saves = ctx.Store.ListSaves(game.Id, device); }
        catch (Exception ex) { Log.Warn($"Sync check: skipped saves of {device}: {ex.Message}"); }
        foreach (var s in saves)
        {
            ResolvedProfile? p = null;
            try { p = ProfileResolver.ResolveSave(game, device, null, s); }
            catch (Exception ex) { Log.Warn($"Sync check: skipped save {device}/{s.Slug}: {ex.Message}"); }
            if (p is not null) yield return (s.Name, p, s);
        }
    }

    private static (int Compared, int Matched, bool Exact) Score(GameContext ctx, ResolvedProfile profile, ManualSave? save, IReadOnlyList<string> fileIds)
    {
        int compared = 0, matched = 0;
        bool exact = false;
        foreach (var id in fileIds)
        {
            var f = ctx.Files.FirstOrDefault(x => x.Id == id);
            if (f?.Path is null) continue;

            // whole-file snapshot of a manual save: identical bytes
            if (save is not null)
            {
                var snap = System.IO.Path.Combine(save.Dir, id + ".snap");
                if (File.Exists(snap))
                {
                    compared++;
                    if (BackupStore.Hash([File.ReadAllBytes(snap)]) == BackupStore.Hash([f.Handler.Snapshot(f.Path)])) { matched++; exact = true; }
                    continue;
                }
            }

            if (!profile.FileBlocks.TryGetValue(id, out var block)) continue;
            var targets = f.Handler.Flatten(block);
            if (targets.Count == 0 || !f.Handler.Exists(f.Path)) continue;
            var now = f.Handler.Read(f.Def, f.Path, targets.Keys);
            foreach (var (key, want) in targets)
            {
                var have = now.GetValueOrDefault(key);
                if (have is null) continue; // key absent from the file: nothing to compare
                compared++;
                if (Values.Equivalent(have, want)) matched++;
            }
        }
        return (compared, matched, exact && compared > 0 && matched == compared);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
