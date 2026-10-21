using OptimusVibe.Config;
using OptimusVibe.Handlers;
using OptimusVibe.Logging;

namespace OptimusVibe.Engine;

public sealed record KeyChange(string Key, string? Current, string Target);

public sealed class FilePlan
{
    public required ResolvedFile File { get; init; }
    public List<KeyChange> Changes { get; init; } = new();
    public bool RestoreSnapshot { get; init; }

    /// <summary>Reason the file is skipped (unresolvable path, not created yet...).</summary>
    public string? Skip { get; init; }

    /// <summary>The skip is only because the settings file does not exist yet (first-run games): worth retrying once the game has run.</summary>
    public bool Missing { get; init; }

    /// <summary>Keys the handler refused for this file (key -> reason); they are left out of <see cref="Changes"/> and of what is applied.</summary>
    public IReadOnlyDictionary<string, string> Rejected { get; init; } = new Dictionary<string, string>();

    public bool HasWork => Skip is null && (RestoreSnapshot || Changes.Count > 0);
}

/// <summary>What was written, used after the game exits to detect in-game changes.</summary>
public sealed class AppliedState
{
    public Dictionary<string, Dictionary<string, string>> Targets { get; } = new();
    public Dictionary<string, string> SnapshotHashes { get; } = new();
}

public sealed record Drift(string FileId, string Key, string? Applied, string? Now);

/// <summary>A key that was applied but is no longer in the file: for gvas the game drops properties equal to their defaults, so this is a note, not an override.</summary>
public sealed record DroppedKey(string FileId, string Key, string Applied);

/// <summary>Plan / apply / drift for one game on this machine.</summary>
public static class GameEngine
{
    public static List<FilePlan> Plan(GameContext ctx)
    {
        var plans = new List<FilePlan>();
        foreach (var f in ctx.Files)
        {
            if (f.Path is null)
            {
                plans.Add(new FilePlan { File = f, Skip = f.Error ?? "path not resolved" });
                continue;
            }
            bool snapshot = f.SnapshotFile is not null;
            if (f.Targets.Count == 0 && !snapshot)
            {
                plans.Add(new FilePlan { File = f, Skip = "no settings in this preset" });
                continue;
            }
            bool exists = f.Handler.Exists(f.Path);
            if (!exists && !snapshot && !f.Def.CreateIfMissing)
            {
                plans.Add(new FilePlan { File = f, Skip = "not found — launch the game once so it creates its settings", Missing = true });
                continue;
            }

            var current = exists ? f.Handler.Read(f.Def, f.Path, f.Targets.Keys) : new Dictionary<string, string?>();
            var changes = f.Targets
                .Where(t => snapshot || !Values.Equivalent(current.GetValueOrDefault(t.Key), t.Value))
                .Select(t => new KeyChange(t.Key, current.GetValueOrDefault(t.Key), t.Value))
                .ToList();
            IReadOnlyDictionary<string, string> rejected = new Dictionary<string, string>();
            if (!snapshot && exists && changes.Count > 0)
            {
                rejected = f.Handler.Preflight(f.Def, f.Path, changes.ToDictionary(c => c.Key, c => c.Target));
                if (rejected.Count > 0) changes = changes.Where(c => !rejected.ContainsKey(c.Key)).ToList();
            }
            plans.Add(new FilePlan { File = f, Changes = changes, RestoreSnapshot = snapshot, Rejected = rejected });
        }
        return plans;
    }

    /// <summary>
    /// The work left after the game started (a launcher's cloud sync may have rewritten the files): plain files only the keys that drifted,
    /// snapshot files only when the file no longer matches what was restored. Files the first apply had to skip are planned again.
    /// </summary>
    public static List<FilePlan> PlanReapply(GameContext ctx, AppliedState applied)
    {
        var result = new List<FilePlan>();
        foreach (var plan in Plan(ctx))
        {
            if (plan.Skip is not null) { result.Add(plan); continue; }
            var f = plan.File;
            if (!plan.RestoreSnapshot) { result.Add(plan); continue; }

            // snapshot file: restore again only when it differs from what the first restore left
            bool intact = false;
            try
            {
                intact = applied.SnapshotHashes.TryGetValue(f.Id, out var hash) && f.Path is not null && f.Handler.Exists(f.Path)
                         && BackupStore.Hash([f.Handler.Snapshot(f.Path)]) == hash;
            }
            catch (Exception ex) { Log.Warn($"  {f.Id}: could not compare with the restored snapshot: {ex.Message}"); }
            var drifted = f.Path is not null && f.Handler.Exists(f.Path) && f.Targets.Count > 0
                ? f.Handler.Read(f.Def, f.Path, f.Targets.Keys)
                : new Dictionary<string, string?>();
            var changes = f.Targets
                .Where(t => !Values.Equivalent(drifted.GetValueOrDefault(t.Key), t.Value))
                .Select(t => new KeyChange(t.Key, drifted.GetValueOrDefault(t.Key), t.Value))
                .ToList();
            result.Add(new FilePlan { File = f, Changes = intact ? changes : plan.Changes, RestoreSnapshot = !intact });
        }
        return result;
    }

