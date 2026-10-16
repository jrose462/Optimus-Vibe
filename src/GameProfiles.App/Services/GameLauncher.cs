using System.Diagnostics;
using GameProfiles.Config;
using GameProfiles.Logging;

namespace GameProfiles.App.Services;

/// <summary>
/// Starts a game. Steam games go through steam://rungameid so Steam's launch options (the gameprofiles wrapper)
/// apply the active preset. Others go through `gameprofiles.exe run &lt;id&gt;` next to the app when present, which
/// applies settings itself; otherwise launch.uri / launch.exe is started directly (settings not applied).
/// </summary>
public static class GameLauncher
{
    public static string CliPath => Path.Combine(AppContext.BaseDirectory, "gameprofiles.exe");

    public static bool CanLaunch(GameDef game) =>
        game.SteamAppId is not null || game.Launch?.Uri is not null || game.Launch?.Exe is not null;

    /// <summary>Returns a short message for the status bar.</summary>
    public static string Launch(GameDef game)
    {
        if (game.SteamAppId is { } appId)
        {
            Shell($"steam://rungameid/{appId}");
            Log.Info($"Launched {game.Id} via Steam ({appId})");
            return $"Launching {game.Name} through Steam — its launch options apply the active preset.";
        }

        if (File.Exists(CliPath) && (game.Launch?.Uri is not null || game.Launch?.Exe is not null))
        {
            var psi = new ProcessStartInfo(CliPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(game.Id);
            Process.Start(psi);
            Log.Info($"Launched {game.Id} via gameprofiles run");
            return $"Launching {game.Name} — settings are applied first and restored after.";
        }

        if (game.Launch?.Uri is { } uri)
        {
            Shell(uri);
            return $"Launching {game.Name} (settings not applied: gameprofiles.exe not found next to the app).";
        }
        if (game.Launch?.Exe is { } exe)
        {
            var path = new PathResolver().Expand(exe);
            var psi = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) ?? "" };
            foreach (var a in game.Launch.Args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return $"Launching {game.Name} (settings not applied: gameprofiles.exe not found next to the app).";
        }
        throw new InvalidOperationException($"{game.Name} has no steamAppId or launch: entry to start it with.");
    }

    private static void Shell(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
