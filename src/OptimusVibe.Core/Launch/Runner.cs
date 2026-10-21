using System.Diagnostics;
using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Display;
using OptimusVibe.Driver;
using OptimusVibe.Engine;
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

        // Did the settings change while we were not running (launcher cloud sync)? Before anything is applied; never throws.
        SyncObserver.BeforeRun(ctx);

        AppliedState? applied = null;
        var timing = LaunchPolicy.EffectiveTiming(ctx.Game);
        try
        {
            if (LaunchPolicy.AppliesBefore(ctx.Game)) applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
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

        SyncObserver.AfterRun(ctx); // interim baseline in case this wrapper is killed before the game exits

        var state = new RunState
        {
            WrapperPid = Environment.ProcessId,
            StartedUtc = DateTime.UtcNow,
            Game = ctx.Game.Id,
        };
        ApplyDisplay(ctx, state);
        ApplyDriver(ctx, state);
        if (!state.IsEmpty) state.Save(ctx.Paths.StateFile);

        int exitCode;
        var processSeen = true;
        try
        {
            var extraArgs = LaunchPolicy.MergeArgs(ctx.Game.Launch?.Args, ctx.Profile.LaunchArgs);
            Action<Func<bool>>? onDetected = applied is not null && LaunchPolicy.AppliesAfterStart(ctx.Game)
                ? stillRunning => ReapplyAfterStart(ctx, applied, stillRunning, LaunchPolicy.Settle(ctx.Game), details)
                : null;
            exitCode = LaunchAndWait(ctx.Game, command, options, out processSeen, extraArgs, onDetected, details);
        }
        finally
        {
            state.Undo();
            if (File.Exists(ctx.Paths.StateFile)) File.Delete(ctx.Paths.StateFile);
        }

        if (applied is not null)
        {
            var verify = options?.Verify == true;
            var drift = RecordResult(ctx, applied, verify, processSeen, details);
            if (prompt && !verify) OfferCapture(ctx, applied, drift: drift);
        }
        SyncObserver.AfterRun(ctx); // baseline for the next launch, after the capture prompt
        Log.Info($"=== {ctx.Game.Id} exited ({exitCode}) ===");
        return exitCode;
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
    internal static List<Drift> RecordResult(GameContext ctx, AppliedState applied, bool verify, bool processSeen, RunDetails? details = null)
    {
        List<Drift> drift;
        try
        {
            drift = GameEngine.FindDrift(ctx, applied);
        }
        catch (Exception ex)
        {
            Log.Error("Checking for changed settings failed", ex);
            return [];
        }
        try
        {
            var result = RunResults.Create(ctx, applied, drift, verify, processSeen);
            if (details is not null)
            {
                result.LaunchCommand = details.Command;
                result.LaunchNote = details.Note;
                result.Reapplied = details.Reapplied.ToList();
                result.LateFiles = details.Late.ToList();
            }
            RunResults.Record(ctx.Paths.DataDir, result);
            Log.Info($"run result ({result.Mode}): {RunResults.Verdict(result).ToString().ToLowerInvariant()}; {result.Drift.Count} of {result.Applied} setting(s) changed");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not record the run result: {ex.Message}");
        }
        return drift;
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

    private static void ApplyDisplay(GameContext ctx, RunState state)
    {
        if (ctx.Profile.DisplayKeep)
        {
            Log.Info("display: keep - the display is left as it is for this preset");
            return;
        }
        var wanted = ctx.Profile.Display ?? (ctx.BigPictureUsed ? ctx.BigPicture?.Mode : null);
        if (wanted is null) return;
        try
        {
            state.Display = DisplayManager.Apply(wanted, ctx.DisplayTarget);
            if (state.Display is not null) Log.Info($"Display set to {wanted}{(ctx.DisplayTarget is { } t ? $" on {t}" : "")}");
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
            var token = driver.Apply(ctx.Game, ctx.DriverTargets);
            if (token is not null)
            {
                state.DriverVendor = driver.Vendor;
                state.DriverToken = token;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply driver settings", ex);
        }
    }

    /// <summary>What a run learned beyond the drift: the command it started, how the launch went, what was written after the game started.</summary>
    internal sealed class RunDetails
    {
        public string? Command { get; set; }
        public string? Note { get; set; }
        public List<string> Reapplied { get; } = [];
        public List<string> Late { get; } = [];
    }

    /// <summary>
    /// Once the game process is seen: wait for the launcher's cloud sync to settle, then write what drifted (plain files: the keys that
    /// changed; snapshots: only a file that no longer matches), and apply files that did not exist before launch as soon as they appear
    /// (up to <see cref="LateFileWait"/>). Files created after the game booted are marked as taking effect next launch. Never throws.
    /// </summary>
    internal static void ReapplyAfterStart(GameContext ctx, AppliedState applied, Func<bool> stillRunning, TimeSpan settle, RunDetails details,
        Action<TimeSpan>? sleep = null, TimeSpan? lateWait = null)
    {
        sleep ??= Thread.Sleep;
        var maxWait = lateWait ?? LateFileWait;
        try
        {
            if (settle > TimeSpan.Zero) sleep(settle);
            var waited = TimeSpan.Zero;
            while (stillRunning())
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

        using var started = Start(game, command, options, extraArgs, details);

        Func<bool> isRunning;
        string what;
        if (watched.Length > 0)
        {
            isRunning = () => watched.Any(IsRunning);
            what = string.Join(", ", game.Processes);
        }
        else if (started is null && installDir is not null)
        {
            var dir = installDir;
            var before = baseline!;
            isRunning = () => ProcessWatch.PidsUnder(dir).Any(pid => !before.Contains(pid));
            what = $"a process from {dir}";
            Log.Info($"No `processes:` configured — waiting for {what}");
        }
        else if (started is null)
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
            EarlyFailure = battleNet ? elapsed => LauncherLogs.BattleNetProbe(logDir, marks, elapsed) : null,
            OnGameDetected = onGameDetected,
            OnStatus = options?.OnStatus,
            StartupTimeout = LaunchPolicy.StartupTimeout(game),
            PollInterval = PollInterval,
        }, options?.Cancel ?? default);

        if (outcome.GameSeen) Log.Info($"Game process detected ({what})");
        else if (outcome.Reason is { } why) Log.Warn($"{game.Id}: {why}");
        if (details is not null) details.Note = outcome.Reason;

        processSeen = outcome.GameSeen;
        if (started is null) return 0;
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
    private static Process? Start(GameDef game, string[] command, RunOptions? options, IReadOnlyList<string>? extraArgs, RunDetails? details)
    {
        var line = ResolveLaunch(game, command, options, extraArgs);
        Log.Info($"Launching: {string.Join(' ', line)}");
        if (details is not null) details.Command = string.Join(' ', line);
        return StartCommand(line);
    }

    /// <summary>
    /// The command line a launch starts, in the same precedence as <see cref="Start"/>. <paramref name="extraArgs"/> (the game's launch.args merged with
    /// the preset's) go after the exe, after Steam's %command%, or into the steam://rungameid/&lt;id&gt;//&lt;args&gt; form; null = a mapping run, where
    /// only an exe launch keeps its configured launch.args (the launcher needs them) and nothing is added to a Steam launch.
    /// </summary>
    internal static string[] ResolveLaunch(GameDef game, string[] command, RunOptions? options, IReadOnlyList<string>? extraArgs)
    {
        var extra = extraArgs ?? [];
        if (command.Length > 0) return [.. LaunchPolicy.AppendArgs(command, extra)];

        var fromOptions = LaunchCommand(options);
        if (fromOptions.Length > 0)
        {
            if (IsShellTarget(fromOptions[0])) return [LaunchPolicy.WithArgs(fromOptions[0], [.. fromOptions.Skip(1), .. extra])];
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
        return [LaunchPolicy.WithArgs(uri, extra)];
    }

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
    internal static SaveResult? OfferCapture(GameContext ctx, AppliedState applied, ICapturePrompt? prompt = null, IReadOnlyList<Drift>? drift = null)
    {
        try
        {
            if (CapturePromptStore.IsSkipped(ctx.Paths.DataDir, ctx.Game.Id))
            {
                Log.Info($"{ctx.Game.Id}: capture prompt turned off for this game");
                return null;
            }
            drift ??= GameEngine.FindDrift(ctx, applied);
            if (drift.Count == 0) return null;

            foreach (var d in drift) Log.Info($"  changed in-game: {d.FileId} {d.Key}: {d.Applied} -> {d.Now}");
            var lines = drift.Take(CapturePrompts.MaxLines)
                .Select(d => d.Applied is null ? $"{d.FileId} (whole file)" : $"{d.Key}: {d.Applied} → {d.Now}").ToList();
            var request = new CaptureRequest(ctx.Game.Id, ctx.Game.Name, ctx.Device.Name, ctx.Profile.Label ?? ctx.Preset?.Name ?? "the preset",
                lines, drift.Count, ctx.Save?.Name, $"Tuned {DateTime.Now:MMM d HH:mm}");

            var decision = (prompt ?? CapturePrompts.Current).Ask(request);
            switch (decision.Choice)
            {
                case CaptureChoice.DontSave:
                    return null;
                case CaptureChoice.DontAskAgain:
                    CapturePromptStore.SetSkipped(ctx.Paths.DataDir, ctx.Game.Id, true);
                    Log.Info($"{ctx.Game.Id}: won't ask to save after playing again");
                    return null;
            }

            var result = decision.Choice == CaptureChoice.SaveAs
                ? ManualSaves.SaveCurrent(ctx, string.IsNullOrWhiteSpace(decision.Name) ? request.SuggestedName : decision.Name.Trim())
                : ctx.Save is { } s
                    ? ManualSaves.SaveCurrent(ctx, s.Name, updateSlug: s.Slug)
                    : ManualSaves.SaveCurrent(ctx, request.SuggestedName);
            Log.Info($"Saved '{result.Save.Name}' ({result.KeysSaved} setting(s)){(result.Sync is { } g ? $"; sync: {g.Message}" : "")}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Capture prompt failed", ex);
            return null;
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
