using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Logging;

namespace OptimusVibe;

/// <summary>
/// `cloud-check [&lt;game&gt;] [--all] [--json]`: do this game's graphics settings travel between machines? Combines the
/// researched claim, Steam Cloud / EA app records and what Optimus-Vibe observed between launches. Read-only.
/// Registered in Program.Main.
/// </summary>
internal static class CloudCheckCommands
{
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

    public static Command CloudCheckCommand()
    {
        var game = new Argument<string?>("game") { Description = "Game id (file name in config\\games without .yaml).", Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all") { Description = "Check every configured game." };
        var json = new Option<bool>("--json") { Description = "Print the reports as JSON." };
        var cmd = new Command("cloud-check", "Check whether a game's graphics settings sync between machines (Steam Cloud, EA app, observed changes).")
        {
            game, all, json,
        };
        cmd.SetAction(Guard(pr =>
        {
            bool asJson = pr.GetValue(json);
            var wasQuiet = Log.Quiet;
            if (asJson) Log.Quiet = true; // keep stdout pure JSON
            try
            {
                var id = pr.GetValue(game);
                if (string.IsNullOrWhiteSpace(id) && !pr.GetValue(all))
                    throw new ConfigException("Name a game (optimus-vibe cloud-check borderlands-4) or use --all.");

                var store = new ConfigStore(AppPaths.Resolve(pr.GetValue<string?>("--config")));
                var device = GameContext.SelectDevice(store, pr.GetValue<string?>("--profile"));
                var checker = new CloudSyncChecker(store, device);
                var ids = string.IsNullOrWhiteSpace(id) ? store.GameIds().ToList() : [id];

                var results = new List<(GameDef Game, CloudSyncReport Report)>();
                foreach (var gid in ids)
                {
                    var def = store.LoadGame(gid);
                    results.Add((def, checker.Check(def)));
                }

                if (asJson)
                {
                    var doc = new
                    {
                        device = device.Name,
                        games = results.Select(r => new
                        {
                            id = r.Game.Id,
                            name = r.Game.Name,
                            declared = r.Report.Declared,
                            detected = r.Report.Detected.ToString(),
                            confidence = r.Report.Confidence.ToString(),
                            effective = r.Report.Effective,
                            headline = r.Report.Headline,
                            evidence = r.Report.Evidence,
                            contradiction = r.Report.Contradiction,
                            suggestOptOut = r.Report.SuggestOptOut,
                            externalChanges = r.Report.HasExternalChanges,
                            cleanLaunches = r.Report.CleanLaunches,
                        }),
                    };
                    Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }));
                    return 0;
                }

                Console.WriteLine($"Config: {store.Paths.ConfigRoot}   This device: {device.Name}");
                foreach (var (def, r) in results)
                {
                    Console.WriteLine();
                    Print(def, r);
                }
                return 0;
            }
            finally
            {
                Log.Quiet = wasQuiet;
            }
        }));
        return cmd;
    }

    private static void Print(GameDef game, CloudSyncReport r)
    {
        Console.WriteLine($"{game.Id} ({game.Name})");
        var conf = r.Confidence == SyncConfidence.None ? "" : $", {r.ConfidenceText}";
        Console.WriteLine($"  Verdict:   {r.Headline}  [{r.Detected}{conf}]");
        Console.WriteLine($"  Declared:  {(r.Declared switch { true => "settings sync (cloudSyncsSettings: true)", false => "per machine (cloudSyncsSettings: false)", _ => "unknown (cloudSyncsSettings not set)" })}");
        Console.WriteLine("  Evidence:");
        foreach (var line in r.Evidence) Console.WriteLine($"    - {line}");
        if (r.Contradiction is { } c) Console.WriteLine($"  ! {c}");
        Console.WriteLine($"  Opt-out:   {(r.SuggestOptOut ? "safe to opt out if you want to (never done automatically)" : "keep managed")}");
    }
}
