using VpnWatchdog.Core;

namespace VpnWatchdog.Core.Correlation;

/// <summary>
/// Pure state-machine/fusion logic for the VPN watchdog. Consumes point-in-time
/// snapshots (adapter, internet, processes, new log lines) handed to it by a
/// polling loop and produces the fused <see cref="VpnState"/> plus the
/// disconnect/reconnect evidence trail (<see cref="DisconnectCorrelation"/>).
///
/// This class does no I/O of its own (no file/network/process access) - it only
/// reasons over the snapshots it is given, so it is fully deterministic and
/// trivially unit-testable with an injected fake clock (every timestamp it
/// records is derived from the <c>now</c> parameter passed into
/// <see cref="Ingest"/>, never from <see cref="DateTimeOffset.Now"/> /
/// <see cref="DateTimeOffset.UtcNow"/>).
///
/// THREAD-SAFETY: this type assumes a single caller invoking <see cref="Ingest"/>
/// sequentially from one polling loop (per the project's design - the watchdog
/// polls in one loop, it does not fan out concurrent polls). There is
/// deliberately no locking here; if that assumption ever changes, callers must
/// add their own synchronization around <see cref="Ingest"/> and the two getters.
/// </summary>
public sealed class VpnEventCorrelator : IVpnEventCorrelator
{
    private readonly string _profileName;
    private readonly TimeSpan _openCorrelationTimeout;
    private readonly int _maxCompletedCorrelationsInMemory;
    private readonly int _maxRecentLogEventBufferSize;

    // Bounded FIFO buffers - this process can run for days, so nothing here is
    // allowed to grow without an eviction policy.
    private readonly List<LogEvent> _recentLogEvents = new();
    private readonly List<DisconnectCorrelation> _completedCorrelations = new();

    private DisconnectCorrelation? _openCorrelation;

    private bool _hasIngestedBefore;
    private VpnState _currentState = VpnState.Unknown;
    private DateTimeOffset _currentStateBeganAt;

    public VpnEventCorrelator(
        string profileName,
        int openCorrelationTimeoutMinutes,
        int maxCompletedCorrelationsInMemory = 1000,
        int maxRecentLogEventBufferSize = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(openCorrelationTimeoutMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCompletedCorrelationsInMemory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecentLogEventBufferSize);

        _profileName = profileName;
        _openCorrelationTimeout = TimeSpan.FromMinutes(openCorrelationTimeoutMinutes);
        _maxCompletedCorrelationsInMemory = maxCompletedCorrelationsInMemory;
        _maxRecentLogEventBufferSize = maxRecentLogEventBufferSize;
    }

