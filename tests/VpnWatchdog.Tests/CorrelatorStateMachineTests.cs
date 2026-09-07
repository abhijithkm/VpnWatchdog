using VpnWatchdog.Core;
using VpnWatchdog.Core.Correlation;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// Unit tests that drive the real <see cref="VpnEventCorrelator"/> directly
/// through hand-built snapshots and explicit <see cref="DateTimeOffset"/> values
/// - never <c>DateTimeOffset.Now</c> - so every test is deterministic and fast.
/// No provider, no real Windows/FortiClient state is touched anywhere here.
/// </summary>
public class CorrelatorStateMachineTests
{
    private const string Profile = "MLA-DEV-VPN-2-LocalAuth";
    private const string OtherProfile = "MLA-DEV-VPN-1-SSO";
    private const string AdapterDescription = "Fortinet Virtual Ethernet Adapter (NDIS 6.30)";

    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 8, 0, 0, TimeSpan.FromHours(-4));
    private static readonly IReadOnlyList<LogEvent> NoLogEvents = Array.Empty<LogEvent>();

    // ------------------------------------------------------------------
    // Snapshot builders - deliberately explicit rather than relying on any
    // "default" constructor behavior, so each test's intent is visible inline.
    // ------------------------------------------------------------------

    private static AdapterSnapshot ConnectedAdapter(DateTimeOffset at, string ip = "10.240.102.5") =>
        new(AdapterFound: true, AdapterName: "Ethernet 7", InterfaceDescription: AdapterDescription,
            InterfaceIndex: 15, IsUp: true, IpAddress: ip, PrefixLength: 32,
            GatewayAddresses: Array.Empty<string>(), ObservedAt: at);

    private static AdapterSnapshot DownAdapter(DateTimeOffset at) =>
        new(AdapterFound: true, AdapterName: "Ethernet 7", InterfaceDescription: AdapterDescription,
            InterfaceIndex: 15, IsUp: false, IpAddress: null, PrefixLength: null,
            GatewayAddresses: Array.Empty<string>(), ObservedAt: at);

    private static InternetSnapshot InternetUp(DateTimeOffset at) =>
        new(InternetState.Up, DefaultGatewayReachable: true, DnsResolved: true, HttpsReachable: true,
            HttpsProbeUrl: "https://example.invalid/robots.txt", ObservedAt: at);

    private static InternetSnapshot InternetDown(DateTimeOffset at) =>
        new(InternetState.Down, DefaultGatewayReachable: false, DnsResolved: false, HttpsReachable: false,
            HttpsProbeUrl: "https://example.invalid/robots.txt", ObservedAt: at);

    // Process names here MUST match what VpnEventCorrelator.IsProcessRunning
    // looks up ("FortiVPN" / "FortiSSLVPNdaemon" exactly) so that
    // FortiVpnProcessRunningAtDisconnect / FortiSslVpnDaemonRunningAtDisconnect
    // actually reflect IsRunning rather than silently defaulting to false.
    private static IReadOnlyList<ProcessSnapshot> ProcessesRunning(DateTimeOffset at) => new[]
    {
        new ProcessSnapshot("FortiVPN", true, 4242, at.AddHours(-1), at),
        new ProcessSnapshot("FortiSSLVPNdaemon", true, 4243, at.AddHours(-1), at),
    };

    private static IReadOnlyList<ProcessSnapshot> ProcessesNotRunning(DateTimeOffset at) => new[]
    {
        new ProcessSnapshot("FortiVPN", false, null, null, at),
        new ProcessSnapshot("FortiSSLVPNdaemon", false, null, null, at),
    };

    private static LogEvent UnexpectedDisconnectLogEvent(DateTimeOffset at, string profileName = Profile) =>
        new(
            Timestamp: at,
            ProfileName: profileName,
            EventType: LogEventType.VpnDisconnectedUnexpectedly,
            RawLine: $"[{at:yyyy-MM-dd HH:mm:ss.fffffff} UTC-04:00] [14160:14164] [FortiVPN  2432   error] " +
                     $"!!! fortivpn::StateMachine::HandleTunnelDisconnected session 1 (.\\ABHIJITHKM) " +
                     $"\"{profileName}\" disconnected unexpectedly!",
            ReasonCode: "21",
            ReasonText: "Cancelled",
            SourceFile: "FortiVPN_1.log");

    // ------------------------------------------------------------------
    // 1. Connected adapter+IP -> State reported as Connected.
    // ------------------------------------------------------------------

    [Fact]
    public void Ingest_AdapterUpWithIp_ReportsConnected()
    {
        var sut = new VpnEventCorrelator(Profile, 30);

        var result = sut.Ingest(ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        Assert.Equal(VpnState.Connected, result.State);
        Assert.Equal(T0, result.ObservedAt);
        Assert.Null(sut.GetOpenCorrelation());
        Assert.Empty(sut.GetCompletedCorrelations());
    }

    // ------------------------------------------------------------------
    // 2. Connected -> adapter-down-with-internet-up -> Recovering -> adapter
    //    back up -> Connected again, with a completed correlation whose
    //    RecoverySucceeded == true and RecoveryDuration is exact.
    // ------------------------------------------------------------------

    [Fact]
    public void Disconnect_WithInternetUp_ThenAdapterBackUp_RecoversAndRecordsSuccessfulCorrelation()
    {
        var sut = new VpnEventCorrelator(Profile, 30);
        var t1 = T0.AddSeconds(30); // disconnect
        var t2 = t1.AddSeconds(45); // reconnect

        sut.Ingest(ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        var duringDisconnect = sut.Ingest(DownAdapter(t1), InternetUp(t1), ProcessesRunning(t1), NoLogEvents, t1);
        Assert.Equal(VpnState.Recovering, duringDisconnect.State);
        var open = sut.GetOpenCorrelation();
        Assert.NotNull(open);
        Assert.Equal(t1, open!.DisconnectedAt);

        var afterRecovery = sut.Ingest(ConnectedAdapter(t2), InternetUp(t2), ProcessesRunning(t2), NoLogEvents, t2);

        Assert.Equal(VpnState.Connected, afterRecovery.State);
        Assert.Null(sut.GetOpenCorrelation());

        var completed = Assert.Single(sut.GetCompletedCorrelations());
        Assert.Equal(t1, completed.DisconnectedAt);
        Assert.Equal(t2, completed.ReconnectedAt);
        Assert.True(completed.RecoverySucceeded);
        Assert.Equal(TimeSpan.FromSeconds(45), completed.RecoveryDuration);
        // The correlator only knows what it has observed - the Connected state
        // was first seen at T0, so the connected span running up to the t1
        // disconnect is exactly 30s.
        Assert.Equal(TimeSpan.FromSeconds(30), completed.PreviousConnectedDuration);
    }

    // ------------------------------------------------------------------
    // 3. Adapter-down while InternetSnapshot.State == Down -> NetworkUnavailable,
    //    never confused with Disconnected/Recovering.
    // ------------------------------------------------------------------

    [Fact]
    public void Disconnect_WithInternetDown_ReportsNetworkUnavailable_NotDisconnectedOrRecovering()
    {
        var sut = new VpnEventCorrelator(Profile, 30);
        var t1 = T0.AddSeconds(10);

        sut.Ingest(ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);
        var result = sut.Ingest(DownAdapter(t1), InternetDown(t1), ProcessesRunning(t1), NoLogEvents, t1);

        Assert.Equal(VpnState.NetworkUnavailable, result.State);
        Assert.NotEqual(VpnState.Disconnected, result.State);
        Assert.NotEqual(VpnState.Recovering, result.State);
    }

    // ------------------------------------------------------------------
    // 4. Disconnect that stays down past openCorrelationTimeoutMinutes with no
    //    recovery -> completed correlation with RecoverySucceeded == false,
    //    State settles to Disconnected.
    // ------------------------------------------------------------------

    [Fact]
    public void Disconnect_PastTimeoutWithNoRecovery_DeclaresFailedCorrelation_AndSettlesToDisconnected()
    {
        const int timeoutMinutes = 5;
        var sut = new VpnEventCorrelator(Profile, timeoutMinutes);
        var t1 = T0.AddSeconds(10);
        var t2 = t1.AddMinutes(timeoutMinutes).AddSeconds(1); // just past the timeout

        sut.Ingest(ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);
        sut.Ingest(DownAdapter(t1), InternetUp(t1), ProcessesRunning(t1), NoLogEvents, t1);

        var result = sut.Ingest(DownAdapter(t2), InternetUp(t2), ProcessesRunning(t2), NoLogEvents, t2);

        Assert.Equal(VpnState.Disconnected, result.State);
        Assert.Null(sut.GetOpenCorrelation());

        var completed = Assert.Single(sut.GetCompletedCorrelations());
        Assert.Equal(t1, completed.DisconnectedAt);
        Assert.False(completed.RecoverySucceeded);
        Assert.Null(completed.ReconnectedAt);
        Assert.Null(completed.RecoveryDuration);
    }

    // ------------------------------------------------------------------
    // 5. A VpnDisconnectedUnexpectedly LogEvent for the configured profile,
    //    right around the disconnect transition -> DisconnectClassification
    //    == "Unexpected", with the reason code/text propagated verbatim.
    // ------------------------------------------------------------------

    [Fact]
    public void UnexpectedDisconnectLogEvent_ForConfiguredProfile_ClassifiesCorrelationAsUnexpected()
    {
        var sut = new VpnEventCorrelator(Profile, 30);
        var t1 = T0.AddSeconds(10);
        var t2 = t1.AddSeconds(20);

        sut.Ingest(ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);
        sut.Ingest(DownAdapter(t1), InternetUp(t1), ProcessesRunning(t1),
            new[] { UnexpectedDisconnectLogEvent(t1) }, t1);
        sut.Ingest(ConnectedAdapter(t2), InternetUp(t2), ProcessesRunning(t2), NoLogEvents, t2);

        var completed = Assert.Single(sut.GetCompletedCorrelations());
        Assert.Equal("Unexpected", completed.DisconnectClassification);
        Assert.Equal("21", completed.DisconnectReasonCode);
        Assert.Equal("Cancelled", completed.DisconnectReasonText);
    }

    // ------------------------------------------------------------------
    // 6. A LogEvent for a DIFFERENT profile must NOT be treated as evidence for
    //    the configured profile's correlation.
    // ------------------------------------------------------------------

    [Fact]
    public void UnexpectedDisconnectLogEvent_ForDifferentProfile_IsNotAttributedToConfiguredProfileCorrelation()
    {
        var sut = new VpnEventCorrelator(Profile, 30);
        var t1 = T0.AddSeconds(10);
        var t2 = t1.AddSeconds(20);

        sut.Ingest(ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);
        sut.Ingest(DownAdapter(t1), InternetUp(t1), ProcessesRunning(t1),
            new[] { UnexpectedDisconnectLogEvent(t1, OtherProfile) }, t1);
        sut.Ingest(ConnectedAdapter(t2), InternetUp(t2), ProcessesRunning(t2), NoLogEvents, t2);

        var completed = Assert.Single(sut.GetCompletedCorrelations());
        Assert.NotEqual("Unexpected", completed.DisconnectClassification);
        Assert.Null(completed.DisconnectReasonCode);
        Assert.Null(completed.DisconnectReasonText);
    }

    // ------------------------------------------------------------------
    // Positive control for the process-tracking fields: proves
    // FortiVpnProcessRunningAtDisconnect / FortiSslVpnDaemonRunningAtDisconnect
    // actually reflect ProcessSnapshot.IsRunning (not just always false),
    // which is what makes the "false" assertions elsewhere meaningful.
    // ------------------------------------------------------------------

    [Fact]
    public void Disconnect_WithFortiProcessesRunning_RecordsBothProcessRunningFlagsAsTrue()
    {
        var sut = new VpnEventCorrelator(Profile, 30);
        var t1 = T0.AddSeconds(10);

        sut.Ingest(ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);
        sut.Ingest(DownAdapter(t1), InternetUp(t1), ProcessesRunning(t1), NoLogEvents, t1);

        var open = sut.GetOpenCorrelation();
        Assert.NotNull(open);
        Assert.True(open!.FortiVpnProcessRunningAtDisconnect);
        Assert.True(open.FortiSslVpnDaemonRunningAtDisconnect);
    }

    [Fact]
    public void Disconnect_WithFortiProcessesNotRunning_RecordsBothProcessRunningFlagsAsFalse()
    {
        var sut = new VpnEventCorrelator(Profile, 30);
        var t1 = T0.AddSeconds(10);

        sut.Ingest(ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);
        sut.Ingest(DownAdapter(t1), InternetUp(t1), ProcessesNotRunning(t1), NoLogEvents, t1);

        var open = sut.GetOpenCorrelation();
        Assert.NotNull(open);
        Assert.False(open!.FortiVpnProcessRunningAtDisconnect);
        Assert.False(open.FortiSslVpnDaemonRunningAtDisconnect);
    }

    // ------------------------------------------------------------------
    // 7. Multiple sequential disconnect/reconnect cycles -> completed count
    //    matches, each with independent, correct timings.
    // ------------------------------------------------------------------

    [Fact]
    public void SequentialDisconnectReconnectCycles_ProduceIndependentCorrelationsWithCorrectTimings()
    {
        var sut = new VpnEventCorrelator(Profile, 30);
        var t = T0;
        sut.Ingest(ConnectedAdapter(t), InternetUp(t), ProcessesRunning(t), NoLogEvents, t);

        var expected = new List<(DateTimeOffset disconnect, DateTimeOffset reconnect)>();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var disconnectAt = t.AddMinutes(1);
            var reconnectAt = disconnectAt.AddSeconds(10 + cycle * 5); // distinct duration per cycle

            sut.Ingest(DownAdapter(disconnectAt), InternetUp(disconnectAt), ProcessesRunning(disconnectAt), NoLogEvents, disconnectAt);
            sut.Ingest(ConnectedAdapter(reconnectAt), InternetUp(reconnectAt), ProcessesRunning(reconnectAt), NoLogEvents, reconnectAt);

            expected.Add((disconnectAt, reconnectAt));

            t = reconnectAt.AddMinutes(1); // stay connected a while before the next cycle
            sut.Ingest(ConnectedAdapter(t), InternetUp(t), ProcessesRunning(t), NoLogEvents, t);
        }

        var completed = sut.GetCompletedCorrelations();
        Assert.Equal(3, completed.Count);
        Assert.Equal(3, completed.Select(c => c.CorrelationId).Distinct().Count());

        for (var i = 0; i < expected.Count; i++)
        {
            var (disconnectAt, reconnectAt) = expected[i];
            var correlation = completed[i];
            Assert.Equal(disconnectAt, correlation.DisconnectedAt);
            Assert.Equal(reconnectAt, correlation.ReconnectedAt);
            Assert.True(correlation.RecoverySucceeded);
            Assert.Equal(reconnectAt - disconnectAt, correlation.RecoveryDuration);
        }
    }

    // ------------------------------------------------------------------
    // 8. maxCompletedCorrelationsInMemory eviction: after exceeding the cap,
    //    the oldest completed correlations are evicted and the list length
    //    never exceeds the cap - proving no unbounded memory growth.
    // ------------------------------------------------------------------

    [Fact]
    public void CompletedCorrelations_ExceedingCap_EvictsOldestAndNeverExceedsCap()
    {
        const int cap = 3;
        var sut = new VpnEventCorrelator(Profile, 30, cap);
        var t = T0;
        sut.Ingest(ConnectedAdapter(t), InternetUp(t), ProcessesRunning(t), NoLogEvents, t);

        var allDisconnectTimes = new List<DateTimeOffset>();

        for (var cycle = 0; cycle < 5; cycle++)
        {
            var disconnectAt = t.AddMinutes(1);
            var reconnectAt = disconnectAt.AddSeconds(15);

            sut.Ingest(DownAdapter(disconnectAt), InternetUp(disconnectAt), ProcessesRunning(disconnectAt), NoLogEvents, disconnectAt);
            sut.Ingest(ConnectedAdapter(reconnectAt), InternetUp(reconnectAt), ProcessesRunning(reconnectAt), NoLogEvents, reconnectAt);

            allDisconnectTimes.Add(disconnectAt);

            t = reconnectAt.AddMinutes(1);
            sut.Ingest(ConnectedAdapter(t), InternetUp(t), ProcessesRunning(t), NoLogEvents, t);

            Assert.True(sut.GetCompletedCorrelations().Count <= cap);
        }

        var completed = sut.GetCompletedCorrelations();
        Assert.Equal(cap, completed.Count);

        // Only the most recent `cap` disconnects should remain; the oldest
        // (allDisconnectTimes.Count - cap) of them must have been evicted.
        var expectedRemaining = allDisconnectTimes.Skip(allDisconnectTimes.Count - cap).ToList();
        Assert.Equal(expectedRemaining, completed.Select(c => c.DisconnectedAt).ToList());
    }
}
