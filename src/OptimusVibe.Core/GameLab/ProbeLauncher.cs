using OptimusVibe.Config;
using OptimusVibe.Launch;

namespace OptimusVibe.GameLab;

/// <summary>
/// Starts a game for a mapping pass exactly as it is (nothing applied, no display change, no prompt) and waits for it to exit. Works for
/// games with no config at all: the launch target and process names then come from the launcher scan.
/// </summary>
public static class ProbeLauncher
{
    /// <summary>The game as the runner needs it: the config's, or a stand-in built from the scan (Steam appid, exe names).</summary>
    public static GameDef GameFor(LabTarget t)
    {
        var g = t.Game;
        var processes = g is { Processes.Count: > 0 } ? g.Processes.ToList() : t.Exes.ToList();
        return new GameDef
        {
            Id = g?.Id ?? SafeId(t),
            Name = t.Name,
            Store = g?.Store ?? t.Store,
            SteamAppId = g?.SteamAppId ?? (t.Store.Equals("steam", StringComparison.OrdinalIgnoreCase) && int.TryParse(t.StoreId, out var id) ? id : null),
            Launch = g?.Launch,
            Processes = processes,
        };
    }

    /// <summary>Launch inputs from the scan, used only when the config (or Steam) does not say how to start the game.</summary>
    public static RunOptions OptionsFor(LabTarget t, GameDef game)
    {
        var configured = game.SteamAppId is not null || game.Launch?.Uri is not null || game.Launch?.Exe is not null;
        var d = t.Detected;
        var target = configured ? null : d?.LaunchUri ?? d?.LaunchExe;
        return new RunOptions
        {
            Probe = true,
            Launch = target,
            LaunchArgs = target is not null && d?.LaunchUri is null ? d!.LaunchArgs : [],
            InstallDir = t.InstallDir,
        };
    }

    /// <summary>Blocks until the game has exited. Returns the exit code.</summary>
    public static int Launch(LabTarget t, string dataDir)
    {
        var game = GameFor(t);
        return Runner.RunProbe(game, dataDir, [], OptionsFor(t, game));
    }

    private static string SafeId(LabTarget t) =>
        new string($"{t.Store}-{t.StoreId}".Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? char.ToLowerInvariant(c) : '-').ToArray());
}
