using System.CommandLine;
using System.Text;
using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Library;
using OptimusVibe.Logging;
using OptimusVibe.Sync;

namespace OptimusVibe;

/// <summary>
/// `lab setup|status|request|evidence|evidence-preview|drain|board|result`: the game lab. The app collects evidence about a game on
/// this PC, files a request issue in the private config repo, answers the workflow's "needs evidence" follow-ups and starts queued
/// requests. Registered in Program.Main.
/// </summary>
internal static class GameLabCommands
{
    private static readonly Option<string?> RepoOption = new("--repo") { Description = "Config repo owner/name (default: the one chosen with `github use`).", Recursive = true };

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

    private sealed record Conn(GitHubApi Api, string Repo, string Branch, AppSettings Settings, string DataDir);

    private static Conn Connect(ParseResult pr)
    {
        var token = GitHubAuth.LoadToken() ?? throw new ConfigException("Not signed in to GitHub — run `optimus-vibe github login` (or `github token <pat>`).");
        var dataDir = AppPaths.DefaultDataDir;
        var settings = AppPaths.LoadSettings(dataDir);
        var repo = pr.GetValue(RepoOption) ?? settings.GitHubRepo
                   ?? throw new ConfigException("No config repo chosen — run `optimus-vibe github use <owner/name>` or pass --repo.");
        if (!GitHubApi.IsValidRepoName(repo)) throw new ConfigException($"'{repo}' is not a repo name — use owner/name.");
        return new Conn(new GitHubApi(token), repo.Trim(), settings.GitHubBranch, settings, dataDir);
    }

    private static AppPaths? TryPaths(ParseResult pr)
    {
        try { return AppPaths.Resolve(pr.GetValue<string?>("--config")); }
        catch (ConfigException) { return null; }
    }

    private static T Wait<T>(Task<T> t) => t.GetAwaiter().GetResult();
    private static void Wait(Task t) => t.GetAwaiter().GetResult();

    public static Command LabCommand()
    {
        var lab = new Command("lab", "Game lab: have Claude research recommended presets for your games through your private config repo.")
        {
            RepoOption,
            SetupCommand(), StatusCommand(), RequestCommand(), EvidenceCommand(), PreviewCommand(), DrainCommand(), BoardCommand(), ResultCommand(),
        };
        return lab;
    }

    // ------------------------------------------------------------ game resolution

    private sealed record Resolved(LabTarget Target, ConfigStore? Store, DeviceSelection? Device, AppPaths? Paths);