    public VpnStateSnapshot Ingest(
        AdapterSnapshot adapter,
        InternetSnapshot internet,
        IReadOnlyList<ProcessSnapshot> processes,
        IReadOnlyList<LogEvent> newLogEvents,
        DateTimeOffset now)
    {
        // Capture "when did the state we're currently in begin" BEFORE anything
        // below mutates it - if prevState is Connected, this is exactly the
        // Connected-since timestamp needed for PreviousConnectedDuration.
        var stateBeganAtBeforeThisTick = _currentStateBeganAt;
        var prevState = _currentState;

        AppendToRecentLogEventBuffer(newLogEvents);

        var isConnectedEvidence =
            adapter.AdapterFound && adapter.IsUp && !string.IsNullOrEmpty(adapter.IpAddress);

        // --- 1..4: priority-ordered VpnState determination from evidence ---
        //
        // VpnState.Connecting is intentionally never assigned anywhere in this
        // chain: AdapterSnapshot only exposes a bool IsUp, not the underlying
        // multi-value NetworkInterface.OperationalStatus, so there is no honest
        // way to distinguish "link is up but the tunnel is still negotiating"
        // from Recovering/Disconnected with the evidence this correlator is
        // given. Rather than force a guess, Connecting is left unused.
        VpnState computedState;
        if (isConnectedEvidence)
        {
            // 1. Adapter is up and has an IP -> the strongest possible signal.
            computedState = VpnState.Connected;
        }
        else if (internet.State == InternetState.Down)
        {
            // 2. The whole network is down, not just the VPN. This must win
            // over every other non-connected classification below - "internet
            // unavailable" and "VPN unavailable" are never the same condition.
            computedState = VpnState.NetworkUnavailable;
        }
        else if (prevState == VpnState.Connected)
        {
            // 3. We just lost it THIS tick and the internet itself is fine ->
            // enter the grace window where FortiClient's own fast auto-reconnect
            // (observed in Phase 1 to succeed in 6-70s) gets a chance to work.
            computedState = VpnState.Recovering;
        }
        else if (prevState == VpnState.Recovering &&
                 (now - stateBeganAtBeforeThisTick) > _openCorrelationTimeout)
        {
            // 3b. Still in the grace window's state but it has run past the
            // configured timeout with no reconnect - the recovery window is
            // over; settle into plain Disconnected going forward.
            computedState = VpnState.Disconnected;
        }
        else if (prevState is VpnState.Recovering or VpnState.Disconnected)
        {
            // 4. Nothing changed the picture this tick - stay exactly where we
            // already were (Recovering continues within its window, or
            // Disconnected stays settled).
            computedState = prevState;
        }
        else if (!_hasIngestedBefore)
        {
            // 5. Very first Ingest call ever, evidence doesn't clearly say
            // Connected (and internet isn't clearly Down either, handled
            // above) - we have no state history yet, so don't guess.
            computedState = VpnState.Unknown;
        }
        else
        {
            // Falls through only when prevState was NetworkUnavailable (or, in
            // principle, a lingering Unknown past the first tick) and internet
            // has now been confirmed Up while the adapter still isn't
            // connected. Neither NetworkUnavailable nor Unknown carries the
            // "just-lost-Connected" grace-window semantics that Recovering
            // requires (rule 3 only grants that window on a direct transition
            // out of Connected), so now that we have live evidence the network
            // itself is fine and the tunnel simply is not up, the honest
            // settled label is Disconnected rather than perpetuating a stale
            // NetworkUnavailable/Unknown reading.
            computedState = VpnState.Disconnected;
        }

        // --- Correlation lifecycle, driven off the transition we just computed ---
        VpnState finalState;
        if (prevState == VpnState.Connected && computedState != VpnState.Connected)
        {
            OpenCorrelation(adapter, internet, processes, now, stateBeganAtBeforeThisTick);
            finalState = computedState;
        }
        else if (_openCorrelation is not null)
        {
            var reconnected = computedState == VpnState.Connected || HasVpnConnectedLogEvent(newLogEvents);
            if (reconnected)
            {
                CloseOpenCorrelationAsRecovered(now);
                finalState = VpnState.Connected;
            }
            else if ((now - _openCorrelation.DisconnectedAt) > _openCorrelationTimeout)
            {
                // The correlation itself has exceeded the "did it recover"
                // window with no reconnect - record it as a non-recovery.
                // Deliberately does NOT force finalState to Disconnected here:
                // if computedState is NetworkUnavailable (internet still down)
                // that classification still wins per rule 2 above - this only
                // closes the evidence record, it never overrides the displayed
                // VpnState.
                CloseOpenCorrelationAsFailed();
                finalState = computedState;
            }
            else
            {
                finalState = computedState;
            }
        }
        else
        {
            finalState = computedState;
        }

        if (!_hasIngestedBefore || finalState != prevState)
        {
            _currentStateBeganAt = now;
        }

        _currentState = finalState;
        _hasIngestedBefore = true;

        return new VpnStateSnapshot(finalState, adapter, now);
    }

    public IReadOnlyList<DisconnectCorrelation> GetCompletedCorrelations() => _completedCorrelations.ToArray();

    public DisconnectCorrelation? GetOpenCorrelation() => _openCorrelation;

    public TimeSpan CurrentStateDuration(DateTimeOffset now) => now - _currentStateBeganAt;

    private void AppendToRecentLogEventBuffer(IReadOnlyList<LogEvent> newLogEvents)
    {
        foreach (var logEvent in newLogEvents)
        {
            _recentLogEvents.Add(logEvent);
        }

        var overflow = _recentLogEvents.Count - _maxRecentLogEventBufferSize;
        if (overflow > 0)
        {
            _recentLogEvents.RemoveRange(0, overflow);
        }
    }

