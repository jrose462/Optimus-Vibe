using System.Diagnostics;
using GameProfiles.Config;
using GameProfiles.Logging;

namespace GameProfiles.App.Services;

/// <summary>
/// Starts a game. Steam games go through steam://rungameid so Steam's launch options (the GameProfilesApp `run`
/// wrapper) apply the active preset. Others are started through this exe's own headless `run &lt;id&gt;`, which
/// applies settings, launches launch.uri / launch.exe, and restores temporary changes afterwards.
/// </summary>
public static class GameLauncher
{
    public static bool CanLaunch(GameDef game) =>
        game.SteamAppId is not null || game.Launch?.Uri is not null || game.Launch?.Exe is not null;

    /// <summary>Returns a short message for the status bar.</summary>
    public static string Launch(GameDef game)
    {
        if (game.SteamAppId is { } appId)
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{appId}") { UseShellExecute = true })?.Dispose();
            Log.Info($"Launched {game.Id} via Steam ({appId})");
            return $"Launching {game.Name} through Steam — its launch options apply the active preset.";
        }

        if (game.Launch?.Uri is null && game.Launch?.Exe is null)
            throw new InvalidOperationException($"{game.Name} has no steamAppId or launch: entry to start it with.");

        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the app's own path");
        var psi = new ProcessStartInfo(self) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(game.Id);
        Process.Start(psi)?.Dispose();
        Log.Info($"Launched {game.Id} via headless run");
        return $"Launching {game.Name} — settings are applied first and restored after.";
    }
}
