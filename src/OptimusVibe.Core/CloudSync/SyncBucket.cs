namespace OptimusVibe.CloudSync;

/// <summary>Where a game's settings sync report lands for the library's "Settings sync" filter.</summary>
public enum SyncBucket { Maybe, StaysOnDevice, Syncs }

public static class SyncBuckets
{
    /// <summary>
    /// The same answer the game's page shows (<see cref="CloudSyncReport.Effective"/>: detection, else the researched
    /// claim), so the library filter and the page never disagree. A strong observation (settings arrived matching
    /// another device's preset) → syncs. No report, or nothing detected or researched → maybe.
    /// </summary>
    public static SyncBucket Classify(CloudSyncReport? report)
    {
        if (report is null) return SyncBucket.Maybe;
        if (report.StrongChange is not null) return SyncBucket.Syncs;
        return report.Effective switch
        {
            true => SyncBucket.Syncs,
            false => SyncBucket.StaysOnDevice,
            _ => SyncBucket.Maybe,
        };
    }

    /// <summary>
    /// The bucket rests on what was seen on this device (a strong observation, or detection with medium or high
    /// confidence), not only on research or a weak hint.
    /// </summary>
    public static bool IsConfirmed(CloudSyncReport? report) =>
        report is not null &&
        (report.StrongChange is not null || (report.Detected != SyncVerdict.Unknown && report.Confidence >= SyncConfidence.Medium));

    /// <summary>How sure the bucket is, for a list row: "high confidence", "seen syncing", "from research", "not known yet".</summary>
    public static string ConfidenceLabel(CloudSyncReport? report) => report switch
    {
        { StrongChange: not null } => "seen syncing",
        { ConfidenceText: { Length: > 0 } c } => c,
        { Declared: not null } => "from research",
        _ => "not known yet",
    };
}
