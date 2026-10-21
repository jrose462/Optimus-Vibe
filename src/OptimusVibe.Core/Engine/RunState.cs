using System.Diagnostics;
using System.Text.Json;
using OptimusVibe.Display;
using OptimusVibe.Driver;
using OptimusVibe.Logging;

namespace OptimusVibe.Engine;

/// <summary>
/// Temporary changes made by `run` (display mode, AMD global driver settings), persisted so they can be undone
/// on the next start if the wrapper is killed or the machine sleeps/crashes mid-game. One file per run
/// (<c>AppPaths.RunStateFile</c>); a part that could not be undone stays in its file and is retried on the next start.
/// </summary>
public sealed class RunState
{
    public int WrapperPid { get; set; }
    public DateTime StartedUtc { get; set; }
    public string Game { get; set; } = "";
    public DisplayModeState? Display { get; set; }
    public string? DriverVendor { get; set; }
    public string? DriverToken { get; set; }

    public bool IsEmpty => Display is null && DriverToken is null;

    public static RunState? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<RunState>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignoring unreadable run state {file}: {ex.Message}");
            return null;
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Undo everything recorded here. Each step is independent so one failure does not block the others.
    /// A restored part is cleared; a driver token that could not be fully restored is kept. True when nothing is left to undo.
    /// </summary>
    public bool Undo()
    {
        if (Display is not null)
        {
            try
            {
                DisplayManager.Restore(Display);
                Log.Info($"Display restored to {Display.Width}x{Display.Height}@{Display.Refresh}Hz");
            }
            catch (Exception ex) { Log.Error("Could not restore display mode", ex); }
            Display = null;
        }
        UndoDriver();
        return IsEmpty;
    }

    /// <summary>Restores the driver settings; the token is only cleared when the restore reported success.</summary>
    public void UndoDriver()
    {
        if (DriverToken is null || DriverVendor is null) return;
        try
        {
            if (DriverSettings.For(DriverVendor).Restore(DriverToken))
            {
                Log.Info($"{DriverVendor} driver settings restored");
                DriverToken = null;
                DriverVendor = null;
            }
            else Log.Warn($"{DriverVendor} driver settings were NOT fully restored; the restore token is kept and retried on the next start.");
        }
        catch (Exception ex) { Log.Error("Could not restore driver settings (the restore token is kept)", ex); }
    }

    /// <summary>The legacy single state file plus every per-run file next to it.</summary>
    private static IEnumerable<string> StateFiles(string legacyFile)
    {
        yield return legacyFile;
        var dir = Path.GetDirectoryName(legacyFile);
        if (dir is null || !Directory.Exists(dir)) yield break;
        string[] files;
        try { files = Directory.GetFiles(dir, "state-*.json"); }
        catch (Exception) { yield break; }
        foreach (var f in files) yield return f;
    }

    /// <summary>
    /// If a previous wrapper died without cleaning up, undo its changes now. A change that cannot be undone stays on disk
    /// (never deleted until restored) and is retried on the next start.
    /// </summary>
    public static void RecoverStale(string file)
    {
        foreach (var path in StateFiles(file))
        {
            var state = Load(path);
            if (state is null) continue;
            if (IsAlive(state.WrapperPid, state.StartedUtc)) continue; // another run is in progress

            Log.Warn($"Previous run of {state.Game} (started {state.StartedUtc.ToLocalTime():g}) did not clean up — restoring now");
            if (state.Undo())
            {
                try { File.Delete(path); } catch (Exception ex) { Log.Warn($"Could not delete {path}: {ex.Message}"); }
            }
            else
            {
                Log.Warn($"Part of the previous run of {state.Game} could not be restored; keeping {Path.GetFileName(path)} to retry");
                try { state.Save(path); } catch (Exception ex) { Log.Warn($"Could not update {path}: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// Another run (live, or dead with an unrestored leftover unless <paramref name="liveOnly"/>) whose state satisfies
    /// <paramref name="holds"/>; null when there is none, otherwise a description for the log. <paramref name="ownFile"/> is skipped.
    /// </summary>
    public static string? FindHolder(string legacyFile, string ownFile, Func<RunState, bool> holds, bool liveOnly = false)
    {
        var own = Path.GetFullPath(ownFile);
        foreach (var path in StateFiles(legacyFile))
        {
            if (string.Equals(Path.GetFullPath(path), own, StringComparison.OrdinalIgnoreCase)) continue;
            var s = Load(path);
            if (s is null || !holds(s)) continue;
            var alive = IsAlive(s.WrapperPid, s.StartedUtc);
            if (liveOnly && !alive) continue;
            return alive
                ? $"another run ({s.Game}, started {s.StartedUtc.ToLocalTime():g}) has changed it and has not restored it yet"
                : $"a previous run of {s.Game} left changes that could not be restored yet";
        }
        return null;
    }

    private static bool IsAlive(int pid, DateTime startedUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // pid reuse: a process that started after our state was written is not the wrapper
            return !p.HasExited && p.StartTime.ToUniversalTime() <= startedUtc.AddSeconds(5);
        }
        catch
        {
            return false;
        }
    }
}
