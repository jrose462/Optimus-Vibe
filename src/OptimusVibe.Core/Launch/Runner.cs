using System.Diagnostics;
using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Display;
using OptimusVibe.Driver;
using OptimusVibe.Engine;
using OptimusVibe.Library;
using OptimusVibe.Logging;

namespace OptimusVibe.Launch;

/// <summary>Extra launch inputs that do not come from the game yaml (e.g. from an auto-detected install).</summary>
public sealed class RunOptions
{
    /// <summary>
    /// What to start when there is no `-- command`: a URI / shell target (steam://rungameid/..., com.epicgames.launcher://...,
    /// shell:AppsFolder\PFN!App) or a launcher/game exe. Takes precedence over the game's `launch:` block.
    /// </summary>
    public string? Launch { get; init; }

    /// <summary>Arguments for an exe <see cref="Launch"/> (ignored for URI / shell targets).</summary>
    public IReadOnlyList<string> LaunchArgs { get; init; } = [];

    /// <summary>
    /// The game's install folder. With no `processes:` and a URI launch, the wrapper waits for new processes running
    /// from here. Defaults to the Steam install dir for games with a steamAppId.
    /// </summary>
    public string? InstallDir { get; init; }

    /// <summary>
    /// A test of the preset (`run --verify`): the player changes nothing and the result says whether the game kept the preset's values.
    /// Suppresses the post-game capture prompt; the result is stored as a verify run.
    /// </summary>
    public bool Verify { get; init; }

    /// <summary>
    /// A mapping run (`run --probe`): nothing is applied, no display or driver change, no capture prompt; the game is only launched and
    /// waited for while the caller fingerprints files and registry keys around it. Leaves a probe-active.json marker for the duration.
    /// </summary>
    public bool Probe { get; init; }

    /// <summary>Called as the launch moves between phases (starting, waiting on the launcher, game running, gave up). May be called from the wait loop's thread.</summary>
    public Action<LaunchPhase, string>? OnStatus { get; init; }

    /// <summary>Stops waiting for the game (and stops managing the run) when cancelled.</summary>
    public CancellationToken Cancel { get; init; }

    /// <summary>Test seam: stands in for <see cref="DisplayManager.Apply(DisplayModeDef, string?)"/> so a run can be tested without changing the screen.</summary>
    internal Func<DisplayModeDef, string?, DisplayModeState?>? DisplayApply { get; init; }

    /// <summary>Test seam: stands in for the store scan that finds a game's install when the run knows no install folder (<see cref="Runner.RunBuild"/>).</summary>
    internal Func<GameDef, DetectedGame?>? FindInstall { get; init; }

    /// <summary>Test seam: stands in for the <see cref="GameLab.ProbeSession"/> snapshot of the passive pass A watch (<see cref="Runner.BeginPassiveWatch"/>).</summary>
    internal Func<GameLab.LabTarget, Func<GameLab.ProbeSnapshot>>? PassiveSnapshot { get; init; }
}

