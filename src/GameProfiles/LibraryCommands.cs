using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameProfiles.Config;
using GameProfiles.Engine;
using GameProfiles.Library;
using GameProfiles.Logging;

namespace GameProfiles;

/// <summary>
/// `library [--stores steam,epic] [--queue] [--json]`, `opt-out <key>`, `opt-in <key>`: installed-game detection joined
/// with the game configs. Registered in Program.Main.
/// </summary>
internal static class LibraryCommands
{
    private static AppPaths Paths(ParseResult pr) => AppPaths.Resolve(pr.GetValue<string?>("--config"));

    /// <summary>Same contract as Program.Guard: config errors print one clean line (exit 2), anything else exit 1.</summary>
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

    public static Command LibraryCommand()
    {
        var stores = new Option<string?>("--stores")
        {
            Description = $"Comma-separated launchers to scan ({string.Join(",", GameScanners.Stores)}). Default: all.",
        };
        var queue = new Option<bool>("--queue") { Description = "Queue games without presets in config\\requests.yaml for Claude." };
        var json = new Option<bool>("--json") { Description = "Print the library as JSON." };
        var cmd = new Command("library", "Detect installed games (Steam, Battle.net, EA, Xbox, Epic, GOG) and show which GameProfiles manages.")
        {
            stores, queue, json,
        };
        cmd.SetAction(Guard(pr =>
        {
            bool asJson = pr.GetValue(json);
            var wasQuiet = Log.Quiet;
            if (asJson) Log.Quiet = true; // keep stdout pure JSON (warnings still go to the log file)
            try
            {
                var store = new ConfigStore(Paths(pr));
                var device = GameContext.SelectDevice(store, pr.GetValue<string?>("--profile"));
                var storeList = pr.GetValue(stores)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (storeList is not null)
                {
                    var unknown = storeList.Where(s => !GameScanners.Stores.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (unknown.Count > 0)
                        throw new ConfigException($"Unknown store(s): {string.Join(", ", unknown)} (use {string.Join(", ", GameScanners.Stores)})");
                }

                var detected = GameScanners.ScanAll(storeList);
                var entries = LibraryService.Build(store, device, detected);
                int queued = pr.GetValue(queue) ? LibraryService.QueueRequests(store, entries, device.Name) : -1;

                if (asJson)
                {
                    var doc = new
                    {
                        device = device.Name,
                        configRoot = store.Paths.ConfigRoot,
                        queued = queued >= 0 ? queued : (int?)null,
                        entries = entries.Select(e => new
                        {
                            e.Key,
                            e.Name,
                            Status = e.Status.ToString(),
                            e.OptedOut,
                            e.SuggestOptOut,
                            GameId = e.Game?.Id,
                            e.Detected?.Store,
                            e.Detected?.StoreId,
                            e.Detected?.InstallDir,
                            e.Detected?.LaunchUri,
                            e.Detected?.LaunchExe,
                            LaunchArgs = e.Detected?.LaunchArgs,
                            e.Detected?.ArtPath,
                            Exes = e.Detected?.Exes,
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
                var scanned = storeList ?? GameScanners.Stores.ToArray();
                Console.WriteLine("Detected: " + string.Join(", ",
                    scanned.Select(s => $"{s.ToLowerInvariant()} {detected.Count(d => d.Store.Equals(s, StringComparison.OrdinalIgnoreCase))}")));
                Console.WriteLine();
                int kw = Math.Max(10, entries.Select(e => e.Key.Length).DefaultIfEmpty(0).Max());
                Console.WriteLine($"  {"STATUS",-13} {"KEY".PadRight(kw)} {"NAME",-40} GAME");
                foreach (var e in entries)
                {
                    var note = e.SuggestOptOut ? "  (settings don't sync: consider opt-out)" : "";
                    Console.WriteLine($"  {e.Status,-13} {e.Key.PadRight(kw)} {Trunc(e.Name, 40),-40} {e.Game?.Id ?? "-"}{note}");
                }
                Console.WriteLine();
                Console.WriteLine("  " + string.Join(", ", Enum.GetValues<LibraryStatus>()
                    .Select(s => $"{s}: {entries.Count(e => e.Status == s)}")));
                if (queued >= 0)
                    Console.WriteLine($"Queued {queued} new request(s) in {store.Paths.RequestsFile} " +
                                      $"({LibraryService.LoadRequests(store).Requests.Count} pending).");
                else if (entries.Any(e => e.Status == LibraryStatus.NeedsPresets && !e.OptedOut))
                    Console.WriteLine("Run `gameprofiles library --queue` to queue the NeedsPresets games for Claude.");
                return 0;
            }
            finally
            {
                Log.Quiet = wasQuiet;
            }
        }));
        return cmd;
    }

    public static Command OptOutCommand() => OptCommand("opt-out", optOut: true,
        "Stop managing a detected game (store:id from `library`, e.g. steam:1285190): it launches plainly.");

    public static Command OptInCommand() => OptCommand("opt-in", optOut: false,
        "Manage a previously opted-out game again (store:id from `library`).");

    private static Command OptCommand(string name, bool optOut, string description)
    {
        var keyArg = new Argument<string>("key") { Description = "Library key: store:id (e.g. steam:1285190, epic:Fortnite, battlenet:Fen)." };
        var cmd = new Command(name, description) { keyArg };
        cmd.SetAction(Guard(pr =>
        {
            var key = pr.GetValue(keyArg)!.Trim();
            var colon = key.IndexOf(':');
            if (colon <= 0 || colon == key.Length - 1)
                throw new ConfigException($"'{key}' is not a library key — use store:id as shown by `gameprofiles library`.");
            var storeName = key[..colon].ToLowerInvariant();
            if (!GameScanners.Stores.Contains(storeName) && storeName != "config")
                throw new ConfigException($"Unknown store '{storeName}' (use {string.Join(", ", GameScanners.Stores)})");
            key = storeName + key[colon..];

            var store = new ConfigStore(Paths(pr));
            var before = LibraryService.LoadLibrary(store).OptOut.Contains(key, StringComparer.OrdinalIgnoreCase);
            LibraryService.SetOptOut(store, key, optOut);
            Console.WriteLine(before == optOut
                ? $"{key} was already {(optOut ? "opted out" : "managed")}."
                : $"{key}: {(optOut ? "opted out — GameProfiles leaves it alone" : "opted in — managed again")} ({store.Paths.LibraryFile}).");
            return 0;
        }));
        return cmd;
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
