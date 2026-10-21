using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OptimusVibe.Engine;
using OptimusVibe.Logging;

namespace OptimusVibe.Launch;

/// <summary>What a finished run says about the preset: did the game keep the values that were written?</summary>
public enum RunVerdict
{
    /// <summary>Nothing the preset wrote was changed.</summary>
    Held,

    /// <summary>A verify run (the player changed nothing) found preset values or whole files the game changed itself.</summary>
    Overridden,

    /// <summary>Can't tell: the game process never appeared, or a normal run changed (the player may have done it), or nothing was applied.</summary>
    Unknown,
}

/// <summary>One setting that was not as the preset left it after the game exited.</summary>
public sealed class RunDrift
{
    public string FileId { get; set; } = "";

    /// <summary>The key, or "(whole file)" for a snapshot file.</summary>
    public string Key { get; set; } = "";

    public string? Applied { get; set; }
    public string? Now { get; set; }

    [JsonIgnore]
    public bool IsWholeFile => Applied is null && Key == WholeFile;

    public const string WholeFile = "(whole file)";

    public static RunDrift From(Drift d) => new() { FileId = d.FileId, Key = d.Key, Applied = d.Applied, Now = d.Now };
}

/// <summary>The outcome of one run for one game on one device. Stored in <c>run-results.json</c>.</summary>
public sealed class RunResult
{
    public const string NormalMode = "normal";
    public const string VerifyMode = "verify";

    public DateTime Utc { get; set; }
    public string Game { get; set; } = "";
    public string Device { get; set; } = "";

    /// <summary>The preset as a reference ("recommended:performance", "manual:steady").</summary>
    public string Preset { get; set; } = "";

    public string PresetLabel { get; set; } = "";

    /// <summary><see cref="NormalMode"/> or <see cref="VerifyMode"/>.</summary>
    public string Mode { get; set; } = NormalMode;

    /// <summary>Individual keys the preset wrote.</summary>
    public int AppliedKeys { get; set; }

    /// <summary>Whole files the preset restored from a snapshot (manual save).</summary>
    public int SnapshotFiles { get; set; }

    public List<RunDrift> Drift { get; set; } = [];

    /// <summary>True when the game process was seen running (the exit means something).</summary>
    public bool ProcessSeen { get; set; } = true;

    /// <summary>Verify run: false (the player was told to change nothing). Normal run: null, not known.</summary>
    public bool? UserChanged { get; set; }

    /// <summary>The command line the wrapper started (exe, Steam URI with its arguments...). Null on results from older versions.</summary>
    public string? LaunchCommand { get; set; }

    /// <summary>How the launch went when it did not go to plan ("The game process never appeared...", "Battle.net selected the game but did not launch it..."); null when fine.</summary>
    public string? LaunchNote { get; set; }

    /// <summary>File ids written again after the game process was seen (a launcher's cloud sync had changed them, or they did not exist before launch).</summary>
    public List<string> Reapplied { get; set; } = [];

    /// <summary>File ids that only existed after the game booted: applied, but they take effect next launch.</summary>
    public List<string> LateFiles { get; set; } = [];

    [JsonIgnore]
    public bool IsVerify => Mode == VerifyMode;

    /// <summary>Settings (keys plus whole files) the preset set.</summary>
    [JsonIgnore]
    public int Applied => AppliedKeys + SnapshotFiles;
}

/// <summary>What <see cref="RunResults.Record"/> keeps per game and device: the last run, and the last verify run.</summary>
public sealed class RunResultEntry
{
    public RunResult? Last { get; set; }
    public RunResult? LastVerify { get; set; }

    /// <summary>
    /// Consecutive verify runs whose verdict was <see cref="RunVerdict.Overridden"/>: a held verify resets it, an unclear one (game never seen,
    /// nothing applied) leaves it alone. Two in a row is what suggests flagging the game "set in-game" (<see cref="Library.GameFlags.SuggestsFromVerify"/>).
    /// </summary>
    public int OverriddenStreak { get; set; }
}

