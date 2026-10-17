namespace OptimusVibe.CloudSync;

/// <summary>Where a game's settings sync report lands for the library's "Settings sync" filter.</summary>
public enum SyncBucket { Maybe, StaysOnDevice, Syncs }

public static class SyncBuckets
{
    /// <summary>
    /// Detected NotSynced with medium or high confidence → stays on the device. Detected Synced with medium or better
    /// confidence, or any strong observation (settings arrived matching another device's preset) → syncs. Everything
    /// else (unknown, low confidence, no report, game not configured) → maybe.
    /// </summary>
    public static SyncBucket Classify(CloudSyncReport? report)
    {
        if (report is null) return SyncBucket.Maybe;
        if (report.StrongChange is not null) return SyncBucket.Syncs;
        return report.Detected switch
        {
            SyncVerdict.NotSynced when report.Confidence >= SyncConfidence.Medium => SyncBucket.StaysOnDevice,
            SyncVerdict.Synced when report.Confidence >= SyncConfidence.Medium => SyncBucket.Syncs,
            _ => SyncBucket.Maybe,
        };
    }
}
