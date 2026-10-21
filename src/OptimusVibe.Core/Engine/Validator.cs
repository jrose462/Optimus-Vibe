using OptimusVibe.Config;
using OptimusVibe.Handlers;

namespace OptimusVibe.Engine;

public sealed class ValidationReport
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public int GamesChecked { get; set; }
}

/// <summary>Static checks over the config folder — run by `validate` and after Claude adds a game.</summary>
public static class Validator
{
    private static readonly string[] Stores = ["steam", "battlenet", "ea", "other"];
    private static readonly string[] Drivers = ["nvidia", "amd", "none"];
    private static readonly string[] MatchKeys = ["model", "manufacturer", "cpu", "gpu", "machine", "hostname"];

    public static ValidationReport Run(ConfigStore store)
    {
        var report = new ValidationReport();
        DevicesFile devices;
        try
        {
            devices = store.LoadDevices();
        }
        catch (Exception ex)
        {
            report.Errors.Add($"devices.yaml: {ex.Message}");
            return report;
        }
        CheckDevices(devices, report);

        foreach (var id in store.GameIds())
        {
            report.GamesChecked++;
            try
            {
                CheckGame(store, id, devices, report);
            }
            catch (Exception ex)
            {
                report.Errors.Add($"games/{id}.yaml: {ex.Message}");
            }
        }
        return report;
    }