    private bool ProfileMatches(string? profileName) =>
        string.Equals(profileName, _profileName, StringComparison.Ordinal);

    private bool HasVpnConnectedLogEvent(IReadOnlyList<LogEvent> newLogEvents)
    {
        foreach (var logEvent in newLogEvents)
        {
            if (logEvent.EventType == LogEventType.VpnConnected && ProfileMatches(logEvent.ProfileName))
            {
                return true;
            }
        }

        return false;
    }

    private void OpenCorrelation(
        AdapterSnapshot adapter,
        InternetSnapshot internet,
        IReadOnlyList<ProcessSnapshot> processes,
        DateTimeOffset now,
        DateTimeOffset connectedSince)
    {
        var (classification, reasonCode, reasonText) = ClassifyNearbyDisconnect();

        var fortiVpnRunning = IsProcessRunning(processes, "FortiVPN");
        var fortiSslVpnDaemonRunning = IsProcessRunning(processes, "FortiSSLVPNdaemon");

        _openCorrelation = new DisconnectCorrelation(
            CorrelationId: Guid.NewGuid().ToString(),
            ProfileName: _profileName,
            DisconnectedAt: now,
            DisconnectClassification: classification,
            DisconnectReasonCode: reasonCode,
            DisconnectReasonText: reasonText,
            InternetStateAtDisconnect: internet.State,
            FortiVpnProcessRunningAtDisconnect: fortiVpnRunning,
            FortiSslVpnDaemonRunningAtDisconnect: fortiSslVpnDaemonRunning,
            PreviousConnectedDuration: now - connectedSince,
            ReconnectAttemptDetectedAt: null, // no such log line exists (confirmed Phase 1) - never fabricated
            ReconnectedAt: null,
            RecoveryDuration: null,
            RecoverySucceeded: null);
    }

    private (string Classification, string? ReasonCode, string? ReasonText) ClassifyNearbyDisconnect()
    {
        // Search newest-first: _recentLogEvents is appended in chronological
        // order, so walking backwards finds the most recent match first.
        for (var i = _recentLogEvents.Count - 1; i >= 0; i--)
        {
            var logEvent = _recentLogEvents[i];
            if (logEvent.EventType == LogEventType.VpnDisconnectedUnexpectedly && ProfileMatches(logEvent.ProfileName))
            {
                return ("Unexpected", logEvent.ReasonCode, logEvent.ReasonText);
            }
        }

        for (var i = _recentLogEvents.Count - 1; i >= 0; i--)
        {
            var logEvent = _recentLogEvents[i];
            if (logEvent.EventType == LogEventType.Unknown &&
                ProfileMatches(logEvent.ProfileName) &&
                !string.IsNullOrEmpty(logEvent.ReasonText))
            {
                // The observed ReasonText itself IS the classification (e.g.
                // "Cancelled") - never re-interpreted into invented semantics.
                return (logEvent.ReasonText, logEvent.ReasonCode, logEvent.ReasonText);
            }
        }

        return ("Unknown", null, null);
    }

    private static bool IsProcessRunning(IReadOnlyList<ProcessSnapshot> processes, string processName)
    {
        foreach (var process in processes)
        {
            if (string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                return process.IsRunning;
            }
        }

        return false;
    }

    private void CloseOpenCorrelationAsRecovered(DateTimeOffset now)
    {
        var correlation = _openCorrelation!;
        var closed = correlation with
        {
            ReconnectedAt = now,
            RecoveryDuration = now - correlation.DisconnectedAt,
            RecoverySucceeded = true
        };

        AddCompletedCorrelation(closed);
        _openCorrelation = null;
    }

    private void CloseOpenCorrelationAsFailed()
    {
        var correlation = _openCorrelation!;
        var closed = correlation with
        {
            ReconnectedAt = null,
            RecoveryDuration = null,
            RecoverySucceeded = false
        };

        AddCompletedCorrelation(closed);
        _openCorrelation = null;
    }

    private void AddCompletedCorrelation(DisconnectCorrelation correlation)
    {
        _completedCorrelations.Add(correlation);

        var overflow = _completedCorrelations.Count - _maxCompletedCorrelationsInMemory;
        if (overflow > 0)
        {
            _completedCorrelations.RemoveRange(0, overflow);
        }
    }
}
