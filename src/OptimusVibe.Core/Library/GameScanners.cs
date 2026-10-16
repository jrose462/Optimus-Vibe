using System.Diagnostics;
using OptimusVibe.Library.Scanners;
using OptimusVibe.Logging;

namespace OptimusVibe.Library;

/// <summary>The launcher scanners (one <see cref="IGameScanner"/> per launcher) and a parallel scan over them.</summary>
public static class GameScanners
{
    /// <summary>Store ids in display order: steam, battlenet, ea, xbox, epic, gog.</summary>
    public static readonly IReadOnlyList<string> Stores = ["steam", "battlenet", "ea", "xbox", "epic", "gog"];

    /// <summary>Scanners for: steam, battlenet, ea, xbox, epic, gog.</summary>
    public static IReadOnlyList<IGameScanner> All() =>
        [new SteamScanner(), new BattleNetScanner(), new EaScanner(), new XboxScanner(), new EpicScanner(), new GogScanner()];

    /// <summary>Run the scanners for the given stores (null = all), de-duplicated by Key, sorted by Name.</summary>
    public static IReadOnlyList<DetectedGame> ScanAll(IEnumerable<string>? stores = null) => ScanAll(All(), stores);

    /// <summary>Scanners run in parallel; one failing or throwing scanner yields nothing and is logged.</summary>
    internal static IReadOnlyList<DetectedGame> ScanAll(IEnumerable<IGameScanner> scanners, IEnumerable<string>? stores)
    {
        var wanted = stores?.Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = scanners.Where(s => wanted is null || wanted.Contains(s.Store)).ToList();
        var tasks = selected.Select(s => Task.Run(() => SafeScan(s))).ToArray();
        Task.WaitAll(tasks);
        return tasks.SelectMany(t => t.Result)
            .DistinctBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DetectedGame> SafeScan(IGameScanner scanner)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var games = scanner.Scan().ToList();
            Log.Info($"Library scan {scanner.Store}: {games.Count} game(s) in {sw.ElapsedMilliseconds} ms");
            return games;
        }
        catch (Exception ex)
        {
            Log.Warn($"Library scan {scanner.Store} failed after {sw.ElapsedMilliseconds} ms: {ex.Message}");
            return [];
        }
    }
}