    private static void CheckDevices(DevicesFile devices, ValidationReport report)
    {
        if (devices.Devices.Count == 0) report.Errors.Add("devices.yaml: no devices defined");
        foreach (var (name, d) in devices.Devices)
        {
            if (d.Match.Count == 0) report.Warnings.Add($"devices.{name}: no match rules — only selectable with --profile");
            foreach (var key in d.Match.Keys.Where(k => !MatchKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
                report.Errors.Add($"devices.{name}: unknown match key '{key}' ({string.Join(", ", MatchKeys)})");
            if (d.Driver is not null && !Drivers.Contains(d.Driver, StringComparer.OrdinalIgnoreCase))
                report.Errors.Add($"devices.{name}: driver must be one of {string.Join(", ", Drivers)}");
            if (d.Display is { } m && (m.Width <= 0 || m.Height <= 0 || m.Refresh <= 0))
                report.Errors.Add($"devices.{name}: display values must be positive");
            CheckPresetsFrom(devices, name, d, report);
        }
    }

    private static void CheckPresetsFrom(DevicesFile devices, string name, DeviceDef d, ValidationReport report)
    {
        var from = d.PresetsFrom?.Trim();
        if (string.IsNullOrEmpty(from)) return;
        if (string.Equals(from, name, StringComparison.Ordinal))
        {
            report.Errors.Add($"devices.{name}: presetsFrom points at the device itself");
            return;
        }
        if (!devices.Devices.TryGetValue(from, out var target))
        {
            report.Errors.Add($"devices.{name}: presetsFrom '{from}' is not a device in devices.yaml");
            return;
        }
        // follow the chain: a loop back to this device is an error; a longer chain is legal but only one level is used
        var seen = new List<string> { name, from };
        for (var next = target.PresetsFrom?.Trim(); !string.IsNullOrEmpty(next); next = devices.Devices.GetValueOrDefault(next)?.PresetsFrom?.Trim())
        {
            if (seen.Contains(next, StringComparer.Ordinal))
            {
                report.Errors.Add($"devices.{name}: presetsFrom cycle: {string.Join(" -> ", seen)} -> {next}");
                return;
            }
            seen.Add(next);
        }
        if (!string.IsNullOrWhiteSpace(target.PresetsFrom))
            report.Warnings.Add($"devices.{name}: presetsFrom '{from}' itself uses presets from '{target.PresetsFrom}'; only one level is followed");
    }

    private static void CheckGame(ConfigStore store, string id, DevicesFile devices, ValidationReport report)
    {
        var where = $"games/{id}.yaml";
        var game = store.LoadGame(id);
        if (!string.Equals(game.Id, id, StringComparison.Ordinal))
            report.Errors.Add($"{where}: id '{game.Id}' does not match the file name");
        if (string.IsNullOrWhiteSpace(game.Name)) report.Errors.Add($"{where}: name is required");
        if (!Stores.Contains(game.Store, StringComparer.OrdinalIgnoreCase))
            report.Errors.Add($"{where}: store must be one of {string.Join(", ", Stores)}");
        if (game.Store.Equals("steam", StringComparison.OrdinalIgnoreCase) && game.SteamAppId is null)
            report.Warnings.Add($"{where}: steam game without steamAppId");
        if (game.CloudSyncsSettings is null)
            report.Warnings.Add($"{where}: cloudSyncsSettings is unknown — run `optimus-vibe cloud-check {game.Id}` / the game-settings skill to find out");
        if (game.Processes.Count == 0)
            report.Warnings.Add($"{where}: no processes listed — `run` cannot follow games started through a launcher");
        if (!game.Store.Equals("steam", StringComparison.OrdinalIgnoreCase) && game.Launch is null)
            report.Warnings.Add($"{where}: non-Steam game without launch.exe/launch.uri — `shortcut` will have nothing to start");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in game.Files)
        {
            var fw = $"{where}: files.{f.Id}";
            if (string.IsNullOrWhiteSpace(f.Id)) { report.Errors.Add($"{where}: a file has no id"); continue; }
            if (!ids.Add(f.Id)) report.Errors.Add($"{fw}: duplicate id");
            if (ProfileResolver.ReservedKeys.Contains(f.Id, StringComparer.OrdinalIgnoreCase))
                report.Errors.Add($"{fw}: '{f.Id}' is reserved, pick another id");
            if (!SettingsHandler.Names.Contains(f.Handler, StringComparer.OrdinalIgnoreCase))
            {
                report.Errors.Add($"{fw}: unknown handler '{f.Handler}' ({string.Join(", ", SettingsHandler.Names)})");
                continue;
            }
            if (string.IsNullOrWhiteSpace(f.Path)) report.Errors.Add($"{fw}: path is required");
            foreach (var token in PathResolver.TokensIn(f.Path).Where(t => !PathResolver.KnownTokens.Contains(t, StringComparer.OrdinalIgnoreCase)))
                report.Errors.Add($"{fw}: unknown path token {{{token}}}");
            if (f.Handler.Equals("registry", StringComparison.OrdinalIgnoreCase))
            {
                try { RegistryHandler.SplitPath(f.Path); }
                catch (ConfigException ex) { report.Errors.Add($"{fw}: {ex.Message}"); }
            }
            if (f.Handler.Equals("keyvalue", StringComparison.OrdinalIgnoreCase))
            {
                try { KeyValueTextHandler.BuildRegex(KeyValueTextHandler.TemplateFor(f)); }
                catch (ConfigException ex) { report.Errors.Add($"{fw}: {ex.Message}"); }
            }
        }

        // Every preset must resolve; JSON keys must parse.
        var extended = game.Profiles.Values
            .Concat(game.Presets.Values.SelectMany(p => p.Values))
            .Select(p => p.GetValueOrDefault(ProfileResolver.Extends)?.ToString())
            .Where(e => e is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var name in game.Profiles.Keys.Where(n => !extended.Contains(n)))
            report.Warnings.Add($"{where}: profiles.{name} is not extended by any preset (profiles are never applied directly)");
        foreach (var (name, body) in game.Profiles)
            if (body.ContainsKey(ProfileResolver.SnapshotsFrom))
                report.Errors.Add($"{where}: profiles.{name} sets snapshotsFrom, which is only allowed in presets");
        bool hasRawFile = game.Files.Any(f => f.Handler.Equals("file", StringComparison.OrdinalIgnoreCase));

        foreach (var (device, presets) in game.Presets)
        {
            if (!devices.Devices.TryGetValue(device, out var deviceDef))
            {
                report.Errors.Add($"{where}: presets.{device}: '{device}' is not a device in devices.yaml");
                continue;
            }
            foreach (var preset in presets.Keys)
            {
                try
                {
                    var resolved = ProfileResolver.ResolvePreset(game, device, deviceDef, preset);
                    CheckBlocks(game, resolved, $"{where}: presets.{device}.{preset}", report);
                    CheckSnapshotsFrom(store, game, device, preset, presets[preset], hasRawFile, where, report);
                    if (resolved.FileBlocks.Count == 0 && resolved.Display is null && resolved.Nvidia.Count == 0 && resolved.Amd.Count == 0)
                        report.Warnings.Add($"{where}: presets.{device}.{preset} sets nothing");
                }
                catch (Exception ex) when (ex is ConfigException or FormatException)
                {
                    report.Errors.Add($"{where}: {ex.Message}");
                }
            }
            if (deviceDef.DefaultPreset is { } dp && !presets.ContainsKey(dp))
                report.Warnings.Add($"{where}: presets.{device} has no '{dp}' (the device default); the first preset is used instead");
        }
        foreach (var device in devices.Devices.Where(d => !ProfileResolver.PresetNames(game, d.Key, d.Value).Any()).Select(d => d.Key))
            report.Warnings.Add($"{where}: no recommended presets for device '{device}' (only manual saves will apply there)");

        foreach (var (device, deviceDef) in devices.Devices)
        {
            IReadOnlyList<ManualSave> saves;
            try { saves = store.ListSaves(game.Id, device); }
            catch (Exception ex) { report.Errors.Add($"saves/{game.Id}/{device}: {ex.Message}"); continue; }
            foreach (var save in saves)
            {
                try { ProfileResolver.ResolveSave(game, device, deviceDef, save); }
                catch (Exception ex) { report.Errors.Add($"saves/{game.Id}/{device}/{save.Slug}: {ex.Message}"); }
            }
        }
    }

    private static void CheckSnapshotsFrom(ConfigStore store, GameDef game, string device, string preset, Dictionary<string, object?> body,
        bool hasRawFile, string where, ValidationReport report)
    {
        if (!body.TryGetValue(ProfileResolver.SnapshotsFrom, out var raw)) return;
        var at = $"{where}: presets.{device}.{preset}";
        var slug = raw is null ? "" : Values.ToScalar(raw).Trim();
        if (raw is Dictionary<string, object?> || !ProfileResolver.IsSlug(slug))
        {
            report.Errors.Add($"{at}: snapshotsFrom must be a manual save folder name (letters, digits, - _ .)");
            return;
        }
        if (!hasRawFile)
            report.Warnings.Add($"{at}: snapshotsFrom has no effect because the game has no 'handler: file' entry");
        try
        {
            if (!File.Exists(Path.Combine(store.Paths.SaveDir(game.Id, device, slug), "save.yaml")))
                report.Warnings.Add($"{at}: snapshotsFrom '{slug}' is not a manual save on this machine (saves are per device; create it in the app)");
        }
        catch (Exception) { /* unusable path: the slug check above already covers bad names */ }
    }

    private static void CheckBlocks(GameDef game, ResolvedProfile profile, string where, ValidationReport report)
    {
        foreach (var (fileId, block) in profile.FileBlocks)
        {
            var def = game.Files.First(f => f.Id == fileId);
            var handler = SettingsHandler.For(def.Handler);
            if (handler is RawFileHandler && block.Count > 0)
                report.Errors.Add($"{where}.{fileId}: handler 'file' takes no keys (use capture --snapshot)");
            var flat = handler.Flatten(block);
            if (handler is JsonHandler)
            {
                foreach (var key in flat.Keys)
                {
                    try { JsonHandler.Parse(key); }
                    catch (ConfigException ex) { report.Errors.Add($"{where}.{fileId}: {ex.Message}"); }
                }
            }
        }
    }
}
