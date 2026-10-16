using GameProfiles.Config;
using GameProfiles.Logging;

namespace GameProfiles.Engine;

public sealed class SaveResult
{
    public required ManualSave Save { get; init; }
    public int KeysSaved { get; init; }
    public List<string> Snapshots { get; init; } = new();
    public List<string> Skipped { get; init; } = new();
    public GitResult? Sync { get; set; }
}

/// <summary>
/// Manual saves: the game's current settings, recorded for this device under a name. Every key that any preset
/// mentions is recorded with its current value; files no preset maps are stored whole. Shared by CLI, runner and app.
/// </summary>
public static class ManualSaves
{
    /// <summary>
    /// Record current settings as a new save, or overwrite <paramref name="updateSlug"/>. Optionally make it the
    /// active preset. Commits and syncs the config repo when AutoSync is on (set <paramref name="sync"/> false to defer).
    /// </summary>
    public static SaveResult SaveCurrent(GameContext ctx, string name, string? updateSlug = null, bool makeActive = true,
        string? notes = null, bool sync = true)
    {
        var existing = updateSlug is null ? null : ctx.Store.LoadSave(ctx.Game.Id, ctx.Device.Name, updateSlug);
        var now = DateTime.Now;
        var save = existing ?? new ManualSave
        {
            Created = now,
            BasedOn = ctx.Profile.Source?.Kind == PresetKind.Manual ? ctx.Save?.BasedOn : ctx.Profile.Source?.ToString(),
        };
        save.Name = string.IsNullOrWhiteSpace(name) ? save.Name : name.Trim();
        if (string.IsNullOrWhiteSpace(save.Name)) save.Name = $"Saved {now:MMM d HH:mm}";
        save.Updated = now;
        if (notes is not null) save.Notes = notes;

        var body = new Dictionary<string, object?>();
        if (ctx.Profile.Display is { } d)
        {
            var display = new Dictionary<string, object?>();
            if (d.Width is not null) display["width"] = d.Width;
            if (d.Height is not null) display["height"] = d.Height;
            if (d.Refresh is not null) display["refresh"] = d.Refresh;
            body[ProfileResolver.Display] = display;
        }
        if (ctx.Profile.Nvidia.Count > 0) body[ProfileResolver.Nvidia] = ctx.Profile.Nvidia.ToDictionary(k => k.Key, k => (object?)k.Value);
        if (ctx.Profile.Amd.Count > 0) body[ProfileResolver.Amd] = ctx.Profile.Amd.ToDictionary(k => k.Key, k => (object?)k.Value);

        var snapshots = new Dictionary<string, byte[]>();
        var skipped = new List<string>();
        int keyCount = 0;
        foreach (var f in ctx.Files)
        {
            if (f.Path is null || !f.Handler.Exists(f.Path))
            {
                skipped.Add($"{f.Id}: {f.Error ?? "not found"}");
                continue;
            }
            var keys = GameEngine.KnownKeys(ctx.Game, f.Def, f.Handler);
            if (keys.Count == 0)
            {
                snapshots[f.Id] = f.Handler.Snapshot(f.Path)!;
                continue;
            }
            var current = f.Handler.Read(f.Def, f.Path, keys)
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);
            keyCount += current.Count;
            if (current.Count > 0) body[f.Id] = f.Handler.Unflatten(current);
        }

        save.Body = body;
        ctx.Store.WriteSave(ctx.Game.Id, ctx.Device.Name, save, snapshots);
        Log.Info($"Manual save '{save.Name}' ({save.Slug}) for {ctx.Game.Id} on {ctx.Device.Name}: {keyCount} setting(s), {snapshots.Count} snapshot(s)");

        if (makeActive) ctx.Selections.Set(ctx.Device.Name, ctx.Game.Id, PresetRef.Manual(save.Slug));

        var result = new SaveResult { Save = save, KeysSaved = keyCount, Snapshots = snapshots.Keys.ToList(), Skipped = skipped };
        if (sync) result.Sync = SyncChange(ctx.Paths, $"chore(saves): {(existing is null ? "add" : "update")} {ctx.Game.Id}/{ctx.Device.Name} '{save.Name}'");
        return result;
    }

    public static GitResult? Rename(ConfigStore store, string gameId, string device, string slug, string newName, bool sync = true)
    {
        var save = store.LoadSave(gameId, device, slug);
        save.Name = newName.Trim();
        save.Updated = DateTime.Now;
        var snaps = Directory.EnumerateFiles(save.Dir, "*.snap")
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), File.ReadAllBytes);
        store.WriteSave(gameId, device, save, snaps);
        return sync ? SyncChange(store.Paths, $"chore(saves): rename {gameId}/{device} '{save.Name}'") : null;
    }

    public static GitResult? Delete(ConfigStore store, string gameId, string device, string slug, bool sync = true)
    {
        store.DeleteSave(gameId, device, slug);
        var selections = new SelectionStore(store.Paths.SelectionsFile);
        if (selections.Get(device, gameId) is { Kind: PresetKind.Manual } sel && sel.Name == slug)
            selections.Set(device, gameId, null);
        return sync ? SyncChange(store.Paths, $"chore(saves): delete {gameId}/{device}/{slug}") : null;
    }

    /// <summary>
    /// Publish config/saves through the configured sync provider (git CLI or GitHub API), if AutoSync is on.
    /// Null when sync is off or the git provider has no repo; never throws.
    /// </summary>
    public static GitResult? SyncChange(AppPaths paths, string message)
    {
        try
        {
            if (!AppPaths.LoadSettings(paths.DataDir).AutoSync) return null;
            var provider = GameProfiles.Sync.SyncProviders.For(paths);
            if (!provider.IsReady(out var why))
            {
                if (provider.Name == GameProfiles.Sync.SyncProviders.Git) return null; // config folder simply isn't a checkout
                Log.Warn($"sync skipped: {why}");
                return new GitResult(false, why);
            }
            return provider.CommitAndSync(message, paths.SavesDir);
        }
        catch (Exception ex)
        {
            Log.Warn($"sync failed: {ex.Message}");
            return new GitResult(false, ex.Message);
        }
    }
}