    public static AppliedState Apply(GameContext ctx, List<FilePlan> plans)
    {
        var state = new AppliedState();
        var backups = new BackupStore(ctx.Paths.BackupsDir);
        if (plans.Any(p => p.HasWork))
        {
            var name = backups.Backup(ctx.Game.Id, ctx.Files.Where(f => f.Path is not null));
            if (name is not null) Log.Info($"Backup saved: {Path.Combine(ctx.Paths.BackupsDir, ctx.Game.Id, name)}");
        }

        foreach (var plan in plans)
        {
            var f = plan.File;
            if (plan.Skip is not null)
            {
                if (f.Targets.Count > 0) Log.Warn($"  {f.Id}: skipped — {plan.Skip}");
                continue;
            }
            try
            {
                f.Handler.SetReadOnly(f.Path!, false);
                if (plan.RestoreSnapshot)
                {
                    f.Handler.RestoreSnapshot(f.Path!, File.ReadAllBytes(f.SnapshotFile!));
                    Log.Info(f.AutoCopy is not null
                        ? $"  {f.Id}: restored this device's copy for {AutoSnapshots.Describe(f.Def, ctx.Preset, ctx.Profile)} (auto)"
                        : ctx.SnapshotsSave is { } from && ctx.Save is null
                        ? $"  {f.Id}: restored snapshot from manual save '{from.Slug}'"
                        : $"  {f.Id}: restored saved snapshot");
                }
                var rejected = plan.Rejected;
                if (plan.Changes.Count > 0)
                {
                    var refused = f.Handler.WritePartial(f.Def, f.Path!, plan.Changes.ToDictionary(c => c.Key, c => c.Target));
                    if (refused.Count > 0) rejected = rejected.Concat(refused).ToDictionary(kv => kv.Key, kv => kv.Value);
                    Log.Info($"  {f.Id}: {plan.Changes.Count - refused.Count} setting(s) written");
                }
                foreach (var (key, reason) in rejected) Log.Warn($"  {f.Id}: key '{key}' not applied: {reason}");
                if (f.Def.ReadOnlyAfterApply) f.Handler.SetReadOnly(f.Path!, true);

                state.Targets[f.Id] = f.Targets.Where(t => !rejected.ContainsKey(t.Key)).ToDictionary(t => t.Key, t => t.Value);
                if (f.Targets.Count == 0 || plan.RestoreSnapshot) // a restored snapshot is remembered even when keys follow it, so re-apply re-restores only a changed one
                    state.SnapshotHashes[f.Id] = BackupStore.Hash([f.Handler.Snapshot(f.Path!)]);
            }
            catch (Exception ex)
            {
                Log.Error($"  {f.Id}: failed to apply ({f.Path})", ex);
            }
        }
        return state;
    }

    /// <summary>Settings the game (or the user, in-game) changed after we applied them.</summary>
    public static List<Drift> FindDrift(GameContext ctx, AppliedState applied, List<DroppedKey>? dropped = null)
    {
        var drift = new List<Drift>();
        foreach (var f in ctx.Files)
        {
            if (f.Path is null) continue;
            try
            {
                if (applied.Targets.TryGetValue(f.Id, out var targets) && targets.Count > 0)
                {
                    var now = f.Handler.Read(f.Def, f.Path, targets.Keys);
                    foreach (var t in targets.Where(t => !Values.Equivalent(now.GetValueOrDefault(t.Key), t.Value)))
                    {
                        var current = now.GetValueOrDefault(t.Key);
                        // Unreal leaves a property out of a GVAS save when it equals its default, so a key that vanished was normalised by the
                        // game (we cannot know the default): note it, but it is not an override.
                        if (current is null && f.Handler is GvasHandler)
                        {
                            dropped?.Add(new DroppedKey(f.Id, t.Key, t.Value));
                            Log.Info($"  {f.Id}: '{t.Key}' (applied {t.Value}) is no longer in the save; the game drops properties equal to their default, so this is not counted as an override");
                            continue;
                        }
                        drift.Add(new Drift(f.Id, t.Key, t.Value, current));
                    }
                }
                else if (applied.SnapshotHashes.TryGetValue(f.Id, out var hash) &&
                         BackupStore.Hash([f.Handler.Snapshot(f.Path)]) != hash)
                {
                    drift.Add(new Drift(f.Id, "(whole file)", null, null));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"  {f.Id}: could not check for changes: {ex.Message}");
            }
        }
        return drift;
    }

    /// <summary>Every key any preset or profile of this game sets for a file — what a manual save records.</summary>
    public static HashSet<string> KnownKeys(GameDef game, FileDef file, SettingsHandler handler)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var bodies = game.Profiles.Values.Concat(game.Presets.Values.SelectMany(p => p.Values));
        // File ids match case-insensitively, as in ProfileResolver.Resolve.
        foreach (var body in bodies)
            foreach (var (key, value) in body)
                if (key.Equals(file.Id, StringComparison.OrdinalIgnoreCase) && value is Dictionary<string, object?> block)
                    keys.UnionWith(handler.Flatten(block).Keys);
        return keys;
    }
}
