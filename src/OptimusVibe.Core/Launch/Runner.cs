using System.Diagnostics;
using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Display;
using OptimusVibe.Driver;
using OptimusVibe.Engine;
using OptimusVibe.Logging;
using OptimusVibe.Ui;

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
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

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

        RunState.RecoverStale(ctx.Paths.StateFile);
        Log.Info($"=== run {ctx.Game.Id} as '{ctx.Device.Name}' ({ctx.Device.Reason}), preset {ctx.Preset?.ToString() ?? "(none)"} ===");

        // Did the settings change while we were not running (launcher cloud sync)? Before anything is applied; never throws.
        SyncObserver.BeforeRun(ctx);

        AppliedState? applied = null;
        try
        {
            applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        }
        catch (Exception ex)
        {
            Log.Error("Applying game settings failed — launching anyway", ex);
        }

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
        try
        {
            exitCode = LaunchAndWait(ctx, command, options);
        }
        finally
        {
            state.Undo();
            if (File.Exists(ctx.Paths.StateFile)) File.Delete(ctx.Paths.StateFile);
        }

        if (applied is not null && prompt) OfferCapture(ctx, applied);
        SyncObserver.AfterRun(ctx); // baseline for the next launch, after the capture prompt
        Log.Info($"=== {ctx.Game.Id} exited ({exitCode}) ===");
        return exitCode;
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
        if (ctx.Profile.Display is null) return;
        try
        {
            state.Display = DisplayManager.Apply(ctx.Profile.Display);
            if (state.Display is not null) Log.Info($"Display set to {ctx.Profile.Display}");
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

    private static int LaunchAndWait(GameContext ctx, string[] command, RunOptions? options)
    {
        var watched = ctx.Game.Processes.Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();

        // No `processes:` → after a URI launch, watch for new processes running from the install folder.
        string? installDir = null;
        HashSet<int>? baseline = null;
        if (watched.Length == 0)
        {
            installDir = InstallDir(ctx, options);
            if (installDir is not null) baseline = ProcessWatch.PidsUnder(installDir);
        }

        using var started = Start(ctx, command, options);

        Func<bool> isRunning;
        string what;
        if (watched.Length > 0)
        {
            isRunning = () => watched.Any(IsRunning);
            what = string.Join(", ", ctx.Game.Processes);
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
            return 0;
        }
        else
        {
            started.WaitForExit();
            return started.ExitCode;
        }

        // Launchers and Steam stubs often exit before (or while) the real game runs: wait on the game's processes.
        var deadline = DateTime.UtcNow + StartupTimeout;
        bool seen = false;
        int quietPolls = 0;
        while (true)
        {
            bool running = SafeProbe(isRunning);
            if (running)
            {
                if (!seen) Log.Info($"Game process detected ({what})");
                seen = true;
                quietPolls = 0;
            }
            else if (seen)
            {
                // two quiet polls in a row, so a game that restarts itself (e.g. after an anti-cheat bootstrap) is not missed
                if (++quietPolls >= 2) break;
            }
            else if ((started is null || started.HasExited) && DateTime.UtcNow > deadline)
            {
                Log.Warn($"Game process never appeared within {StartupTimeout.TotalMinutes:0} minutes");
                break;
            }
            Thread.Sleep(PollInterval);
        }

        if (started is null) return 0;
        if (!started.HasExited) started.WaitForExit();
        return started.ExitCode;
    }

    private static bool SafeProbe(Func<bool> probe)
    {
        try { return probe(); }
        catch (Exception ex)
        {
            Log.Warn($"Process check failed: {ex.Message}");
            return false;
        }
    }

    private static string? InstallDir(GameContext ctx, RunOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.InstallDir))
            return Directory.Exists(options.InstallDir) ? Path.GetFullPath(options.InstallDir) : null;
        if (ctx.Game.SteamAppId is not { } appId) return null;
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
    private static Process? Start(GameContext ctx, string[] command, RunOptions? options)
    {
        if (command.Length > 0)
        {
            Log.Info($"Launching: {string.Join(' ', command)}");
            return StartCommand(command);
        }

        var fromOptions = LaunchCommand(options);
        if (fromOptions.Length > 0)
        {
            Log.Info($"Launching: {string.Join(' ', fromOptions)}");
            return StartCommand(fromOptions);
        }

        var launch = ctx.Game.Launch;
        if (!string.IsNullOrEmpty(launch?.Exe))
        {
            var exe = IsShellTarget(launch.Exe) ? launch.Exe : new PathResolver().Expand(launch.Exe);
            Log.Info($"Launching: {exe} {string.Join(' ', launch.Args)}");
            return StartCommand([exe, .. launch.Args]);
        }

        var uri = launch?.Uri ?? (ctx.Game.SteamAppId is { } id ? $"steam://rungameid/{id}" : null)
                  ?? throw new ConfigException($"{ctx.Game.Id}: nothing to launch — pass `-- %command%` or set launch.exe/launch.uri");
        Log.Info($"Launching: {uri}");
        ShellOpen(uri);
        return null;
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

    private static void OfferCapture(GameContext ctx, AppliedState applied)
    {
        var drift = GameEngine.FindDrift(ctx, applied);
        if (drift.Count == 0) return;

        foreach (var d in drift) Log.Info($"  changed in-game: {d.FileId} {d.Key}: {d.Applied} -> {d.Now}");
        var lines = drift.Take(8).Select(d => d.Applied is null ? $"• {d.FileId} (whole file)" : $"• {d.Key}: {d.Applied} → {d.Now}");
        var more = drift.Count > 8 ? $"\n…and {drift.Count - 8} more" : "";
        var question = ctx.Save is { } current
            ? $"Update your manual save '{current.Name}' with these changes?"
            : $"Keep them as a new manual save for {ctx.Device.Name}? It becomes this game's active setting.";
        var message = $"{drift.Count} setting(s) changed while playing {ctx.Game.Name}:\n\n{string.Join("\n", lines)}{more}\n\n" +
                      $"{question}\n(No = '{ctx.Profile.Label}' is re-applied next launch)";

        if (!NativeUi.AskYesNo("Optimus-Vibe", message)) return;
        var result = ctx.Save is { } s
            ? ManualSaves.SaveCurrent(ctx, s.Name, updateSlug: s.Slug)
            : ManualSaves.SaveCurrent(ctx, $"Tuned {DateTime.Now:MMM d HH:mm}");
        Log.Info($"Saved '{result.Save.Name}' ({result.KeysSaved} setting(s)){(result.Sync is { } g ? $"; sync: {g.Message}" : "")}");
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