/// <summary>
/// Launch wrapper (Apollo-style do/undo): apply settings → launch → wait for the game → undo temporary changes
/// → offer to capture anything changed in-game. Settings failures never stop the game from launching.
/// A run holds the named mutex <see cref="MutexName"/>; a second run of the same game while it is held (Steam
/// starting our launch option after the app or an outer wrapper asked Steam to launch the game) is a pass-through.
/// </summary>
public static class Runner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Named mutex held while a run of this game manages the launch.</summary>
    public static string MutexName(string gameId)
    {
        var safe = new string(gameId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? char.ToLowerInvariant(c) : '_').ToArray());
        return $@"Local\OptimusVibe.run.{safe}";
    }

    /// <summary>
    /// Take the run mutex for a game; null when another run (or the app) already holds it. Dispose on the same thread
    /// to release. Hold it while launching a game yourself so Steam's nested wrapper run is a pass-through.
    /// </summary>
    public static IDisposable? TryAcquireRunGuard(string gameId) => RunGuard.TryAcquire(MutexName(gameId));

    /// <summary>True when a URI or shell target (not an exe path): "://" inside or a "shell:" prefix.</summary>
    public static bool IsShellTarget(string target) =>
        target.Contains("://", StringComparison.Ordinal) || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    public static int Run(GameContext ctx, string[] command, bool prompt = true, RunOptions? options = null)
    {
        using var guard = RunGuard.TryAcquire(MutexName(ctx.Game.Id));
        if (guard is null)
        {
            Log.Info($"nested run: pass-through ({ctx.Game.Id} is already being managed by another run)");
            return LaunchPlain(command.Length > 0 ? command : LaunchCommand(options));
        }

        // A mapping run, or a run of a game the app/CLI is mapping right now (probe-active.json, e.g. the player started it from
        // Steam): the game must start exactly as it is, so nothing is applied.
        if (options?.Probe == true || ProbeMarker.IsActive(ctx.Paths.DataDir, ctx.Game.Id))
            return ProbeLaunch(ctx.Game, ctx.Paths.DataDir, command, options);

        RunState.RecoverStale(ctx.Paths.StateFile);
        Log.Info($"=== run {ctx.Game.Id} as '{ctx.Device.Name}' ({ctx.Device.Reason}), preset {ctx.Preset?.ToString() ?? "(none)"} ===");
        if (ctx.BigPictureUsed) Log.Info($"Big Picture (TV): {ctx.BigPicture!.Reason}");
        else if (ctx.BigPictureNote is { } bpNote) Log.Warn(bpNote);

        // "Set in-game": settings off, rest on. Nothing below writes, restores, re-applies, captures or asks about the game's settings
        // (and there is nothing to test); display, driver, launch args and the watchdog work as usual.
        var inGame = ctx.InGame;
        if (inGame is not null)
        {
            Log.Info(GameFlags.RunLine(inGame));
            // what led to the flag must not suggest it again once it is lifted (never throws)
            RunResults.MarkFlagged(ctx.Paths.DataDir, ctx.Game.Id, ctx.Device.Name);
            if (options?.Verify == true) Log.Info("Test preset: nothing to test, this game's settings are left to the game");
        }

        // "Also check presets on my next launch" (the game page, a manual opt-in per game): this launch is a Test preset run, so no capture prompt.
        var verifyNext = inGame is null && options?.Verify != true && VerifyNextArmed(ctx);
        var verify = options?.Verify == true || verifyNext;
        if (verifyNext) Log.Info("Checking the preset on this launch (asked on the game page): no capture prompt, the result is stored as a test");

        // Did the settings change while we were not running (launcher cloud sync)? Before anything is applied; never throws.
        if (inGame is null) SyncObserver.BeforeRun(ctx);

        AppliedState? applied = null;
        var timing = LaunchPolicy.EffectiveTiming(ctx.Game);
        try
        {
            if (inGame is not null) { /* settings are left to the game */ }
            else if (LaunchPolicy.AppliesBefore(ctx.Game)) applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
            else
            {
                applied = new AppliedState();
                Log.Info($"applyTiming {timing}: settings are written once the game process is seen");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Applying game settings failed — launching anyway", ex);
        }
        var details = new RunDetails();

        // Passive pass A: what the game rewrites by itself, watched around this launch (what the run wrote above is left out). The "before"
        // snapshot is taken alongside the display and driver switch below and joined before the launch. Never throws.
        var beginWatch = PassiveWatchWanted(ctx) ? Task.Run(() => BeginPassiveWatch(ctx, options, options?.PassiveSnapshot, applied)) : null;

        if (inGame is null) SyncObserver.AfterRun(ctx); // interim baseline in case this wrapper is killed before the game exits

        var state = new RunState
        {
            WrapperPid = Environment.ProcessId,
            StartedUtc = DateTime.UtcNow,
            Game = ctx.Game.Id,
        };
        ApplyDisplay(ctx, state, options?.DisplayApply);
        ApplyDriver(ctx, state);
        if (!state.IsEmpty) state.Save(ctx.Paths.RunStateFile(ctx.Game.Id));

        var watch = JoinPassiveWatch(beginWatch);

        int exitCode;
        var processSeen = true;
        try
        {
            var extraArgs = LaunchPolicy.MergeArgs(ctx.Game.Launch?.Args, ctx.Profile.LaunchArgs);
            var reapply = applied is not null && LaunchPolicy.AppliesAfterStart(ctx.Game);
            void OnDetected(Func<bool> stillRunning)
            {
                details.GameSeenUtc ??= DateTime.UtcNow; // the session starts when the game is seen, not when the launcher was asked
                if (reapply) ReapplyAfterStart(ctx, applied!, stillRunning, LaunchPolicy.Settle(ctx.Game), details, cancel: options?.Cancel ?? default);
            }
            details.StartedUtc = DateTime.UtcNow;
            exitCode = LaunchAndWait(ctx.Game, command, options, out processSeen, extraArgs, OnDetected, details);
        }
        finally
        {
            details.EndedUtc = DateTime.UtcNow;
            var stateFile = ctx.Paths.RunStateFile(ctx.Game.Id);
            if (state.Undo()) { if (File.Exists(stateFile)) File.Delete(stateFile); }
            else state.Save(stateFile); // a driver restore that failed keeps its token for the next start
        }

        // the "after" snapshot reads the game's files while the result is measured and the capture prompt is open (none of that writes them)
        var skipWhy = PassiveSkipReason(processSeen, options?.Cancel.IsCancellationRequested == true, details);
        var endWatch = watch is null ? null : Task.Run(() => watch.End(skipWhy));

        // This device's own copy of cloud-synced whole files (autoSnapshot): normal and verify runs, only when the game was seen and the run was not cancelled
        if (inGame is null) AutoSnapshots.CaptureAfterSession(ctx, processSeen, options?.Cancel.IsCancellationRequested == true);

        // the check asked for "my next launch" is done once the game was seen (a test of it counts too); a launch that never started keeps it
        if (inGame is null && verify && processSeen && options?.Cancel.IsCancellationRequested != true) ClearVerifyNext(ctx);

        if (applied is not null)
        {
            details.Build = RunBuild(ctx.Game, InstallDir(ctx.Game, options), options?.FindInstall);
            if (inGame is null) ObservePresetsBuild(ctx, details.Build);
            var (drift, result) = MeasureResult(ctx, applied, verify, processSeen, details);
            if (prompt && !verify && drift.Count > 0)
            {
                // Stored before the prompt (pending, no streak step yet) so a wrapper killed while it is open still leaves the run; the answer follows.
                StoreResult(ctx, result, capture: null);
                FinishResult(ctx, result, AskCapture(ctx, applied, drift: drift).Answer);
            }
            else StoreResult(ctx, result, CaptureAnswer.NotAsked);
        }
        if (inGame is null) SyncObserver.AfterRun(ctx); // baseline for the next launch, after the capture prompt
        JoinPassiveWatch(endWatch);
        Log.Info($"=== {ctx.Game.Id} exited ({exitCode}) ===");
        return exitCode;
    }

    /// <summary>The player asked the game page to check the preset on this launch (<see cref="GameLab.LabState.VerifyNext"/>). Never throws.</summary>
    internal static bool VerifyNextArmed(GameContext ctx)
    {
        try { return new GameLab.LabStateStore(ctx.Paths.DataDir).VerifiesNext(ctx.Game.Id); }
        catch (Exception ex)
        {
            Log.Warn($"Could not read whether to check the preset on this launch: {ex.Message}");
            return false;
        }
    }

    private static void ClearVerifyNext(GameContext ctx)
    {
        try
        {
            var store = new GameLab.LabStateStore(ctx.Paths.DataDir);
            if (store.VerifiesNext(ctx.Game.Id)) store.SetVerifyNext(ctx.Game.Id, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not clear the check on next launch: {ex.Message}");
        }
    }

    /// <summary>Waits for a background passive-watch step; its result, or null when there was none or it failed. Never throws.</summary>
    private static T? JoinPassiveWatch<T>(Task<T>? task) where T : class?
    {
        if (task is null) return null;
        try { return task.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Log.Warn($"session watch: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A mapping launch for a game that may have no preset (or no config at all): holds the run mutex, starts the game exactly as it is
    /// and waits for it to exit. Returns the game's exit code. The caller fingerprints around it (<see cref="GameLab.ProbeSession"/>).
    /// </summary>
    public static int RunProbe(GameDef game, string dataDir, string[] command, RunOptions? options = null)
    {
        using var guard = RunGuard.TryAcquire(MutexName(game.Id));
        if (guard is null)
        {
            Log.Info($"nested run: pass-through ({game.Id} is already being managed by another run)");
            return LaunchPlain(command.Length > 0 ? command : LaunchCommand(options));
        }
        return ProbeLaunch(game, dataDir, command, options);
    }

    private static int ProbeLaunch(GameDef game, string dataDir, string[] command, RunOptions? options)
    {
        Log.Info($"=== mapping run {game.Id}: nothing is applied, changed or asked ===");
        ProbeMarker.Set(dataDir, game.Id);
        try
        {
            var exitCode = LaunchAndWait(game, command, options, out var seen);
            if (!seen) Log.Warn($"{game.Id}: the game process was never seen, so this pass may have missed the game's changes");
            Log.Info($"=== {game.Id} exited ({exitCode}) ===");
            return exitCode;
        }
        finally
        {
            ProbeMarker.Clear(dataDir, game.Id);
        }
    }

    /// <summary>
    /// After the game exits: finds what no longer matches the preset (once), stores it in run-results.json for every run, and returns
    /// it so the capture prompt reuses it. Never throws.
    /// </summary>
    internal static List<Drift> RecordResult(GameContext ctx, AppliedState applied, bool verify, bool processSeen, RunDetails? details = null,
        CaptureAnswer capture = CaptureAnswer.NotAsked)
    {
        var (drift, result) = MeasureResult(ctx, applied, verify, processSeen, details);
        StoreResult(ctx, result, capture);
        return drift;
    }

    /// <summary>
    /// The first half of <see cref="RecordResult"/>: what no longer matches the preset, and the result built from it (not stored yet; null when
    /// there is nothing to store, e.g. a set in-game game or a failed check). Never throws.
    /// </summary>
    internal static (List<Drift> Drift, RunResult? Result) MeasureResult(GameContext ctx, AppliedState applied, bool verify, bool processSeen,
        RunDetails? details = null)
    {
        if (ctx.InGame is not null) return ([], null); // set in-game: no result, no streak, no prompt
        List<Drift> drift;
        try
        {
            drift = GameEngine.FindDrift(ctx, applied);
            // a normal run's whole-file change on an autoSnapshot file is expected (kept automatically); verify reports it as before
            if (!verify) drift = drift.Where(d => !AutoSnapshots.IsExpectedDrift(ctx, d)).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Checking for changed settings failed", ex);
            return ([], null);
        }
        try
        {
            var result = RunResults.Create(ctx, applied, drift, verify, processSeen);
            if (details is not null)
            {
                result.LaunchCommand = details.Command;
                result.LaunchNote = details.Note;
                result.Reapplied = details.Reapplied.ToList();
                result.SyncReapplied = details.SyncReapplied.ToList();
                result.LateFiles = details.Late.ToList();
                result.DurationSec = processSeen ? details.DurationSec : null;
                result.Build = details.Build;
            }
            return (drift, result);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not build the run result: {ex.Message}");
            return (drift, null);
        }
    }

    /// <summary>
    /// The second half of <see cref="RecordResult"/>: stores the result with the player's answer at the capture prompt, or (<paramref name="capture"/>
    /// null) pending, before the prompt is shown; <see cref="FinishResult"/> then adds the answer. Never throws.
    /// </summary>
    internal static void StoreResult(GameContext ctx, RunResult? result, CaptureAnswer? capture)
    {
        if (result is null || ctx.InGame is not null) return;
        try
        {
            result.Capture = capture;
            RunResults.Record(ctx.Paths.DataDir, result);
            Log.Info($"run result ({result.Mode}): {RunResults.Verdict(result).ToString().ToLowerInvariant()}; {result.Drift.Count} of {result.Applied} setting(s) changed" +
                     $"{(result.DurationSec is { } sec ? $", {sec} s played" : "")}; capture {capture?.ToString().ToLowerInvariant() ?? "pending"}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not record the run result: {ex.Message}");
        }
    }

    /// <summary>Adds the player's answer to a result stored pending by <see cref="StoreResult"/> (and its passive streak step). Never throws.</summary>
    internal static void FinishResult(GameContext ctx, RunResult? result, CaptureAnswer capture)
    {
        if (result is null || ctx.InGame is not null) return;
        try
        {
            RunResults.SetCapture(ctx.Paths.DataDir, result, capture);
            Log.Info($"run result: capture {capture.ToString().ToLowerInvariant()}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not record the capture answer: {ex.Message}");
        }
    }

    /// <summary>
    /// The build this run played (<see cref="GameBuild"/>): the Steam build id, else the exe fingerprint of the install folder the run knows
    /// (<c>--install-dir</c> or the Steam folder), else the build of the install a scan of the game's store finds (<paramref name="findInstall"/>,
    /// default <see cref="LibraryService.FindInstall"/>), fingerprinted the same way the library and the lab do, so held runs of any store carry
    /// a build comparable with theirs. Null when unknown. Never throws.
    /// </summary>
    internal static string? RunBuild(GameDef game, string? installDir, Func<GameDef, DetectedGame?>? findInstall = null)
    {
        try
        {
            if ((installDir is not null || game.SteamAppId is not null) && GameBuild.ForGame(game, installDir) is { } known) return known;
            return (findInstall ?? DetectInstall)(game) is { } detected ? GameBuild.Of(detected) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not tell the build of {game.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The detected install of a game from a scan of its own store only (null for a store no scanner knows).</summary>
    private static DetectedGame? DetectInstall(GameDef game) =>
        GameScanners.Stores.Contains(game.Store, StringComparer.OrdinalIgnoreCase)
            ? LibraryService.FindInstall(game, GameScanners.ScanAll([game.Store]))
            : null;

    /// <summary>
    /// Starts the passive pass A watch (<see cref="GameLab.PassiveWatch"/>) for a run whose settings Optimus-Vibe manages, when "check presets
    /// after each session" is on and the game has a store id to key it by (the same key "Map this game" uses). Null otherwise. What the run
    /// wrote before it (<paramref name="applied"/>) is left out of the session's diff (<see cref="PassiveExclusionsFor"/>). Never throws.
    /// </summary>
    internal static GameLab.PassiveWatch? BeginPassiveWatch(GameContext ctx, RunOptions? options, Func<GameLab.LabTarget, Func<GameLab.ProbeSnapshot>>? snapshotFor = null,
        AppliedState? applied = null)
    {
        if (!PassiveWatchWanted(ctx)) return null;
        try
        {
            if (!AppPaths.LoadSettings(ctx.Paths.DataDir).GameLabWatchSessions) return null;
            var target = GameLab.LabTarget.From(null, ctx.Game) with { InstallDir = InstallDir(ctx.Game, options) };
            if (target.StoreId.Length == 0) return null;
            var session = new GameLab.ProbeSession(target, new GameLab.EvidenceOptions { CurrentConfig = GameLab.LabCloud.CurrentConfigFrom(ctx.Paths) });
            Func<GameLab.ProbeSnapshot> snapshot = snapshotFor is not null ? snapshotFor(target) : session.Snapshot;
            return GameLab.PassiveWatch.Begin(ctx.Paths.DataDir, target.Key, snapshot, exclude: PassiveExclusionsFor(ctx, applied, session.Tokenize));
        }
        catch (Exception ex)
        {
            Log.Warn($"session watch: could not start for {ctx.Game.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A run worth watching: its settings are managed (not set in-game) and written before the launch. A game whose settings are written once
    /// its process is seen (<c>applyTiming: afterProcessStart</c>) has them written while it runs, so its sessions could never be kept.
    /// </summary>
    internal static bool PassiveWatchWanted(GameContext ctx) => ctx.InGame is null && LaunchPolicy.AppliesBefore(ctx.Game);

    /// <summary>Handlers whose keys a mapping snapshot reads with the same names, per the file extensions it reads them by.</summary>
    private static bool KeysMatchProbe(ResolvedFile f) => (f.Handler.Name, Path.GetExtension(f.Path ?? "").ToLowerInvariant()) switch
    {
        ("ini", ".ini" or ".cfg" or ".conf") => true,
        ("json", ".json") => true,
        ("xml", ".xml" or ".lsx" or ".config") => true,
        ("keyvalue", _) => true,
        _ => false,
    };

    /// <summary>
    /// What this run wrote before the game started (<paramref name="applied"/>): the preset's keys of each text file, the whole file for a whole-file
    /// save or a format whose keys a snapshot can't match, and registry values. A passive session leaves them out: a normal launch writes them
    /// first, while a real pass A writes nothing, so a game that resets a preset key on every launch must not make it "state".
    /// </summary>
    internal static GameLab.PassiveExclusions PassiveExclusionsFor(GameContext ctx, AppliedState? applied, Func<string, string> tokenize)
    {
        var exclude = new GameLab.PassiveExclusions();
        if (applied is null) return exclude;
        foreach (var f in ctx.Files)
        {
            if (f.Path is null) continue;
            var whole = applied.SnapshotHashes.ContainsKey(f.Id);
            var keys = applied.Targets.TryGetValue(f.Id, out var t) && t.Count > 0 ? t.Keys.ToList() : null;
            if (!whole && keys is null) continue;
            if (!f.Handler.IsFile) exclude.Registry(f.Path, whole ? null : keys);
            else exclude.File(tokenize(f.Path), whole || !KeysMatchProbe(f) ? null : keys);
        }
        return exclude;
    }

    /// <summary>Why a session can't show what the game rewrites by itself; null when it can.</summary>
    internal static string? PassiveSkipReason(bool processSeen, bool cancelled, RunDetails details) =>
        !processSeen ? "the game process was never seen"
        : cancelled ? "the run was cancelled"
        : details.Reapplied.Count > 0 ? "settings were written while the game ran"
        : null;

    /// <summary>The first build seen after the lab's presets arrived without one counts as theirs (<see cref="GameLab.LabStateStore.ObservePresetsBuild"/>). Never throws.</summary>
    private static void ObservePresetsBuild(GameContext ctx, string? build)
    {
        if (build is null) return;
        try
        {
            if (new GameLab.LabStateStore(ctx.Paths.DataDir).ObservePresetsBuild(ctx.Game.Id, build)) Log.Info($"presets build: {build}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not note the presets build: {ex.Message}");
        }
    }

    /// <summary>
    /// Start a command line without applying anything and wait for it: an exe (+ args) → its exit code; a single URI /
    /// shell target → 0 right after handing it to the shell. Empty command → 0. Start failures → 2.
    /// </summary>
    public static int LaunchPlain(IReadOnlyList<string> command)
    {
        if (command.Count == 0)
        {
            Log.Warn("Nothing to launch");
            return 0;
        }
        try
        {
            Log.Info($"Launching: {string.Join(' ', command)}");
            using var p = StartCommand(command);
            if (p is null) return 0;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start {command[0]}", ex);
            return 2;
        }
    }

    private static string[] LaunchCommand(RunOptions? options) =>
        string.IsNullOrWhiteSpace(options?.Launch) ? [] : [options.Launch, .. options.LaunchArgs];

    private static void ApplyDisplay(GameContext ctx, RunState state, Func<DisplayModeDef, string?, DisplayModeState?>? apply = null)
    {
        if (ctx.Profile.DisplayKeep)
        {
            Log.Info("display: keep - the display is left as it is for this preset");
            return;
        }
        var wanted = ctx.Profile.Display ?? (ctx.BigPictureUsed ? ctx.BigPicture?.Mode : null);
        if (wanted is null) return;
        var stateFile = ctx.Paths.RunStateFile(ctx.Game.Id);
        if (RunState.FindHolder(ctx.Paths.StateFile, stateFile, s => s.Display is not null, liveOnly: true) is { } held)
        {
            Log.Warn($"display change skipped: {held}");
            return;
        }
        try
        {
            state.Display = (apply ?? DisplayManager.Apply)(wanted, ctx.DisplayTarget);
            if (state.Display is not null) Log.Info($"Display set to {wanted}{(ctx.DisplayTarget is { } t ? $" on {t}" : "")}");
            if (state.Display is not null) state.Save(stateFile); // recoverable even if killed before the launch
        }
        catch (Exception ex)
        {
            Log.Error("Could not change display mode", ex);
        }
    }

    private static void ApplyDriver(GameContext ctx, RunState state)
    {
        if (ctx.DriverTargets.Count == 0) return;
        try
        {
            var driver = DriverSettings.For(ctx.Device.Driver);
            if (!driver.IsAvailable(out var reason))
            {
                Log.Warn($"{ctx.Device.Driver} driver settings skipped: {reason}");
                return;
            }
            // Driver settings are machine-wide: never stack on another run's (or an unrestored leftover's) changes, or their "previous" values would be wrong.
            var stateFile = ctx.Paths.RunStateFile(ctx.Game.Id);
            if (RunState.FindHolder(ctx.Paths.StateFile, stateFile, s => s.DriverToken is not null) is { } held)
            {
                Log.Warn($"{driver.Vendor} driver settings skipped: {held}");
                return;
            }
            // The token is persisted before the first write (intent record), so a kill between apply and save is still recoverable.
            var token = driver.Apply(ctx.Game, ctx.DriverTargets, t =>
            {
                state.DriverVendor = driver.Vendor;
                state.DriverToken = t;
                state.Save(stateFile);
            });
            state.DriverVendor = token is null ? null : driver.Vendor;
            state.DriverToken = token;
            if (token is null) // nothing changed after all: drop the intent record
            {
                if (state.IsEmpty) { if (File.Exists(stateFile)) File.Delete(stateFile); }
                else state.Save(stateFile);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply driver settings", ex);
            state.UndoDriver(); // a partial apply is undone right away; if that fails the token stays and is retried
        }
    }

    /// <summary>What a run learned beyond the drift: the command it started, how the launch went, what was written after the game started.</summary>
    internal sealed class RunDetails
    {
        public string? Command { get; set; }
        public string? Note { get; set; }
        public List<string> Reapplied { get; } = [];
        public List<string> Late { get; } = [];

        /// <summary>Files written before launch and written again once the game was seen because they had changed meanwhile (a launcher's cloud sync).</summary>
        public List<string> SyncReapplied { get; } = [];

        /// <summary>When the launch was started, when the game process was first seen (null: never, or not watched), and when the wait ended.</summary>
        public DateTime? StartedUtc { get; set; }
        public DateTime? GameSeenUtc { get; set; }
        public DateTime? EndedUtc { get; set; }

        /// <summary>The game build after the session (<see cref="GameBuild"/>); null when unknown.</summary>
        public string? Build { get; set; }

        /// <summary>Seconds from the game being seen (else from the launch) to the end of the wait; null when either end is missing.</summary>
        public int? DurationSec => (GameSeenUtc ?? StartedUtc) is { } from && EndedUtc is { } to && to >= from ? (int)(to - from).TotalSeconds : null;
    }

    /// <summary>
    /// Once the game process is seen: wait for the launcher's cloud sync to settle, then write what drifted (plain files: the keys that
    /// changed; snapshots: only a file that no longer matches), and apply files that did not exist before launch as soon as they appear
    /// (up to <see cref="LateFileWait"/>). Files created after the game booted are marked as taking effect next launch. Never throws.
    /// </summary>
    internal static void ReapplyAfterStart(GameContext ctx, AppliedState applied, Func<bool> stillRunning, TimeSpan settle, RunDetails details,
        Action<TimeSpan>? sleep = null, TimeSpan? lateWait = null, CancellationToken cancel = default)
    {
        // The default sleep is sliced so a cancel or the game exiting ends the wait within a second.
        sleep ??= total =>
        {
            for (var left = total; left > TimeSpan.Zero && !cancel.IsCancellationRequested && stillRunning(); left -= TimeSpan.FromSeconds(1))
                cancel.WaitHandle.WaitOne(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
        };
        var maxWait = lateWait ?? LateFileWait;
        // what the pre-launch apply wrote: only a rewrite of one of these means something changed it in between (afterProcessStart writes nothing before)
        var writtenBefore = applied.Targets.Where(t => t.Value.Count > 0).Select(t => t.Key).Concat(applied.SnapshotHashes.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (settle > TimeSpan.Zero) sleep(settle);
            var waited = TimeSpan.Zero;
            while (!cancel.IsCancellationRequested && stillRunning())
            {
                var plans = GameEngine.PlanReapply(ctx, applied);
                var work = plans.Where(p => p.HasWork).ToList();
                if (work.Count > 0)
                {
                    var state = GameEngine.Apply(ctx, plans);
                    foreach (var (id, targets) in state.Targets) applied.Targets[id] = targets;
                    foreach (var (id, hash) in state.SnapshotHashes) applied.SnapshotHashes[id] = hash;
                    foreach (var p in work)
                    {
                        var late = waited > TimeSpan.Zero;
                        var label = late ? $"{p.File.Id} (created late: takes effect next launch)" : p.File.Id;
                        if (!details.Reapplied.Contains(p.File.Id)) details.Reapplied.Add(p.File.Id);
                        if (late && !details.Late.Contains(p.File.Id)) details.Late.Add(p.File.Id);
                        if (!late && writtenBefore.Contains(p.File.Id) && !details.SyncReapplied.Contains(p.File.Id)) details.SyncReapplied.Add(p.File.Id);
                        Log.Info($"  re-applied after launch: {label}");
                    }
                }
                if (!plans.Any(p => p.Missing) || waited >= maxWait) break;
                sleep(PollInterval);
                waited += PollInterval;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Re-applying settings after the game started failed", ex);
        }
    }

    private static readonly TimeSpan LateFileWait = TimeSpan.FromSeconds(120);

    private static int LaunchAndWait(GameDef game, string[] command, RunOptions? options, out bool processSeen,
        IReadOnlyList<string>? extraArgs = null, Action<Func<bool>>? onGameDetected = null, RunDetails? details = null)
    {
        processSeen = true; // false only when we waited for a game process that never appeared, or could not tell
        var watched = game.Processes.Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
        var launcherNames = (game.Launch?.LauncherProcesses ?? []).Select(p => Path.GetFileNameWithoutExtension(p)).Where(p => p.Length > 0).ToArray();
        var battleNet = game.Store.Equals("battlenet", StringComparison.OrdinalIgnoreCase);
        var logDir = LauncherLogs.BattleNetLogDir();
        var marks = battleNet ? LauncherLogs.Mark(logDir) : new Dictionary<string, long>();

        // No `processes:` → after a URI launch, watch for new processes running from the install folder.
        string? installDir = null;
        HashSet<int>? baseline = null;
        if (watched.Length == 0)
        {
            installDir = InstallDir(game, options);
            if (installDir is not null) baseline = ProcessWatch.PidsUnder(installDir);
        }

        var line = ResolveLaunch(game, command, options, extraArgs);
        var viaBattleNet = battleNet && command.Length == 0 && LauncherLogs.GoesThroughBattleNet(line);
        using var started = Start(line, details);
        // Steam's own exe, started with -applaunch, is only a hand-off (it may even become the Steam client): treat it like a URI launch.
        var handoff = started is null || IsSteamApplaunch(line);

        Func<bool> isRunning;
        string what;
        if (watched.Length > 0)
        {
            isRunning = () => watched.Any(IsRunning);
            what = string.Join(", ", game.Processes);
        }
        else if (handoff && installDir is not null)
        {
            var dir = installDir;
            var before = baseline!;
            isRunning = () => ProcessWatch.PidsUnder(dir).Any(pid => !before.Contains(pid));
            what = $"a process from {dir}";
            Log.Info($"No `processes:` configured — waiting for {what}");
        }
        else if (handoff)
        {
            Log.Warn("Launched via URI with no `processes:` or install folder — cannot tell when the game exits; display/driver changes are undone now");
            processSeen = false;
            return 0;
        }
        else
        {
            started.WaitForExit();
            return started.ExitCode;
        }

        // Launchers and Steam stubs often exit before (or while) the real game runs: wait on the game's processes.
        var outcome = LaunchWatchdog.Watch(new WatchdogInputs
        {
            GameRunning = isRunning,
            LauncherRunning = () => launcherNames.Any(IsRunning),
            StarterGone = () => started is null || started.HasExited,
            // Only when Battle.net itself was asked to start the game, and never while a game process exists.
            EarlyFailure = viaBattleNet ? elapsed => isRunning() ? null : LauncherLogs.BattleNetProbe(logDir, marks, elapsed) : null,
            OnGameDetected = onGameDetected,
            OnStatus = options?.OnStatus,
            StartupTimeout = LaunchPolicy.StartupTimeout(game),
            PollInterval = PollInterval,
        }, options?.Cancel ?? default);

        if (outcome.GameSeen) Log.Info($"Game process detected ({what})");
        else if (outcome.Reason is { } why) Log.Warn($"{game.Id}: {why}");
        if (details is not null) details.Note = outcome.Reason;

        processSeen = outcome.GameSeen;
        if (started is null || handoff) return 0;
        if (outcome.Phase is LaunchPhase.Failed or LaunchPhase.GaveUp or LaunchPhase.Cancelled && !started.HasExited) return 0; // not our game: do not block on a stub
        if (!started.HasExited) started.WaitForExit();
        return started.ExitCode;
    }


    private static string? InstallDir(GameDef game, RunOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.InstallDir))
            return Directory.Exists(options.InstallDir) ? Path.GetFullPath(options.InstallDir) : null;
        if (game.SteamAppId is not { } appId) return null;
        try
        {
            var dir = new SteamLocator().InstallDir(appId);
            return dir is not null && Directory.Exists(dir) ? dir : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not find the Steam install folder of {appId}: {ex.Message}");
            return null;
        }
    }

    private static bool IsRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
    /// <summary>
    /// Precedence: `-- command` (Steam's %command%) → <see cref="RunOptions.Launch"/> → the game's launch.exe / launch.uri
    /// → steam://rungameid/&lt;steamAppId&gt;. Returns null for URI / shell targets.
    /// </summary>
    private static Process? Start(string[] line, RunDetails? details)
    {
        Log.Info($"Launching: {string.Join(' ', line)}");
        if (details is not null) details.Command = string.Join(' ', line);
        return StartCommand(line);
    }

    /// <summary>
    /// The command line a launch starts, in the same precedence as <see cref="Start"/>. <paramref name="extraArgs"/> (the game's launch.args merged with
    /// the preset's) go after the exe, after Steam's %command%, or (Steam game, started by us) after `steam.exe -applaunch &lt;id&gt;`, because Steam drops
    /// the arguments of a steam://rungameid/&lt;id&gt;//&lt;args&gt; URI; null = a mapping run, where
    /// only an exe launch keeps its configured launch.args (the launcher needs them) and nothing is added to a Steam launch.
    /// <paramref name="steamExe"/> finds steam.exe (default: the Steam root); null from it = the plain URI, arguments dropped, with a warning.
    /// </summary>
    internal static string[] ResolveLaunch(GameDef game, string[] command, RunOptions? options, IReadOnlyList<string>? extraArgs, Func<string?>? steamExe = null)
    {
        var extra = extraArgs ?? [];
        if (command.Length > 0) return [.. LaunchPolicy.AppendArgs(command, extra)];

        var fromOptions = LaunchCommand(options);
        if (fromOptions.Length > 0)
        {
            if (IsShellTarget(fromOptions[0])) return SteamRoute(fromOptions[0], [.. fromOptions.Skip(1), .. extra], steamExe);
            return [.. LaunchPolicy.AppendArgs(fromOptions, extra)];
        }

        var launch = game.Launch;
        if (!string.IsNullOrEmpty(launch?.Exe))
        {
            var exe = IsShellTarget(launch.Exe) ? launch.Exe : new PathResolver().Expand(launch.Exe);
            return [exe, .. extraArgs ?? launch.Args];
        }

        var uri = launch?.Uri ?? (game.SteamAppId is { } id ? $"steam://rungameid/{id}" : null)
                  ?? throw new ConfigException($"{game.Id}: nothing to launch — pass `-- %command%` or set launch.exe/launch.uri");
        return SteamRoute(uri, extra, steamExe);
    }

    /// <summary>
    /// A plain steam://rungameid/&lt;id&gt; URI with arguments becomes `steam.exe -applaunch &lt;id&gt; &lt;args&gt;`; without arguments, or for any other
    /// target, the target itself. No steam.exe: the plain URI and a warning that the arguments are dropped.
    /// </summary>
    internal static string[] SteamRoute(string target, IReadOnlyList<string> args, Func<string?>? steamExe = null)
    {
        if (args.Count == 0 || LaunchPolicy.PlainSteamAppId(target) is not { } id) return [target];
        var exe = (steamExe ?? FindSteamExe)();
        if (exe is null)
        {
            Log.Warn($"steam.exe not found: starting {target} without its arguments ({string.Join(' ', args)}) - Steam drops arguments given in a URI");
            return [target];
        }
        return LaunchPolicy.SteamApplaunch(exe, id, args);
    }

    private static string? FindSteamExe()
    {
        try
        {
            var root = new SteamLocator().SteamRoot;
            var exe = root is null ? null : Path.Combine(root, "steam.exe");
            return exe is not null && File.Exists(exe) ? exe : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not find steam.exe: {ex.Message}");
            return null;
        }
    }

    /// <summary>True for the `steam.exe -applaunch` form: a hand-off to the Steam client, whose own process says nothing about the game.</summary>
    internal static bool IsSteamApplaunch(IReadOnlyList<string> line) =>
        line.Count > 1 && line[1].Equals("-applaunch", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileNameWithoutExtension(line[0]).Equals("steam", StringComparison.OrdinalIgnoreCase);

    /// <summary>A single URI / shell target is shell-opened (null process); otherwise exe + args.</summary>
    internal static Process? StartCommand(IReadOnlyList<string> command)
    {
        if (IsShellTarget(command[0]))
        {
            if (command.Count > 1) Log.Warn($"Arguments ignored for {command[0]}: {string.Join(' ', command.Skip(1))}");
            ShellOpen(command[0]);
            return null;
        }
        return StartExe(command[0], command.Skip(1));
    }

    private static void ShellOpen(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    private static Process StartExe(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var dir = Path.GetDirectoryName(exe);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
        return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
    }

    /// <summary>
    /// After the game exits: when its settings drifted (the list <see cref="RecordResult"/> found, or found here), ask (through <see cref="ICapturePrompt"/>) whether to keep them. Skipped when
    /// the player chose "don't ask again" for this game. Returns the save that was written, or null. Never throws.
    /// </summary>
    internal static SaveResult? OfferCapture(GameContext ctx, AppliedState applied, ICapturePrompt? prompt = null, IReadOnlyList<Drift>? drift = null) =>
        AskCapture(ctx, applied, prompt, drift).Save;

    /// <summary>What the capture prompt led to: the player's answer, and the save written for it (null unless saved).</summary>
    internal sealed record CaptureOutcome(CaptureAnswer Answer, SaveResult? Save);

    /// <summary>
    /// <see cref="OfferCapture"/> that also says what the player answered (<see cref="CaptureAnswer.NotAsked"/> when nothing was asked,
    /// <see cref="CaptureAnswer.Off"/> when the question is turned off for the game). A save that fails keeps the answer Saved: it was the
    /// player's intent. Never throws.
    /// </summary>
    internal static CaptureOutcome AskCapture(GameContext ctx, AppliedState applied, ICapturePrompt? prompt = null, IReadOnlyList<Drift>? drift = null)
    {
        var answer = CaptureAnswer.NotAsked;
        try
        {
            if (ctx.InGame is not null) return new(answer, null); // set in-game: the game's settings are not ours to keep
            if (CapturePromptStore.IsSkipped(ctx.Paths.DataDir, ctx.Game.Id))
            {
                Log.Info($"{ctx.Game.Id}: capture prompt turned off for this game");
                return new(CaptureAnswer.Off, null);
            }
            drift ??= GameEngine.FindDrift(ctx, applied);
            // whole-file drift on an autoSnapshot file is kept automatically, so it never asks; keyed drift still does
            drift = drift.Where(d => !AutoSnapshots.IsExpectedDrift(ctx, d)).ToList();
            if (drift.Count == 0) return new(answer, null);

            foreach (var d in drift) Log.Info($"  changed in-game: {d.FileId} {d.Key}: {d.Applied} -> {d.Now}");
            var lines = drift.Take(CapturePrompts.MaxLines)
                .Select(d => d.Applied is null ? $"{d.FileId} (whole file)" : $"{d.Key}: {d.Applied} → {d.Now}").ToList();
            var request = new CaptureRequest(ctx.Game.Id, ctx.Game.Name, ctx.Device.Name, ctx.Profile.Label ?? ctx.Preset?.Name ?? "the preset",
                lines, drift.Count, ctx.Save?.Name, $"Tuned {DateTime.Now:MMM d HH:mm}");

            var decision = (prompt ?? CapturePrompts.Current).Ask(request);
            switch (decision.Choice)
            {
                case CaptureChoice.DontSave:
                    return new(CaptureAnswer.DontSave, null);
                case CaptureChoice.DontAskAgain:
                    answer = CaptureAnswer.DontAskAgain;
                    CapturePromptStore.SetSkipped(ctx.Paths.DataDir, ctx.Game.Id, true);
                    Log.Info($"{ctx.Game.Id}: won't ask to save after playing again");
                    return new(answer, null);
            }

            answer = CaptureAnswer.Saved;
            var result = decision.Choice == CaptureChoice.SaveAs
                ? ManualSaves.SaveCurrent(ctx, string.IsNullOrWhiteSpace(decision.Name) ? request.SuggestedName : decision.Name.Trim())
                : ctx.Save is { } s
                    ? ManualSaves.SaveCurrent(ctx, s.Name, updateSlug: s.Slug)
                    : ManualSaves.SaveCurrent(ctx, request.SuggestedName);
            Log.Info($"Saved '{result.Save.Name}' ({result.KeysSaved} setting(s)){(result.Sync is { } g ? $"; sync: {g.Message}" : "")}");
            return new(answer, result);
        }
        catch (Exception ex)
        {
            Log.Error("Capture prompt failed", ex);
            return new(answer, null);
        }
    }

    /// <summary>Holds a named mutex; released (on the acquiring thread) when disposed.</summary>
    private sealed class RunGuard : IDisposable
    {
        private Mutex? _mutex;

        private RunGuard(Mutex mutex) => _mutex = mutex;

        public static RunGuard? TryAcquire(string name)
        {
            var mutex = new Mutex(initiallyOwned: false, name);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // previous holder died without releasing: it is ours now
            }
            if (owned) return new RunGuard(mutex);
            mutex.Dispose();
            return null;
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
}
