using System.CommandLine;
using System.Diagnostics;
using System.Text;
using GameProfiles.Config;
using GameProfiles.Devices;
using GameProfiles.Display;
using GameProfiles.Driver;
using GameProfiles.Engine;
using GameProfiles.Handlers;
using GameProfiles.Launch;
using GameProfiles.Logging;
using GameProfiles.Ui;

namespace GameProfiles;

public static class Program
{
    private static readonly Option<string?> ConfigOption = new("--config")
    {
        Description = "Config folder (the repo's config\\ dir). Defaults to settings.json / GAMEPROFILES_CONFIG / next to the exe.",
        Recursive = true,
    };

    private static readonly Option<string?> ProfileOption = new("--profile")
    {
        Description = "Force a device profile (e.g. desktop, ally-x) instead of auto-detecting.",
        Recursive = true,
    };

    private static readonly Argument<string> GameArg = new("game") { Description = "Game id (file name in config\\games without .yaml)" };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.Init(Path.Combine(AppPaths.DefaultDataDir, "logs"));

        var root = new RootCommand("Applies per-device graphics settings to games (desktop vs handheld) from a shared config repo.")
        {
            ConfigOption, ProfileOption,
            RunCommand(), ApplyCommand(), CaptureCommand(), RestoreCommand(), DetectCommand(), InspectCommand(),
            ListCommand(), ValidateCommand(), UpdateCommand(), ShortcutCommand(), SetupCommand(),
        };
        return root.Parse(args).Invoke();
    }

    /// <summary>Wraps a verb so config errors print one clean line instead of a stack trace.</summary>
    private static Func<ParseResult, int> Guard(Func<ParseResult, int> action) => pr =>
    {
        try
        {
            return action(pr);
        }
        catch (Exception ex) when (ex is ConfigException or PathResolveException)
        {
            Log.Error(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Log.Error("Unexpected error", ex);
            return 1;
        }
    };

    private static AppPaths Paths(ParseResult pr) => AppPaths.Resolve(pr.GetValue(ConfigOption));

    private static GameContext Context(ParseResult pr) =>
        GameContext.Load(Paths(pr), pr.GetValue(GameArg)!, pr.GetValue(ProfileOption));

    // ---------------------------------------------------------------- run

    private static Command RunCommand()
    {
        var commandArg = new Argument<string[]>("command")
        {
            Description = "The game command line. In Steam launch options use: run <game> -- %command%",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var noPrompt = new Option<bool>("--no-prompt") { Description = "Don't offer to save settings changed in-game." };
        var cmd = new Command("run", "Apply settings, launch the game, wait for it to exit, then undo temporary changes.")
        {
            GameArg, commandArg, noPrompt,
        };
        cmd.SetAction(Guard(pr =>
        {
            NativeUi.HideConsoleIfOwned();
            var command = pr.GetValue(commandArg) ?? [];
            GameContext ctx;
            try
            {
                ctx = Context(pr);
            }
            catch (Exception ex)
            {
                // Never block playing: if config is broken, still launch what Steam asked for.
                Log.Error($"Settings not applied: {ex.Message}");
                if (command.Length == 0) return 2;
                using var p = Process.Start(new ProcessStartInfo(command[0], command.Skip(1)) { UseShellExecute = false });
                p?.WaitForExit();
                return p?.ExitCode ?? 2;
            }
            return Runner.Run(ctx, command, prompt: !pr.GetValue(noPrompt));
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- apply

    private static Command ApplyCommand()
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would change without writing anything." };
        var cmd = new Command("apply", "Write this device's settings into the game's config files now (no launch).") { GameArg, dryRun };
        cmd.SetAction(Guard(pr =>
        {
            var ctx = Context(pr);
            bool dry = pr.GetValue(dryRun);
            PrintHeader(ctx);
            var plans = GameEngine.Plan(ctx);
            PrintPlan(plans);

            if (ctx.Profile.Display is not null)
                Console.WriteLine($"  display: {ctx.Profile.Display} (applied only by `run`, restored on exit)");

            var driver = DriverSettings.For(ctx.Device.Driver);
            if (ctx.DriverTargets.Count > 0)
            {
                if (!driver.IsAvailable(out var reason))
                    Console.WriteLine($"  {ctx.Device.Driver} driver: skipped — {reason}");
                else
                    foreach (var c in driver.Plan(ctx.Game, ctx.DriverTargets))
                        Console.WriteLine($"  {driver.Vendor}: {c.Setting}: {c.Current ?? "(unset)"} -> {c.Target}");
            }

            if (dry)
            {
                Console.WriteLine("Dry run — nothing written.");
                return 0;
            }

            GameEngine.Apply(ctx, plans);
            if (ctx.DriverTargets.Count > 0 && driver.Vendor == "nvidia" && driver.IsAvailable(out _))
                driver.Apply(ctx.Game, ctx.DriverTargets); // NVIDIA per-app profiles persist; AMD global settings are run-only
            Console.WriteLine("Done.");
            return 0;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- capture

    private static Command CaptureCommand()
    {
        var snapshot = new Option<bool>("--snapshot") { Description = "Store whole files instead of mapped keys." };
        var cmd = new Command("capture", "Save the game's current settings as this device's settings (tune in-game, then capture).")
        {
            GameArg, snapshot,
        };
        cmd.SetAction(Guard(pr =>
        {
            var ctx = Context(pr);
            PrintHeader(ctx);
            var result = GameEngine.Capture(ctx, pr.GetValue(snapshot));
            Console.WriteLine($"Captured {result.KeysCaptured} setting(s) that differ from the base profile -> " +
                              ctx.Paths.CaptureFile(ctx.Game.Id, ctx.Device.Name));
            foreach (var s in result.Snapshots)
                Console.WriteLine($"Snapshot of '{s}' -> {ctx.Paths.SnapshotDir(ctx.Game.Id, ctx.Device.Name)}");
            foreach (var s in result.Skipped) Console.WriteLine($"Skipped {s}");
            Console.WriteLine("Commit the config repo to share this with your other machines.");
            return 0;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- restore

    private static Command RestoreCommand()
    {
        var original = new Option<bool>("--original") { Description = "Restore the files as they were before GameProfiles first touched them." };
        var stamp = new Option<string?>("--timestamp") { Description = "Restore a specific backup (see the list printed without options)." };
        var cmd = new Command("restore", "Roll a game's config files back from a local backup.") { GameArg, original, stamp };
        cmd.SetAction(Guard(pr =>
        {
            var ctx = Context(pr);
            var store = new BackupStore(ctx.Paths.BackupsDir);
            var name = pr.GetValue(original) ? BackupStore.Original : pr.GetValue(stamp);
            if (name is null)
            {
                var list = store.List(ctx.Game.Id);
                Console.WriteLine(list.Count == 0 ? "No backups yet." : "Backups (newest first):");
                foreach (var b in list) Console.WriteLine($"  {b}");
                Console.WriteLine("Use --original or --timestamp <name>.");
                return 0;
            }
            store.Restore(ctx.Game.Id, name);
            Console.WriteLine($"Restored {ctx.Game.Name} from backup '{name}'. The next `run` will re-apply the profile.");
            return 0;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- detect

    private static Command DetectCommand()
    {
        var cmd = new Command("detect", "Show detected hardware and which device profile this machine uses.");
        cmd.SetAction(Guard(pr =>
        {
            var hw = DeviceDetector.Detect();
            Console.WriteLine($"manufacturer: {hw.Manufacturer}");
            Console.WriteLine($"model:        {hw.Model}");
            Console.WriteLine($"cpu:          {hw.Cpu}");
            foreach (var g in hw.Gpus) Console.WriteLine($"gpu:          {g}");
            foreach (var g in hw.IgnoredAdapters) Console.WriteLine($"ignored:      {g} (virtual adapter)");
            Console.WriteLine($"machine:      {Environment.MachineName}");
            var mode = DisplayManager.GetCurrent();
            if (mode is not null) Console.WriteLine($"display:      {mode.Width}x{mode.Height}@{mode.Refresh}Hz ({mode.DeviceName})");

            try
            {
                var store = new ConfigStore(Paths(pr));
                var device = GameContext.SelectDevice(store, pr.GetValue(ProfileOption));
                Console.WriteLine($"profile:      {device.Name} ({device.Reason})");
                var driver = DriverSettings.For(device.Driver);
                Console.WriteLine($"driver:       {device.Driver} — {(driver.IsAvailable(out var reason) ? "available" : reason)}");
            }
            catch (ConfigException ex)
            {
                Console.WriteLine($"profile:      none — {ex.Message}");
                return 1;
            }
            return 0;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- inspect

    private static Command InspectCommand()
    {
        var full = new Option<bool>("--full") { Description = "Print whole files instead of the first 200 lines." };
        var cmd = new Command("inspect", "Show resolved paths, current values and raw file contents (input for mapping a game).")
        {
            GameArg, full,
        };
        cmd.SetAction(Guard(pr =>
        {
            var ctx = Context(pr);
            PrintHeader(ctx);
            foreach (var f in ctx.Files)
            {
                Console.WriteLine();
                Console.WriteLine($"## {f.Id} [{f.Handler.Name}] {f.Def.Path}");
                if (f.Path is null) { Console.WriteLine($"   unresolved: {f.Error}"); continue; }
                bool exists = f.Handler.Exists(f.Path);
                Console.WriteLine($"   -> {f.Path} {(exists ? "" : "(MISSING)")}");
                if (f.SnapshotFile is not null) Console.WriteLine($"   snapshot: {f.SnapshotFile}");
                if (!exists) continue;

                if (f.Targets.Count > 0)
                {
                    var current = f.Handler.Read(f.Def, f.Path, f.Targets.Keys);
                    foreach (var (key, target) in f.Targets)
                    {
                        var now = current.GetValueOrDefault(key);
                        var mark = Values.Equivalent(now, target) ? "=" : "≠";
                        Console.WriteLine($"   {mark} {key}: now {now ?? "(missing)"}, profile {target}");
                    }
                }

                Console.WriteLine("   --- contents ---");
                if (!f.Handler.IsFile)
                {
                    Console.WriteLine(Encoding.UTF8.GetString(f.Handler.Snapshot(f.Path) ?? []));
                    continue;
                }
                var lines = TextFile.ReadAllText(f.Path).Split('\n');
                int max = pr.GetValue(full) ? int.MaxValue : 200;
                foreach (var line in lines.Take(max)) Console.WriteLine("   " + line.TrimEnd('\r'));
                if (lines.Length > max) Console.WriteLine($"   ... {lines.Length - max} more lines (use --full)");
            }
            return 0;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- list / validate

    private static Command ListCommand()
    {
        var cmd = new Command("list", "List configured games and their profiles.");
        cmd.SetAction(Guard(pr =>
        {
            var store = new ConfigStore(Paths(pr));
            string? device = null;
            try { device = GameContext.SelectDevice(store, pr.GetValue(ProfileOption)).Name; } catch (ConfigException) { }
            Console.WriteLine($"Config: {store.Paths.ConfigRoot}   This device: {device ?? "(unknown)"}");
            foreach (var id in store.GameIds())
            {
                try
                {
                    var g = store.LoadGame(id);
                    var profiles = string.Join(", ", g.Profiles.Keys);
                    var captured = device is not null && File.Exists(store.Paths.CaptureFile(g.Id, device)) ? " [captured]" : "";
                    Console.WriteLine($"  {g.Id,-24} {g.Name,-32} {g.Store,-9} profiles: {profiles}{captured}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {id,-24} ERROR: {ex.Message}");
                }
            }
            return 0;
        }));
        return cmd;
    }

    private static Command ValidateCommand()
    {
        var cmd = new Command("validate", "Check devices.yaml and every game file for mistakes.");
        cmd.SetAction(Guard(pr =>
        {
            var store = new ConfigStore(Paths(pr));
            var report = Validator.Run(store);
            foreach (var w in report.Warnings) Log.Warn(w);
            foreach (var e in report.Errors) Log.Error(e);
            Console.WriteLine($"{report.GamesChecked} game(s) checked: {report.Errors.Count} error(s), {report.Warnings.Count} warning(s).");
            return report.Errors.Count == 0 ? 0 : 1;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- update / shortcut / setup

    private static Command UpdateCommand()
    {
        var cmd = new Command("update", "git pull the config repo (fast-forward only).");
        cmd.SetAction(Guard(pr =>
        {
            var root = Paths(pr).ConfigRoot;
            var psi = new ProcessStartInfo("git") { UseShellExecute = false };
            foreach (var a in new[] { "-C", root, "pull", "--ff-only" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("git not found on PATH");
            p.WaitForExit();
            return p.ExitCode;
        }));
        return cmd;
    }

    private static Command ShortcutCommand()
    {
        var outDir = new Option<string?>("--out") { Description = "Folder for the .lnk (default: Desktop)." };
        var cmd = new Command("shortcut", "Create a shortcut that launches the game through `run` (for Battle.net/EA, or to add to Steam as a non-Steam game).")
        {
            GameArg, outDir,
        };
        cmd.SetAction(Guard(pr =>
        {
            var ctx = Context(pr);
            var dir = pr.GetValue(outDir) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var lnk = Path.Combine(dir, $"{ctx.Game.Name} (GameProfiles).lnk");
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine own exe path");

            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = exe;
            shortcut.Arguments = $"run {ctx.Game.Id}";
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            var icon = ctx.Game.Launch?.Exe is { } launchExe ? new PathResolver().Expand(launchExe) : null;
            if (icon is not null && File.Exists(icon)) shortcut.IconLocation = icon + ",0";
            shortcut.Description = $"{ctx.Game.Name} with per-device settings";
            shortcut.Save();
            Console.WriteLine($"Created {lnk}");
            if (ctx.Game.Launch is null && ctx.Game.SteamAppId is null)
                Log.Warn("This game has no launch.exe/launch.uri, so the shortcut has nothing to start.");
            return 0;
        }));
        return cmd;
    }

    private static Command SetupCommand()
    {
        var root = new Option<string?>("--config-root") { Description = "Path to the config folder of your GameProfiles repo checkout." };
        var force = new Option<string?>("--force-profile") { Description = "Always use this device profile on this machine." };
        var clear = new Option<bool>("--clear-force") { Description = "Go back to auto-detecting the device." };
        var cmd = new Command("setup", "One-time per-machine settings (stored in %LOCALAPPDATA%\\GameProfiles\\settings.json).")
        {
            root, force, clear,
        };
        cmd.SetAction(Guard(pr =>
        {
            var dataDir = AppPaths.DefaultDataDir;
            var settings = AppPaths.LoadSettings(dataDir);
            if (pr.GetValue(root) is { } r)
            {
                var full = Path.GetFullPath(r);
                if (!File.Exists(Path.Combine(full, "devices.yaml")))
                    throw new ConfigException($"{full} has no devices.yaml — point at the repo's config folder");
                settings.ConfigRoot = full;
            }
            if (pr.GetValue(force) is { } f) settings.ForcedProfile = f;
            if (pr.GetValue(clear)) settings.ForcedProfile = null;
            AppPaths.SaveSettings(dataDir, settings);
            Console.WriteLine($"Settings: {AppPaths.SettingsFile(dataDir)}");
            Console.WriteLine($"  configRoot:    {settings.ConfigRoot ?? "(auto)"}");
            Console.WriteLine($"  forcedProfile: {settings.ForcedProfile ?? "(auto-detect)"}");
            return 0;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- output helpers

    private static void PrintHeader(GameContext ctx)
    {
        Console.WriteLine($"{ctx.Game.Name} — device '{ctx.Device.Name}' ({ctx.Device.Reason})");
        if (!ctx.Profile.HasProfile) Log.Warn($"No profiles.{ctx.Device.Name} in games/{ctx.Game.Id}.yaml");
        if (ctx.Profile.HasCapture) Console.WriteLine($"  + captured overrides: {ctx.Paths.CaptureFile(ctx.Game.Id, ctx.Device.Name)}");
    }

    private static void PrintPlan(List<FilePlan> plans)
    {
        foreach (var p in plans)
        {
            var f = p.File;
            if (p.Skip is not null)
            {
                Console.WriteLine($"  {f.Id}: skipped — {p.Skip}");
                continue;
            }
            Console.WriteLine($"  {f.Id}: {f.Path}");
            if (p.RestoreSnapshot) Console.WriteLine("    restore captured snapshot");
            if (p.Changes.Count == 0 && !p.RestoreSnapshot) Console.WriteLine("    up to date");
            foreach (var c in p.Changes)
                Console.WriteLine($"    {c.Key}: {c.Current ?? "(missing)"} -> {c.Target}");
        }
    }
}