    private static Resolved ResolveGame(ParseResult pr, string query)
    {
        var paths = TryPaths(pr);
        ConfigStore? store = paths is null ? null : new ConfigStore(paths);
        DeviceSelection? device = null;
        try { if (store is not null) device = GameContext.SelectDevice(store, pr.GetValue<string?>("--profile")); }
        catch (Exception ex) when (ex is ConfigException or InvalidOperationException) { Log.Warn($"Device not detected: {ex.Message}"); }

        var games = new List<GameDef>();
        if (store is not null)
            foreach (var id in store.GameIds())
            {
                try { games.Add(store.LoadGame(id)); }
                catch (ConfigException ex) { Log.Warn($"Skipping game {id}: {ex.Message}"); }
            }
        var detected = GameScanners.ScanAll(paths is null ? null : AppPaths.LoadSettings(paths.DataDir).ScanStores);

        var def = games.FirstOrDefault(g => g.Id.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (def is not null) return new Resolved(LabTarget.From(LibraryService.FindInstall(def, detected), def), store, device, paths);

        var norm = LibraryService.NormalizeName(query);
        var hits = detected.Where(d => d.Key.Equals(query, StringComparison.OrdinalIgnoreCase) || d.StoreId.Equals(query, StringComparison.OrdinalIgnoreCase)
                                       || LibraryService.NormalizeName(d.Name) == norm).ToList();
        if (hits.Count == 0 && norm.Length >= 3)
            hits = detected.Where(d => LibraryService.NormalizeName(d.Name).Contains(norm)).ToList();
        if (hits.Count == 0)
        {
            def = games.FirstOrDefault(g => LibraryService.NormalizeName(g.Name) == norm);
            if (def is not null) return new Resolved(LabTarget.From(LibraryService.FindInstall(def, detected), def), store, device, paths);
            throw new ConfigException($"No game '{query}': not a config id and not installed on this PC.");
        }
        if (hits.Count > 1)
            throw new ConfigException($"'{query}' matches several installed games: {string.Join(", ", hits.Take(6).Select(h => $"{h.Name} ({h.Key})"))}. Use the store key.");
        var d0 = hits[0];
        return new Resolved(LabTarget.From(d0, LibraryService.MatchGame(d0, games)), store, device, paths);
    }

    private static EvidenceCollector CollectorFor(Resolved r)
    {
        var opts = new EvidenceOptions
        {
            CloudCheck = r.Store is not null && r.Device is not null ? LabCloud.ForChecker(r.Store, r.Device) : null,
            CurrentConfig = r.Paths is not null ? LabCloud.CurrentConfigFrom(r.Paths) : null,
        };
        return new EvidenceCollector(opts);
    }

    private static LabKind DefaultKind(LabTarget t) => t.Game is null ? LabKind.New : LabKind.Refresh;

    private static LabKind ParseKind(string? text, LabTarget t)
    {
        if (string.IsNullOrWhiteSpace(text)) return DefaultKind(t);
        if (!GameLabContract.TryParseKind(text, out var kind)) throw new ConfigException("--kind must be new, refresh or sync-check.");
        if (kind != LabKind.New && t.Game is null) throw new ConfigException($"{t.Name} has no config yet, so only `--kind new` makes sense.");
        return kind;
    }

    // ------------------------------------------------------------ setup

    private static Command SetupCommand()
    {
        var step = new Option<string?>("--step") { Description = "Only this step: scopes | labels | workflow | secret | variables | settings | board | test." };
        var check = new Option<bool>("--check") { Description = "Only report what is in place; change nothing." };
        var claudeToken = new Option<string?>("--claude-token") { Description = "Claude Code OAuth token (from `claude setup-token`) to store as the CLAUDE_CODE_OAUTH_TOKEN secret. '-' reads it from stdin; also CLAUDE_CODE_OAUTH_TOKEN env." };
        var dailyMax = new Option<int?>("--daily-max") { Description = $"GAME_LAB_DAILY_MAX (requests per day; default {GameLabContract.DefaultDailyMax})." };
        var model = new Option<string?>("--model") { Description = $"GAME_LAB_MODEL (default {GameLabContract.DefaultModel})." };
        var test = new Option<bool>("--test") { Description = "Also file a sync-check request for an installed, configured game as a test." };
        var cmd = new Command("setup", "Get your config repo ready for the game lab: workflow, labels, secret, variables, repo settings, board.")
        {
            step, check, claudeToken, dailyMax, model, test,
        };
        cmd.SetAction(Guard(pr =>
        {
            var c = Connect(pr);
            var token = pr.GetValue(claudeToken);
            if (token == "-") token = Console.In.ReadLine();
            token ??= Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");

            var wantStep = pr.GetValue(step);
            Func<CancellationToken, Task<string>>? testRun = null;
            if (pr.GetValue(test) || string.Equals(wantStep, "test", StringComparison.OrdinalIgnoreCase))
                testRun = ct => TestRequestAsync(pr, c, ct);

            var setup = new GameLabSetup(c.Api, c.Repo, c.Branch, new GameLabSetupOptions
            {
                ClaudeToken = token, DailyMax = pr.GetValue(dailyMax), Model = pr.GetValue(model), RunTestRequest = testRun, DataDir = c.DataDir,
            });

            var steps = wantStep is null ? setup.Steps : [setup.Step(wantStep) ?? throw new ConfigException($"Unknown step '{wantStep}'. Steps: {string.Join(", ", setup.Steps.Select(s => s.Name))}.")];
            var failed = false;
            foreach (var s in steps)
            {
                if (pr.GetValue(check))
                {
                    SetupCheck r;
                    try { r = Wait(s.CheckAsync()); }
                    catch (GitHubException ex) { r = new SetupCheck(false, "could not check: " + ex.Message); }
                    Console.WriteLine($"{Mark(r.Done, r.Skipped)} {s.Name,-10} {r.Detail}");
                    if (r.Warning is not null) Console.WriteLine($"             ! {r.Warning}");
                    failed |= !r.Done && !r.Skipped;
                    continue;
                }
                var res = Wait(setup.RunStepAsync(s));
                Console.WriteLine($"{Mark(res.Ok, res.Skipped)} {s.Name,-10} {res.Message}");
                if (res.NeedsLogin) Console.WriteLine("             -> run `optimus-vibe github login` and approve all scopes, then repeat.");
                failed |= !res.Ok;
                if (!res.Ok && s.Name is "scopes" or "workflow") break;
            }
            if (!pr.GetValue(check) && wantStep is null && !failed)
                Console.WriteLine("Done. File a request with `optimus-vibe lab request <game>`.");
            return failed ? 1 : 0;
        }));
        return cmd;
    }

    private static string Mark(bool ok, bool skipped) => skipped ? "[skip]" : ok ? "[ ok ]" : "[todo]";

    private static async Task<string> TestRequestAsync(ParseResult pr, Conn c, CancellationToken ct)
    {
        var paths = TryPaths(pr) ?? throw new ConfigException("The test request needs a config folder with at least one configured game.");
        var store = new ConfigStore(paths);
        var detected = GameScanners.ScanAll();
        foreach (var id in store.GameIds())
        {
            GameDef def;
            try { def = store.LoadGame(id); } catch (ConfigException) { continue; }
            var install = LibraryService.FindInstall(def, detected);
            if (install is null) continue;
            var device = GameContext.SelectDevice(store, null);
            var r = new Resolved(LabTarget.From(install, def), store, device, paths);
            var filed = await new GameLabClient(c.Api, c.Repo, c.Branch).FileRequestAsync(LabKind.SyncCheck, r.Target, device.Name, CollectorFor(r), ct).ConfigureAwait(false);
            return $"Test request: {filed.Message} {filed.Url}";
        }
        return "No installed, configured game to test with; skipped.";
    }

    // ------------------------------------------------------------ status

    private static Command StatusCommand()
    {
        var cmd = new Command("status", "Setup state and the open game-lab requests.");
        cmd.SetAction(Guard(pr =>
        {
            var c = Connect(pr);
            var client = new GameLabClient(c.Api, c.Repo, c.Branch);
            var status = Wait(new GameLabSetup(c.Api, c.Repo, c.Branch).GetStatusAsync(client));
            Console.WriteLine($"Config repo: {c.Repo}");
            foreach (var s in status.Steps)
            {
                Console.WriteLine($"{Mark(s.Done, s.Skipped)} {s.Name,-10} {s.Detail}");
                if (s.Warning is not null) Console.WriteLine($"             ! {s.Warning}");
            }
            Console.WriteLine();
            Console.WriteLine(status.Summary);
            foreach (var i in status.Open) Console.WriteLine($"  {i.Describe()}  {i.Url}");
            return status.Ready ? 0 : 1;
        }));
        return cmd;
    }

    // ------------------------------------------------------------ request

    private static Command RequestCommand()
    {
        var game = new Argument<string>("game") { Description = "Game id (config\\games), store key (steam:1234) or name of an installed game." };
        var kind = new Option<string?>("--kind") { Description = "new | refresh | sync-check (default: new for a game without config, else refresh)." };
        var cmd = new Command("request", "Collect evidence on this PC and file a game-lab request in your config repo.") { game, kind };
        cmd.SetAction(Guard(pr =>
        {
            var c = Connect(pr);
            var r = ResolveGame(pr, pr.GetValue(game)!);
            var k = ParseKind(pr.GetValue(kind), r.Target);
            var device = r.Device?.Name ?? "unknown";
            var filed = Wait(new GameLabClient(c.Api, c.Repo, c.Branch).FileRequestAsync(k, r.Target, device, CollectorFor(r)));
            Console.WriteLine(filed.Message);
            Console.WriteLine(filed.Url);
            return 0;
        }));
        return cmd;
    }

    // ------------------------------------------------------------ evidence (answer needs-evidence)

    private static Command EvidenceCommand()
    {
        var issue = new Argument<int>("issue") { Description = "Issue number of the request." };
        var cmd = new Command("evidence", "Answer a 'needs evidence' request: collect exactly what the workflow asked for and post it.") { issue };
        cmd.SetAction(Guard(pr =>
        {
            var c = Connect(pr);
            var number = pr.GetValue(issue);
            var client = new GameLabClient(c.Api, c.Repo, c.Branch);
            var pending = Wait(client.FindNeedsEvidenceAsync()).FirstOrDefault(p => p.Issue.Number == number);
            if (pending is null)
            {
                Console.WriteLine($"#{number} is not waiting for evidence.");
                return 1;
            }
            var detected = GameScanners.ScanAll();
            var req = pending.Issue.Request;
            var install = req is null ? null : detected.FirstOrDefault(d => d.Key.Equals(req.Key, StringComparison.OrdinalIgnoreCase));
            var target = req is null ? null : new LabTarget
            {
                Name = req.Name, Store = req.Store, StoreId = req.StoreId, GameId = req.GameId,
                InstallDir = install?.InstallDir, Exes = install?.Exes ?? [], Detected = install,
            };
            var answered = Wait(client.AnswerNeedsEvidenceAsync(pending, new EvidenceCollector(), target));
            Console.WriteLine($"Posted {answered.Sections} section(s) in {answered.Comments} comment(s) to #{number}.");
            foreach (var n in answered.Notes) Console.WriteLine("  - " + n);
            return 0;
        }));
        return cmd;
    }

    // ------------------------------------------------------------ evidence-preview

    private static Command PreviewCommand()
    {
        var game = new Argument<string>("game") { Description = "Game id, store key or installed game name." };
        var full = new Option<bool>("--full") { Description = "Print the complete evidence text (what would be posted), not only the summary." };
        var cmd = new Command("evidence-preview", "Show what would be sent for a game. Reads this PC only; no network, nothing is sent.") { game, full };
        cmd.SetAction(Guard(pr =>
        {
            var r = ResolveGame(pr, pr.GetValue(game)!);
            var bundle = CollectorFor(r).Collect(r.Target);
            var parts = bundle.Split();
            Console.WriteLine($"{r.Target.Name}  [{r.Target.Key}]  install: {(r.Target.InstallDir ?? "(not installed)")}");
            Console.WriteLine($"Sections: {bundle.Sections.Count}  (files {bundle.Count(EvidenceKind.File)}, directories {bundle.Count(EvidenceKind.Directory)}, registry {bundle.Count(EvidenceKind.Registry)}), " +
                              $"{parts.Count} part(s), {parts.Sum(p => p.Length)} chars");
            foreach (var s in bundle.Sections)
                Console.WriteLine($"  {s.Heading[4..],-60} {s.Body.Length,8} chars");
            foreach (var n in bundle.Notes) Console.WriteLine("  note: " + n);
            if (pr.GetValue(full))
                foreach (var p in parts) { Console.WriteLine("\n----- part -----"); Console.WriteLine(p); }
            return 0;
        }));
        return cmd;
    }

    // ------------------------------------------------------------ drain / board / result

    private static Command DrainCommand()
    {
        var cmd = new Command("drain", "Start the game-lab workflow for the oldest queued request when nothing is running.");
        cmd.SetAction(Guard(pr =>
        {
            var c = Connect(pr);
            var res = Wait(new GameLabClient(c.Api, c.Repo, c.Branch).DrainQueueAsync());
            Console.WriteLine(res.Message);
            return 0;
        }));
        return cmd;
    }

    private static Command BoardCommand()
    {
        var cmd = new Command("board", "Move each request's card on the 'Optimus-Vibe game lab' project to its current status.");
        cmd.SetAction(Guard(pr =>
        {
            var c = Connect(pr);
            Console.WriteLine(Wait(new BoardSync(c.Api, c.Repo, c.DataDir).SyncAsync()).Message);
            return 0;
        }));
        return cmd;
    }

    private static Command ResultCommand()
    {
        var issue = new Argument<int>("issue") { Description = "Issue number of the request." };
        var apply = new Option<bool>("--apply") { Description = "Pull the merged config to this PC." };
        var cmd = new Command("result", "Show the lab's result for a request (and optionally pull it).") { issue, apply };
        cmd.SetAction(Guard(pr =>
        {
            var c = Connect(pr);
            var result = Wait(new GameLabClient(c.Api, c.Repo, c.Branch).GetResultAsync(pr.GetValue(issue)));
            if (result is null) { Console.WriteLine("No result yet."); return 1; }
            Console.WriteLine($"Game {result.GameId}: {result.PresetCount} preset(s){(result.Presets.Count > 0 ? " (" + string.Join(", ", result.Presets) + ")" : "")}, " +
                              $"cloud sync {(result.CloudSyncsSettings is { } b ? (b ? "yes" : "no") : "unknown")}{(result.SyncConfidence is null ? "" : $" ({result.SyncConfidence})")}, " +
                              $"PR {(result.PrNumber is { } n ? "#" + n : "-")}{(result.Merged ? " merged" : " not merged")}");
            if (pr.GetValue(apply))
            {
                var paths = AppPaths.Resolve(pr.GetValue<string?>("--config"));
                var (ok, message) = GameLabClient.ApplyResultLocally(result, paths, SyncProviders.For(paths));
                Console.WriteLine(message);
                return ok ? 0 : 1;
            }
            return 0;
        }));
        return cmd;
    }
}
