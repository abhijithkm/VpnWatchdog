using System.IO;
using VpnWatchdog.Core;
using VpnWatchdog.Core.Correlation;
using VpnWatchdog.Core.Logging;
using VpnWatchdog.Tests.Fakes;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// Higher-level scenario tests covering the 10 scenarios requested by the
/// project owner. All scenarios except #7 (log rotation) drive the real
/// <see cref="VpnEventCorrelator"/> through the fakes from
/// <see cref="VpnWatchdog.Tests.Fakes"/> - no real Windows APIs, no real
/// FortiClient installation, no real files. Scenario 7 is the one deliberate
/// exception: it exercises a real <see cref="FortiClientLogMonitor"/> against a
/// temporary, test-owned file/directory it creates and cleans up itself.
/// </summary>
public class SimulationScenarioTests
{
    private const string Profile = "MLA-DEV-VPN-2-LocalAuth";
    private const string AdapterPattern = "Fortinet Virtual Ethernet Adapter";
    private const string AdapterDescription = "Fortinet Virtual Ethernet Adapter (NDIS 6.30)";

    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 8, 0, 0, TimeSpan.FromHours(-4));
    private static readonly IReadOnlyList<LogEvent> NoLogEvents = Array.Empty<LogEvent>();

    // ------------------------------------------------------------------
    // Snapshot builders (see CorrelatorStateMachineTests for the same shapes;
    // duplicated here so this file is self-contained).
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
    // FortiVpnProcessRunningAtDisconnect actually reflects IsRunning rather
    // than silently defaulting to false regardless of what's fed in.
    private static IReadOnlyList<ProcessSnapshot> ProcessesRunning(DateTimeOffset at) => new[]
    {
        new ProcessSnapshot("FortiVPN", true, 4242, at.AddHours(-1), at),
        new ProcessSnapshot("FortiSSLVPNdaemon", true, 4243, at.AddHours(-1), at),
    };

    private static IReadOnlyList<ProcessSnapshot> ProcessesGone(DateTimeOffset at) => new[]
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

    private static LogEvent ConnectedLogEvent(DateTimeOffset at, string profileName = Profile) =>
        new(
            Timestamp: at,
            ProfileName: profileName,
            EventType: LogEventType.VpnConnected,
            RawLine: $"[{at:yyyy-MM-dd HH:mm:ss.fffffff} UTC-04:00] [14160:14164] [FortiVPN  2204    info] " +
                     $"fortivpn::StateMachine::HandleTunnelConnected \"{profileName}\" is connected.",
            ReasonCode: null,
            ReasonText: null,
            SourceFile: "FortiVPN_1.log");

    /// <summary>
    /// Routes one polling "tick" through the fakes (so <see cref="IConnectionStateProvider"/>
    /// and <see cref="IInternetStateProvider"/> are genuinely exercised, not bypassed) and
    /// into the real correlator's <c>Ingest</c>.
    /// </summary>
    private static async Task<VpnStateSnapshot> TickAsync(
        VpnEventCorrelator sut,
        FakeConnectionStateProvider adapterProvider,
        FakeInternetStateProvider internetProvider,
        AdapterSnapshot adapter,
        InternetSnapshot internet,
        IReadOnlyList<ProcessSnapshot> processes,
        IReadOnlyList<LogEvent> logEvents,
        DateTimeOffset now)
    {
        adapterProvider.NextSnapshot = adapter;
        internetProvider.NextSnapshot = internet;

        var observedAdapter = await adapterProvider.GetAdapterSnapshotAsync(AdapterPattern, CancellationToken.None);
        var observedInternet = await internetProvider.GetCurrentStateAsync(CancellationToken.None);

        return sut.Ingest(observedAdapter, observedInternet, processes, logEvents, now);
    }

    private static async Task<IReadOnlyList<LogEvent>> DrainAsync(FakeLogMonitor monitor)
    {
        var events = new List<LogEvent>();
        await foreach (var e in monitor.TailNewEventsAsync(CancellationToken.None))
        {
            events.Add(e);
        }
        return events;
    }

    // ------------------------------------------------------------------
    // 1. Normal connected state (steady Connected, no correlations created).
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario1_NormalConnectedState_StaysConnected_NoCorrelationsCreated()
    {
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var sut = new VpnEventCorrelator(Profile, 30);

        var t = T0;
        for (var tick = 0; tick < 5; tick++)
        {
            t = t.AddSeconds(2);
            var result = await TickAsync(sut, adapterProvider, internetProvider,
                ConnectedAdapter(t), InternetUp(t), ProcessesRunning(t), NoLogEvents, t);

            Assert.Equal(VpnState.Connected, result.State);
        }

        Assert.Empty(sut.GetCompletedCorrelations());
        Assert.Null(sut.GetOpenCorrelation());
    }

    // ------------------------------------------------------------------
    // 2. VPN disconnect + internet available -> Recovering, then either
    //    recovers or times out depending on what happens next.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario2_DisconnectWithInternetAvailable_RecoversBeforeTimeout_ReportsRecoveringThenConnected()
    {
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var sut = new VpnEventCorrelator(Profile, 30);

        await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        var t1 = T0.AddSeconds(8);
        var recovering = await TickAsync(sut, adapterProvider, internetProvider,
            DownAdapter(t1), InternetUp(t1), ProcessesRunning(t1), NoLogEvents, t1);
        Assert.Equal(VpnState.Recovering, recovering.State);

        var t2 = t1.AddSeconds(20);
        var recovered = await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(t2), InternetUp(t2), ProcessesRunning(t2), NoLogEvents, t2);

        Assert.Equal(VpnState.Connected, recovered.State);
        var completed = Assert.Single(sut.GetCompletedCorrelations());
        Assert.True(completed.RecoverySucceeded);
        Assert.Equal(TimeSpan.FromSeconds(20), completed.RecoveryDuration);
    }

    [Fact]
    public async Task Scenario2_DisconnectWithInternetAvailable_NoRecoveryBeforeTimeout_ReportsRecoveringThenDisconnected()
    {
        const int timeoutMinutes = 5;
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var sut = new VpnEventCorrelator(Profile, timeoutMinutes);

        await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        var t1 = T0.AddSeconds(8);
        var recovering = await TickAsync(sut, adapterProvider, internetProvider,
            DownAdapter(t1), InternetUp(t1), ProcessesRunning(t1), NoLogEvents, t1);
        Assert.Equal(VpnState.Recovering, recovering.State);

        var t2 = t1.AddMinutes(timeoutMinutes).AddSeconds(1);
        var timedOut = await TickAsync(sut, adapterProvider, internetProvider,
            DownAdapter(t2), InternetUp(t2), ProcessesRunning(t2), NoLogEvents, t2);

        Assert.Equal(VpnState.Disconnected, timedOut.State);
        var completed = Assert.Single(sut.GetCompletedCorrelations());
        Assert.False(completed.RecoverySucceeded);
    }

    // ------------------------------------------------------------------
    // 3. VPN disconnect + internet unavailable -> NetworkUnavailable, not
    //    Disconnected/Recovering.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario3_DisconnectWithInternetUnavailable_ReportsNetworkUnavailable_NotDisconnectedOrRecovering()
    {
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var sut = new VpnEventCorrelator(Profile, 30);

        await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        var t1 = T0.AddSeconds(6);
        var result = await TickAsync(sut, adapterProvider, internetProvider,
            DownAdapter(t1), InternetDown(t1), ProcessesRunning(t1), NoLogEvents, t1);

        Assert.Equal(VpnState.NetworkUnavailable, result.State);
        Assert.NotEqual(VpnState.Disconnected, result.State);
        Assert.NotEqual(VpnState.Recovering, result.State);
    }

    // ------------------------------------------------------------------
    // 4. Automatic FortiClient recovery: disconnect, then adapter-up evidence
    //    plus a VpnConnected log line shortly after -> fast RecoverySucceeded
    //    == true with a short RecoveryDuration (Phase 1 observed 6-70s).
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario4_AutomaticFortiClientRecovery_FastReconnectWithLogEvidence_RecordsShortSuccessfulRecovery()
    {
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var logMonitor = new FakeLogMonitor();
        var sut = new VpnEventCorrelator(Profile, 30);

        await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        var t1 = T0.AddSeconds(4);
        logMonitor.PendingEvents.Enqueue(UnexpectedDisconnectLogEvent(t1));
        var disconnectLogEvents = await DrainAsync(logMonitor);
        await TickAsync(sut, adapterProvider, internetProvider,
            DownAdapter(t1), InternetUp(t1), ProcessesRunning(t1), disconnectLogEvents, t1);

        // FortiClient's own built-in fast auto-reconnect (observed 6-70s in the
        // Phase 1 trace logs).
        var t2 = t1.AddSeconds(12);
        logMonitor.PendingEvents.Enqueue(ConnectedLogEvent(t2));
        var reconnectLogEvents = await DrainAsync(logMonitor);
        var result = await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(t2), InternetUp(t2), ProcessesRunning(t2), reconnectLogEvents, t2);

        Assert.Equal(VpnState.Connected, result.State);
        var completed = Assert.Single(sut.GetCompletedCorrelations());
        Assert.True(completed.RecoverySucceeded);
        Assert.Equal(TimeSpan.FromSeconds(12), completed.RecoveryDuration);
        Assert.True(completed.RecoveryDuration < TimeSpan.FromSeconds(70));
    }

    // ------------------------------------------------------------------
    // 5. Failed recovery: disconnect, no recovery evidence ever arrives,
    //    timeout elapses -> RecoverySucceeded == false.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario5_FailedRecovery_NoEvidenceEverArrives_TimeoutElapses_RecordsUnsuccessfulRecovery()
    {
        const int timeoutMinutes = 3;
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var sut = new VpnEventCorrelator(Profile, timeoutMinutes);

        await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        var t1 = T0.AddSeconds(5);
        await TickAsync(sut, adapterProvider, internetProvider,
            DownAdapter(t1), InternetUp(t1), ProcessesRunning(t1), NoLogEvents, t1);

        // A few polls while still down; no adapter/internet/log evidence of recovery ever shows up.
        var t2 = t1.AddMinutes(1);
        await TickAsync(sut, adapterProvider, internetProvider,
            DownAdapter(t2), InternetUp(t2), ProcessesRunning(t2), NoLogEvents, t2);

        var t3 = t1.AddMinutes(timeoutMinutes).AddSeconds(1);
        var result = await TickAsync(sut, adapterProvider, internetProvider,
            DownAdapter(t3), InternetUp(t3), ProcessesRunning(t3), NoLogEvents, t3);

        Assert.Equal(VpnState.Disconnected, result.State);
        var completed = Assert.Single(sut.GetCompletedCorrelations());
        Assert.False(completed.RecoverySucceeded);
        Assert.Null(completed.ReconnectedAt);
    }

    // ------------------------------------------------------------------
    // 6. Multiple disconnects in sequence -> multiple independent
    //    correlations, no state bleed between them.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario6_MultipleDisconnectsInSequence_ProduceIndependentCorrelationsWithNoStateBleed()
    {
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var sut = new VpnEventCorrelator(Profile, 30);

        var t = T0;
        await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(t), InternetUp(t), ProcessesRunning(t), NoLogEvents, t);

        var disconnectTimes = new List<DateTimeOffset>();

        for (var i = 0; i < 4; i++)
        {
            var disconnectAt = t.AddSeconds(30);
            await TickAsync(sut, adapterProvider, internetProvider,
                DownAdapter(disconnectAt), InternetUp(disconnectAt), ProcessesRunning(disconnectAt), NoLogEvents, disconnectAt);

            var reconnectAt = disconnectAt.AddSeconds(10 + i * 3);
            await TickAsync(sut, adapterProvider, internetProvider,
                ConnectedAdapter(reconnectAt), InternetUp(reconnectAt), ProcessesRunning(reconnectAt), NoLogEvents, reconnectAt);

            disconnectTimes.Add(disconnectAt);

            t = reconnectAt.AddSeconds(60);
            await TickAsync(sut, adapterProvider, internetProvider,
                ConnectedAdapter(t), InternetUp(t), ProcessesRunning(t), NoLogEvents, t);
        }

        var completed = sut.GetCompletedCorrelations();
        Assert.Equal(4, completed.Count);
        Assert.Equal(4, completed.Select(c => c.CorrelationId).Distinct().Count());
        Assert.Equal(disconnectTimes, completed.Select(c => c.DisconnectedAt).ToList());
        Assert.All(completed, c => Assert.True(c.RecoverySucceeded));
    }

    // ------------------------------------------------------------------
    // 7. Log rotation. The ONLY test in this suite allowed to touch real (but
    //    test-owned, temporary, non-FortiClient) files: it points a real
    //    FortiClientLogMonitor at a temp directory, writes lines, tails them,
    //    then truncates-and-rewrites the file shorter (simulating rotation)
    //    and asserts the monitor recovers (re-reads from offset 0) instead of
    //    throwing or silently missing everything forever.
    //
    //    ASSUMPTION (documented because IFortiClientLogMonitor's interface
    //    takes no constructor-shaped parameters): FortiClientLogMonitor is
    //    constructed as `new FortiClientLogMonitor(logDirectory)`, matching
    //    WatchdogConfig.FortiClientLogDirectory's single-directory shape. If
    //    the real constructor differs, only this test's setup needs updating.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario7_LogRotation_RealLogMonitor_RecoversFromTruncationInsteadOfThrowingOrMissingEvents()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnwatchdog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "FortiVPN_1.log");

        try
        {
            // Pre-existing content, as if the log already had history before this
            // process ever started watching it. The real monitor treats the very
            // first observation of a file as a priming call - it deliberately skips
            // to end-of-file rather than backfilling months of history - so this
            // content must NOT appear in the first batch.
            var disconnectAt = T0;
            var connectedAt = T0.AddSeconds(6);
            File.WriteAllText(logPath,
                BuildRawDisconnectLine(disconnectAt) + Environment.NewLine +
                BuildRawConnectedLine(connectedAt) + Environment.NewLine);

            IFortiClientLogMonitor monitor = new FortiClientLogMonitor(tempDir, Profile);

            var primingBatch = await ReadAvailableEventsAsync(monitor, TimeSpan.FromMilliseconds(500));
            Assert.Empty(primingBatch); // pre-existing content must not be backfilled

            // Simulate a live append happening after monitoring has started.
            var liveDisconnectAt = T0.AddSeconds(10);
            File.AppendAllText(logPath, BuildRawDisconnectLine(liveDisconnectAt) + Environment.NewLine);

            var firstBatch = await ReadAvailableEventsAsync(monitor, TimeSpan.FromMilliseconds(1500));
            Assert.NotEmpty(firstBatch); // genuinely-new content since priming IS observed

            // Simulate rotation: the file is truncated and rewritten with content
            // shorter than what was already tailed.
            var rotatedConnectedAt = T0.AddSeconds(20);
            File.WriteAllText(logPath, BuildRawConnectedLine(rotatedConnectedAt) + Environment.NewLine);

            var secondBatch = await ReadAvailableEventsAsync(monitor, TimeSpan.FromMilliseconds(1500));

            Assert.NotEmpty(secondBatch); // must re-read from offset 0, not stay silent forever
            Assert.Contains(secondBatch, e => e.EventType == LogEventType.VpnConnected);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string BuildRawDisconnectLine(DateTimeOffset at) =>
        $"[{at:yyyy-MM-dd HH:mm:ss.fffffff} UTC-04:00] [14160:14164] [FortiVPN  2432   error] " +
        $"!!! fortivpn::StateMachine::HandleTunnelDisconnected session 1 (.\\ABHIJITHKM) " +
        $"\"{Profile}\" disconnected unexpectedly!";

    private static string BuildRawConnectedLine(DateTimeOffset at) =>
        $"[{at:yyyy-MM-dd HH:mm:ss.fffffff} UTC-04:00] [14160:14164] [FortiVPN  2204    info] " +
        $"fortivpn::StateMachine::HandleTunnelConnected \"{Profile}\" is connected.";

    /// <summary>
    /// Pulls whatever <see cref="IFortiClientLogMonitor.TailNewEventsAsync"/>
    /// currently has available within <paramref name="timeout"/>. Works whether
    /// the real implementation completes the enumerable on its own once caught
    /// up (like <see cref="FakeLogMonitor"/> does), or tails continuously and
    /// would otherwise never complete - in the latter case cancellation after
    /// the timeout is expected and swallowed, not treated as a failure.
    /// </summary>
    private static async Task<List<LogEvent>> ReadAvailableEventsAsync(IFortiClientLogMonitor monitor, TimeSpan timeout)
    {
        var results = new List<LogEvent>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var evt in monitor.TailNewEventsAsync(cts.Token))
            {
                results.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected for a continuously-tailing implementation once no more
            // events are available within `timeout`.
        }
        return results;
    }

    // ------------------------------------------------------------------
    // 8. Adapter disappearing (AdapterFound flips to false / NotFound) ->
    //    handled without throwing, treated as disconnect evidence.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario8_AdapterDisappearing_HandledWithoutThrowing_TreatedAsDisconnectEvidence()
    {
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var sut = new VpnEventCorrelator(Profile, 30);

        await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        var t1 = T0.AddSeconds(5);
        VpnStateSnapshot? result = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            result = await TickAsync(sut, adapterProvider, internetProvider,
                AdapterSnapshot.NotFound(t1), InternetUp(t1), ProcessesRunning(t1), NoLogEvents, t1);
        });

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.NotEqual(VpnState.Connected, result!.State);
        Assert.NotEqual(VpnState.Unknown, result.State);
        Assert.NotNull(sut.GetOpenCorrelation());
    }

    // ------------------------------------------------------------------
    // 9. Process disappearing does not itself change VpnState (adapter/IP
    //    remains the primary signal), but IS recorded correctly in the next
    //    opened correlation's FortiVpnProcessRunningAtDisconnect field.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario9_ProcessDisappearing_DoesNotChangeState_ButIsRecordedOnNextOpenedCorrelation()
    {
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var sut = new VpnEventCorrelator(Profile, 30);

        await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        var t1 = T0.AddSeconds(5);
        var stillConnected = await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(t1), InternetUp(t1), ProcessesGone(t1), NoLogEvents, t1);

        // Adapter/IP is still up - process disappearing alone must not flip state.
        Assert.Equal(VpnState.Connected, stillConnected.State);
        Assert.Null(sut.GetOpenCorrelation());

        var t2 = t1.AddSeconds(5);
        var afterDisconnect = await TickAsync(sut, adapterProvider, internetProvider,
            DownAdapter(t2), InternetUp(t2), ProcessesGone(t2), NoLogEvents, t2);

        Assert.NotEqual(VpnState.Connected, afterDisconnect.State);
        var open = sut.GetOpenCorrelation();
        Assert.NotNull(open);
        Assert.False(open!.FortiVpnProcessRunningAtDisconnect);
    }

    // ------------------------------------------------------------------
    // 10. Monitor restart while VPN is already connected: a freshly
    //     constructed correlator's very first Ingest call receives
    //     already-connected evidence -> reports Connected immediately,
    //     without synthesizing a spurious disconnect episode for time before
    //     the monitor even started.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scenario10_MonitorRestartWhileAlreadyConnected_FirstObservationReportsConnected_NoSpuriousDisconnectSynthesized()
    {
        var adapterProvider = new FakeConnectionStateProvider();
        var internetProvider = new FakeInternetStateProvider();
        var sut = new VpnEventCorrelator(Profile, 30);

        var result = await TickAsync(sut, adapterProvider, internetProvider,
            ConnectedAdapter(T0), InternetUp(T0), ProcessesRunning(T0), NoLogEvents, T0);

        Assert.Equal(VpnState.Connected, result.State);
        Assert.Null(sut.GetOpenCorrelation());
        Assert.Empty(sut.GetCompletedCorrelations());
    }
}
