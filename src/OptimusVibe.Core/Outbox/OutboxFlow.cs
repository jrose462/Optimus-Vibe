using OptimusVibe.Feedback;
using OptimusVibe.GameLab;
using OptimusVibe.Logging;
using OptimusVibe.Sync;

namespace OptimusVibe.Outbox;

/// <summary>Outcome of "send now, or queue when offline" for a bug report. Exactly one of the two is set.</summary>
/// <param name="Url">The issue URL when it was sent.</param>
/// <param name="Queued">The outbox item when we are offline and the report is waiting to be sent.</param>
public sealed record BugReportOutcome(string? Url, OutboxItem? Queued)
{
    public bool WasQueued => Queued is not null;
}

/// <summary>Outcome of "file now, or queue when offline" for a lab request. Exactly one of the two is set.</summary>
public sealed record LabRequestOutcome(FiledRequest? Filed, OutboxItem? Queued)
{
    public bool WasQueued => Queued is not null;
}

/// <summary>The try-now-else-queue helpers and the real send delegate for <see cref="OutboxSender.DrainAsync"/>.</summary>
public static class OutboxFlow
{
    // ------------------------------------------------------------ bug reports

    /// <summary>
    /// Queues a report. The daily limit (<see cref="BugReportLimiter"/>) is checked and recorded HERE, at queue time, and not again when
    /// the report is finally sent. Throws <see cref="GitHubException"/> when today's quota is used up.
    /// </summary>
    public static OutboxItem QueueBugReport(OutboxStore store, BugReportDraft draft, bool asSignedInUser, string? dataDir = null)
    {
        BugReportLimiter.EnsureAllowed(dataDir);
        var item = store.Add(OutboxItem.ForBugReport(draft, asSignedInUser));
        BugReportLimiter.Record(dataDir);
        return item;
    }

    /// <summary>
    /// <see cref="BugReport.SubmitAsync(BugReportDraft, string?, CancellationToken, string?)"/>; when that fails because we are offline the
    /// report is queued instead (counted against the daily limit now). Any other failure is thrown as before. Pass the user token
    /// exactly as for SubmitAsync (null = built-in); the token itself is never stored, only "asSignedInUser".
    /// </summary>
    public static async Task<BugReportOutcome> SendBugReportAsync(OutboxStore store, BugReportDraft draft, string? userToken,
        CancellationToken ct = default, string? dataDir = null)
    {
        try
        {
            return new BugReportOutcome(await BugReport.SubmitAsync(draft, userToken, ct, dataDir).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (OfflineDetector.IsOffline(ex, ct))
        {
            Log.Info($"bug report: offline ({ex.Message}); queueing");
            return new BugReportOutcome(null, QueueBugReport(store, draft, !string.IsNullOrWhiteSpace(userToken), dataDir));
        }
    }

    // ------------------------------------------------------------ lab requests

    /// <summary>
    /// <see cref="GameLabClient.FileRequestAsync(LabKind, LabTarget, string, EvidenceCollector, CancellationToken)"/> with an offline fallback:
    /// the evidence is collected once, on this PC, and either posted or stored in the outbox exactly as collected (no network is
    /// needed to queue). An already open request for the same game is still reused when we can reach GitHub (nothing is queued then).
    /// </summary>
    public static async Task<LabRequestOutcome> FileLabRequestAsync(OutboxStore store, GameLabClient client, LabKind kind, LabTarget target,
        string device, EvidenceCollector collector, CancellationToken ct = default)
    {
        var info = GameLabClient.InfoFor(kind, target, device);
        var offline = false;
        try
        {
            var existing = await client.FindOpenAsync(info, ct).ConfigureAwait(false);
            if (existing is not null)
                return new LabRequestOutcome(new FiledRequest(existing.Number, existing.Url, true, 0,
                    $"A {GameLabContract.KindText(kind)} request for {target.Name} is already open (#{existing.Number}, {GameLabContract.StatusText(existing.Status)})."), null);
        }
        catch (Exception ex) when (OfflineDetector.IsOffline(ex, ct)) { offline = true; }

        var prepared = GameLabClient.PrepareRequest(kind, target, device, collector);   // collected once; what the user confirms
        if (!offline)
        {
            try
            {
                return new LabRequestOutcome(await client.FilePreparedAsync(prepared, skipDedupe: true, ct).ConfigureAwait(false), null);
            }
            catch (Exception ex) when (OfflineDetector.IsOffline(ex, ct)) { offline = true; }
        }
        return new LabRequestOutcome(null, store.Add(OutboxItem.ForLabRequest(client.Repo, client.Branch, prepared)));
    }

    // ------------------------------------------------------------ draining

    /// <summary>
    /// The send delegate for <see cref="OutboxSender.DrainAsync"/>: bug reports go through <see cref="BugReport.SubmitQueuedAsync"/> (no
    /// second limit check), lab requests through <see cref="GameLabClient.FilePreparedAsync"/> (same dedupe, same stored evidence).
    /// Returns the issue URL. A report queued "as the signed-in user" is filed with <paramref name="userToken"/> when there is one now,
    /// else with the built-in access.
    /// </summary>
    /// <param name="userToken">The current GitHub sign-in token, or null.</param>
    /// <param name="labClient">Builds the client for (repo, branch) of a stored lab request; null when the user is signed out or the lab is gone (the item then fails and stays).</param>
    /// <param name="submitReport">Test seam; defaults to <see cref="BugReport.SubmitQueuedAsync"/>.</param>
    public static Func<OutboxItem, CancellationToken, Task<string>> CreateSender(Func<string?> userToken,
        Func<string, string, GameLabClient?> labClient,
        Func<BugReportDraft, string?, CancellationToken, Task<string>>? submitReport = null) =>
        async (item, ct) =>
        {
            if (item.Kind == OutboxKind.BugReport)
            {
                var p = item.BugReport;
                return await (submitReport ?? BugReport.SubmitQueuedAsync)(p.ToDraft(), p.AsSignedInUser ? userToken() : null, ct).ConfigureAwait(false);
            }
            var lab = item.LabRequest;
            var client = labClient(lab.Repo, lab.Branch)
                         ?? throw new GitHubException($"Can't send the queued request for {lab.Name}: sign in to GitHub and set up the game lab for {lab.Repo}.");
            var filed = await client.FilePreparedAsync(lab.ToPrepared(), ct: ct).ConfigureAwait(false);
            return filed.Url;
        };
}