/// <summary>
/// <c>run-results.json</c> in the per-machine data dir (game id → device → <see cref="RunResultEntry"/>), plus the verdict and the
/// sentences built from a result. Written atomically; never throws on a missing or corrupt file; not synced.
/// </summary>
public static class RunResults
{
    /// <summary>A normal run that changed more than this fraction of the preset's settings gets a hint on the game page.</summary>
    public const double HintRatio = 0.5;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Model
    {
        public Dictionary<string, Dictionary<string, RunResultEntry>> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static string FileFor(string dataDir) => Path.Combine(dataDir, "run-results.json");

    // ---------------------------------------------------------------- store

    /// <summary>Remember a result as the game's last run on that device (and as its last verify run when it is one).</summary>
    public static void Record(string dataDir, RunResult result)
    {
        lock (Gate)
        {
            try
            {
                var m = Load(dataDir);
                if (!m.Games.TryGetValue(result.Game, out var devices)) m.Games[result.Game] = devices = new(StringComparer.OrdinalIgnoreCase);
                if (!devices.TryGetValue(result.Device, out var entry)) devices[result.Device] = entry = new RunResultEntry();
                entry.Last = result;
                if (result.IsVerify)
                {
                    entry.LastVerify = result;
                    entry.OverriddenStreak = NextStreak(entry.OverriddenStreak, Verdict(result));
                }
                Directory.CreateDirectory(dataDir);
                var tmp = FileFor(dataDir) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(m, Opts), new UTF8Encoding(false));
                File.Move(tmp, FileFor(dataDir), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not save {FileFor(dataDir)}: {ex.Message}");
            }
        }
    }

    /// <summary>The streak after one more verify run: overridden adds one, held starts over, an unclear verdict changes nothing.</summary>
    public static int NextStreak(int streak, RunVerdict verdict) => verdict switch
    {
        RunVerdict.Overridden => streak + 1,
        RunVerdict.Held => 0,
        _ => streak,
    };

    /// <summary>Consecutive overridden verify runs of a game on a device (0 when none recorded).</summary>
    public static int OverriddenStreak(string dataDir, string gameId, string device)
    {
        lock (Gate)
        {
            var m = Load(dataDir);
            return m.Games.TryGetValue(gameId, out var devices) && devices.TryGetValue(device, out var e) ? e.OverriddenStreak : 0;
        }
    }

    /// <summary>Starts the streak over (the user chose to manage the game's settings again: it must be tested again before the flag is suggested).</summary>
    public static void ResetStreak(string dataDir, string gameId, string device)
    {
        lock (Gate)
        {
            try
            {
                var m = Load(dataDir);
                if (!m.Games.TryGetValue(gameId, out var devices) || !devices.TryGetValue(device, out var e) || e.OverriddenStreak == 0) return;
                e.OverriddenStreak = 0;
                var tmp = FileFor(dataDir) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(m, Opts), new UTF8Encoding(false));
                File.Move(tmp, FileFor(dataDir), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not save {FileFor(dataDir)}: {ex.Message}");
            }
        }
    }

    /// <summary>The last run (or, with <paramref name="verifyOnly"/>, the last verify run) of a game on a device; null when none.</summary>
    public static RunResult? Get(string dataDir, string gameId, string device, bool verifyOnly = false)
    {
        lock (Gate)
        {
            var m = Load(dataDir);
            if (!m.Games.TryGetValue(gameId, out var devices) || !devices.TryGetValue(device, out var e)) return null;
            return verifyOnly ? e.LastVerify : e.Last;
        }
    }

    private static Model Load(string dataDir)
    {
        try
        {
            var file = FileFor(dataDir);
            if (File.Exists(file))
            {
                var m = JsonSerializer.Deserialize<Model>(File.ReadAllText(file), Opts);
                if (m is not null)
                    return new Model { Games = new Dictionary<string, Dictionary<string, RunResultEntry>>(m.Games, StringComparer.OrdinalIgnoreCase) };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ignoring unreadable {FileFor(dataDir)}: {ex.Message}");
        }
        return new Model();
    }

    // ---------------------------------------------------------------- verdict

    /// <summary>
    /// Held: no drift. Overridden: drift in the preset's keys or snapshot files during a verify run. Unknown: the game process never
    /// appeared, nothing was applied, or a normal run changed something (the player may have done it, so it proves nothing).
    /// </summary>
    public static RunVerdict Verdict(RunResult r)
    {
        if (!r.ProcessSeen || r.Applied == 0) return RunVerdict.Unknown;
        if (r.Drift.Count == 0) return RunVerdict.Held;
        return r.IsVerify ? RunVerdict.Overridden : RunVerdict.Unknown;
    }

    /// <summary>Fraction of the preset's settings (keys plus whole files) that were not as written afterwards; 0 when nothing was applied.</summary>
    public static double DriftRatio(RunResult r) => r.Applied == 0 ? 0 : Math.Min(1.0, (double)r.Drift.Count / r.Applied);

    /// <summary>Normal runs only: the game page shows a quiet "Test preset?" hint when more than <see cref="HintRatio"/> drifted.</summary>
    public static bool NeedsHint(RunResult? r) => r is { IsVerify: false, ProcessSeen: true } && DriftRatio(r) > HintRatio;

    // ---------------------------------------------------------------- words

    /// <summary>"The game kept all 14 settings." / "The game changed 10 of 14 settings: A, B, C and 7 more." / why we can't tell.</summary>
    public static string Describe(RunResult r, int maxNames = 5)
    {
        switch (Verdict(r))
        {
            case RunVerdict.Held:
                return r.Applied == 1 ? "The game kept the setting." : $"The game kept all {r.Applied} settings.";
            case RunVerdict.Overridden:
                return $"The game changed {r.Drift.Count} of {r.Applied} setting{(r.Applied == 1 ? "" : "s")}: {Names(r, maxNames)}";
            default:
                if (!r.ProcessSeen) return r.LaunchNote is { Length: > 0 } note ? $"{note.TrimEnd('.')}, so there is nothing to compare." : "The game never started, so there is nothing to compare.";
                if (r.Applied == 0) return "The preset wrote no settings, so there is nothing to compare.";
                return $"{r.Drift.Count} of {r.Applied} settings changed while you played, so it isn't clear whether the game or you changed them.";
        }
    }

    /// <summary>The names of the changed settings, at most <paramref name="max"/>, then "and N more".</summary>
    public static string Names(RunResult r, int max = 5)
    {
        var names = r.Drift.Select(DriftName).Distinct().ToList();
        var shown = string.Join(", ", names.Take(max));
        return names.Count > max ? $"{shown} and {names.Count - max} more." : shown + ".";
    }

    public static string DriftName(RunDrift d) => d.IsWholeFile ? $"{d.FileId} (whole file)" : d.Key;

    /// <summary>The line on the game page after a normal run that changed most of the preset.</summary>
    public static string HintLine(RunResult r) =>
        $"Last launch: the game changed {r.Drift.Count} of {r.Applied} preset settings. Test preset?";

    /// <summary>
    /// The body of the lab's "### Verify run" section: what the preset wrote, what the game left, key by key. Plain text; the caller
    /// sanitizes it like any evidence.
    /// </summary>
    public static string EvidenceText(RunResult r, int maxLines = 200)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"verdict: {Verdict(r).ToString().ToLowerInvariant()}");
        sb.AppendLine($"preset: {(r.PresetLabel.Length > 0 ? r.PresetLabel : r.Preset)} ({r.Preset})");
        sb.AppendLine($"device: {r.Device}");
        sb.AppendLine($"run: {r.Mode}, {r.Utc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"player changed nothing: {(r.UserChanged == false ? "yes" : "unknown")}");
        sb.AppendLine($"game process seen: {(r.ProcessSeen ? "yes" : "no")}");
        sb.AppendLine($"preset wrote: {r.AppliedKeys} setting(s), {r.SnapshotFiles} whole file(s)");
        sb.AppendLine($"changed afterwards: {r.Drift.Count}");
        sb.AppendLine(Describe(r, int.MaxValue));
        if (r.Drift.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("changed settings (file key: preset wrote -> after the game):");
            foreach (var d in r.Drift.Take(maxLines))
                sb.AppendLine(d.IsWholeFile
                    ? $"{d.FileId} (whole file): restored from the manual save -> different"
                    : $"{d.FileId} {d.Key}: {d.Applied ?? "(not set)"} -> {d.Now ?? "(missing)"}");
            if (r.Drift.Count > maxLines) sb.AppendLine($"... and {r.Drift.Count - maxLines} more");
        }
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- build

    /// <summary>Builds the result of a run from what the engine applied and the drift found afterwards.</summary>
    public static RunResult Create(GameContext ctx, AppliedState applied, IReadOnlyList<Drift> drift, bool verify, bool processSeen,
        DateTime? utc = null) => new()
    {
        Utc = utc ?? DateTime.UtcNow,
        Game = ctx.Game.Id,
        Device = ctx.Device.Name,
        Preset = ctx.Preset?.ToString() ?? "",
        PresetLabel = ctx.Profile.Label ?? ctx.Preset?.Name ?? "",
        Mode = verify ? RunResult.VerifyMode : RunResult.NormalMode,
        AppliedKeys = applied.Targets.Values.Sum(t => t.Count),
        SnapshotFiles = applied.SnapshotHashes.Count,
        Drift = drift.Select(RunDrift.From).ToList(),
        ProcessSeen = processSeen,
        UserChanged = verify ? false : null,
    };
}
