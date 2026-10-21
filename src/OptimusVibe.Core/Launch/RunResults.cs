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

/// <summary>What the player answered when the wrapper asked to keep settings changed in-game (stored on the run as <see cref="RunResult.Capture"/>).</summary>
public enum CaptureAnswer
{
    /// <summary>No question: nothing drifted, a verify run, or the prompt was turned off for the run (<c>--no-prompt</c>).</summary>
    NotAsked,

    /// <summary>Kept as a manual save (Save or Save as): the drift is the player's own tuning.</summary>
    Saved,

    /// <summary>Asked, and the player chose not to keep them.</summary>
    DontSave,

    /// <summary>Asked, and the player turned the question off for this game.</summary>
    DontAskAgain,

    /// <summary>Not asked because the player turned the question off for this game earlier.</summary>
    Off,
}

/// <summary>
/// What one normal run (no test, the player may have changed things) suggests about the preset, judged with the normal run before it. See
/// <see cref="RunResults.Passive"/>. Never as strong as a verify run: it only says what stays true whatever the player did.
/// </summary>
public enum PassiveVerdict
{
    /// <summary>Can't tell: the game was not seen, nothing was applied, the session was short or its length unknown, or the drift has no pattern.</summary>
    Unknown,

    /// <summary>The game ran for a while and kept every value the preset wrote.</summary>
    Held,

    /// <summary>The drift looks like the game's own doing: the same values twice in a row, or most of the preset in a short session not kept as a save.</summary>
    Overridden,

    /// <summary>Only files a launcher's cloud sync had rewritten drifted: it points at sync, not at the game.</summary>
    SyncSuspect,

    /// <summary>The player kept the changes as a manual save: their own work, never counted against the preset.</summary>
    UserSaved,
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

    /// <summary>
    /// File ids written after the game process was seen: rewritten because a launcher's cloud sync had changed them, created late, or (applyTiming
    /// afterProcessStart) written for the first time.
    /// </summary>
    public List<string> Reapplied { get; set; } = [];

    /// <summary>
    /// The part of <see cref="Reapplied"/> that the wrapper had already written before launch and wrote again because it had changed in between
    /// (a launcher's cloud sync). Only these point at sync (<see cref="PassiveVerdict.SyncSuspect"/>). Empty on results from older versions.
    /// </summary>
    public List<string> SyncReapplied { get; set; } = [];

    /// <summary>File ids that only existed after the game booted: applied, but they take effect next launch.</summary>
    public List<string> LateFiles { get; set; } = [];

    /// <summary>
    /// What the player answered at the "keep these changes?" prompt. Null on results from older versions, and while the prompt is still open
    /// (the run is stored before it is shown, so a wrapper killed meanwhile still leaves its result; <see cref="RunResults.SetCapture"/> fills it in).
    /// </summary>
    public CaptureAnswer? Capture { get; set; }

    /// <summary>How long the game ran, in seconds (from the game process being seen, else from the launch, to its exit). Null when unknown.</summary>
    public int? DurationSec { get; set; }

    /// <summary>The game build this run played (<see cref="Library.GameBuild"/>: "steam:&lt;buildid&gt;" or an exe fingerprint). Null when unknown.</summary>
    public string? Build { get; set; }

    [JsonIgnore]
    public bool IsVerify => Mode == VerifyMode;

    /// <summary>Settings (keys plus whole files) the preset set.</summary>
    [JsonIgnore]
    public int Applied => AppliedKeys + SnapshotFiles;
}

/// <summary>What <see cref="RunResults.Record"/> keeps per game and device: the last run, the last verify run and the last few normal runs.</summary>
public sealed class RunResultEntry
{
    public RunResult? Last { get; set; }
    public RunResult? LastVerify { get; set; }

    /// <summary>The last normal runs, oldest first, at most <see cref="RunResults.RecentCap"/>. Verify runs are not in it (they have <see cref="LastVerify"/>).</summary>
    public List<RunResult> Recent { get; set; } = [];

    /// <summary>
    /// Consecutive normal runs whose <see cref="PassiveVerdict"/> was <see cref="PassiveVerdict.Overridden"/>: a passively held run resets it, a run kept
    /// as a save or an unclear one leaves it alone. Separate from <see cref="OverriddenStreak"/>, which stays verify-only.
    /// </summary>
    public int PassiveOverriddenStreak { get; set; }

    /// <summary>
    /// Consecutive verify runs whose verdict was <see cref="RunVerdict.Overridden"/>: a held verify resets it, an unclear one (game never seen,
    /// nothing applied) leaves it alone. Two in a row is what suggests flagging the game "set in-game" (<see cref="Library.GameFlags.SuggestsFromVerify"/>).
    /// </summary>
    public int OverriddenStreak { get; set; }

