using System.CommandLine;
using System.Diagnostics;
using System.Text;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Display;
using OptimusVibe.Driver;
using OptimusVibe.Engine;
using OptimusVibe.Handlers;
using OptimusVibe.Launch;
using OptimusVibe.Logging;
using OptimusVibe.Sync;
using OptimusVibe.Ui;

namespace OptimusVibe;

public static class Program
{
    private static readonly Option<string?> ConfigOption = new("--config")
    {
        Description = "Config folder (your config repo's config\\ dir). Defaults to settings.json / OPTIMUSVIBE_CONFIG / a config\\ folder in the current directory.",
        Recursive = true,
    };

    private static readonly Option<string?> ProfileOption = new("--profile")
    {
        Description = "Force a device profile (e.g. desktop, ally-x) instead of auto-detecting.",
        Recursive = true,
    };

    private static readonly Option<string?> PresetOption = new("--preset")
    {
        Description = "Use this preset instead of the remembered one: quality | performance | battery | manual:<save>.",
        Recursive = true,
    };

    private static readonly Option<bool> NoBigPictureOption = new("--no-big-picture")
    {
        Description = "Don't switch to the Big Picture (TV) preset even if a 4K TV or stream is detected.",
    };

    private static readonly Argument<string> GameArg = new("game") { Description = "Game id (file name in config\\games without .yaml)" };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.Init(Path.Combine(AppPaths.DefaultDataDir, "logs"));
        LegacyMigration.Run(); // one-time copy of the pre-rename GameProfiles data folder, before anything reads settings

