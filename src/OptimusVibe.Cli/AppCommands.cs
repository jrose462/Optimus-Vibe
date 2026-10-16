using System.CommandLine;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Engine;
using OptimusVibe.Feedback;
using OptimusVibe.Logging;
using OptimusVibe.Sync;
using OptimusVibe.Updates;

namespace OptimusVibe;

/// <summary>
/// `version`, `update-app [--check]`, `report-bug "&lt;description&gt;" [--dry-run]`: app releases and bug reports on the
/// APP repo (jrose462/Optimus-Vibe), independent of the config repo. Registered in Program.Main.
/// </summary>
internal static class AppCommands
{
    /// <summary>Same contract as Program.Guard: expected errors print one clean line (exit 2), anything else exit 1.</summary>
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

    private static string? Token()
    {
        try { return GitHubAuth.LoadToken(); }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read the GitHub sign-in: {ex.Message}");
            return null;
        }
    }

    /// <summary>A published single-file exe (Assembly.Location is empty) — the only build that can replace itself.</summary>
    private static bool IsSingleFile => string.IsNullOrEmpty(typeof(AppCommands).Assembly.Location);

    public static Command VersionCommand()
    {
        var cmd = new Command("version", "Show this build's version.");
        cmd.SetAction(Guard(_ =>
        {
            Console.WriteLine($"optimus-vibe {AppVersion.Current}");
            return 0;
        }));
        return cmd;
    }

    public static Command UpdateAppCommand()
    {
        var check = new Option<bool>("--check") { Description = "Only check for a newer release; don't download anything." };
        var cmd = new Command("update-app",
            $"Update optimus-vibe.exe (and OptimusVibe.exe next to it, unless it's running) from the latest release of {AppRepo.FullName}.")
        {
            check,
        };
        cmd.SetAction(Guard(pr =>
        {
            var token = Token();
            var result = UpdateService.CheckAsync(token).GetAwaiter().GetResult();
            Console.WriteLine(result.Message);
            if (result.Status is not (UpdateStatus.UpdateAvailable or UpdateStatus.UpToDate)) return 1;
            var update = result.Update!;
            if (!result.UpdateAvailable) return 0;

            Console.WriteLine();
            Console.WriteLine($"{update.Name} — {update.HtmlUrl}");
            if (!string.IsNullOrWhiteSpace(update.Notes)) Console.WriteLine(update.Notes.Trim());
            if (pr.GetValue(check)) return 0;

            var exe = Environment.ProcessPath;
            if (exe is null || !IsSingleFile || !Path.GetFileName(exe).Equals(AppRepo.CliAsset, StringComparison.OrdinalIgnoreCase))
                throw new ConfigException($"update-app only replaces a published {AppRepo.CliAsset} (this is a dev build: {exe}). Download it from {update.HtmlUrl}.");

            Console.WriteLine();
            var lastShown = -1;
            var progress = new ConsoleProgress(p =>
            {
                var pct = (int)(p * 100);
                if (pct / 10 == lastShown / 10) return;
                lastShown = pct;
                Console.Write($"\rDownloading... {pct,3}%");
            });
            var staged = UpdateService.StageAllAsync(update, exe, progress, token).GetAwaiter().GetResult();
            Console.WriteLine();
            foreach (var note in staged.Notes) Console.WriteLine(note);
            var replaced = UpdateService.ApplyStaged(exe);
            foreach (var r in replaced) Console.WriteLine($"Updated {r} to {update.Version}.");
            return 0;
        }));
        return cmd;
    }

    public static Command ReportBugCommand()
    {
        var description = new Argument<string>("description") { Description = "What went wrong (first line becomes the title)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the report instead of filing it." };
        var game = new Option<string?>("--game") { Description = "Game id the problem is about (added to the report)." };
        var cmd = new Command("report-bug",
            $"File a bug report (with version, device and recent log, tokens and your user folder redacted) on {AppRepo.FullName}.")
        {
            description, dryRun, game,
        };
        cmd.SetAction(Guard(pr =>
        {
            DeviceSelection? device = null;
            HardwareInfo? hw = null;
            try
            {
                hw = DeviceDetector.Detect();
                var store = new ConfigStore(AppPaths.Resolve(pr.GetValue<string?>("--config")));
                device = DeviceDetector.Select(store.LoadDevices(), hw, pr.GetValue<string?>("--profile")
                                                                      ?? AppPaths.Env("PROFILE")
                                                                      ?? AppPaths.LoadSettings(store.Paths.DataDir).ForcedProfile);
            }
            catch (Exception ex)
            {
                Log.Warn($"report-bug: device detection incomplete ({ex.Message})");
            }

            var ctx = BugReportContext.Collect("optimus-vibe CLI", device, hw, pr.GetValue(game), pr.GetValue<string?>("--preset"));
            var draft = BugReport.Build(pr.GetValue(description)!, ctx);
            if (pr.GetValue(dryRun))
            {
                Console.WriteLine($"Title:  {draft.Title}");
                Console.WriteLine($"Labels: {string.Join(", ", draft.Labels)}");
                Console.WriteLine($"Repo:   {AppRepo.FullName}");
                Console.WriteLine();
                Console.WriteLine(draft.Body);
                return 0;
            }

            var token = Token() ?? throw new ConfigException("Not signed in to GitHub — run `optimus-vibe github login` (or `github token <pat>`) first, or use --dry-run.");
            var url = BugReport.SubmitAsync(draft, token).GetAwaiter().GetResult();
            Console.WriteLine($"Bug report filed: {url}");
            return 0;
        }));
        return cmd;
    }

    /// <summary>`github create &lt;name&gt; [--public]`: create a config repo, seed it from the current config, clone it and switch to it. Added under the `github` verb.</summary>
    public static Command GitHubCreateCommand(Option<string?> configOption)
    {
        var name = new Argument<string>("name") { Description = "Name of the new config repo (created under your account)." };
        var isPublic = new Option<bool>("--public") { Description = "Make the repo public (default: private)." };
        var cmd = new Command("create", "Create a new GitHub config repo, seed it with the current config, and switch this machine to it.") { name, isPublic };
        cmd.SetAction(Guard(pr =>
        {
            var token = Token() ?? throw new ConfigException("Not signed in to GitHub — run `optimus-vibe github login` or `github token <pat>` first.");
            var api = new GitHubApi(token);
            var paths = AppPaths.Resolve(pr.GetValue(configOption));
            if (!File.Exists(paths.DevicesFile))
                throw new ConfigException($"No config to seed from: {paths.DevicesFile} not found (use --config).");

            var info = api.CreateRepoAsync(pr.GetValue(name)!, !pr.GetValue(isPublic), "Optimus-Vibe config (per-device graphics presets)").GetAwaiter().GetResult();
            Console.WriteLine($"Created {info.FullName} ({(info.Private ? "private" : "public")}).");
            var seeded = ConfigRepoSeeder.SeedAsync(api, info.FullName, info.DefaultBranch, paths.ConfigRoot).GetAwaiter().GetResult();
            Console.WriteLine($"Seeded {seeded.Paths.Count} file(s) in one commit.");

            var dataDir = AppPaths.DefaultDataDir;
            var result = GitHubMirror.Clone(dataDir, info.FullName, info.DefaultBranch, token);
            Console.WriteLine($"{info.FullName} ({info.DefaultBranch}): {result.Message}");
            if (!result.Ok) return 1;
            var settings = AppPaths.LoadSettings(dataDir);
            settings.SyncProvider = SyncProviders.GitHub;
            settings.GitHubRepo = info.FullName;
            settings.GitHubBranch = info.DefaultBranch;
            AppPaths.SaveSettings(dataDir, settings);
            Console.WriteLine($"Sync provider: github. Config folder: {Path.Combine(AppPaths.MirrorDir(dataDir, info.FullName), "config")}");
            return 0;
        }));
        return cmd;
    }

    private sealed class ConsoleProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
