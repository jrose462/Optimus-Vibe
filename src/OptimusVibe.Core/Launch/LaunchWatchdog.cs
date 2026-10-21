using OptimusVibe.Logging;

namespace OptimusVibe.Launch;

/// <summary>Where a launch is: the wrapper started something, a launcher is up but the game is not, or the game runs.</summary>
public enum LaunchPhase
{
    Launching,
    WaitingOnLauncher,
    GameRunning,
    Exited,
    GaveUp,
    Failed,
    Cancelled,
}

/// <summary>How a watch ended.</summary>
public sealed record WatchOutcome(bool GameSeen, LaunchPhase Phase, string? Reason);

/// <summary>Everything the watchdog needs from the outside, so tests can fake processes and time.</summary>
public sealed class WatchdogInputs
{
    public required Func<bool> GameRunning { get; init; }
    public Func<bool> LauncherRunning { get; init; } = () => false;

    /// <summary>The process we started has exited (or there is none), so only the deadline can end the wait before the game appears.</summary>
    public Func<bool> StarterGone { get; init; } = () => true;

    /// <summary>A launcher-log probe: given the time since launch, a reason the game will never start, or null.</summary>
    public Func<TimeSpan, string?>? EarlyFailure { get; init; }

    /// <summary>Called once when the game is first seen; gets a "still running" check. Never throws out of the watchdog.</summary>
    public Action<Func<bool>>? OnGameDetected { get; init; }

    public Action<LaunchPhase, string>? OnStatus { get; init; }
    public required TimeSpan StartupTimeout { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>Sleeps one poll; returns true when cancelled. Default waits on the token.</summary>
    public Func<TimeSpan, bool>? Sleep { get; init; }
}

/// <summary>
/// The wait loop of a launch: Launching -> WaitingOnLauncher (launcher alive, no game) -> GameRunning -> Exited. Gives up only after the
/// startup timeout, fails early when a launcher log proves the game was never started, and ends at once when cancelled.
/// </summary>
public static class LaunchWatchdog
{
    public static WatchOutcome Watch(WatchdogInputs w, CancellationToken cancel = default)
    {
        var begun = w.Now();
        var seen = false;
        var quietPolls = 0;
        var phase = LaunchPhase.Launching;
        w.OnStatus?.Invoke(LaunchPhase.Launching, "Starting the game");

        while (true)
        {
            if (cancel.IsCancellationRequested) return new WatchOutcome(seen, LaunchPhase.Cancelled, "cancelled");

            var running = Safe(w.GameRunning);
            if (running)
            {
                if (!seen)
                {
                    seen = true;
                    Report(w, ref phase, LaunchPhase.GameRunning, "Game is running");
                    if (w.OnGameDetected is { } cb)
                    {
                        try { cb(() => Safe(w.GameRunning)); }
                        catch (Exception ex) { Log.Error("Applying settings after the game started failed", ex); }
                    }
                }
                quietPolls = 0;
            }
            else if (seen)
            {
                // two quiet polls in a row, so a game that restarts itself (e.g. after an anti-cheat bootstrap) is not missed
                if (++quietPolls >= 2) return new WatchOutcome(true, LaunchPhase.Exited, null);
            }
            else
            {
                var elapsed = w.Now() - begun;
                if (Safe(w.LauncherRunning))
                    Report(w, ref phase, LaunchPhase.WaitingOnLauncher, "Waiting on the launcher - answer its prompt if it shows one");

                if (w.EarlyFailure is { } probe)
                {
                    string? why = null;
                    try { why = probe(elapsed); }
                    catch (Exception ex) { Log.Warn($"Launcher log check failed: {ex.Message}"); }
                    if (why is not null)
                    {
                        Report(w, ref phase, LaunchPhase.Failed, why);
                        return new WatchOutcome(false, LaunchPhase.Failed, why);
                    }
                }

                if (elapsed > w.StartupTimeout && Safe(w.StarterGone))
                {
                    var reason = $"The game process never appeared within {w.StartupTimeout.TotalMinutes:0.#} minutes";
                    Report(w, ref phase, LaunchPhase.GaveUp, reason);
                    return new WatchOutcome(false, LaunchPhase.GaveUp, reason);
                }
            }

            if (w.Sleep is { } sleep ? sleep(w.PollInterval) : cancel.WaitHandle.WaitOne(w.PollInterval))
                return new WatchOutcome(seen, LaunchPhase.Cancelled, "cancelled");
        }
    }

    private static void Report(WatchdogInputs w, ref LaunchPhase current, LaunchPhase next, string message)
    {
        if (current == next) return;
        current = next;
        Log.Info($"launch: {message}");
        w.OnStatus?.Invoke(next, message);
    }

    private static bool Safe(Func<bool> probe)
    {
        try { return probe(); }
        catch (Exception ex)
        {
            Log.Warn($"Process check failed: {ex.Message}");
            return false;
        }
    }
}