        var root = new RootCommand("Applies per-device graphics settings to games (desktop vs handheld) from a shared config repo.")
        {
            ConfigOption, ProfileOption, PresetOption,
            BigPictureCommands.DisplayCommand(), BigPictureCommands.TvCommand(),
            RunCommand(), ApplyCommand(), PresetsCommand(), UseCommand(), SaveCommand(), DeleteSaveCommand(),
            RestoreCommand(), DetectCommand(), InspectCommand(), ListCommand(), ValidateCommand(),
            SyncCommand(), UpdateCommand(), ShortcutCommand(), SetupCommand(),
            GitHubCommand(), SyncProviderCommand(),
            AppCommands.VersionCommand(), AppCommands.UpdateAppCommand(), AppCommands.ReportBugCommand(),
            SteamCommands.SteamOptionsCommand(),
            LibraryCommands.LibraryCommand(), LibraryCommands.OptOutCommand(), LibraryCommands.OptInCommand(),
            CloudCheckCommands.CloudCheckCommand(), DeviceCommands.DevicesCommand(),
            GameLabCommands.LabCommand(), OutboxCommands.OutboxCommand(),
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
        catch (Exception ex) when (ex is ConfigException or PathResolveException or GitHubException)
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

    /// <summary>run/apply: also lets a detected TV (docked 4K display or 4K stream) pick the Big Picture preset.</summary>
    private static GameContext ContextWithBigPicture(ParseResult pr) =>
        GameContext.Load(Paths(pr), pr.GetValue(GameArg)!, pr.GetValue(ProfileOption),
            pr.GetValue(PresetOption) is { } p ? PresetRef.Parse(p) : null,
            bigPicture: new BigPictureOptions { Suppress = pr.GetValue(NoBigPictureOption) });

    private static GameContext Context(ParseResult pr) =>
        GameContext.Load(Paths(pr), pr.GetValue(GameArg)!, pr.GetValue(ProfileOption),
            pr.GetValue(PresetOption) is { } p ? PresetRef.Parse(p) : null);

    // ---------------------------------------------------------------- run

    private static Command RunCommand()
    {
        var commandArg = new Argument<string[]>("command")
        {
            Description = "The game command line. For Steam launch options prefer OptimusVibe.exe run <game> -- %command% (no console window).",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var noPrompt = new Option<bool>("--no-prompt") { Description = "Don't offer to save settings changed in-game." };
        var cmd = new Command("run", "Apply settings, launch the game, wait for it to exit, then undo temporary changes.")
        {
            GameArg, commandArg, noPrompt, NoBigPictureOption,
        };
        cmd.SetAction(Guard(pr =>
        {
            NativeUi.HideConsoleIfOwned();
            var command = pr.GetValue(commandArg) ?? [];
            GameContext ctx;
            try
            {
                ctx = ContextWithBigPicture(pr);
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
        var cmd = new Command("apply", "Write this device's settings into the game's config files now (no launch).") { GameArg, dryRun, NoBigPictureOption };
        cmd.SetAction(Guard(pr =>
        {
            var ctx = ContextWithBigPicture(pr);
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

    private static Command SaveCommand()
    {
        var name = new Argument<string>("name") { Description = "Name for the save, e.g. \"Steady 40\"." };
        var update = new Option<string?>("--update") { Description = "Overwrite this existing save (slug) instead of creating a new one." };
        var notes = new Option<string?>("--notes") { Description = "Free-text notes shown in the app." };
        var noActivate = new Option<bool>("--no-activate") { Description = "Don't make the new save this game's active preset." };
        var noSync = new Option<bool>("--no-sync") { Description = "Don't commit/push the config repo." };
        var cmd = new Command("save", "Save the game's current settings as a named manual save for this device (tune in-game, then save).")
        {
            GameArg, name, update, notes, noActivate, noSync,
        };
        cmd.SetAction(Guard(pr =>
        {
            var ctx = Context(pr);
            PrintHeader(ctx);
            var result = ManualSaves.SaveCurrent(ctx, pr.GetValue(name)!, pr.GetValue(update), !pr.GetValue(noActivate),
                pr.GetValue(notes), sync: !pr.GetValue(noSync));
            Console.WriteLine($"Saved '{result.Save.Name}' (manual:{result.Save.Slug}): {result.KeysSaved} setting(s) -> {result.Save.Dir}");
            foreach (var s in result.Snapshots) Console.WriteLine($"  whole-file snapshot: {s}");
            foreach (var s in result.Skipped) Console.WriteLine($"  skipped {s}");
            if (!pr.GetValue(noActivate)) Console.WriteLine("  now the active preset for this game on this device");
            if (result.Sync is { } g) Console.WriteLine($"  sync: {g.Message}");
            return 0;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- presets / use / delete-save / sync

    private static Command PresetsCommand()
    {
        var cmd = new Command("presets", "List recommended presets and manual saves for this device (* = active).") { GameArg };
        cmd.SetAction(Guard(pr =>
        {
            var ctx = Context(pr);
            PrintHeader(ctx);
            Console.WriteLine("Recommended:");
            foreach (var o in PresetCatalog.Recommended(ctx.Game, ctx.Device, ctx.Preset))
                Console.WriteLine($"  {(o.IsActive ? "*" : " ")} {o.Ref.Name,-14} {o.Label}{(o.TargetFps is { } f ? $" ({f} fps)" : "")}" +
                                  $"{(o.IsDefault ? " [device default]" : "")}{(o.Error is null ? "" : $" ERROR: {o.Error}")}");
            Console.WriteLine("Manual saves:");
            var manual = PresetCatalog.Manual(ctx.Store, ctx.Game, ctx.Device, ctx.Preset);
            if (manual.Count == 0) Console.WriteLine("    (none — tune in-game, then `optimus-vibe save <game> <name>`)");
            foreach (var o in manual)
                Console.WriteLine($"  {(o.IsActive ? "*" : " ")} manual:{o.Ref.Name,-20} {o.Label}  (updated {o.Save!.Updated:g})");
            return 0;
        }));
        return cmd;
    }

    private static Command UseCommand()
    {
        var preset = new Argument<string>("preset") { Description = "quality | performance | battery | manual:<save> | default" };
        var cmd = new Command("use", "Remember which preset this game uses on this device.") { GameArg, preset };
        cmd.SetAction(Guard(pr =>
        {
            var value = pr.GetValue(preset)!;
            var paths = Paths(pr);
            var store = new ConfigStore(paths);
            var device = GameContext.SelectDevice(store, pr.GetValue(ProfileOption));
            var gameId = store.LoadGame(pr.GetValue(GameArg)!).Id;
            var selections = new SelectionStore(paths.SelectionsFile);
            if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                selections.Set(device.Name, gameId, null);
                Console.WriteLine($"{gameId} on {device.Name}: back to the device default");
                return 0;
            }
            var reference = PresetRef.Parse(value);
            var ctx = GameContext.Load(store, gameId, device, reference); // throws if it doesn't exist
            selections.Set(device.Name, gameId, reference);
            Console.WriteLine($"{gameId} on {device.Name}: now uses {reference} ({ctx.Profile.Label})");
            return 0;
        }));
        return cmd;
    }

    private static Command DeleteSaveCommand()
    {
        var slug = new Argument<string>("save") { Description = "Save slug (see `presets`), with or without the manual: prefix." };
        var cmd = new Command("delete-save", "Delete a manual save for this device.") { GameArg, slug };
        cmd.SetAction(Guard(pr =>
        {
            var store = new ConfigStore(Paths(pr));
            var device = GameContext.SelectDevice(store, pr.GetValue(ProfileOption));
            var gameId = store.LoadGame(pr.GetValue(GameArg)!).Id;
            var name = pr.GetValue(slug)!;
            if (name.StartsWith("manual:", StringComparison.OrdinalIgnoreCase)) name = name["manual:".Length..];
            store.LoadSave(gameId, device.Name, name); // throws if missing
            var git = ManualSaves.Delete(store, gameId, device.Name, name);
            Console.WriteLine($"Deleted manual:{name}{(git is null ? "" : $" (git: {git.Message})")}");
            return 0;
        }));
        return cmd;
    }

    private static Command SyncCommand()
    {
        var cmd = new Command("sync", "Publish manual saves and pull config changes (git checkout or GitHub, per `sync-provider`).");
        cmd.SetAction(Guard(pr =>
        {
            var paths = Paths(pr);
            var provider = SyncProviders.For(paths);
            if (!provider.IsReady(out var why)) throw new ConfigException($"Can't sync ({provider.Name}): {why}");
            var result = provider.CommitAndSync("chore(saves): sync manual saves", paths.SavesDir);
            Console.WriteLine($"{provider.Describe()}: {result.Message}");
            return result.Ok ? 0 : 1;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- restore

    private static Command RestoreCommand()
    {
        var original = new Option<bool>("--original") { Description = "Restore the files as they were before Optimus-Vibe first touched them." };
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
            DeviceSelection? device = null;
            try { device = GameContext.SelectDevice(store, pr.GetValue(ProfileOption)); } catch (ConfigException) { }
            Console.WriteLine($"Config: {store.Paths.ConfigRoot}   This device: {device?.Name ?? "(unknown)"}");
            foreach (var id in store.GameIds())
            {
                try
                {
                    if (device is null)
                    {
                        var g = store.LoadGame(id);
                        Console.WriteLine($"  {g.Id,-24} {g.Name,-32} {g.Store}");
                        continue;
                    }
                    var ctx = GameContext.Load(store, id, device);
                    var presets = string.Join(", ", ProfileResolver.PresetNames(ctx.Game, device.Name, device.Def));
                    var saves = store.ListSaves(ctx.Game.Id, device.Name).Count;
                    Console.WriteLine($"  {ctx.Game.Id,-24} {ctx.Game.Name,-32} active: {ctx.Preset?.ToString() ?? "(none)",-26} " +
                                      $"presets: {(presets.Length == 0 ? "-" : presets)}; manual saves: {saves}");
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
        var cmd = new Command("update", "Pull config changes from the repo (git checkout or GitHub, per `sync-provider`).");
        cmd.SetAction(Guard(pr =>
        {
            var provider = SyncProviders.For(Paths(pr));
            if (!provider.IsReady(out var why)) throw new ConfigException($"Can't update ({provider.Name}): {why}");
            var result = provider.Pull();
            Console.WriteLine($"{provider.Describe()}: {result.Message}");
            return result.Ok ? 0 : 1;
        }));
        return cmd;
    }

    // ---------------------------------------------------------------- github / sync-provider

    private static Command GitHubCommand()
    {
        var gh = new Command("github", "Sign in to GitHub and sync the config repo through the GitHub API (no git needed).");

        var clientId = new Option<string?>("--client-id") { Description = "Client id of a GitHub OAuth App with Device Flow enabled (remembered in settings)." };
        var login = new Command("login", "Sign in with a one-time code (GitHub device flow).") { clientId };
        login.SetAction(Guard(pr =>
        {
            var dataDir = AppPaths.DefaultDataDir;
            var settings = AppPaths.LoadSettings(dataDir);
            var id = pr.GetValue(clientId) is { Length: > 0 } given ? given.Trim() : GitHubAuth.ResolveClientId(settings);
            if (string.IsNullOrWhiteSpace(id))
                throw new ConfigException("No GitHub OAuth App client id in this build. Register one (github.com/settings/developers, enable Device Flow) and pass " +
                                          "--client-id <id> (or set OPTIMUSVIBE_GITHUB_CLIENT_ID), or store a personal access token with `github token <pat>`.");

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.CancelKeyPress += onCancel;
            try
            {
                var code = GitHubAuth.StartDeviceFlowAsync(id, cts.Token).GetAwaiter().GetResult();
                Console.WriteLine($"Open {code.VerificationUri} and enter the code:  {code.UserCode}");
                Console.WriteLine($"Waiting for approval (expires in {code.ExpiresInSeconds / 60} min; Ctrl+C to cancel)...");
                var token = GitHubAuth.PollForTokenAsync(id, code, cts.Token).GetAwaiter().GetResult();
                var user = new GitHubApi(token).GetLoginAsync().GetAwaiter().GetResult();
                GitHubAuth.SaveToken(token);
                settings.GitHubLogin = user;
                settings.GitHubClientId = id;
                AppPaths.SaveSettings(dataDir, settings);
                Console.WriteLine($"Signed in as {user}. Pick your private config repo with `github use <owner/name>` or make one with `github create optimus-vibe-config`.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Sign-in cancelled.");
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }));

        var patArg = new Argument<string>("pat") { Description = "A GitHub personal access token with access to the config repo (classic: 'repo' scope; fine-grained: Contents read/write)." };
        var token = new Command("token", "Store a personal access token instead of signing in with a code.") { patArg };
        token.SetAction(Guard(pr =>
        {
            var pat = pr.GetValue(patArg)!.Trim();
            var user = new GitHubApi(pat).GetLoginAsync().GetAwaiter().GetResult();
            GitHubAuth.SaveToken(pat);
            var dataDir = AppPaths.DefaultDataDir;
            var settings = AppPaths.LoadSettings(dataDir);
            settings.GitHubLogin = user;
            AppPaths.SaveSettings(dataDir, settings);
            Console.WriteLine($"Token saved to Windows Credential Manager; signed in as {user}.");
            return 0;
        }));

        var repos = new Command("repos", "List repos the signed-in account can push to (most recently pushed first).");
        repos.SetAction(Guard(_ =>
        {
            var settings = AppPaths.LoadSettings(AppPaths.DefaultDataDir);
            var list = RequireApi().ListReposAsync().GetAwaiter().GetResult();
            if (list.Count == 0) Console.WriteLine("No repos with push access.");
            foreach (var r in list)
            {
                var mark = r.FullName.Equals(settings.GitHubRepo, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
                Console.WriteLine($"{mark} {r.FullName,-40} {r.DefaultBranch,-10} {(r.Private ? "private" : "public "),-8} {r.Description}");
            }
            return 0;
        }));

        var repoArg = new Argument<string>("repo") { Description = "owner/name of your (private) config repo." };
        var branch = new Option<string?>("--branch") { Description = "Branch to sync (default: the repo's default branch)." };
        var use = new Command("use", "Download the repo's config/ folder and switch this machine to GitHub sync.") { repoArg, branch };
        use.SetAction(Guard(pr =>
        {
            var repo = pr.GetValue(repoArg)!.Trim();
            if (!GitHubApi.IsValidRepoName(repo)) throw new ConfigException($"'{repo}' is not a repo name — use owner/name.");
            var api = RequireApi();
            var info = api.GetRepoAsync(repo).GetAwaiter().GetResult();
            var br = pr.GetValue(branch) ?? info.DefaultBranch;
            var dataDir = AppPaths.DefaultDataDir;
            var result = GitHubMirror.Clone(dataDir, info.FullName, br, api.Token);
            Console.WriteLine($"{info.FullName} ({br}): {result.Message}");
            if (!result.Ok) return 1;
            var settings = AppPaths.LoadSettings(dataDir);
            settings.SyncProvider = SyncProviders.GitHub;
            settings.GitHubRepo = info.FullName;
            settings.GitHubBranch = br;
            AppPaths.SaveSettings(dataDir, settings);
            Console.WriteLine($"Sync provider: github. Config folder: {Path.Combine(AppPaths.MirrorDir(dataDir, info.FullName), "config")}");
            if (pr.GetValue(ConfigOption) is not null || AppPaths.Env("CONFIG") is not null)
                Log.Warn("--config / OPTIMUSVIBE_CONFIG overrides the GitHub copy; drop it to use the synced config.");
            return 0;
        }));

        var logout = new Command("logout", "Forget the stored GitHub token.");
        logout.SetAction(Guard(_ =>
        {
            GitHubAuth.ClearToken();
            var dataDir = AppPaths.DefaultDataDir;
            var settings = AppPaths.LoadSettings(dataDir);
            settings.GitHubLogin = null;
            AppPaths.SaveSettings(dataDir, settings);
            Console.WriteLine("Signed out of GitHub.");
            if (SyncProviders.IsGitHub(settings))
                Console.WriteLine("Sync provider is still github: the local copy keeps working, but won't sync until you sign in again.");
            return 0;
        }));

        var status = new Command("status", "Show the sync provider, GitHub account and repo.");
        status.SetAction(Guard(pr =>
        {
            var dataDir = AppPaths.DefaultDataDir;
            var settings = AppPaths.LoadSettings(dataDir);
            var tokenValue = GitHubAuth.LoadToken();
            Console.WriteLine($"Sync provider: {settings.SyncProvider}");
            Console.WriteLine($"GitHub repo:   {settings.GitHubRepo ?? "(none chosen)"} ({settings.GitHubBranch})");
            Console.Write("Signed in:     ");
            if (tokenValue is null) Console.WriteLine("no");
            else
            {
                try { Console.WriteLine($"yes, as {new GitHubApi(tokenValue).GetLoginAsync().GetAwaiter().GetResult()}"); }
                catch (GitHubException ex) { Console.WriteLine($"token stored{(settings.GitHubLogin is { } l ? $" ({l})" : "")}, but: {ex.Message}"); }
            }
            if (settings.GitHubRepo is { Length: > 0 } cfgRepo)
            {
                var mirror = new GitHubMirror(AppPaths.MirrorDir(dataDir, cfgRepo), cfgRepo, settings.GitHubBranch, null);
                var at = mirror.BaseCommit is { } sha ? sha[..Math.Min(7, sha.Length)] : "?";
                Console.WriteLine($"Local copy:    {(mirror.IsCloned ? $"{mirror.ConfigDir} @ {at}" : "(none)")}" +
                                  $"{(mirror.LastSyncUtc is { } t ? $", last synced {t.ToLocalTime():g}" : "")}");
            }
            try
            {
                var provider = SyncProviders.For(Paths(pr));
                Console.WriteLine($"Active:        {provider.Describe()} — {(provider.IsReady(out var why) ? "ready" : why)}");
            }
            catch (ConfigException ex)
            {
                Console.WriteLine($"Active:        {ex.Message}");
            }
            return 0;
        }));

        gh.Subcommands.Add(login);
        gh.Subcommands.Add(token);
        gh.Subcommands.Add(repos);
        gh.Subcommands.Add(use);
        gh.Subcommands.Add(logout);
        gh.Subcommands.Add(status);
        gh.Subcommands.Add(AppCommands.GitHubCreateCommand(ConfigOption));
        return gh;
    }

    private static GitHubApi RequireApi() =>
        GitHubAuth.LoadToken() is { } t ? new GitHubApi(t) : throw new ConfigException("Not signed in to GitHub — run `optimus-vibe github login` or `github token <pat>`.");

    private static Command SyncProviderCommand()
    {
        var which = new Argument<string>("provider") { Description = "git (this machine's git checkout) or github (the app's GitHub copy)." };
        var cmd = new Command("sync-provider", "Choose how the config repo syncs on this machine.") { which };
        cmd.SetAction(Guard(pr =>
        {
            var value = pr.GetValue(which)!.Trim().ToLowerInvariant();
            var dataDir = AppPaths.DefaultDataDir;
            var settings = AppPaths.LoadSettings(dataDir);
            switch (value)
            {
                case SyncProviders.Git:
                    settings.SyncProvider = SyncProviders.Git;
                    AppPaths.SaveSettings(dataDir, settings);
                    Console.WriteLine($"Sync provider: git. Config folder: {settings.ConfigRoot ?? "(auto: next to the exe / --config)"}");
                    return 0;
                case SyncProviders.GitHub:
                    if (settings.GitHubRepo is not { Length: > 0 } ghRepo)
                        throw new ConfigException("No GitHub config repo chosen yet — run `optimus-vibe github use <owner/name>` or `github create <name>`.");
                    var mirror = new GitHubMirror(AppPaths.MirrorDir(dataDir, ghRepo), ghRepo, settings.GitHubBranch, null);
                    if (!mirror.IsCloned)
                        throw new ConfigException($"No GitHub copy of {ghRepo} yet — run `optimus-vibe github use {ghRepo}`.");
                    settings.SyncProvider = SyncProviders.GitHub;
                    AppPaths.SaveSettings(dataDir, settings);
                    Console.WriteLine($"Sync provider: github ({settings.GitHubRepo}, {settings.GitHubBranch}). Config folder: {mirror.ConfigDir}");
                    return 0;
                default:
                    throw new ConfigException($"Unknown sync provider '{value}' — use git or github.");
            }
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
            var lnk = Path.Combine(dir, $"{ctx.Game.Name} (Optimus-Vibe).lnk");
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine own exe path");
            // Prefer the windowed app next to us: its headless `run` has no console flash.
            var app = Path.Combine(Path.GetDirectoryName(self)!, "OptimusVibe.exe");
            var exe = File.Exists(app) ? app : self;

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
        var root = new Option<string?>("--config-root") { Description = "Path to the config folder of your Optimus-Vibe repo checkout." };
        var force = new Option<string?>("--force-profile") { Description = "Always use this device profile on this machine." };
        var clear = new Option<bool>("--clear-force") { Description = "Go back to auto-detecting the device." };
        var cmd = new Command("setup", "One-time per-machine settings (stored in %LOCALAPPDATA%\\OptimusVibe\\settings.json).")
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
        if (ctx.Profile.IsEmpty)
            Log.Warn($"No presets for '{ctx.Device.Name}' in games/{ctx.Game.Id}.yaml and no manual save selected");
        else
            Console.WriteLine($"  preset: {ctx.Preset} — {ctx.Profile.Label}" +
                              $"{(ctx.Profile.TargetFps is { } f ? $" ({f} fps target)" : "")}{(ctx.IsSelected ? "" : " [device default]")}");
        if (ctx.BigPictureUsed) Console.WriteLine($"  Big Picture (TV): {ctx.BigPicture!.Reason}{(ctx.DisplayTarget is { } t ? $" -> display {t}" : "")}");
        else if (ctx.BigPictureNote is { } note) Console.WriteLine($"  {note}");
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
