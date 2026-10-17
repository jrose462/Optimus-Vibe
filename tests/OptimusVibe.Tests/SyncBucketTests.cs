using OptimusVibe.CloudSync;

namespace OptimusVibe.Tests;

public sealed class SyncBucketTests
{
    private static CloudSyncReport R(SyncVerdict v, SyncConfidence c, ExternalChangeRecord? strong = null, bool? declared = null) =>
        new() { GameId = "g", Device = "d", Detected = v, Confidence = c, StrongChange = strong, Declared = declared };

    [Theory]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.Medium, SyncBucket.StaysOnDevice)]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.High, SyncBucket.StaysOnDevice)]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.Low, SyncBucket.Maybe)]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.None, SyncBucket.Maybe)]
    [InlineData(SyncVerdict.Synced, SyncConfidence.Medium, SyncBucket.Syncs)]
    [InlineData(SyncVerdict.Synced, SyncConfidence.High, SyncBucket.Syncs)]
    [InlineData(SyncVerdict.Synced, SyncConfidence.Low, SyncBucket.Maybe)]
    [InlineData(SyncVerdict.Unknown, SyncConfidence.None, SyncBucket.Maybe)]
    [InlineData(SyncVerdict.Unknown, SyncConfidence.High, SyncBucket.Maybe)]
    public void Classify(SyncVerdict verdict, SyncConfidence confidence, SyncBucket expected) =>
        Assert.Equal(expected, SyncBuckets.Classify(R(verdict, confidence)));

    [Fact]
    public void NoReport_IsMaybe() => Assert.Equal(SyncBucket.Maybe, SyncBuckets.Classify(null));

    [Fact]
    public void StrongObservation_Syncs_EvenAtLowConfidence() =>
        Assert.Equal(SyncBucket.Syncs, SyncBuckets.Classify(R(SyncVerdict.Synced, SyncConfidence.Low,
            new ExternalChangeRecord { FromDevice = "desktop" })));

    [Fact]
    public void DeclaredAlone_IsStillMaybe() =>
        Assert.Equal(SyncBucket.Maybe, SyncBuckets.Classify(R(SyncVerdict.Unknown, SyncConfidence.None, declared: false)));
}
