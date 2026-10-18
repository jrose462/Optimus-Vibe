using OptimusVibe.Config;
using OptimusVibe.Outbox;

namespace OptimusVibe.GameLab;

/// <summary>
/// The counts behind the library note ("1 in the game lab · 1 waiting to send · 2 waiting on ally-x"). Pure; build with <see cref="Build"/>.
/// </summary>
/// <param name="Lab">Open lab requests that are queued, researching or waiting for evidence.</param>
/// <param name="WaitingToSend">Lab requests stored in the offline outbox.</param>
/// <param name="WaitingOnOtherDevices">Device id → entries of requests.yaml queued by another device (only when the lab is set up), largest first.</param>
/// <param name="LocalOnly">Entries of requests.yaml when the lab is NOT set up (they wait for Claude to be run on the config repo).</param>
/// <param name="LabReady">The game lab is set up.</param>
public sealed record QueueSummary(int Lab, int WaitingToSend, IReadOnlyList<KeyValuePair<string, int>> WaitingOnOtherDevices, int LocalOnly, bool LabReady)
{
    public static QueueSummary Build(IEnumerable<LabIssue>? openLabIssues, IEnumerable<OutboxItem> outbox, IEnumerable<GameRequest> requests,
        string thisDevice, bool labReady)
    {
        var lab = openLabIssues?.Count(i => i.IsOpen && i.Status is LabStatus.Queued or LabStatus.Researching or LabStatus.NeedsEvidence) ?? 0;
        var waiting = outbox.Count(i => i.Kind == OutboxKind.LabRequest);
        var list = requests.ToList();
        if (!labReady) return new QueueSummary(lab, waiting, [], list.Count, false);

        var others = list
            .Where(r => !string.Equals(r.Device, thisDevice, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Device) ? "another device" : r.Device!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new QueueSummary(lab, waiting, others, 0, true);
    }

    public bool IsEmpty => Lab == 0 && WaitingToSend == 0 && LocalOnly == 0 && WaitingOnOtherDevices.Count == 0;

    /// <summary>The note, zero parts omitted; empty when there is nothing. Device ids are shown as given.</summary>
    public string Text => Describe(null);

    /// <summary>Like <see cref="Text"/> with device ids mapped to display names.</summary>
    public string Describe(Func<string, string>? displayName)
    {
        var parts = new List<string>();
        if (!LabReady && LocalOnly > 0) parts.Add($"{LocalOnly} {(LocalOnly == 1 ? "game" : "games")} queued for Claude");
        if (Lab > 0) parts.Add($"{Lab} in the game lab");
        if (WaitingToSend > 0) parts.Add($"{WaitingToSend} waiting to send");
        foreach (var (device, count) in WaitingOnOtherDevices)
            parts.Add($"{count} waiting on {displayName?.Invoke(device) ?? device}");
        return string.Join(" · ", parts);
    }
}
