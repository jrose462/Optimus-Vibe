using System.Diagnostics;
using System.Text.Json;
using OptimusVibe.Display;
using OptimusVibe.Driver;
using OptimusVibe.Logging;

namespace OptimusVibe.Engine;

/// <summary>
/// Temporary changes made by `run` (display mode, AMD global driver settings), persisted so they can be undone
/// on the next start if the wrapper is killed or the machine sleeps/crashes mid-game.
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

    /// <summary>Undo everything recorded here. Each step is independent so one failure does not block the others.</summary>
    public void Undo()
    {
        if (Display is not null)
        {
            try
            {
                DisplayManager.Restore(Display);
                Log.Info($"Display restored to {Display.Width}x{Display.Height}@{Display.Refresh}Hz");
            }
            catch (Exception ex) { Log.Error("Could not restore display mode", ex); }
        }
        if (DriverToken is not null && DriverVendor is not null)
        {
            try
            {
                DriverSettings.For(DriverVendor).Restore(DriverToken);
                Log.Info($"{DriverVendor} driver settings restored");
            }
            catch (Exception ex) { Log.Error("Could not restore driver settings", ex); }
        }
    }

    /// <summary>If a previous wrapper died without cleaning up, undo its changes now.</summary>
    public static void RecoverStale(string file)
    {
        var state = Load(file);
        if (state is null) return;
        if (IsAlive(state.WrapperPid, state.StartedUtc)) return; // another run is in progress

        Log.Warn($"Previous run of {state.Game} (started {state.StartedUtc.ToLocalTime():g}) did not clean up — restoring now");
        state.Undo();
        File.Delete(file);
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