    /// <summary>
    /// Evidence older than this does not count for the game's health or for suggesting the "set in-game" flag: set when the game ran flagged
    /// on this device (<see cref="RunResults.MarkFlagged"/>) and when the user chose to manage its settings again (<see cref="RunResults.ResetStreak"/>).
    /// Whatever led to the flag was answered by it; once lifted, the preset is judged on fresh launches and tests. Null = everything counts.
    /// </summary>
    public DateTime? EvidenceSinceUtc { get; set; }
}

/// <summary>
/// <c>run-results.json</c> in the per-machine data dir (game id → device → <see cref="RunResultEntry"/>), plus the verdict and the
/// sentences built from a result. Written atomically; never throws on a missing or corrupt file; not synced.
/// </summary>
public static class RunResults
{
    /// <summary>A normal run that changed more than this fraction of the preset's settings gets a hint on the game page.</summary>
    public const double HintRatio = 0.5;

    /// <summary>How many normal runs <see cref="RunResultEntry.Recent"/> keeps.</summary>
    public const int RecentCap = 8;

    /// <summary>A normal run counts as <see cref="PassiveVerdict.Held"/> only when the game ran at least this long (seconds).</summary>
    public const int PassiveHeldMinSec = 60;

    /// <summary>A session shorter than this (seconds) that changed most of the preset and was not saved looks like the game's own doing.</summary>
    public const int ShortSessionSec = 300;

    private static readonly object Gate = new();

