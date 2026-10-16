using GameProfiles.Config;
using GameProfiles.Handlers;
using GameProfiles.Logging;

namespace GameProfiles.Engine;

public sealed record KeyChange(string Key, string? Current, string Target);

public sealed class FilePlan
{
    public required ResolvedFile File { get; init; }
    public List<KeyChange> Changes { get; init; } = new();
    public bool RestoreSnapshot { get; init; }

    /// <summary>Reason the file is skipped (unresolvable path, not created yet...).</summary>
    public string? Skip { get; init; }

    public bool HasWork => Skip is null && (RestoreSnapshot || Changes.Count > 0);
}

/// <summary>What was written, used after the game exits to detect in-game changes.</summary>
public sealed class AppliedState
{
    public Dictionary<string, Dictionary<string, string>> Targets { get; } = new();
    public Dictionary<string, string> SnapshotHashes { get; } = new();
}

public sealed record Drift(string FileId, string Key, string? Applied, string? Now);

public sealed class CaptureResult
{
    public int KeysCaptured { get; set; }
    public List<string> Snapshots { get; } = new();
    public List<string> Skipped { get; } = new();
}

/// <summary>Plan / apply / capture / drift for one game on this machine.</summary>
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
                plans.Add(new FilePlan { File = f, Skip = "no settings for this profile" });
                continue;
            }
            bool exists = f.Handler.Exists(f.Path);
            if (!exists && !snapshot && !f.Def.CreateIfMissing)
            {
                plans.Add(new FilePlan { File = f, Skip = "not found — launch the game once so it creates its settings" });
                continue;
            }

            var current = exists ? f.Handler.Read(f.Def, f.Path, f.Targets.Keys) : new Dictionary<string, string?>();
            var changes = f.Targets
                .Where(t => snapshot || !Values.Equivalent(current.GetValueOrDefault(t.Key), t.Value))
                .Select(t => new KeyChange(t.Key, current.GetValueOrDefault(t.Key), t.Value))
                .ToList();
            plans.Add(new FilePlan { File = f, Changes = changes, RestoreSnapshot = snapshot });
        }
        return plans;
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
                    Log.Info($"  {f.Id}: restored captured snapshot");
                }
                if (plan.Changes.Count > 0)
                {
                    f.Handler.Write(f.Def, f.Path!, plan.Changes.ToDictionary(c => c.Key, c => c.Target));
                    Log.Info($"  {f.Id}: {plan.Changes.Count} setting(s) written");
                }
                if (f.Def.ReadOnlyAfterApply) f.Handler.SetReadOnly(f.Path!, true);

                state.Targets[f.Id] = new Dictionary<string, string>(f.Targets);
                if (f.Targets.Count == 0)
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
    public static List<Drift> FindDrift(GameContext ctx, AppliedState applied)
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
                    drift.AddRange(targets
                        .Where(t => !Values.Equivalent(now.GetValueOrDefault(t.Key), t.Value))
                        .Select(t => new Drift(f.Id, t.Key, t.Value, now.GetValueOrDefault(t.Key))));
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

    /// <summary>
    /// Save the game's current settings for this device. Mapped keys go to captures\&lt;game&gt;\&lt;device&gt;.yaml as
    /// differences from the base profile; files with no mapped keys (or all files with <paramref name="snapshot"/>)
    /// are copied whole to snapshots\&lt;game&gt;\&lt;device&gt;\.
    /// </summary>
    public static CaptureResult Capture(GameContext ctx, bool snapshot, IReadOnlyCollection<string>? onlyFiles = null)
    {
        var result = new CaptureResult();
        var baseProfile = ProfileResolver.Resolve(ctx.Game, ctx.Device.Name, ctx.Device.Def, new());
        var overlay = ctx.Store.LoadCapture(ctx.Game.Id, ctx.Device.Name);

        foreach (var f in ctx.Files)
        {
            if (onlyFiles is not null && !onlyFiles.Contains(f.Id)) continue;
            if (f.Path is null || !f.Handler.Exists(f.Path))
            {
                result.Skipped.Add($"{f.Id}: {f.Error ?? "not found"}");
                continue;
            }

            var baseTargets = baseProfile.FileBlocks.TryGetValue(f.Id, out var block)
                ? f.Handler.Flatten(block)
                : new Dictionary<string, string>();
            var overlayTargets = overlay.GetValueOrDefault(f.Id) is Dictionary<string, object?> ob
                ? f.Handler.Flatten(ob)
                : new Dictionary<string, string>();

            if (snapshot || (baseTargets.Count == 0 && overlayTargets.Count == 0))
            {
                var data = f.Handler.Snapshot(f.Path);
                var dir = ctx.Paths.SnapshotDir(ctx.Game.Id, ctx.Device.Name);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, f.Id + ".snap"), data!);
                result.Snapshots.Add(f.Id);
                continue;
            }

            var keys = baseTargets.Keys.Union(overlayTargets.Keys).ToList();
            var current = f.Handler.Read(f.Def, f.Path, keys);
            var diff = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var now = current.GetValueOrDefault(key);
                if (now is null) continue;
                if (baseTargets.TryGetValue(key, out var b) && Values.Equivalent(b, now)) continue;
                diff[key] = now;
            }
            result.KeysCaptured += diff.Count;
            if (diff.Count > 0) overlay[f.Id] = f.Handler.Unflatten(diff);
            else overlay.Remove(f.Id);
        }

        ctx.Store.SaveCapture(ctx.Game.Id, ctx.Device.Name, overlay);
        return result;
    }
}
