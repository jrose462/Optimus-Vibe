using OptimusVibe.Config;

namespace OptimusVibe.Driver;

/// <summary>
/// Text for `optimus-vibe driver status` and `driver test-amd`. Plain "key: value" lines so the output can be pasted back into a chat or issue.
/// Nothing here changes a setting except <see cref="TestAmd"/> with <c>applyAndRestore</c>, which writes one harmless value and puts it back.
/// </summary>
public static class DriverDiagnostics
{
    public static IReadOnlyList<string> Status(string vendor) => vendor.Trim().ToLowerInvariant() switch
    {
        "amd" => AmdStatus(new AmdAdlxManager()),
        "nvidia" => NvidiaStatus(),
        _ => ["vendor: none", "driver settings: no supported GPU driver API for this device (set `driver:` in devices.yaml)"],
    };

    public static IReadOnlyList<string> TestAmd(bool applyAndRestore, out bool passed) =>
        TestAmd(new AmdAdlxManager(), applyAndRestore, out passed);

    internal static IReadOnlyList<string> AmdStatus(AmdAdlxManager manager)
    {
        var lines = new List<string> { "vendor: amd" };
        var s = manager.Status();
        lines.Add(s.Available ? "adlx: available" : $"adlx: unavailable - {s.Reason}");
        if (!s.Available)
        {
            lines.Add("per-application profiles: not supported by ADLX (settings are global; Optimus-Vibe applies at launch and restores at exit)");
            return lines;
        }
        if (s.Reason.Length > 0) lines.Add($"note: {s.Reason}");
        if (s.Info is { } i)
        {
            lines.Add($"adlx dll: {i.DllPath}");
            lines.Add($"adlx version (driver): {i.DriverAdlxVersion}");
            foreach (var g in i.Gpus) lines.Add($"gpu: {g}");
            lines.Add($"gpu in use: {i.GpuInUse}");
        }
        lines.Add("per-application profiles: not supported by ADLX (settings are global; Optimus-Vibe applies at launch and restores at exit)");
        lines.Add("settings (current value; range when the driver reports one):");
        var width = AmdSettingTable.All.Max(x => x.Key.Length);
        foreach (var r in s.Rows)
            lines.Add($"  {r.Key.PadRight(width)} = {r.Value}{(r.Range.Length > 0 ? $"  [{r.Range}]" : "")}");
        var supported = s.Rows.Count(r => !r.Value.StartsWith("unsupported", StringComparison.Ordinal) && !r.Value.StartsWith("error", StringComparison.Ordinal));
        lines.Add($"summary: {supported}/{s.Rows.Count} settings readable");
        return lines;
    }

    private static IReadOnlyList<string> NvidiaStatus()
    {
        var lines = new List<string> { "vendor: nvidia" };
        var m = new NvidiaDrsManager();
        lines.Add(m.IsAvailable(out var reason) ? "nvapi (DRS): available" : $"nvapi (DRS): unavailable - {reason}");
        lines.Add("per-application profiles: supported (NVIDIA DRS profile per game exe; persistent, no restore needed)");
        lines.Add($"known setting names: {NvidiaSettingTable.Ids.Count} (plus FrameRateLimiter, VSync, PowerMode aliases and raw 0x ids)");
        lines.Add("read a game's current values with: optimus-vibe inspect <game>  (or `apply <game> --dry-run`)");
        return lines;
    }

    /// <summary>
    /// Self-test of the read/apply/restore path with one harmless numeric setting (an image-sharpness value changed by 1).
    /// Dry run (default): reads and reports what it would do. With <paramref name="applyAndRestore"/>: applies, reads back, restores, reads back.
    /// </summary>
    internal static IReadOnlyList<string> TestAmd(AmdAdlxManager manager, bool applyAndRestore, out bool passed)
    {
        var lines = new List<string> { $"amd self-test ({(applyAndRestore ? "apply and restore" : "dry run: nothing is written")})" };
        passed = false;
        var status = manager.Status();
        if (!status.Available)
        {
            lines.Add($"FAIL: ADLX unavailable - {status.Reason}");
            return lines;
        }

        string? key = null;
        string? original = null;
        string? target = null;
        foreach (var candidate in new[] { "RisSharpness", "RsrSharpness" })
        {
            var row = status.Rows.FirstOrDefault(r => r.Key == candidate);
            if (row is null || !int.TryParse(row.Value, out var cur)) continue;
            var range = row.Range.Split("..");
            var max = range.Length == 2 && int.TryParse(range[1], out var m) ? m : 100;
            var min = range.Length == 2 && int.TryParse(range[0], out var n) ? n : 0;
            var next = cur + 1 <= max ? cur + 1 : cur - 1;
            if (next < min) continue;
            key = candidate;
            original = cur.ToString();
            target = next.ToString();
            break;
        }
        if (key is null)
        {
            lines.Add("SKIP: no harmless numeric setting (RisSharpness / RsrSharpness) is readable on this driver; nothing to test.");
            passed = true;
            return lines;
        }

        lines.Add($"test setting: {key}, current {original}, would set {target}, then restore {original}");
        if (!applyAndRestore)
        {
            lines.Add("PASS (dry run): read path works. Re-run with --apply-and-restore to test writes.");
            passed = true;
            return lines;
        }

        var game = new GameDef { Name = "driver self-test" };
        var request = new Dictionary<string, string> { [key] = target! };
        var token = manager.Apply(game, request);
        var afterApply = manager.Plan(game, request).FirstOrDefault()?.Current;
        lines.Add($"apply: token {(token is null ? "none" : "issued")}, driver now reports {afterApply ?? "?"} (expected {target})");
        if (token is not null) manager.Restore(token);
        var afterRestore = manager.Plan(game, new Dictionary<string, string> { [key] = original! }).FirstOrDefault()?.Current;
        lines.Add($"restore: driver now reports {afterRestore ?? "?"} (expected {original})");

        passed = token is not null && afterApply == target && afterRestore == original;
        lines.Add(passed ? "PASS: apply and restore round-trip works." : "FAIL: see the values above and the log (%LOCALAPPDATA%\\OptimusVibe\\logs).");
        return lines;
    }
}