    /// <summary>Held across processes (wrappers of different games, the app) around every read and read-modify-write of the file.</summary>
    private const string FileMutexName = @"Local\OptimusVibe.run-results";
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class Model
    {
        public Dictionary<string, Dictionary<string, RunResultEntry>> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static string FileFor(string dataDir) => Path.Combine(dataDir, "run-results.json");

    // ---------------------------------------------------------------- store

    /// <summary>
    /// Remember a result as the game's last run on that device: as its last verify run when it is one, otherwise in the
    /// <see cref="RunResultEntry.Recent"/> ring (and the passive streak).
    /// </summary>
    /// <remarks>
    /// A normal run whose <see cref="RunResult.Capture"/> is null is pending (the capture prompt is still open): it is stored, but its step of the
    /// passive streak waits for <see cref="SetCapture"/>.
    /// </remarks>
    public static void Record(string dataDir, RunResult result) => Modify(dataDir, m =>
    {
        if (!m.Games.TryGetValue(result.Game, out var devices)) m.Games[result.Game] = devices = new(StringComparer.OrdinalIgnoreCase);
        if (!devices.TryGetValue(result.Device, out var entry)) devices[result.Device] = entry = new RunResultEntry();
        entry.Last = result;
        if (result.IsVerify)
        {
            entry.LastVerify = result;
            entry.OverriddenStreak = NextStreak(entry.OverriddenStreak, Verdict(result));
        }
        else
        {
            entry.Recent ??= [];
            if (result.Capture is not null)
                entry.PassiveOverriddenStreak = NextPassiveStreak(entry.PassiveOverriddenStreak, Passive(result, entry.Recent.LastOrDefault()));
            entry.Recent.Add(result);
            if (entry.Recent.Count > RecentCap) entry.Recent.RemoveRange(0, entry.Recent.Count - RecentCap);
        }
        return true;
    });

    /// <summary>
    /// Fills in the capture answer of a normal run stored pending (<see cref="Record"/> with a null <see cref="RunResult.Capture"/>) and takes its
    /// step of the passive streak. Stores the run whole when it is not there (the first write failed). Never throws.
    /// </summary>
    public static void SetCapture(string dataDir, RunResult result, CaptureAnswer answer)
    {
        var found = false;
        Modify(dataDir, m =>
        {
            if (!m.Games.TryGetValue(result.Game, out var devices) || !devices.TryGetValue(result.Device, out var entry)) return false;
            entry.Recent ??= [];
            var i = entry.Recent.FindLastIndex(r => r.Utc == result.Utc && !r.IsVerify);
            if (i < 0) return false;
            found = true;
            var run = entry.Recent[i];
            if (run.Capture is not null) return false; // already final
            run.Capture = answer;
            if (entry.Last is { IsVerify: false } last && last.Utc == run.Utc) last.Capture = answer;
            entry.PassiveOverriddenStreak = NextPassiveStreak(entry.PassiveOverriddenStreak, Passive(run, i > 0 ? entry.Recent[i - 1] : null));
            return true;
        });
        result.Capture = answer;
        if (!found) Record(dataDir, result);
    }

    /// <summary>The streak after one more verify run: overridden adds one, held starts over, an unclear verdict changes nothing.</summary>
    public static int NextStreak(int streak, RunVerdict verdict) => verdict switch
    {
        RunVerdict.Overridden => streak + 1,
        RunVerdict.Held => 0,
        _ => streak,
    };

    /// <summary>The passive streak after one more normal run: overridden adds one, held starts over, anything else (saved, unclear, sync) changes nothing.</summary>
    public static int NextPassiveStreak(int streak, PassiveVerdict verdict) => verdict switch
    {
        PassiveVerdict.Overridden => streak + 1,
        PassiveVerdict.Held => 0,
        _ => streak,
    };

    /// <summary>Consecutive overridden verify runs of a game on a device (0 when none recorded).</summary>
    public static int OverriddenStreak(string dataDir, string gameId, string device)
    {
        var m = Read(dataDir);
        return m.Games.TryGetValue(gameId, out var devices) && devices.TryGetValue(device, out var e) ? e.OverriddenStreak : 0;
    }

    /// <summary>
    /// Starts both streaks over, verify and passive, and stamps <see cref="RunResultEntry.EvidenceSinceUtc"/> (the user chose to manage the game's
    /// settings again: it must be tested again before the flag is suggested, so the runs, tests and mapping run from before don't count).
    /// </summary>
    public static void ResetStreak(string dataDir, string gameId, string device, DateTime? nowUtc = null) => Modify(dataDir, m =>
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(device)) return false;
        var e = EntryFor(m, gameId, device);
        e.OverriddenStreak = 0;
        e.PassiveOverriddenStreak = 0;
        e.EvidenceSinceUtc = nowUtc ?? DateTime.UtcNow;
        return true;
    });

    /// <summary>
    /// The game is flagged "set in-game" and this device saw it: it was flagged here, ran flagged here, or the library scan found the flag
    /// (also one synced from another device). Starts both streaks over and stamps <see cref="RunResultEntry.EvidenceSinceUtc"/> so that, should
    /// the flag be lifted (here or on another device), what led to it does not suggest it again. Writes only when the stamp is missing or older
    /// than the newest stored run (a flagged run records none, so once per flag). Never throws.
    /// </summary>
    public static void MarkFlagged(string dataDir, string gameId, string device, DateTime? nowUtc = null) =>
        MarkFlagged(dataDir, [gameId], device, nowUtc);

    /// <summary><see cref="MarkFlagged(string, string, string, DateTime?)"/> for several games in one write (the library scan). Never throws.</summary>
    public static void MarkFlagged(string dataDir, IReadOnlyCollection<string> gameIds, string device, DateTime? nowUtc = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(device) || gameIds.All(string.IsNullOrWhiteSpace)) return;
            Modify(dataDir, m =>
            {
                var changed = false;
                foreach (var gameId in gameIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    var e = EntryFor(m, gameId, device);
                    if (e.EvidenceSinceUtc is { } mark && !(NewestRunUtc(e) is { } newest && newest > mark)) continue;
                    e.OverriddenStreak = 0;
                    e.PassiveOverriddenStreak = 0;
                    e.EvidenceSinceUtc = nowUtc ?? DateTime.UtcNow;
                    changed = true;
                }
                return changed;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not mark {string.Join(", ", gameIds)} as flagged in the run history: {ex.Message}");
        }
    }

    private static RunResultEntry EntryFor(Model m, string gameId, string device)
    {
        if (!m.Games.TryGetValue(gameId, out var devices) || devices is null) m.Games[gameId] = devices = new(StringComparer.OrdinalIgnoreCase);
        if (!devices.TryGetValue(device, out var e) || e is null) devices[device] = e = new RunResultEntry();
        e.Recent ??= [];
        return e;
    }

    private static DateTime? NewestRunUtc(RunResultEntry e)
    {
        DateTime? newest = e.Last?.Utc;
        if (e.LastVerify is { } v && (newest is null || v.Utc > newest)) newest = v.Utc;
        foreach (var r in e.Recent)
            if (newest is null || r.Utc > newest) newest = r.Utc;
        return newest;
    }

    /// <summary>The last run (or, with <paramref name="verifyOnly"/>, the last verify run) of a game on a device; null when none.</summary>
    public static RunResult? Get(string dataDir, string gameId, string device, bool verifyOnly = false)
    {
        var m = Read(dataDir);
        if (!m.Games.TryGetValue(gameId, out var devices) || !devices.TryGetValue(device, out var e)) return null;
        return verifyOnly ? e.LastVerify : e.Last;
    }

    /// <summary>Everything stored for a game on a device (a fresh copy: changing it changes nothing on disk); null when none.</summary>
    public static RunResultEntry? Entry(string dataDir, string gameId, string device)
    {
        var m = Read(dataDir);
        if (!m.Games.TryGetValue(gameId, out var devices) || !devices.TryGetValue(device, out var e)) return null;
        e.Recent ??= [];
        return e;
    }

    /// <summary>Game id → everything stored for it on a device, in one read of the file (fresh copies; empty when none).</summary>
    public static Dictionary<string, RunResultEntry> Entries(string dataDir, string device)
    {
        var m = Read(dataDir);
        var all = new Dictionary<string, RunResultEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gameId, devices) in m.Games)
        {
            if (devices is null || !devices.TryGetValue(device, out var e) || e is null) continue;
            e.Recent ??= [];
            all[gameId] = e;
        }
        return all;
    }

    /// <summary>The last normal runs of a game on a device, oldest first (empty when none).</summary>
    public static IReadOnlyList<RunResult> Recent(string dataDir, string gameId, string device) =>
        Entry(dataDir, gameId, device)?.Recent ?? [];

    /// <summary>Consecutive passively overridden normal runs of a game on a device (0 when none recorded).</summary>
    public static int PassiveOverriddenStreak(string dataDir, string gameId, string device) =>
        Entry(dataDir, gameId, device)?.PassiveOverriddenStreak ?? 0;

    /// <summary>A read of the whole file, under both locks.</summary>
    private static Model Read(string dataDir)
    {
        lock (Gate)
        {
            using var _ = FileLock.Acquire();
            return Load(dataDir);
        }
    }

    /// <summary>
    /// Read-modify-write of the file under the in-process lock and the cross-process mutex; <paramref name="change"/> returns false when there
    /// is nothing to write. Written to a temp file of this writer's own, then moved over the file (retried briefly while a reader holds it).
    /// Never throws for file errors.
    /// </summary>
    private static void Modify(string dataDir, Func<Model, bool> change)
    {
        lock (Gate)
        {
            using var _ = FileLock.Acquire();
            string? tmp = null;
            try
            {
                var m = Load(dataDir);
                if (!change(m)) return;
                Directory.CreateDirectory(dataDir);
                tmp = $"{FileFor(dataDir)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(m, Opts), new UTF8Encoding(false));
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(tmp!, FileFor(dataDir), overwrite: true);
                        tmp = null;
                        break;
                    }
                    catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                    {
                        Thread.Sleep(40 * attempt);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not save {FileFor(dataDir)}: {ex.Message}");
            }
            finally
            {
                if (tmp is not null)
                    try { File.Delete(tmp); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Could not remove {tmp}: {ex.Message}"); }
            }
        }
    }

    /// <summary>The cross-process mutex around the file. When it can't be had in time the caller goes on without it (logged).</summary>
    private sealed class FileLock : IDisposable
    {
        private Mutex? _mutex;

        public static FileLock Acquire()
        {
            var l = new FileLock();
            try
            {
                var mutex = new Mutex(initiallyOwned: false, FileMutexName);
                bool owned;
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { owned = true; } // the holder died without releasing: it is ours now
                if (owned) l._mutex = mutex;
                else
                {
                    mutex.Dispose();
                    Log.Warn("run-results.json: another process held it too long, going on without the lock");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                Log.Warn($"run-results.json: no cross-process lock ({ex.Message})");
            }
            return l;
        }

        public void Dispose()
        {
            if (_mutex is null) return;
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned by this thread */ }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static Model Load(string dataDir)
    {
        try
        {
            var file = FileFor(dataDir);
            if (File.Exists(file))
            {
                string text;
                // opened fully shared: a reader outside the mutex (an older build, a file watcher) never makes this read fail
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();
                var m = JsonSerializer.Deserialize<Model>(text, Opts);
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

    /// <summary>
    /// What a normal run says without a test, judged with the normal run before it (<paramref name="previous"/>, may be null). The rules stay
    /// true whatever the player did in-game:
    /// <list type="bullet">
    /// <item>kept as a save: <see cref="PassiveVerdict.UserSaved"/>, never against the preset;</item>
    /// <item>no drift in a session of at least <see cref="PassiveHeldMinSec"/>: <see cref="PassiveVerdict.Held"/>;</item>
    /// <item>only files written again because a launcher's cloud sync changed them after the pre-launch apply
    /// (<see cref="RunResult.SyncReapplied"/>) drifted: <see cref="PassiveVerdict.SyncSuspect"/>;</item>
    /// <item>a setting drifted to the same value as in the previous normal run, and neither drift was kept as a save (answered Don't save, or
    /// never asked: the question turned off with Don't ask again / Off, or <c>--no-prompt</c>): <see cref="PassiveVerdict.Overridden"/>. The
    /// preset is written again before every launch, so a player's own edit only comes back when they make it again every session, while a
    /// game that rewrites its own value does it every time. Whole files never count (what they became is not known);</item>
    /// <item>the player was asked about the drift and answered Don't save (<see cref="CaptureAnswer.DontSave"/>), and at least
    /// <see cref="HintRatio"/> of the preset drifted in a session shorter than <see cref="ShortSessionSec"/>: <see cref="PassiveVerdict.Overridden"/>.
    /// This one needs the answer: a player who turned the question off may well try many settings in one short session;</item>
    /// <item>anything else, including an answer still pending, one-off drift the player was never asked about, a verify run, an unseen game
    /// or nothing applied: <see cref="PassiveVerdict.Unknown"/>. The player may have made unasked changes on purpose.</item>
    /// </list>
    /// </summary>
    public static PassiveVerdict Passive(RunResult r, RunResult? previous)
    {
        if (r.IsVerify || !r.ProcessSeen || r.Applied == 0) return PassiveVerdict.Unknown;
        if (r.Capture == CaptureAnswer.Saved) return PassiveVerdict.UserSaved;
        if (r.Drift.Count == 0) return r.DurationSec >= PassiveHeldMinSec ? PassiveVerdict.Held : PassiveVerdict.Unknown;
        var sync = r.SyncReapplied ?? [];
        if (sync.Count > 0 && r.Drift.All(d => sync.Contains(d.FileId, StringComparer.OrdinalIgnoreCase))) return PassiveVerdict.SyncSuspect;
        if (r.Capture is null) return PassiveVerdict.Unknown; // the answer is still pending
        if (Recurs(r, previous)) return PassiveVerdict.Overridden;
        // most of the preset in one short session counts only when the player was asked and declined: unasked, it may be their own tuning
        if (r.Capture == CaptureAnswer.DontSave && DriftRatio(r) >= HintRatio && r.DurationSec is { } sec && sec < ShortSessionSec)
            return PassiveVerdict.Overridden;
        return PassiveVerdict.Unknown;
    }

    /// <summary>The passive verdict of every run in <paramref name="recent"/> (oldest first), each judged with the one before it.</summary>
    public static IReadOnlyList<PassiveVerdict> PassiveVerdicts(IReadOnlyList<RunResult> recent) =>
        recent.Select((r, i) => Passive(r, i > 0 ? recent[i - 1] : null)).ToList();

    /// <summary>How many of the newest runs in <paramref name="recent"/> were <see cref="PassiveVerdict.Held"/> in a row (0 when the newest was not).</summary>
    public static int PassiveHeldStreak(IReadOnlyList<RunResult> recent)
    {
        var verdicts = PassiveVerdicts(recent);
        var n = 0;
        for (var i = verdicts.Count - 1; i >= 0 && verdicts[i] == PassiveVerdict.Held; i--) n++;
        return n;
    }

    /// <summary>
    /// A setting drifted to the same value in the previous normal run too, where it was not kept as a save either (<see cref="NotKept"/>: declined,
    /// or never asked). Whole-file drift never counts: only "differs from the save" is known, not what the file became.
    /// </summary>
    private static bool Recurs(RunResult r, RunResult? previous)
    {
        if (!NotKept(r.Capture) || previous is null || previous.IsVerify || !previous.ProcessSeen || !NotKept(previous.Capture) || previous.Drift.Count == 0)
            return false;
        var before = previous.Drift.Where(d => !d.IsWholeFile).Select(DriftSignature).ToHashSet(StringComparer.Ordinal);
        return r.Drift.Any(d => !d.IsWholeFile && before.Contains(DriftSignature(d)));
    }

    /// <summary>A final capture answer that did not keep the drift: declined, or never asked (the question off, or <c>--no-prompt</c>).</summary>
    private static bool NotKept(CaptureAnswer? answer) =>
        answer is CaptureAnswer.DontSave or CaptureAnswer.DontAskAgain or CaptureAnswer.Off or CaptureAnswer.NotAsked;

    private static string DriftSignature(RunDrift d) => $"{d.FileId.ToLowerInvariant()}\n{d.Key}\n{d.Now}";

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
