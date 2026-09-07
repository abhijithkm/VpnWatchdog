using VpnWatchdog.Core.Diagnostics;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// <see cref="NetworkThroughputTracker"/> in isolation - no real adapter, no real
/// clock. Every timestamp is an explicit <see cref="DateTimeOffset"/>, matching the
/// convention everywhere else in this project. The properties under test are the
/// ones the doc comment promises: null (not a zero or a garbage rate) whenever
/// there is not yet enough trustworthy information to compute one.
/// </summary>
public class NetworkThroughputTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 7, 8, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void Update_OnTheFirstCall_ReturnsNull_ThereIsNothingToDiffAgainstYet()
    {
        var tracker = new NetworkThroughputTracker();

        ThroughputSample? result = tracker.Update(1000, 500, T0);

        Assert.Null(result);
    }

    [Fact]
    public void Update_OnTheSecondCall_ComputesTheRateFromTheElapsedTime()
    {
        var tracker = new NetworkThroughputTracker();
        tracker.Update(1_000, 500, T0);

        // 10,000 bytes received and 2,000 sent over exactly 2 seconds.
        ThroughputSample? result = tracker.Update(11_000, 2_500, T0.AddSeconds(2));

        Assert.NotNull(result);
        Assert.Equal(5_000, result!.DownloadBytesPerSecond, precision: 3);
        Assert.Equal(1_000, result.UploadBytesPerSecond, precision: 3);
        Assert.Equal(11_000, result.TotalBytesReceived);
        Assert.Equal(2_500, result.TotalBytesSent);
    }

    [Fact]
    public void Update_AccumulatesCorrectlyAcrossManyConsecutiveTicks()
    {
        var tracker = new NetworkThroughputTracker();
        tracker.Update(0, 0, T0);

        for (int tick = 1; tick <= 5; tick++)
        {
            ThroughputSample? result = tracker.Update(tick * 2_000L, tick * 1_000L, T0.AddSeconds(tick));
            Assert.NotNull(result);
            Assert.Equal(2_000, result!.DownloadBytesPerSecond, precision: 3);
            Assert.Equal(1_000, result.UploadBytesPerSecond, precision: 3);
        }
    }

    [Fact]
    public void Update_WhenEitherCounterGoesBackwards_ReturnsNull_NotANegativeRate()
    {
        // A VPN reconnect assigns the adapter a fresh instance whose counters
        // restart at (near) zero - the drop must never be reported as a huge
        // negative download rate.
        var tracker = new NetworkThroughputTracker();
        tracker.Update(50_000, 20_000, T0);

        ThroughputSample? result = tracker.Update(1_000, 500, T0.AddSeconds(2));

        Assert.Null(result);
    }

    [Fact]
    public void Update_AfterACounterReset_RecoversOnTheNextGoodPairOfSamples()
    {
        var tracker = new NetworkThroughputTracker();
        tracker.Update(50_000, 20_000, T0);
        tracker.Update(1_000, 500, T0.AddSeconds(2)); // the reset tick itself: null

        ThroughputSample? result = tracker.Update(3_000, 1_500, T0.AddSeconds(3));

        Assert.NotNull(result);
        Assert.Equal(2_000, result!.DownloadBytesPerSecond, precision: 3);
        Assert.Equal(1_000, result.UploadBytesPerSecond, precision: 3);
    }

    [Fact]
    public void Update_WhenEitherCounterIsNull_ReturnsNull_AndForgetsThePreviousSample()
    {
        var tracker = new NetworkThroughputTracker();
        tracker.Update(1_000, 500, T0);

        ThroughputSample? duringGap = tracker.Update(null, null, T0.AddSeconds(1));
        Assert.Null(duringGap);

        // The gap must not be silently bridged - the next good pair is diffed
        // against nothing, not against the pre-gap baseline.
        ThroughputSample? afterGap = tracker.Update(5_000, 2_000, T0.AddSeconds(2));
        Assert.Null(afterGap);
    }

    [Fact]
    public void Update_WhenElapsedTimeIsBelowTheMinimumInterval_ReturnsNull()
    {
        var tracker = new NetworkThroughputTracker();
        tracker.Update(1_000, 500, T0);

        ThroughputSample? result = tracker.Update(1_100, 550, T0.AddMilliseconds(50));

        Assert.Null(result);
    }

    [Fact]
    public void Update_WhenCountersDoNotChange_ReturnsAZeroRate_NotNull()
    {
        // Idle is a real, valid measurement (0 bytes/sec) and must be
        // distinguishable from "not enough information yet" (null).
        var tracker = new NetworkThroughputTracker();
        tracker.Update(1_000, 500, T0);

        ThroughputSample? result = tracker.Update(1_000, 500, T0.AddSeconds(2));

        Assert.NotNull(result);
        Assert.Equal(0, result!.DownloadBytesPerSecond, precision: 3);
        Assert.Equal(0, result.UploadBytesPerSecond, precision: 3);
    }

    [Fact]
    public void Reset_ForgetsThePreviousSample_SoTheNextCallStartsFreshRatherThanDiffing()
    {
        var tracker = new NetworkThroughputTracker();
        tracker.Update(1_000, 500, T0);

        tracker.Reset();

        ThroughputSample? result = tracker.Update(50_000, 20_000, T0.AddSeconds(2));
        Assert.Null(result);
    }

    [Fact]
    public void DownloadAppearsUnsupported_IsFalse_BeforeEnoughSamplesHaveAccumulated()
    {
        // Confirmed against a real FortiClient tunnel adapter: BytesSent climbs
        // normally while BytesReceived sits at a permanent 0. A handful of
        // upload-only ticks must not immediately flip the verdict - that would
        // misdiagnose an ordinary quiet moment on a normal adapter.
        var tracker = new NetworkThroughputTracker();
        tracker.Update(0, 0, T0);

        for (int tick = 1; tick <= 3; tick++)
        {
            tracker.Update(0, tick * 1_000L, T0.AddSeconds(tick));
        }

        Assert.False(tracker.DownloadAppearsUnsupported);
    }

    [Fact]
    public void DownloadAppearsUnsupported_IsTrue_AfterRepeatedUploadOnlySamples()
    {
        var tracker = new NetworkThroughputTracker();
        tracker.Update(0, 0, T0);

        for (int tick = 1; tick <= 5; tick++)
        {
            tracker.Update(0, tick * 1_000L, T0.AddSeconds(tick));
        }

        Assert.True(tracker.DownloadAppearsUnsupported);
    }

    [Fact]
    public void DownloadAppearsUnsupported_StaysFalse_OnceAnyDownloadHasEverBeenObserved()
    {
        var tracker = new NetworkThroughputTracker();
        tracker.Update(0, 0, T0);
        tracker.Update(500, 1_000, T0.AddSeconds(1)); // one real download tick

        for (int tick = 2; tick <= 6; tick++)
        {
            tracker.Update(500, tick * 1_000L, T0.AddSeconds(tick));
        }

        Assert.False(tracker.DownloadAppearsUnsupported);
    }

    [Fact]
    public void Reset_ClearsTheUnsupportedDownloadVerdict()
    {
        var tracker = new NetworkThroughputTracker();
        tracker.Update(0, 0, T0);
        for (int tick = 1; tick <= 5; tick++)
        {
            tracker.Update(0, tick * 1_000L, T0.AddSeconds(tick));
        }
        Assert.True(tracker.DownloadAppearsUnsupported);

        tracker.Reset();

        Assert.False(tracker.DownloadAppearsUnsupported);
    }
}
