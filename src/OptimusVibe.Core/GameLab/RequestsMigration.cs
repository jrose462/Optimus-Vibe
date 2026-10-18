using OptimusVibe.Config;
using OptimusVibe.Library;

namespace OptimusVibe.GameLab;

/// <summary>
/// Moves entries of the local queue (config/requests.yaml) into the game lab once it is set up. An entry may be sent from THIS
/// PC only when it was queued by this device AND the game is installed here (the lab collects evidence from the local install).
/// Entries of other devices stay put until that device sends them.
/// </summary>
public static class RequestsMigration
{
    /// <summary>The entries that should be filed in the lab from here: Device equals <paramref name="thisDevice"/> and the game is among <paramref name="installed"/> (by store + storeId).</summary>
    public static IReadOnlyList<GameRequest> ToSendFromHere(IEnumerable<GameRequest> requests, string thisDevice, IEnumerable<DetectedGame> installed)
    {
        var here = installed.Select(d => Key(d.Store, d.StoreId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requests
            .Where(r => string.Equals(r.Device, thisDevice, StringComparison.OrdinalIgnoreCase) && here.Contains(Key(r.Store, r.StoreId)))
            .ToList();
    }

    /// <summary>The same selection, paired with the detected install to build the evidence target from.</summary>
    public static IReadOnlyList<(GameRequest Request, DetectedGame Install)> ToSendFromHereWithInstalls(IEnumerable<GameRequest> requests, string thisDevice,
        IEnumerable<DetectedGame> installed)
    {
        var map = new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in installed) map.TryAdd(Key(d.Store, d.StoreId), d);
        return requests
            .Where(r => string.Equals(r.Device, thisDevice, StringComparison.OrdinalIgnoreCase) && map.ContainsKey(Key(r.Store, r.StoreId)))
            .Select(r => (r, map[Key(r.Store, r.StoreId)]))
            .ToList();
    }

    /// <summary>The lab target for a queued entry (a new game without a config: <see cref="LabKind.New"/>).</summary>
    public static LabTarget ToTarget(GameRequest request, DetectedGame? install = null) => new()
    {
        Name = string.IsNullOrWhiteSpace(request.Name) ? install?.Name ?? request.StoreId : request.Name,
        Store = request.Store,
        StoreId = request.StoreId,
        InstallDir = install?.InstallDir ?? request.InstallDir,
        Exes = install?.Exes ?? request.Exes,
        Detected = install,
    };

    /// <summary>Removes a filed entry from requests.yaml (only the one this device made); the rest of the file is untouched. False when it was already gone.</summary>
    public static bool RemoveFiled(ConfigStore store, GameRequest request) =>
        LibraryService.RemoveRequest(store, request.Store, request.StoreId, request.Device);

    private static string Key(string store, string storeId) => $"{store}:{storeId}";
}
