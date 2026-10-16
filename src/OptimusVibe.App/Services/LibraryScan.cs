using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Library;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>Outcome of one library pass. Each step is independent: a failing scanner never hides the configured games.</summary>
public sealed class LibraryScanResult
{
    public IReadOnlyList<DetectedGame> Detected { get; init; } = [];
    public IReadOnlyList<LibraryEntry> Entries { get; init; } = [];

    /// <summary>Requests added to config/requests.yaml by this pass.</summary>
    public int Queued { get; init; }

    /// <summary>Requests currently waiting in config/requests.yaml (null if unknown).</summary>
    public int? Pending { get; init; }

    /// <summary>Steam launch options that differ from what the library wants.</summary>
    public IReadOnlyList<LaunchOptionChange> SteamChanges { get; init; } = [];

    public bool SteamRunning { get; init; }

    /// <summary>Changes written by this pass (Steam was closed).</summary>
    public int SteamApplied { get; init; }

    /// <summary>Problems worth one toast (scanner unavailable, ...).</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>Scan launchers → join with game configs → queue requests → plan (and maybe apply) Steam launch options.</summary>
public static class LibraryScan
{
    /// <param name="detected">Reuse a previous launcher scan (opt-out toggles, settings changes); null = scan now.</param>
    public static LibraryScanResult Run(AppSession session, IReadOnlyList<GameSnapshot> games, IReadOnlyList<DetectedGame>? detected, bool queue)
    {
        var settings = SettingsStore.Load();
        var warnings = new List<string>();

        if (detected is null)
        {
            try
            {
                detected = GameScanners.ScanAll(settings.ScanStores);
                Log.Info($"Library scan: {detected.Count} installed game(s) in {string.Join(", ", settings.ScanStores)}");
            }
            catch (NotImplementedException)
            {
                warnings.Add("Launcher scanning isn't available in this build yet — showing configured games only.");
                detected = [];
            }
            catch (Exception ex)
            {
                Log.Error("Library scan failed", ex);
                warnings.Add($"Scanning launchers failed: {ex.Message}");
                detected = [];
            }
        }

        IReadOnlyList<LibraryEntry> entries;
        try
        {
            entries = LibraryService.Build(session.Store, session.Device, detected, new CloudSyncChecker(session.Store, session.Device));
        }
        catch (Exception ex)
        {
            if (ex is not NotImplementedException) Log.Error("Library build failed", ex);
            entries = Fallback(games);
        }

        int queued = 0;
        int? pending = null;
        if (queue)
        {
            try
            {
                var needs = entries.Where(e => e.Status == LibraryStatus.NeedsPresets && !e.OptedOut).ToList();
                if (needs.Count > 0) queued = LibraryService.QueueRequests(session.Store, needs, session.Device.Name);
                pending = LibraryService.LoadRequests(session.Store).Requests.Count;
                if (queued > 0) Log.Info($"Queued {queued} game(s) for presets in {session.Paths.RequestsFile}");
            }
            catch (NotImplementedException) { }
            catch (Exception ex)
            {
                Log.Error("Queueing requests failed", ex);
                warnings.Add($"Couldn't queue games for presets: {ex.Message}");
            }
        }

        IReadOnlyList<LaunchOptionChange> changes = [];
        bool running = false;
        int applied = 0;
        if (settings.WriteSteamLaunchOptions)
        {
            try
            {
                changes = PlanSteam(session);
                if (changes.Count > 0)
                {
                    running = SteamLaunchOptions.IsSteamRunning();
                    if (!running)
                    {
                        SteamLaunchOptions.Apply(changes);
                        applied = changes.Count;
                        Log.Info($"Steam launch options updated for {applied} game(s)");
                        changes = [];
                    }
                }
            }
            catch (NotImplementedException) { changes = []; }
            catch (Exception ex)
            {
                Log.Error("Steam launch options failed", ex);
                warnings.Add($"Couldn't update Steam launch options: {ex.Message}");
                changes = [];
            }
        }

        var result = new LibraryScanResult
        {
            Detected = detected,
            Entries = entries,
            Queued = queued,
            Pending = pending,
            SteamChanges = changes,
            SteamRunning = running,
            SteamApplied = applied,
        };
        result.Warnings.AddRange(warnings);
        return result;
    }

    /// <summary>Managed Steam games carry the wrapper; opted-out ones don't (targets from Core's library rules).</summary>
    public static IReadOnlyList<LaunchOptionChange> PlanSteam(AppSession session)
    {
        var (enable, disable) = SteamLaunchOptions.ManagedTargets(session.Store, session.Device.Name);
        return SteamLaunchOptions.Plan(enable, disable, SteamLaunchOptions.DefaultWrapperExe());
    }

    /// <summary>Until the library service exists: configured games only, Managed when they have presets here.</summary>
    private static IReadOnlyList<LibraryEntry> Fallback(IReadOnlyList<GameSnapshot> games) =>
        games.Where(g => g.Game is not null)
            .Select(g => new LibraryEntry
            {
                Key = g.Game!.SteamAppId is { } a ? $"steam:{a}" : $"{g.Game.Store}:{g.Id}",
                Name = g.Name,
                Game = g.Game,
                Status = g.Context?.Profile.IsEmpty == false ? LibraryStatus.Managed : LibraryStatus.NeedsPresets,
            })
            .ToList();
}
