using OptimusVibe.CloudSync;

namespace OptimusVibe.Tests;

public sealed class SyncBucketTests
{
    private static CloudSyncReport R(SyncVerdict v, SyncConfidence c, ExternalChangeRecord? strong = null, bool? declared = null) =>
        new() { GameId = "g", Device = "d", Detected = v, Confidence = c, StrongChange = strong, Declared = declared };

    [Theory]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.Medium, SyncBucket.StaysOnDevice)]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.High, SyncBucket.StaysOnDevice)]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.Low, SyncBucket.StaysOnDevice)]
    [InlineData(SyncVerdict.Synced, SyncConfidence.Medium, SyncBucket.Syncs)]
    [InlineData(SyncVerdict.Synced, SyncConfidence.High, SyncBucket.Syncs)]
    [InlineData(SyncVerdict.Synced, SyncConfidence.Low, SyncBucket.Syncs)]
    [InlineData(SyncVerdict.Unknown, SyncConfidence.None, SyncBucket.Maybe)]
    public void Classify(SyncVerdict verdict, SyncConfidence confidence, SyncBucket expected) =>
        Assert.Equal(expected, SyncBuckets.Classify(R(verdict, confidence)));

    [Fact]
    public void NoReport_IsMaybe() => Assert.Equal(SyncBucket.Maybe, SyncBuckets.Classify(null));

    [Fact]
    public void StrongObservation_Syncs_EvenAtLowConfidence() =>
        Assert.Equal(SyncBucket.Syncs, SyncBuckets.Classify(R(SyncVerdict.Synced, SyncConfidence.Low,
            new ExternalChangeRecord { FromDevice = "desktop" })));

    [Theory]
    [InlineData(false, SyncBucket.StaysOnDevice)]
    [InlineData(true, SyncBucket.Syncs)]
    public void Declared_WithNothingDetected_MatchesTheGamePage(bool declared, SyncBucket expected)
    {
        var r = R(SyncVerdict.Unknown, SyncConfidence.None, declared: declared);
        Assert.Equal(expected, SyncBuckets.Classify(r));
        Assert.Equal(r.Effective, expected == SyncBucket.Syncs); // same answer as the page's headline
        Assert.False(SyncBuckets.IsConfirmed(r));
        Assert.Equal("from research", SyncBuckets.ConfidenceLabel(r));
    }

    [Fact]
    public void WeakDetection_AgainstResearch_FollowsResearch() =>
        Assert.Equal(SyncBucket.Syncs, SyncBuckets.Classify(R(SyncVerdict.NotSynced, SyncConfidence.Low, declared: true)));

    [Theory]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.Medium, true)]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.High, true)]
    [InlineData(SyncVerdict.NotSynced, SyncConfidence.Low, false)]
    [InlineData(SyncVerdict.Synced, SyncConfidence.Medium, true)]
    [InlineData(SyncVerdict.Unknown, SyncConfidence.High, false)]
    public void IsConfirmed(SyncVerdict verdict, SyncConfidence confidence, bool expected) =>
        Assert.Equal(expected, SyncBuckets.IsConfirmed(R(verdict, confidence)));

    [Fact]
    public void ConfidenceLabel_Cases()
    {
        Assert.Equal("not known yet", SyncBuckets.ConfidenceLabel(null));
        Assert.Equal("high confidence", SyncBuckets.ConfidenceLabel(R(SyncVerdict.NotSynced, SyncConfidence.High)));
        Assert.Equal("seen syncing", SyncBuckets.ConfidenceLabel(R(SyncVerdict.Synced, SyncConfidence.Low, new ExternalChangeRecord { FromDevice = "desktop" })));
    }
}
