using System.Diagnostics;
using GameProfiles.Config;
using GameProfiles.Library;
using GameProfiles.Logging;

namespace GameProfiles.App.Services;

/// <summary>What a launch started: a message for the status bar and, when we own it, the process to wait on.</summary>
public sealed record LaunchHandle(string Message, Process? Process);

/// <summary>
/// Starts a game. Managed games go through this exe's own headless `run &lt;id&gt;`, which applies settings, launches
/// the game (launch.uri / launch.exe / steam://rungameid, or the detected install via --launch), and restores
/// temporary changes afterwards. Unmanaged games (opted out, no presets yet) start plainly through their launcher.
/// </summary>
public static class GameLauncher
{
    public static bool CanLaunch(GameDef game) =>
        game.SteamAppId is not null || game.Launch?.Uri is not null || game.Launch?.Exe is not null;

    public static bool CanLaunch(GameDef? game, DetectedGame? detected) =>
        (game is not null && CanLaunch(game)) || detected?.LaunchUri is not null || detected?.LaunchExe is not null;

    /// <summary>Manage view without library info: the original behaviour. Returns a short message for the status bar.</summary>
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

        RunHeadless(game.Id, null)?.Dispose();
        Log.Info($"Launched {game.Id} via headless run");
        return $"Launching {game.Name} — settings are applied first and restored after.";
    }

    /// <summary>
    /// Library play. <paramref name="managed"/>: apply the active preset through the headless wrapper; otherwise just
    /// start the game the way its launcher would.
    /// </summary>
    public static LaunchHandle Play(string name, GameDef? game, DetectedGame? detected, bool managed)
    {
        if (managed && game is not null)
        {
            var useInstall = !CanLaunch(game) ? detected : null;
            var p = RunHeadless(game.Id, useInstall);
            Log.Info($"Play {game.Id}: headless run{(useInstall is null ? "" : " with detected install " + useInstall.Key)}");
            return new LaunchHandle($"Starting {name} — your preset is applied first and restored after.", p);
        }

        if (detected?.LaunchUri is { } uri)
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            Log.Info($"Play {detected.Key}: {uri}");
            return new LaunchHandle($"Starting {name}.", null);
        }
        if (detected?.LaunchExe is { } exe)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = detected.InstallDir ?? Path.GetDirectoryName(exe) ?? "",
            };
            foreach (var a in detected.LaunchArgs) psi.ArgumentList.Add(a);
            var proc = Process.Start(psi);
            Log.Info($"Play {detected.Key}: {exe} {string.Join(' ', detected.LaunchArgs)}");
            return new LaunchHandle($"Starting {name}.", proc);
        }
        if (game is not null && CanLaunch(game))
        {
            // Configured but not detected, and not managed here: start it without our wrapper.
            if (game.SteamAppId is { } appId)
                Process.Start(new ProcessStartInfo($"steam://rungameid/{appId}") { UseShellExecute = true })?.Dispose();
            else if (game.Launch?.Uri is { } u)
                Process.Start(new ProcessStartInfo(u) { UseShellExecute = true })?.Dispose();
            else
                Process.Start(new ProcessStartInfo(game.Launch!.Exe!, game.Launch.Args) { UseShellExecute = false })?.Dispose();
            return new LaunchHandle($"Starting {name}.", null);
        }
        throw new InvalidOperationException($"Don't know how to start {name}: no launcher entry was found for it.");
    }

    /// <summary>
    /// `GameProfilesApp.exe run &lt;id&gt;`; when the yaml has no launch target, the detected install is passed as
    /// `--launch &lt;uri-or-exe&gt; [--launch-arg a]... --install-dir &lt;dir&gt;`.
    /// </summary>
    private static Process? RunHeadless(string gameId, DetectedGame? install)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the app's own path");
        var psi = new ProcessStartInfo(self) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(gameId);
        if (install is not null && (install.LaunchUri ?? install.LaunchExe) is { } target)
        {
            psi.ArgumentList.Add("--launch");
            psi.ArgumentList.Add(target);
            if (install.LaunchUri is null)
                foreach (var a in install.LaunchArgs)
                {
                    psi.ArgumentList.Add("--launch-arg");
                    psi.ArgumentList.Add(a);
                }
            if (install.InstallDir is { Length: > 0 } dir)
            {
                psi.ArgumentList.Add("--install-dir");
                psi.ArgumentList.Add(dir);
            }
        }
        return Process.Start(psi);
    }
}
