using System.Diagnostics;
using GameProfiles.Config;
using GameProfiles.Engine;
using GameProfiles.Launch;
using GameProfiles.Logging;

namespace GameProfiles.App.Services;

/// <summary>
/// `GameProfilesApp.exe run &lt;game&gt; [--preset p] [--profile d] [--config dir] [--no-prompt] [-- %command%]`:
/// the Steam launch-option wrapper, without a window or console. Same behaviour as `gameprofiles run`.
/// </summary>
public static class HeadlessRun
{
    public static bool IsRunInvocation(string[] args) =>
        args.Length > 0 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase);

    public static int Execute(string[] args)
    {
        string? game = null, preset = null, profile = null, config = null;
        bool prompt = true;
        var command = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                command.AddRange(args.Skip(i + 1));
                break;
            }
            switch (a.ToLowerInvariant())
            {
                case "--preset" when i + 1 < args.Length: preset = args[++i]; break;
                case "--profile" when i + 1 < args.Length: profile = args[++i]; break;
                case "--config" when i + 1 < args.Length: config = args[++i]; break;
                case "--no-prompt": prompt = false; break;
                default:
                    if (game is null && !a.StartsWith("--")) game = a;
                    else command.Add(a); // tolerate a missing "--" before the game command line
                    break;
            }
        }

        if (game is null)
        {
            Log.Error("run: missing game id. Usage: GameProfilesApp.exe run <game> -- %command%");
            return LaunchUnmanaged(command);
        }

        GameContext ctx;
        try
        {
            ctx = GameContext.Load(AppPaths.Resolve(config), game, profile, preset is null ? null : PresetRef.Parse(preset));
        }
        catch (Exception ex)
        {
            // Never block playing: if config is broken, still launch what Steam asked for.
            Log.Error($"Settings not applied for {game}: {ex.Message}");
            return LaunchUnmanaged(command);
        }
        return Runner.Run(ctx, command.ToArray(), prompt);
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
