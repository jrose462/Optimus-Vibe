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
                    Log.Info($"  {f.Id}: restored saved snapshot");
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
