using System.Diagnostics;
using GameProfiles.Config;
using GameProfiles.Display;
using GameProfiles.Driver;
using GameProfiles.Engine;
using GameProfiles.Logging;
using GameProfiles.Ui;

namespace GameProfiles.Launch;

/// <summary>
/// Launch wrapper (Apollo-style do/undo): apply settings → launch → wait for the game → undo temporary changes
/// → offer to capture anything changed in-game. Settings failures never stop the game from launching.
/// </summary>
public static class Runner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    public static int Run(GameContext ctx, string[] command, bool prompt = true)
    {
        RunState.RecoverStale(ctx.Paths.StateFile);
        Log.Info($"=== run {ctx.Game.Id} as '{ctx.Device.Name}' ({ctx.Device.Reason}) ===");

        AppliedState? applied = null;
        try
        {
            applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        }
        catch (Exception ex)
        {
            Log.Error("Applying game settings failed — launching anyway", ex);
        }

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
            exitCode = LaunchAndWait(ctx, command);
        }
        finally
        {
            state.Undo();
            if (File.Exists(ctx.Paths.StateFile)) File.Delete(ctx.Paths.StateFile);
        }

        if (applied is not null && prompt) OfferCapture(ctx, applied);
        Log.Info($"=== {ctx.Game.Id} exited ({exitCode}) ===");
        return exitCode;
    }

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

    private static int LaunchAndWait(GameContext ctx, string[] command)
    {
        using var started = Start(ctx, command);
        var watched = ctx.Game.Processes.Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();

        if (watched.Length == 0)
        {
            if (started is null)
            {
                Log.Warn("Launched via URI with no `processes:` configured — cannot tell when the game exits; display/driver changes are undone now");
                return 0;
            }
            started.WaitForExit();
            return started.ExitCode;
        }

        // Launchers and Steam stubs often exit before (or while) the real game runs: wait on the game's process names.
        var deadline = DateTime.UtcNow + StartupTimeout;
        bool seen = false;
        int quietPolls = 0;
        while (true)
        {
            bool running = watched.Any(IsRunning);
            if (running)
            {
                if (!seen) Log.Info($"Game process detected ({string.Join(", ", ctx.Game.Processes)})");
                seen = true;
                quietPolls = 0;
            }
            else if (seen)
            {
                // two quiet polls in a row, so a game that restarts itself (e.g. after an anti-cheat bootstrap) is not missed
                if (++quietPolls >= 2) break;
            }
            else if (started is { HasExited: true } && DateTime.UtcNow > deadline)
            {
                Log.Warn($"Game process never appeared within {StartupTimeout.TotalMinutes:0} minutes");
                break;
            }
            else if (started is null && DateTime.UtcNow > deadline)
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

    private static bool IsRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    private static Process? Start(GameContext ctx, string[] command)
    {
        if (command.Length > 0)
        {
            Log.Info($"Launching: {string.Join(' ', command)}");
            return StartExe(command[0], command.Skip(1));
        }

        var launch = ctx.Game.Launch;
        if (!string.IsNullOrEmpty(launch?.Exe))
        {
            var exe = new PathResolver().Expand(launch.Exe);
            Log.Info($"Launching: {exe} {string.Join(' ', launch.Args)}");
            return StartExe(exe, launch.Args);
        }

        var uri = launch?.Uri ?? (ctx.Game.SteamAppId is { } id ? $"steam://rungameid/{id}" : null)
                  ?? throw new ConfigException($"{ctx.Game.Id}: nothing to launch — pass `-- %command%` or set launch.exe/launch.uri");
        Log.Info($"Launching: {uri}");
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        return null;
    }

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
        var message = $"{drift.Count} setting(s) changed while playing {ctx.Game.Name}:\n\n{string.Join("\n", lines)}{more}\n\n" +
                      $"Save them as the '{ctx.Device.Name}' settings for this game?\n(No = they will be reset next launch)";

        if (!NativeUi.AskYesNo("GameProfiles", message)) return;
        var files = drift.Select(d => d.FileId).ToHashSet();
        var snapshotFiles = drift.Where(d => d.Applied is null).Select(d => d.FileId).ToHashSet();
        var result = GameEngine.Capture(ctx, snapshot: false, files.Except(snapshotFiles).ToList());
        if (snapshotFiles.Count > 0) GameEngine.Capture(ctx, snapshot: true, snapshotFiles);
        Log.Info($"Captured {result.KeysCaptured} setting(s){(snapshotFiles.Count > 0 ? $" and {snapshotFiles.Count} snapshot(s)" : "")} for {ctx.Device.Name}");
    }
}
