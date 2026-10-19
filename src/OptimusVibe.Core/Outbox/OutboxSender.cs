using System.Collections.Concurrent;
using OptimusVibe.Logging;

namespace OptimusVibe.Outbox;

/// <summary>An item that was sent; <paramref name="Result"/> is whatever the send delegate returned (the issue URL).</summary>
public sealed record OutboxSent(OutboxItem Item, string Result);

/// <summary>An item that failed for a reason other than being offline; it stays queued with <see cref="OutboxItem.Attempts"/> bumped.</summary>
public sealed record OutboxFailed(OutboxItem Item, string Error);

/// <param name="Sent">Items sent and removed from the outbox, oldest first.</param>
/// <param name="Failed">Items that failed with a non-network error (kept for the next drain).</param>
/// <param name="StillOffline">The drain stopped at the first network failure; everything not yet sent is still queued.</param>
/// <param name="Remaining">Items left in the outbox afterwards.</param>
/// <param name="Busy">Another drain is running; this call did nothing.</param>
/// <param name="Cancelled">The token was cancelled mid-drain.</param>
public sealed record OutboxDrainResult(IReadOnlyList<OutboxSent> Sent, IReadOnlyList<OutboxFailed> Failed, bool StillOffline, int Remaining,
    bool Busy = false, bool Cancelled = false)
{
    public static OutboxDrainResult BusyResult(int remaining) => new([], [], false, remaining, Busy: true);
}

/// <summary>
/// Sends the outbox oldest first through a delegate (the App/CLI supplies the real GitHub calls, see <see cref="OutboxFlow.CreateSender"/>).
/// Only one drain runs at a time per outbox folder in this process; a second call returns <see cref="OutboxDrainResult.Busy"/>.
/// </summary>
public sealed class OutboxSender(OutboxStore store)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public OutboxStore Store { get; } = store;

    /// <summary>
    /// For each queued item, oldest first: send; success removes the item; a network failure (<see cref="OfflineDetector"/>) stops the whole
    /// drain and keeps the rest; any other failure records the error, bumps Attempts, keeps the item and moves on to the next one.
    /// </summary>
    public async Task<OutboxDrainResult> DrainAsync(Func<OutboxItem, CancellationToken, Task<string>> send, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(Store.Dir), _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return OutboxDrainResult.BusyResult(Store.Count);
        try
        {
            var sent = new List<OutboxSent>();
            var failed = new List<OutboxFailed>();
            var offline = false;
            var cancelled = false;
            foreach (var item in Store.List())
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                try
                {
                    var result = await send(item, ct).ConfigureAwait(false);
                    Store.Remove(item.Id);
                    sent.Add(new OutboxSent(item, result));
                    Log.Info($"outbox: sent {item.Kind} {item.Id}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex) when (OfflineDetector.IsOffline(ex, ct))
                {
                    Log.Info($"outbox: still offline ({ex.Message}); {item.Kind} {item.Id} stays queued");
                    offline = true;
                    break;
                }
                catch (Exception ex)
                {
                    var updated = item with { Attempts = item.Attempts + 1, LastError = ex.Message, LastAttemptUtc = DateTime.UtcNow };
                    Store.Update(updated);
                    failed.Add(new OutboxFailed(updated, ex.Message));
                    Log.Warn($"outbox: {item.Kind} {item.Id} failed (attempt {updated.Attempts}): {ex.Message}");
                }
            }
            return new OutboxDrainResult(sent, failed, offline, Store.Count, Cancelled: cancelled);
        }
        finally
        {
            gate.Release();
        }
    }
}
