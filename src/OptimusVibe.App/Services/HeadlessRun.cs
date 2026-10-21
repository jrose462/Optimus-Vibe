using System.Diagnostics;
using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Launch;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>
/// `OptimusVibe.exe run &lt;game&gt; [--preset p] [--profile d] [--config dir] [--no-prompt]
/// [--verify] [--launch &lt;uri-or-exe&gt; [--launch-arg a]... [--install-dir d]] [-- %command%]`:
/// the Steam launch-option wrapper, without a window or console. Same behaviour as `optimus-vibe run`.
/// </summary>
public static class HeadlessRun
{
    public static bool IsRunInvocation(string[] args) =>
        args.Length > 0 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase);

    public static int Execute(string[] args)
    {
        var parsed = RunArgs.Parse(args);
        string? game = parsed.Game, preset = parsed.Preset, profile = parsed.Profile, config = parsed.Config, launch = parsed.Launch;
        var launchArgs = parsed.LaunchArgs;
        var command = parsed.Command;

        if (game is null)
        {
            Log.Error("run: missing game id. Usage: OptimusVibe.exe run <game> -- %command%");
            return command.Count == 0 && launch is not null ? Runner.LaunchPlain([launch, .. launchArgs]) : LaunchUnmanaged(command);
        }

        PrePull(config);

        GameContext ctx;
        try
        {
            ctx = GameContext.Load(AppPaths.Resolve(config), game, profile, preset is null ? null : PresetRef.Parse(preset),
                bigPicture: new BigPictureOptions { Suppress = parsed.NoBigPicture });
        }
        catch (Exception ex)
        {
            // Never block playing: if config is broken, still launch what Steam asked for.
            Log.Error($"Settings not applied for {game}: {ex.Message}");
            return command.Count == 0 && launch is not null ? Runner.LaunchPlain([launch, .. launchArgs]) : LaunchUnmanaged(command);
        }
        return Runner.Run(ctx, command.ToArray(), parsed.Prompt, parsed.ToOptions());
    }

    /// <summary>
    /// Best effort before the game starts: fetch new presets, but never hold the launch up (about 5 s at most, and not at all when the
    /// last pull was under 2 minutes ago). Turned off by Settings → Sync → Auto-update presets. Never throws.
    /// </summary>
    private static void PrePull(string? config)
    {
        try
        {
            var paths = AppPaths.Resolve(config);
            if (!AppPaths.LoadSettings(paths.DataDir).AutoPull) return;
            var r = OptimusVibe.Sync.AutoPull.Run(OptimusVibe.Sync.SyncProviders.For(paths), paths, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5));
            Log.Info($"pre-launch pull: {r.Status} ({r.Message}){(r.ChangedGames.Count > 0 ? $"; changed: {string.Join(", ", r.ChangedGames)}" : "")}");
        }
        catch (Exception ex)
        {
            Log.Warn($"pre-launch pull skipped: {ex.Message}");
        }
    }

    private static int LaunchUnmanaged(List<string> command)
    {
        if (command.Count == 0) return 2;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(command[0], command.Skip(1)) { UseShellExecute = false });
            p?.WaitForExit();
            return p?.ExitCode ?? 2;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start {command[0]}", ex);
            return 2;
        }
    }
}
