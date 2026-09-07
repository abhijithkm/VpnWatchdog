namespace VpnWatchdog.Core.Diagnostics;

/// <summary>
/// One rate measurement, downstream and upstream in bytes/second, plus the raw
/// cumulative totals it was computed from (useful for showing "X total this
/// session" alongside the live rate).
/// </summary>
public sealed record ThroughputSample(
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    long TotalBytesReceived,
    long TotalBytesSent);

/// <summary>
/// Turns successive cumulative byte counters (as read straight off the OS by
/// <see cref="AdapterConnectionStateProvider"/> into <see cref="AdapterSnapshot"/>)
/// into a live throughput rate. Pure, deterministic, stateful ONLY in the sense
/// that it remembers the previous sample - every timestamp is supplied by the
/// caller, never read from the clock itself, so this is fully unit-testable
/// without a real adapter or real elapsed wall-clock time.
/// <para>
/// One instance belongs to one monitoring session: construct a fresh one every
/// time monitoring (re)starts, the same way the correlator and reconnect policy
/// are also rebuilt fresh - carrying counters across a Start/Stop cycle would
/// produce a bogus rate spike (or a bogus negative) the moment monitoring
/// resumes against what might now be a different adapter instance.
/// </para>
/// </summary>
public sealed class NetworkThroughputTracker
{
    private long? _lastBytesReceived;
    private long? _lastBytesSent;
    private DateTimeOffset? _lastObservedAt;

    // Below this, a rate computed from the elapsed time is not trustworthy - two
    // observations arriving unrealistically close together (a duplicate poll, a
    // clock quirk) would otherwise produce a wildly inflated or infinite rate.
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Folds in one new observation. Returns null - not a zero-rate sample - when
    /// there is not yet enough information for a trustworthy rate: the very first
    /// call, right after a reset (either input null, e.g. the adapter momentarily
    /// unreadable), when either counter has gone BACKWARDS since the last call
    /// (the underlying adapter instance was very likely recreated, restarting its
    /// counters near 0 - so the "since last time" delta is meaningless, not
    /// negative), or when the elapsed time is below <see cref="MinimumInterval"/>.
    /// </summary>
    public ThroughputSample? Update(long? bytesReceived, long? bytesSent, DateTimeOffset now)
    {
        // No evidence this tick (adapter not found, stats unsupported) - forget
        // whatever came before so the NEXT good pair does not get diffed against
        // a stale, possibly now-meaningless baseline.
        if (bytesReceived is not { } rx || bytesSent is not { } tx)
        {
            Reset();
            return null;
        }

        ThroughputSample? result = null;
        if (_lastBytesReceived is { } prevRx && _lastBytesSent is { } prevTx && _lastObservedAt is { } prevAt)
        {
            TimeSpan elapsed = now - prevAt;
            if (elapsed >= MinimumInterval && rx >= prevRx && tx >= prevTx)
            {
                double seconds = elapsed.TotalSeconds;
                result = new ThroughputSample(
                    DownloadBytesPerSecond: (rx - prevRx) / seconds,
                    UploadBytesPerSecond: (tx - prevTx) / seconds,
                    TotalBytesReceived: rx,
                    TotalBytesSent: tx);
            }
        }

        _lastBytesReceived = rx;
        _lastBytesSent = tx;
        _lastObservedAt = now;
        return result;
    }

    /// <summary>Forgets the previous sample, so the next call cannot be diffed against a stale baseline.</summary>
    public void Reset()
    {
        _lastBytesReceived = null;
        _lastBytesSent = null;
        _lastObservedAt = null;
    }
}
