using System.Linq;
using System.Text;
using VpnWatchdog.Core;
using VpnWatchdog.Core.Correlation;
using VpnWatchdog.Core.Diagnostics;
using VpnWatchdog.Core.Logging;
using VpnWatchdog.Core.Providers;
using VpnWatchdog.Core.Reconnect;
using VpnWatchdog.Core.Storage;
using VpnWatchdog.Core.Updates;

namespace VpnWatchdog.Cli;

/// <summary>
/// Phase 3 console front-end. Everything Phase 2 did (observe, correlate, persist,
/// report) is unchanged; the one new capability of the MONITORING run is that - when,
/// and only when, auto-reconnect is explicitly switched on - this process may ask
/// FortiClient to CONNECT the configured profile through FortiClient's own COM
/// automation interface. The monitoring run can never disconnect the tunnel: the poll
/// loop, the correlator and the reconnect policy are only ever handed an
/// <see cref="IReconnectController"/>, which has no Disconnect on it at all.
/// <para>
/// Separately, this front-end also offers two ONE-SHOT commands the user types
/// explicitly - <c>--connect</c> and <c>--disconnect</c> - which are the only code paths
/// in the CLI allowed to use <see cref="IManualVpnControl"/>. They act once and exit
/// immediately: no monitor, no database, no session summary.
/// </para>
/// <para>
/// Nothing here spawns FortiClient processes, and nothing here handles, stores or logs a
/// credential of any kind (FortiClient uses its own saved password).
/// </para>
/// </summary>
public static class Program
{
    /// <summary>
    /// Opt-in flag for this run only. It can turn auto-reconnect ON in addition to the
    /// config file's AutoReconnectEnabled setting; nothing here can be used to turn it
    /// on implicitly - absent both, auto-reconnect stays OFF.
    /// </summary>
    private const string AutoReconnectFlag = "auto-reconnect";

    /// <summary>One-shot: ask FortiClient to connect the configured profile, then exit.</summary>
    private const string ConnectFlag = "connect";

    /// <summary>One-shot: ask FortiClient to disconnect the configured profile, then exit.</summary>
    private const string DisconnectFlag = "disconnect";

    /// <summary>
    /// The one FortiClient process whose presence the activity log tracks. FortiVPN is
    /// the process that actually holds the tunnel - the tray and settings helpers coming
    /// and going is not news anyone reading the history needs - and it is the same name
    /// the correlator records at disconnect time, so both views agree on what "FortiClient
    /// running" means.
    /// </summary>
    private const string FortiVpnProcessName = "FortiVPN";

    // Exit codes. A script driving --connect/--disconnect has to be able to tell the
    // failure modes apart - in particular "this machine cannot drive FortiClient at all"
    // is a very different problem from "we asked, and it did not happen" - so each gets
    // its own code and they must stay stable.
    private const int ExitSuccess = 0;
    private const int ExitUsageError = 2;
    private const int ExitComUnavailable = 3;
    private const int ExitOperationFailed = 4;
    private const int ExitCancelled = 5;

    /// <summary>How long shutdown waits for an in-flight attempt before releasing COM.</summary>
    private static readonly TimeSpan AttemptDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Bound on the read-only startup profile-name check, so COM can never hang startup.</summary>
    private static readonly TimeSpan TunnelListTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a session that downgraded to observe-only waits before re-probing
    /// FortiClient COM.
    /// <para>
    /// WHY THIS EXISTS: a COM failure is not evidence that this machine can never
    /// reconnect - most often it means FortiClient simply was not up yet. That is
    /// overwhelmingly likely exactly when this app starts (at boot, or when a
    /// scheduled task starts it after a resume), and a single blip used to switch
    /// auto-reconnect off for a run that then lasted DAYS. So the downgrade is
    /// temporary: one read-only call every few minutes costs nothing and gets the
    /// capability back the moment FortiClient is ready.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ComReprobeInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often a REPEATING poll-tick error is allowed to say anything more, after
    /// its first (full, with stack trace) report. At a 2s poll a persistent fault
    /// produces ~43,000 stack traces a day, which fills a redirected log file and
    /// buries every other line - so repeats are counted and summarised instead.
    /// </summary>
    private static readonly TimeSpan PollErrorSummaryInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Slack added to the configured verify timeout to bound a one-shot manual command.
    /// The controller already gives up on its own verify loop; this only exists so a
    /// wedged COM server cannot leave the user staring at a hung console forever.
    /// </summary>
    private static readonly TimeSpan ManualCommandSlack = TimeSpan.FromSeconds(20);

    public static async Task<int> Main(string[] args)
    {
        // Every flag is consumed and stripped here, before AppConfigLoader sees the args:
        // that loader treats args[0] as an explicit config-file path, so passing a flag
        // through would make it look for a file literally named "--auto-reconnect" and
        // silently ignore the real appsettings.json sitting next to the executable.
        bool autoReconnectRequestedOnCommandLine = args.Any(IsAutoReconnectFlag);
        bool connectRequested = args.Any(IsConnectFlag);
        bool disconnectRequested = args.Any(IsDisconnectFlag);
        string[] configArgs = args.Where(arg => !IsKnownFlag(arg)).ToArray();

        // ------------------------------------------------------------------
        // Contradictory command lines are refused outright rather than resolved by
        // guessing. Both of these ask for opposite things to happen to the tunnel, and
        // quietly picking one would be exactly the "app fights the user" behaviour this
        // feature exists to avoid.
        // ------------------------------------------------------------------
        if (connectRequested && disconnectRequested)
        {
            Console.Error.WriteLine(
                $"ERROR: --{ConnectFlag} and --{DisconnectFlag} contradict each other - pass exactly one of them.");
            PrintUsage();
            return ExitUsageError;
        }

        if (disconnectRequested && autoReconnectRequestedOnCommandLine)
        {
            Console.Error.WriteLine(
                $"ERROR: --{DisconnectFlag} and --{AutoReconnectFlag} contradict each other: one asks for the tunnel");
            Console.Error.WriteLine(
                "       to go DOWN, the other arms the watchdog to bring it straight back UP. Refusing to guess");
            Console.Error.WriteLine(
                "       which one you meant - re-run with exactly one of them.");
            PrintUsage();
            return ExitUsageError;
        }

        WatchdogConfig config = AppConfigLoader.Load(configArgs);

        // One-shot manual commands are handled HERE, before any monitoring machinery is
        // constructed: no database is opened, no poll loop runs, no ProcessExit handler is
        // registered and no session summary is written. The process performs the single
        // action the user asked for and exits with a code describing how it went.
        // Deliberately ahead of the auto-reconnect merge below, so that in a --disconnect
        // run the only thing that can report auto-reconnect as ON is the config FILE
        // (--disconnect together with --auto-reconnect was already refused above).
        if (connectRequested || disconnectRequested)
        {
            return await RunManualVpnCommandAsync(
                config,
                connect: connectRequested,
                autoReconnectFlagPassed: autoReconnectRequestedOnCommandLine);
        }

        // Either source may switch it on; neither can switch it off once the other asked
        // for it. Default (neither) remains OFF, which is the required safe default.
        if (autoReconnectRequestedOnCommandLine && !config.AutoReconnectEnabled)
        {
            config = config with { AutoReconnectEnabled = true };
        }

        // ------------------------------------------------------------------
        // Manual "new" construction of the dependency graph - no DI container.
        // ------------------------------------------------------------------
        IConnectionStateProvider adapterProvider = new AdapterConnectionStateProvider();
        IInternetStateProvider internetProvider = new InternetStateProvider(config.HttpsProbeUrl);
        IFortiClientProcessProvider processProvider = new FortiClientProcessProvider();
        IFortiClientLogMonitor logMonitor = new FortiClientLogMonitor(config.FortiClientLogDirectory, config.ProfileName);
        IVpnEventCorrelator correlator = new VpnEventCorrelator(config.ProfileName, config.OpenCorrelationTimeoutMinutes);
        IVpnEventStore store = new SqliteVpnEventStore(config.DatabasePath);
        var throughputTracker = new NetworkThroughputTracker();

        // The human-readable trail of what happened and what we did. Deliberately a
        // SEPARATE store from the monitoring database above, at a fixed shared path, so
        // the GUI and the CLI append to one history instead of each keeping a private
        // half of the story.
        IVpnActivityLog activityLog = CreateActivityLog(config);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancelEventArgs) =>
        {
            // Prevent the runtime from tearing the process down immediately so the loop
            // below can observe cancellation and shut down cleanly (final report, etc.).
            cancelEventArgs.Cancel = true;
            cts.Cancel();
        };
        CancellationToken ct = cts.Token;

        // One anonymous, unauthenticated check against GitHub's public release API,
        // fired once and forgotten - never awaited, never allowed to delay startup or
        // the poll loop, and its own checker guarantees it can never throw. The result
        // (if any) surfaces as one more line on the dashboard once it resolves; there
        // is nothing to show while it is still in flight or if it fails.
        string? updateNotice = null;
        _ = Task.Run(async () =>
        {
            try
            {
                using var updateChecker = new GitHubUpdateChecker();
                Version currentVersion = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0);
                UpdateCheckResult result = await updateChecker.CheckForUpdateAsync(currentVersion, ct).ConfigureAwait(false);
                if (result.Outcome == UpdateCheckOutcome.UpdateAvailable)
                {
                    updateNotice = $"A newer version ({result.LatestVersionTag}) is available: {result.ReleaseUrl}";
                }
            }
            catch
            {
                // A background version check must never take down the monitor.
            }
        }, ct);

        Console.WriteLine(config.AutoReconnectEnabled
            ? "VPN Watchdog starting - monitoring with AUTO-RECONNECT. Press Ctrl+C to stop."
            : "VPN Watchdog starting - OBSERVE ONLY mode. Press Ctrl+C to stop.");

        // Did the USER ask for auto-reconnect at all? config.AutoReconnectEnabled is the
        // EFFECTIVE value and gets switched off and back on again as FortiClient's COM
        // server comes and goes, so it can no longer answer that question - and the answer
        // is what decides whether we are allowed to touch FortiClient at all. An
        // observe-only run must never activate the COM server, not even to re-probe it.
        bool autoReconnectRequested = config.AutoReconnectEnabled;

        // The FortiClient COM server (fccomint.exe) is activated ONLY when auto-reconnect
        // is actually switched on - an observe-only run must not touch FortiClient at all.
        // If COM turns out to be unusable (FortiClient not installed, interface not
        // registered, ...) the monitor must still run, so that downgrades this session to
        // observe-only rather than aborting.
        //
        // The downgrade is TEMPORARY, not a verdict on the machine: nextComReprobeAt says
        // when to look again, and a re-probe that succeeds re-arms auto-reconnect for the
        // rest of the run. Null means "not downgraded" - either armed, or never requested.
        IReconnectController? reconnectController = null;
        bool comUnavailable = false;
        DateTimeOffset? nextComReprobeAt = null;
        if (config.AutoReconnectEnabled)
        {
            (reconnectController, string? armFailure) =
                await TryArmReconnectControllerAsync(config, ct).ConfigureAwait(false);

            if (reconnectController is null)
            {
                comUnavailable = true;
                config = config with { AutoReconnectEnabled = false };
                nextComReprobeAt = DateTimeOffset.Now + ComReprobeInterval;
                PrintAutoReconnectDowngraded(armFailure!);
                RecordActivity(
                    activityLog,
                    VpnActivityKind.AutoReconnectDisarmed,
                    $"Auto-reconnect disarmed for '{config.ProfileName}': {armFailure}. " +
                    $"Observing only; FortiClient will be re-checked every {ComReprobeInterval.TotalMinutes:0} minutes.",
                    config.ProfileName,
                    armFailure);
            }
            else
            {
                RecordActivity(
                    activityLog,
                    VpnActivityKind.AutoReconnectArmed,
                    $"Auto-reconnect armed for '{config.ProfileName}' - FortiClient COM automation answered.",
                    config.ProfileName);
            }
        }

        // Constructed after the final AutoReconnectEnabled value is known, because the
        // policy is handed the config and decides DisabledByUser from it - building it
        // any earlier would let a downgraded session still evaluate to Triggered. For the
        // same reason it is REPLACED (never mutated) when the arming state changes later:
        // the switch is read once, at construction.
        ReconnectPolicy policy = new(config);
        var reconnectStats = new ReconnectStats();

        var dashboard = new ConsoleDashboard();

        PrintAutoReconnectBanner(config);

        DateTimeOffset startedAt = DateTimeOffset.Now;
        VpnState? lastKnownState = null;

        RecordActivity(
            activityLog,
            VpnActivityKind.MonitoringStarted,
            config.AutoReconnectEnabled
                ? $"Monitoring started for '{config.ProfileName}' with auto-reconnect ARMED (connect only)."
                : $"Monitoring started for '{config.ProfileName}' in observe-only mode.",
            config.ProfileName,
            $"poll {config.PollIntervalMs}ms, heartbeat {config.HeartbeatIntervalMs}ms");

        // Console.CancelKeyPress only fires for a genuine Ctrl+C/Ctrl+Break - a plain
        // termination request (e.g. `taskkill` without /F, a service stop, losing the
        // console) never reaches it, which would silently lose the session summary.
        // ProcessExit is the reliable catch-all for those cases; it must be fast and
        // synchronous (the runtime does not guarantee it can wait on async work), and
        // idempotent since the normal end-of-loop path may also have already run it.
        int reportWritten = 0;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (Interlocked.Exchange(ref reportWritten, 1) == 1) return;
            RecordActivity(
                activityLog,
                VpnActivityKind.MonitoringStopped,
                $"Monitoring stopped for '{config.ProfileName}' (process exiting).",
                config.ProfileName);
            WriteSessionSummarySync(
                config, correlator, startedAt, DateTimeOffset.Now, lastKnownState, reconnectStats, comUnavailable);
        };

        // Previous tick's decision, so only genuine TRANSITIONS are logged - evaluating
        // every 2 seconds and printing each result would bury the console in noise.
        ReconnectDecision? lastDecision = null;

        // Same reasoning for the activity log: the internet state is re-read every tick,
        // but only a CHANGE is worth a line in a history a person reads.
        InternetState? lastInternetState = null;

        // Whether the tunnel was UP or DOWN the last time the activity log said so. This
        // is deliberately coarser than lastKnownState: Recovering, NetworkUnavailable and
        // Disconnected are all "down" to a person reading the history, and logging each
        // hop between them would turn one outage into three lines. It is tri-state so
        // that Unknown (a measurement gap, not evidence) can never manufacture a
        // disconnect line - the same mapping the GUI uses, so both front-ends write the
        // same story into the shared log.
        bool? lastLoggedVpnConnected = null;

        // Whether the FortiVPN process was running the last time the log said so. Only
        // a CHANGE is recorded; process snapshots arrive every tick.
        bool? lastLoggedFortiVpnRunning = null;

        // Snapshot persistence bookkeeping. A row per tick is ~43,000 rows a day, for
        // ever, for data nothing reads - see the persistence block in the loop.
        VpnState? lastPersistedState = null;
        DateTimeOffset lastSnapshotPersistedAt = DateTimeOffset.MinValue;
        TimeSpan snapshotHeartbeat =
            TimeSpan.FromMilliseconds(Math.Max(config.PollIntervalMs, config.HeartbeatIntervalMs));

        // Throttles the poll loop's catch-all error reporting (see the catch below).
        var pollErrors = new PollErrorThrottle(PollErrorSummaryInterval);

        // Set by a background attempt task when FortiClient's COM server turns out to be
        // genuinely gone, and consumed by the poll loop. The loop - and ONLY the loop -
        // changes the arming state (controller, policy, config), so that state needs no
        // lock at all; a background task that discovers bad news just hands it over.
        // 0 = nothing to report, 1 = COM was unavailable during an attempt.
        int comWentUnavailable = 0;
        string? comUnavailableReason = null;

        // The single in-flight reconnect attempt, if any. Single-flight is enforced by
        // policy.BeginAttempt(), so at most one of these is ever live.
        Task? attemptTask = null;

        bool modeHeadlineReprinted = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    DateTimeOffset now = DateTimeOffset.Now;

                    // ----------------------------------------------------------
                    // Arming state. Both branches below run ONLY on this thread and
                    // only while no attempt is in flight, which is what makes it safe
                    // to swap the policy object: a live attempt captured the policy it
                    // started with, and swapping under it would hand its EndAttempt()
                    // to a different single-flight latch than the one it claimed. Do
                    // not "simplify" either guard away.
                    // ----------------------------------------------------------

                    // (a) A background attempt found FortiClient's COM server genuinely
                    //     gone. Disarm, but only until the next re-probe: the tunnel is
                    //     still worth watching, and FortiClient may well come back.
                    if (Volatile.Read(ref comWentUnavailable) == 1 && !policy.IsAttemptInFlight)
                    {
                        Interlocked.Exchange(ref comWentUnavailable, 0);
                        string reason = Volatile.Read(ref comUnavailableReason)
                            ?? "FortiClient COM automation became unavailable";

                        comUnavailable = true;
                        config = config with { AutoReconnectEnabled = false };
                        DisposeReconnectController(reconnectController);
                        reconnectController = null;
                        policy = new ReconnectPolicy(config);
                        lastDecision = null;
                        nextComReprobeAt = now + ComReprobeInterval;

                        PrintAutoReconnectDowngraded(reason);
                        RecordActivity(
                            activityLog,
                            VpnActivityKind.AutoReconnectDisarmed,
                            $"Auto-reconnect disarmed for '{config.ProfileName}': {reason}. " +
                            $"Observing only; FortiClient will be re-checked every {ComReprobeInterval.TotalMinutes:0} minutes.",
                            config.ProfileName,
                            reason);
                    }

                    // (b) Re-probe after a downgrade. nextComReprobeAt is only ever set
                    //     when the user actually asked for auto-reconnect, so an
                    //     observe-only run still never touches FortiClient. The probe is
                    //     bounded by TunnelListTimeout, so the worst case is one delayed
                    //     tick every ComReprobeInterval while COM is down - cheap next to
                    //     silently spending the rest of a multi-day run unable to recover.
                    else if (autoReconnectRequested
                        && nextComReprobeAt is { } reprobeDueAt
                        && now >= reprobeDueAt
                        && !policy.IsAttemptInFlight)
                    {
                        // The failure reason is discarded on purpose: this re-probe repeats
                        // every few minutes for as long as FortiClient stays away, and the
                        // downgrade it is retrying was already announced loudly, once.
                        // Reprinting it 288 times a day is exactly the noise DEFECT 2 is about.
                        (IReconnectController? reprobed, _) =
                            await TryArmReconnectControllerAsync(config, ct).ConfigureAwait(false);

                        if (reprobed is null)
                        {
                            nextComReprobeAt = now + ComReprobeInterval;
                        }
                        else
                        {
                            reconnectController = reprobed;
                            nextComReprobeAt = null;
                            config = config with { AutoReconnectEnabled = true };
                            policy = new ReconnectPolicy(config);
                            lastDecision = null;

                            PrintAutoReconnectRearmed(config);
                            RecordActivity(
                                activityLog,
                                VpnActivityKind.AutoReconnectArmed,
                                $"Auto-reconnect re-armed for '{config.ProfileName}' - FortiClient COM automation is answering again.",
                                config.ProfileName);
                        }
                    }

                    AdapterSnapshot adapterSnapshot =
                        await adapterProvider.GetAdapterSnapshotAsync(config.AdapterDescriptionPattern, ct);
                    InternetSnapshot internetSnapshot = await internetProvider.GetCurrentStateAsync(ct);
                    IReadOnlyList<ProcessSnapshot> processSnapshots = await processProvider.GetProcessesAsync(ct);

                    // null (not a fabricated zero) until there are two trustworthy
                    // samples - see NetworkThroughputTracker for the guarantees.
                    ThroughputSample? throughput =
                        throughputTracker.Update(adapterSnapshot.BytesReceived, adapterSnapshot.BytesSent, now);

                    // Drain everything currently available from the log monitor for this tick
                    // only - it is expected to yield the new lines since the last tracked byte
                    // offset and then complete, not to block waiting for future lines.
                    var newLogEvents = new List<LogEvent>();
                    await foreach (LogEvent logEvent in logMonitor.TailNewEventsAsync(ct))
                    {
                        newLogEvents.Add(logEvent);
                    }

                    DisconnectCorrelation? openBefore = correlator.GetOpenCorrelation();
                    int completedCountBefore = correlator.GetCompletedCorrelations().Count;

                    // This is the ONE place DateTimeOffset.Now is allowed to feed the correlator -
                    // the correlator itself must remain a pure function of its inputs.
                    VpnStateSnapshot vpnStateSnapshot =
                        correlator.Ingest(adapterSnapshot, internetSnapshot, processSnapshots, newLogEvents, now);

                    DisconnectCorrelation? openAfter = correlator.GetOpenCorrelation();
                    IReadOnlyList<DisconnectCorrelation> completedAfter = correlator.GetCompletedCorrelations();

                    VpnState? previousState = lastKnownState;
                    lastKnownState = vpnStateSnapshot.State;

                    // ----------------------------------------------------------
                    // Persist the fused state on a CHANGE, plus a heartbeat - never on
                    // every tick. A row per tick is ~43,000 rows a day, growing for as
                    // long as the process runs, for a table nothing reads; the history
                    // is just as useful (every transition is still there, and the
                    // heartbeat still proves the watchdog was alive and what it saw)
                    // at a small fraction of the size. The heartbeat reuses
                    // HeartbeatIntervalMs, the same "nothing changed, say so anyway"
                    // cadence the dashboard redraws on, and is floored at the poll
                    // interval so a heartbeat shorter than a tick cannot ask for more
                    // rows than there are ticks.
                    // ----------------------------------------------------------
                    bool vpnStateChanged = lastPersistedState != vpnStateSnapshot.State;
                    if (vpnStateChanged || now - lastSnapshotPersistedAt >= snapshotHeartbeat)
                    {
                        await store.SaveVpnStateSnapshotAsync(vpnStateSnapshot, ct);
                        lastPersistedState = vpnStateSnapshot.State;
                        lastSnapshotPersistedAt = now;
                    }

                    foreach (LogEvent logEvent in newLogEvents)
                    {
                        await store.SaveLogEventAsync(logEvent, ct);
                    }

                    // Activity log: TRANSITIONS only, for the same reason - a line every
                    // 2 seconds saying "still connected" is not a history anyone can read.
                    //
                    // The transition that matters is UP <-> DOWN, not every hop of the
                    // correlator's state machine. The first state out of Connected is
                    // always Recovering (or NetworkUnavailable), never Disconnected, so
                    // keying the disconnect line on == Disconnected used to skip the whole
                    // Connected -> Recovering -> Connected episode - the history showed two
                    // "connected" lines with no drop between them - and reached Disconnected
                    // only after the correlation had already timed out, by which point the
                    // evidence about WHY it dropped was gone.
                    bool? vpnConnectedNow = IsTunnelUp(vpnStateSnapshot.State);
                    if (vpnConnectedNow is bool vpnIsConnected && lastLoggedVpnConnected != vpnIsConnected)
                    {
                        string transition = $"{previousState?.ToString() ?? "(startup)"} -> {vpnStateSnapshot.State}";

                        if (vpnIsConnected)
                        {
                            // If this tick also closed a disconnect episode, say how long the
                            // tunnel was down. RecoveryDuration is disconnect -> connected as
                            // the correlator measured it - it is NOT the duration of any
                            // reconnect attempt this watchdog made, and must not be read as one.
                            DisconnectCorrelation? recovered =
                                FindRecoveryCompletedThisTick(openBefore, openAfter, completedAfter);
                            string detail = recovered?.RecoveryDuration is { } recoveryDuration
                                ? $"{transition}; Recovery: {FormatWholeSeconds(recoveryDuration)}"
                                : transition;

                            RecordActivity(
                                activityLog,
                                VpnActivityKind.VpnConnected,
                                $"VPN '{config.ProfileName}' is connected.",
                                config.ProfileName,
                                detail);
                        }
                        else if (openAfter?.DisconnectClassification == "Unexpected")
                        {
                            // The classification comes ONLY from FortiClient's own trace log
                            // text; the reason code is shown exactly as observed and never
                            // translated - no one has documented what the numbers mean, and
                            // inventing a meaning would be worse than showing none.
                            string reason = string.IsNullOrWhiteSpace(openAfter.DisconnectReasonCode)
                                ? "Unexpected disconnect"
                                : $"Unexpected disconnect - Reason: {openAfter.DisconnectReasonCode}";

                            RecordActivity(
                                activityLog,
                                VpnActivityKind.VpnDisconnectedUnexpectedly,
                                $"VPN '{config.ProfileName}' disconnected unexpectedly.",
                                config.ProfileName,
                                $"{reason}; {transition}");
                        }
                        else
                        {
                            RecordActivity(
                                activityLog,
                                VpnActivityKind.VpnDisconnected,
                                $"VPN '{config.ProfileName}' is disconnected.",
                                config.ProfileName,
                                transition);
                        }

                        lastLoggedVpnConnected = vpnIsConnected;
                    }

                    if (lastInternetState != internetSnapshot.State)
                    {
                        // Only the two states a person acts on are logged; Unknown is a
                        // measurement gap, not news, and must not read like an outage.
                        if (internetSnapshot.State == InternetState.Down)
                        {
                            RecordActivity(
                                activityLog,
                                VpnActivityKind.InternetLost,
                                "Internet connectivity lost - a VPN reconnect cannot help until it is back.",
                                config.ProfileName,
                                $"probe {internetSnapshot.HttpsProbeUrl}");
                        }
                        else if (internetSnapshot.State == InternetState.Up && lastInternetState == InternetState.Down)
                        {
                            RecordActivity(
                                activityLog,
                                VpnActivityKind.InternetRestored,
                                "Internet connectivity restored.",
                                config.ProfileName,
                                $"probe {internetSnapshot.HttpsProbeUrl}");
                        }

                        lastInternetState = internetSnapshot.State;
                    }

                    // FortiClient process: TRANSITIONS only, same discipline. A snapshot list
                    // without a FortiVPN entry means we did not look, not that it is absent,
                    // so nothing is logged in that case rather than a false "not running".
                    ProcessSnapshot? fortiVpnProcess = FindProcess(processSnapshots, FortiVpnProcessName);
                    if (fortiVpnProcess is not null && lastLoggedFortiVpnRunning != fortiVpnProcess.IsRunning)
                    {
                        if (fortiVpnProcess.IsRunning)
                        {
                            RecordActivity(
                                activityLog,
                                VpnActivityKind.FortiClientRunning,
                                "FortiClient is running.",
                                config.ProfileName,
                                fortiVpnProcess.ProcessId is { } pid
                                    ? $"{FortiVpnProcessName} process running (PID {pid})"
                                    : $"{FortiVpnProcessName} process running");
                        }
                        else
                        {
                            RecordActivity(
                                activityLog,
                                VpnActivityKind.FortiClientNotRunning,
                                "FortiClient is not running.",
                                config.ProfileName,
                                $"{FortiVpnProcessName} process not found");
                        }

                        lastLoggedFortiVpnRunning = fortiVpnProcess.IsRunning;
                    }

                    // A correlation was newly opened this tick if there either wasn't one before,
                    // or the one that exists now is a different one than before.
                    bool openedNewCorrelation = openAfter is not null
                        && (openBefore is null || openBefore.CorrelationId != openAfter.CorrelationId);
                    if (openedNewCorrelation)
                    {
                        await store.UpsertCorrelationAsync(openAfter!, ct);
                    }

                    // One or more correlations reached a terminal outcome this tick.
                    if (completedAfter.Count > completedCountBefore)
                    {
                        for (int i = completedCountBefore; i < completedAfter.Count; i++)
                        {
                            await store.UpsertCorrelationAsync(completedAfter[i], ct);
                        }
                    }

                    dashboard.Render(
                        config, vpnStateSnapshot, internetSnapshot, processSnapshots, correlator, now,
                        throughput: throughput,
                        updateNotice: updateNotice);

                    // The dashboard clears the screen on its first render, which would wipe the
                    // startup banner within a couple of seconds; restate the one-line mode
                    // headline once, directly underneath that first frame.
                    if (!modeHeadlineReprinted)
                    {
                        modeHeadlineReprinted = true;
                        Console.WriteLine(AutoReconnectHeadline(config));
                    }

                    // ----------------------------------------------------------
                    // Reconnect decision. Pure policy - no I/O, no COM, no side
                    // effects; it just says what should happen right now.
                    // ----------------------------------------------------------
                    ReconnectDecision decision = policy.Evaluate(vpnStateSnapshot.State, internetSnapshot.State, now);

                    if (decision != lastDecision)
                    {
                        string previous = lastDecision?.ToString() ?? "(startup)";
                        Console.WriteLine(
                            $"[{now:yyyy-MM-dd HH:mm:ss}] RECONNECT: {previous} -> {decision} - {DescribeDecision(decision, config)}");

                        // Note: no policy.Reset() call here. ReconnectPolicy.Evaluate already
                        // clears its own outage bookkeeping the moment it sees VpnState.Connected,
                        // so resetting from out here would be redundant, not belt-and-braces.
                        lastDecision = decision;

                        if (decision == ReconnectDecision.GaveUp)
                        {
                            RecordActivity(
                                activityLog,
                                VpnActivityKind.AutoReconnectGaveUp,
                                $"Auto-reconnect gave up on '{config.ProfileName}' after {config.ReconnectMaxAttempts} " +
                                "attempts for this outage - manual action is needed.",
                                config.ProfileName,
                                $"attempts: {reconnectStats.Attempts}");
                        }
                    }

                    // BeginAttempt() is the single-flight latch: it returns false if an attempt
                    // is already running, so two Connect() calls can never overlap even if the
                    // policy and the loop momentarily disagree.
                    if (decision == ReconnectDecision.Triggered
                        && reconnectController is not null
                        && policy.BeginAttempt())
                    {
                        IReconnectController controller = reconnectController;
                        string profileName = config.ProfileName;
                        int attemptNumber = reconnectStats.StartAttempt();

                        // The policy this attempt claimed the single-flight slot from. It
                        // MUST be the one released below: the loop replaces `policy` when
                        // auto-reconnect is disarmed or re-armed, and letting this task
                        // follow that variable would end an attempt on a latch it never
                        // claimed, leaving the real one shut for the rest of the run.
                        ReconnectPolicy attemptPolicy = policy;

                        RecordActivity(
                            activityLog,
                            VpnActivityKind.AutoReconnectTriggered,
                            $"Attempting VPN recovery for '{profileName}' - asking FortiClient to reconnect, " +
                            $"the {config.ReconnectGracePeriodSeconds}s grace period having elapsed without it recovering on its own.",
                            profileName,
                            $"attempt #{attemptNumber}");

                        // Deliberately NOT awaited: a reconnect takes seconds (~4s observed) and
                        // the poll loop must keep observing, persisting and rendering while it
                        // runs. Task.Run is given CancellationToken.None on purpose - handing it
                        // `ct` would mean an already-cancelled token silently skips the delegate
                        // entirely, so EndAttempt() would never run and the single-flight latch
                        // would stay shut for the rest of the process's life.
                        attemptTask = Task.Run(async () =>
                        {
                            bool succeeded = false;
                            try
                            {
                                Console.WriteLine(
                                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] RECONNECT: attempt #{attemptNumber} - " +
                                    $"asking FortiClient to connect '{profileName}' (no credentials are supplied by us).");

                                await controller.ReconnectAsync(profileName, ct).ConfigureAwait(false);

                                succeeded = true;
                                reconnectStats.RecordSuccess();
                                Console.WriteLine(
                                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] RECONNECT: attempt #{attemptNumber} SUCCEEDED " +
                                    $"for '{profileName}'.");
                                RecordActivity(
                                    activityLog,
                                    VpnActivityKind.AutoReconnectSucceeded,
                                    $"Recovery successful - FortiClient brought '{profileName}' back up.",
                                    profileName,
                                    $"attempt #{attemptNumber}");
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                reconnectStats.RecordFailure();
                                Console.Error.WriteLine(
                                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] RECONNECT: attempt #{attemptNumber} ABORTED - " +
                                    "the watchdog is shutting down.");
                                RecordActivity(
                                    activityLog,
                                    VpnActivityKind.AutoReconnectFailed,
                                    $"Auto-reconnect attempt #{attemptNumber} for '{profileName}' was abandoned - the watchdog is shutting down.",
                                    profileName,
                                    "cancelled at shutdown");
                            }
                            catch (FortiClientComUnavailableException ex)
                            {
                                // Genuinely unavailable, as opposed to an attempt that was
                                // made and failed (a plain InvalidOperationException) - the
                                // controller draws that line, and a plain failure must NEVER
                                // disarm the session. Hand the fact to the poll loop instead
                                // of acting on it here: the loop owns the arming state, and
                                // this task is still holding the single-flight slot.
                                reconnectStats.RecordFailure();
                                Console.Error.WriteLine(
                                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] RECONNECT: attempt #{attemptNumber} FAILED " +
                                    $"for '{profileName}': {ex.GetType().Name}: {ex.Message}");
                                RecordActivity(
                                    activityLog,
                                    VpnActivityKind.AutoReconnectFailed,
                                    $"Auto-reconnect attempt #{attemptNumber} for '{profileName}' failed: FortiClient COM automation is unavailable.",
                                    profileName,
                                    $"{ex.GetType().Name}: {ex.Message}");

                                Volatile.Write(
                                    ref comUnavailableReason,
                                    $"FortiClient COM automation became unavailable ({ex.Message})");
                                Interlocked.Exchange(ref comWentUnavailable, 1);
                            }
                            catch (Exception ex)
                            {
                                // Catch-all on purpose: a COM error, a timeout or anything else
                                // from a single attempt must never take down the poll loop.
                                reconnectStats.RecordFailure();
                                Console.Error.WriteLine(
                                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] RECONNECT: attempt #{attemptNumber} FAILED " +
                                    $"for '{profileName}': {ex.GetType().Name}: {ex.Message}");
                                RecordActivity(
                                    activityLog,
                                    VpnActivityKind.AutoReconnectFailed,
                                    $"Auto-reconnect attempt #{attemptNumber} for '{profileName}' failed: {ex.Message}",
                                    profileName,
                                    $"{ex.GetType().Name}: {ex.Message}");
                            }
                            finally
                            {
                                try
                                {
                                    attemptPolicy.RecordAttemptResult(succeeded, DateTimeOffset.Now);
                                }
                                catch (Exception ex)
                                {
                                    Console.Error.WriteLine(
                                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] RECONNECT: could not record the result of " +
                                        $"attempt #{attemptNumber}: {ex.GetType().Name}: {ex.Message}");
                                }
                                finally
                                {
                                    // Must run no matter what, or the single-flight latch stays
                                    // shut and no further attempt is ever possible. Released on
                                    // the SAME policy the slot was claimed from - see above.
                                    try
                                    {
                                        attemptPolicy.EndAttempt();
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.Error.WriteLine(
                                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] RECONNECT: failed to clear the in-flight " +
                                            $"flag after attempt #{attemptNumber}: {ex.GetType().Name}: {ex.Message}");
                                    }
                                }
                            }
                        }, CancellationToken.None);
                    }

                    // The tick got all the way through: close out any error the throttle
                    // below is still suppressing, so the next occurrence reports in full.
                    pollErrors.RecordTickSucceeded(now);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A single tick's transient failure (e.g. a momentary file read error) must
                    // never bring down the whole watchdog process. Reporting goes through the
                    // throttle: a fault that persists (a missing log directory, a locked
                    // database) recurs every PollIntervalMs for as long as it lasts, and
                    // printing a full stack trace each time floods a redirected log file with
                    // tens of thousands of copies of one problem.
                    pollErrors.Report(ex, DateTimeOffset.Now);
                }

                try
                {
                    await Task.Delay(config.PollIntervalMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            // Give an attempt that is still talking to FortiClient a bounded moment to
            // finish before the COM object underneath it is released. The attempt task
            // swallows all of its own exceptions, so this can never throw.
            if (attemptTask is { IsCompleted: false })
            {
                Console.WriteLine(
                    $"Waiting up to {AttemptDrainTimeout.TotalSeconds:0}s for the in-flight reconnect attempt to finish...");
                await Task.WhenAny(attemptTask, Task.Delay(AttemptDrainTimeout, CancellationToken.None))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Release the COM server explicitly rather than leaving it to the finalizer -
            // this process is expected to run for days and must not leak fccomint.exe.
            DisposeReconnectController(reconnectController);
        }

        if (Interlocked.Exchange(ref reportWritten, 1) == 0)
        {
            RecordActivity(
                activityLog,
                VpnActivityKind.MonitoringStopped,
                $"Monitoring stopped for '{config.ProfileName}'.",
                config.ProfileName,
                $"ran for {FormatTimeSpan(DateTimeOffset.Now - startedAt)}");
            WriteSessionSummarySync(
                config, correlator, startedAt, DateTimeOffset.Now, lastKnownState, reconnectStats, comUnavailable);
        }

        // Closed only here, after the last entry: the ProcessExit handler above shares
        // this log and is guarded by the same reportWritten latch, so by this point it
        // can no longer run and cannot find a closed log underneath it.
        DisposeActivityLog(activityLog);

        return 0;
    }

    private static bool IsAutoReconnectFlag(string arg) => MatchesFlag(arg, AutoReconnectFlag);

    private static bool IsConnectFlag(string arg) => MatchesFlag(arg, ConnectFlag);

    private static bool IsDisconnectFlag(string arg) => MatchesFlag(arg, DisconnectFlag);

    /// <summary>
    /// Any flag this app understands. EVERY one of them has to be stripped before
    /// AppConfigLoader sees the args, or the loader would take it for a config-file path.
    /// </summary>
    private static bool IsKnownFlag(string arg) =>
        IsAutoReconnectFlag(arg) || IsConnectFlag(arg) || IsDisconnectFlag(arg);

    /// <summary>
    /// Accepts the three prefix styles this CLI has always accepted (--flag, /flag,
    /// -flag), compared case-insensitively.
    /// </summary>
    private static bool MatchesFlag(string arg, string flagName)
    {
        if (string.IsNullOrWhiteSpace(arg)) return false;

        string trimmed = arg.Trim();
        string withoutPrefix = trimmed switch
        {
            _ when trimmed.StartsWith("--", StringComparison.Ordinal) => trimmed[2..],
            _ when trimmed.StartsWith("/", StringComparison.Ordinal) => trimmed[1..],
            _ when trimmed.StartsWith("-", StringComparison.Ordinal) => trimmed[1..],
            _ => trimmed
        };

        return withoutPrefix.Equals(flagName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Performs exactly ONE user-initiated tunnel action and returns the process exit
    /// code.
    /// <para>
    /// This method is the ONLY place in the CLI allowed to touch
    /// <see cref="IManualVpnControl"/>. It is reached only from a command the user typed,
    /// it runs before any monitoring machinery exists, and the process exits the moment it
    /// is done. The poll loop, the correlator and the reconnect policy are handed an
    /// <see cref="IReconnectController"/> and nothing else, so they cannot reach Disconnect
    /// at all - that boundary is enforced by the type system and must never be widened by
    /// calling this from automatic, timer or policy code.
    /// </para>
    /// <para>
    /// No credentials are involved on any path here: the controller asks FortiClient to
    /// drive its own saved profile, and this app has no field or parameter for a secret.
    /// </para>
    /// </summary>
    private static async Task<int> RunManualVpnCommandAsync(
        WatchdogConfig config,
        bool connect,
        bool autoReconnectFlagPassed)
    {
        string action = connect ? "connect" : "disconnect";
        string profileName = config.ProfileName;

        VpnActivityKind requestedKind =
            connect ? VpnActivityKind.ManualConnectRequested : VpnActivityKind.ManualDisconnectRequested;
        VpnActivityKind succeededKind =
            connect ? VpnActivityKind.ManualConnectSucceeded : VpnActivityKind.ManualDisconnectSucceeded;
        VpnActivityKind failedKind =
            connect ? VpnActivityKind.ManualConnectFailed : VpnActivityKind.ManualDisconnectFailed;

        // The activity log IS opened for a one-shot command, even though the monitoring
        // database is not: a user asking for the tunnel by hand is exactly the kind of
        // thing the shared history exists to explain later ("why did it go down at 16:05?").
        IVpnActivityLog activityLog = CreateActivityLog(config);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancelEventArgs) =>
        {
            // Same reason as the monitoring path: take the shutdown into our own hands so
            // the COM object below is released rather than abandoned mid-call.
            cancelEventArgs.Cancel = true;
            cts.Cancel();
        };
        CancellationToken ct = cts.Token;

        Console.WriteLine();
        Console.WriteLine($"Asking FortiClient to {action} '{profileName}'...");
        RecordActivity(
            activityLog,
            requestedKind,
            $"User asked to {action} '{profileName}' from the command line.",
            profileName,
            $"--{(connect ? ConnectFlag : DisconnectFlag)}");
        Console.WriteLine(
            "  No credentials are supplied by this app - FortiClient uses its own saved password, and this");
        Console.WriteLine(
            "  process has nowhere to put one even if it wanted to.");
        Console.WriteLine(
            "  One-shot command: nothing is monitored, no database is opened, no session summary is written.");

        if (autoReconnectFlagPassed)
        {
            // Only reachable as --connect --auto-reconnect (--disconnect with it is
            // refused outright). Nothing here is armed by that flag, because this process
            // exits without ever starting a watchdog - say so rather than swallow it.
            Console.WriteLine(
                $"  IGNORED: --{AutoReconnectFlag} has no effect on a one-shot command - it arms the monitoring loop,");
            Console.WriteLine(
                "           which this run never starts. Re-run without --connect to monitor with auto-reconnect.");
        }

        if (!connect && config.AutoReconnectEnabled)
        {
            // The type system keeps THIS process's poll loop away from Disconnect; it
            // cannot do anything about a SEPARATE watchdog process that was started with
            // auto-reconnect armed and will observe this drop and helpfully undo it. Say
            // so plainly instead of letting the tunnel silently pop back up and look like
            // the disconnect never worked.
            Console.WriteLine();
            Console.WriteLine(
                "  NOTE: the config file sets AutoReconnectEnabled=true. This command exits immediately, so it");
            Console.WriteLine(
                "        cannot fight anything itself - but any watchdog session running with auto-reconnect ON");
            Console.WriteLine(
                $"        will see this drop and reconnect after its {config.ReconnectGracePeriodSeconds}s grace period. Stop that session first if");
            Console.WriteLine(
                "        you want the tunnel to stay down.");
        }

        Console.WriteLine();

        // The controller's own verify loop already gives up after the configured timeout;
        // this outer budget only exists so a wedged COM server cannot hang the command.
        TimeSpan budget =
            TimeSpan.FromSeconds(Math.Max(1, config.ReconnectVerifyTimeoutSeconds)) + ManualCommandSlack;

        FortiClientComReconnectController controller;
        try
        {
            controller = FortiClientComReconnectController.FromConfig(config);
        }
        catch (Exception ex)
        {
            // Construction is cheap and binds nothing, so a failure here is a genuine
            // "this machine cannot do it" - report it as the unavailable case.
            Console.Error.WriteLine(
                $"ERROR: the FortiClient COM controller could not be created ({ex.GetType().Name}: {ex.Message}).");
            Console.Error.WriteLine(
                $"       '{profileName}' was NOT {action}ed. (exit code {ExitComUnavailable})");
            RecordActivity(
                activityLog,
                failedKind,
                $"Manual {action} of '{profileName}' failed: the FortiClient COM controller could not be created.",
                profileName,
                $"{ex.GetType().Name}: {ex.Message}");
            DisposeActivityLog(activityLog);
            return ExitComUnavailable;
        }

        try
        {
            // The one and only use of the manual-control interface in this app's console
            // front-end. Note the local is typed as IManualVpnControl deliberately: it
            // documents at the call site which capability is being exercised.
            IManualVpnControl manualControl = controller;

            // The controller verifies for us: Connect polls IsConnected until it reads
            // true, Disconnect polls until it reads FALSE. That polling is not optional -
            // FortiClient's Disconnect returns before the tunnel has finished tearing down
            // and IsConnected keeps reading stale-TRUE for several seconds afterwards, so
            // trusting the immediate return would wrongly report the tunnel as still up.
            Task operation = connect
                ? manualControl.ConnectAsync(profileName, ct)
                : manualControl.DisconnectAsync(profileName, ct);

            await operation.WaitAsync(budget, ct).ConfigureAwait(false);

            Console.WriteLine($"FortiClient confirmed the {action}: '{profileName}' is now {StateWord(connect)}.");

            // Read FortiClient's own view back once more, purely to print the final state.
            // This is safe to trust here (and not a stale read) precisely because the call
            // above already waited for IsConnected to FLIP - anything different now is a
            // real change that happened afterwards, not tear-down lag.
            bool? finalState = await ReadBackStateAsync(controller, profileName, ct).ConfigureAwait(false);

            if (finalState is null)
            {
                Console.WriteLine(
                    "  (FortiClient's final state could not be re-read; the confirmation above still stands.)");
                RecordActivity(
                    activityLog,
                    succeededKind,
                    $"FortiClient confirmed the manual {action} of '{profileName}'.",
                    profileName,
                    "final state could not be re-read");
                return ExitSuccess;
            }

            Console.WriteLine($"  Final state per FortiClient: {StateWord(finalState.Value)}.");

            if (finalState.Value == connect)
            {
                RecordActivity(
                    activityLog,
                    succeededKind,
                    $"FortiClient confirmed the manual {action} of '{profileName}' - it is now {StateWord(finalState.Value)}.",
                    profileName);
                return ExitSuccess;
            }

            string likelyCause = connect
                ? "the tunnel appears to have dropped again immediately."
                : "a watchdog session running with auto-reconnect ON does exactly this.";

            Console.Error.WriteLine(
                $"WARNING: '{profileName}' already reads {StateWord(finalState.Value)} again. The {action} was confirmed a");
            Console.Error.WriteLine(
                $"         moment ago, so something changed it straight back - {likelyCause}");
            Console.Error.WriteLine(
                $"         Reporting this as a {action} that did not stick. (exit code {ExitOperationFailed})");
            RecordActivity(
                activityLog,
                failedKind,
                $"Manual {action} of '{profileName}' did not stick - FortiClient confirmed it, then the tunnel " +
                $"read {StateWord(finalState.Value)} again moments later.",
                profileName,
                likelyCause);
            return ExitOperationFailed;
        }
        catch (FortiClientComUnavailableException ex)
        {
            // Distinct from a failed operation on purpose: this means the capability is
            // missing on this machine (FortiClient not installed, COM server not
            // registered or not runnable), not that FortiClient refused the request.
            Console.Error.WriteLine($"ERROR: FortiClient COM automation is unavailable - {ex.Message}");
            Console.Error.WriteLine(
                $"       '{profileName}' was NOT {action}ed, and no request reached FortiClient. (exit code {ExitComUnavailable})");
            RecordActivity(
                activityLog,
                failedKind,
                $"Manual {action} of '{profileName}' failed: FortiClient COM automation is unavailable, so no request reached it.",
                profileName,
                $"{ex.GetType().Name}: {ex.Message}");
            return ExitComUnavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"CANCELLED: stopped waiting for the {action} of '{profileName}'. FortiClient may still be acting on the");
            Console.Error.WriteLine(
                $"           request - check its own state before assuming anything. (exit code {ExitCancelled})");
            RecordActivity(
                activityLog,
                failedKind,
                $"Manual {action} of '{profileName}' was cancelled before FortiClient confirmed it - " +
                "the request may still be in progress.",
                profileName,
                "cancelled by the user");
            return ExitCancelled;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(
                $"ERROR: FortiClient did not confirm the {action} of '{profileName}' within {budget.TotalSeconds:0}s.");
            Console.Error.WriteLine(
                $"       The request was issued but the tunnel never reached {StateWord(connect)}. (exit code {ExitOperationFailed})");
            RecordActivity(
                activityLog,
                failedKind,
                $"Manual {action} of '{profileName}' was issued but not confirmed within {budget.TotalSeconds:0}s.",
                profileName,
                $"never reached {StateWord(connect)}");
            return ExitOperationFailed;
        }
        catch (Exception ex)
        {
            // Everything else - including the controller's own "issued but never verified"
            // InvalidOperationException - is an operation that was attempted and failed,
            // which a script must be able to tell apart from COM being missing entirely.
            Console.Error.WriteLine(
                $"ERROR: the {action} of '{profileName}' failed ({ex.GetType().Name}: {ex.Message})");
            Console.Error.WriteLine(
                $"       FortiClient COM was reachable; the operation itself did not succeed. (exit code {ExitOperationFailed})");
            RecordActivity(
                activityLog,
                failedKind,
                $"Manual {action} of '{profileName}' failed: {ex.Message}",
                profileName,
                $"{ex.GetType().Name}: {ex.Message}");
            return ExitOperationFailed;
        }
        finally
        {
            // If we stopped waiting (timeout/Ctrl+C) the controller's verify loop may still
            // be running; cancel it before the COM object underneath it is released so it
            // cannot keep calling into a disposed controller on a background thread.
            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: failed to cancel the in-flight manual command: {ex.Message}");
            }

            try
            {
                controller.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Warning: failed to release the FortiClient COM controller cleanly: {ex.Message}");
            }

            // Every path through the try above has already written its outcome entry, so
            // the log is only closed once there is nothing left to say.
            DisposeActivityLog(activityLog);
        }
    }

    /// <summary>
    /// Reads FortiClient's own view of the tunnel once, for reporting only. Never throws:
    /// a state we could not re-read is reported as unknown (null) rather than turning a
    /// completed action into an error.
    /// </summary>
    private static async Task<bool?> ReadBackStateAsync(
        IReconnectController controller,
        string profileName,
        CancellationToken ct)
    {
        try
        {
            return await controller
                .IsConnectedAsync(profileName, ct)
                .WaitAsync(TunnelListTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"WARNING: could not read FortiClient's final view of '{profileName}' " +
                $"({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    private static string StateWord(bool connected) => connected ? "CONNECTED" : "DISCONNECTED";

    /// <summary>
    /// Short usage text, printed when the command line contradicts itself. The same
    /// commands are named in the startup banner so they stay discoverable in normal use.
    /// </summary>
    private static void PrintUsage()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: VpnWatchdog [config-file] [--auto-reconnect]");
        Console.Error.WriteLine("       VpnWatchdog [config-file] --connect");
        Console.Error.WriteLine("       VpnWatchdog [config-file] --disconnect");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  (no flags)        Monitor only - never connects or disconnects the VPN.");
        Console.Error.WriteLine("  --auto-reconnect  Monitor, and ask FortiClient to reconnect after the grace period.");
        Console.Error.WriteLine("  --connect         One-shot: ask FortiClient to connect the configured profile, then exit.");
        Console.Error.WriteLine("  --disconnect      One-shot: ask FortiClient to disconnect it, then exit.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  --connect and --disconnect exclude each other, and --disconnect excludes");
        Console.Error.WriteLine("  --auto-reconnect. Neither one-shot command starts the monitor, opens the");
        Console.Error.WriteLine("  database or writes a session summary, and no credentials are ever supplied by");
        Console.Error.WriteLine("  this app - FortiClient uses its own saved password.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"  Exit codes: {ExitSuccess} = done and confirmed, {ExitUsageError} = bad command line, " +
            $"{ExitComUnavailable} = FortiClient COM unavailable,");
        Console.Error.WriteLine(
            $"              {ExitOperationFailed} = attempted but not confirmed, {ExitCancelled} = cancelled.");
        Console.Error.WriteLine();
    }

    /// <summary>
    /// Builds a reconnect controller and proves - with one read-only COM call - that
    /// FortiClient's automation interface is usable RIGHT NOW. Used both at startup and,
    /// after a downgrade, by the poll loop's periodic re-probe, so that both paths arm
    /// auto-reconnect on exactly the same evidence.
    /// </summary>
    /// <returns>
    /// The armed controller and a null reason, or a null controller and the reason
    /// auto-reconnect must be downgraded. Nothing is left undisposed either way.
    /// </returns>
    private static async Task<(IReconnectController? Controller, string? FailureReason)> TryArmReconnectControllerAsync(
        WatchdogConfig config,
        CancellationToken ct)
    {
        FortiClientComReconnectController controller;
        try
        {
            // Same type as `new FortiClientComReconnectController()`, but this factory
            // hands it config.ReconnectVerifyTimeoutSeconds instead of silently using
            // the class default, so that config field actually takes effect.
            controller = FortiClientComReconnectController.FromConfig(config);
        }
        catch (Exception ex)
        {
            return (null, $"the COM controller could not be created ({ex.GetType().Name}: {ex.Message})");
        }

        (bool usable, string? reason) =
            await ProbeFortiClientComAsync(controller, config.ProfileName, ct).ConfigureAwait(false);

        if (usable)
        {
            return (controller, null);
        }

        // Nothing may hold a COM object we have just declared unusable - this process
        // runs for days and re-probes on a timer, so a leak here would accumulate.
        DisposeReconnectController(controller);
        return (null, reason);
    }

    /// <summary>
    /// One read-only COM call, doing two jobs:
    /// (1) proving FortiClient's automation interface is actually usable on this machine -
    /// the controller binds lazily, so nothing before this point can tell; and
    /// (2) checking the configured profile against FortiClient's own tunnel list, since a
    /// typo in ProfileName would otherwise only surface as every reconnect failing at 3am.
    /// Reads only: it never connects, disconnects or changes anything.
    /// </summary>
    /// <returns>
    /// Usable=false only when FortiClient COM is genuinely unusable, together with the
    /// reason to report. A merely unhelpful answer (empty list, unknown profile, a plain
    /// failure that is not proof of anything) warns and reports usable.
    /// </returns>
    private static async Task<(bool Usable, string? Reason)> ProbeFortiClientComAsync(
        IReconnectController controller,
        string profileName,
        CancellationToken ct)
    {
        IReadOnlyList<string> tunnels;
        try
        {
            // WaitAsync bounds the call so a wedged COM server cannot hang startup.
            tunnels = await controller
                .GetTunnelListAsync(ct)
                .WaitAsync(TunnelListTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (FortiClientComUnavailableException ex)
        {
            // The controller raises THIS type only for HRESULTs that genuinely mean the
            // automation server is not there. That is the one and only signal allowed to
            // switch auto-reconnect off - and even then only until the next re-probe.
            return (false, $"FortiClient COM automation is unavailable ({ex.Message})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C during startup - the loop is about to exit anyway.
            return (true, null);
        }
        catch (TimeoutException)
        {
            // The probe never came back. This used to warn and report SUCCESS, which armed
            // auto-reconnect against a COM server we had demonstrably never reached - every
            // later attempt would then hang the same way, on the same wedged server, at
            // exactly the moment recovery mattered. A probe that times out is COM NOT
            // usable; the periodic re-probe is what makes that verdict temporary.
            return (
                false,
                $"FortiClient's COM server did not answer a read-only call within {TunnelListTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            // We reached COM and it answered with something we could not use (odd HRESULT,
            // unexpected VARIANT shape). That is not proof reconnect cannot work, so warn
            // only - a plain failure must never cost the session its recovery capability.
            Console.Error.WriteLine(
                $"WARNING: could not read FortiClient's tunnel list to validate the profile name " +
                $"({ex.GetType().Name}: {ex.Message}). Auto-reconnect stays enabled.");
            return (true, null);
        }

        if (tunnels.Count == 0)
        {
            Console.Error.WriteLine(
                "WARNING: FortiClient reported no configured tunnels - reconnect attempts are unlikely to succeed.");
            return (true, null);
        }

        if (!tunnels.Any(tunnel => string.Equals(tunnel, profileName, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine(
                $"WARNING: profile '{profileName}' is not in FortiClient's tunnel list " +
                $"({string.Join(", ", tunnels)}). Reconnect attempts will very likely fail - " +
                "check ProfileName in the config file.");
        }

        return (true, null);
    }

    /// <summary>
    /// Releases the COM server explicitly rather than leaving it to the finalizer - this
    /// process is expected to run for days, and the arming state can now be torn down and
    /// rebuilt several times within one run, so a leak here would accumulate fccomint.exe
    /// instances. Never throws.
    /// </summary>
    private static void DisposeReconnectController(IReconnectController? controller)
    {
        if (controller is not IDisposable disposable)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Warning: failed to release the FortiClient COM controller cleanly: {ex.Message}");
        }
    }

    /// <summary>
    /// Says, once and loudly, that auto-reconnect was asked for but cannot be provided
    /// right now, that monitoring continues regardless, and - crucially - that this is
    /// not the last word on it.
    /// </summary>
    private static void PrintAutoReconnectDowngraded(string reason)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"WARNING: auto-reconnect was requested, but {reason}.");
        Console.Error.WriteLine(
            "         AUTO-RECONNECT IS DISABLED for now - continuing in observe-only mode.");
        Console.Error.WriteLine(
            $"         FortiClient will be re-checked every {ComReprobeInterval.TotalMinutes:0} minutes and auto-reconnect");
        Console.Error.WriteLine(
            "         re-armed automatically if it becomes usable (this is often just FortiClient");
        Console.Error.WriteLine(
            "         not being up yet at boot or after a resume).");
        Console.Error.WriteLine(
            "         Monitoring, correlation, persistence and the session summary are unaffected.");
        Console.Error.WriteLine();
    }

    /// <summary>
    /// Says, just as loudly, that the capability came back - a downgrade the user was
    /// told about must not silently reverse itself.
    /// </summary>
    private static void PrintAutoReconnectRearmed(WatchdogConfig config)
    {
        Console.WriteLine();
        Console.WriteLine("NOTICE: FortiClient COM automation is answering again.");
        Console.WriteLine(
            $"        AUTO-RECONNECT IS RE-ARMED for '{config.ProfileName}' - this session may once again ask");
        Console.WriteLine(
            $"        FortiClient to CONNECT after the {config.ReconnectGracePeriodSeconds}s grace period. It still cannot disconnect");
        Console.WriteLine(
            "        the tunnel, and still never handles credentials.");
        Console.WriteLine();
    }

    private static string AutoReconnectHeadline(WatchdogConfig config) =>
        config.AutoReconnectEnabled
            ? "AUTO-RECONNECT: ENABLED - will ask FortiClient to reconnect after the grace period"
            : "AUTO-RECONNECT: DISABLED (observe only)";

    private static void PrintAutoReconnectBanner(WatchdogConfig config)
    {
        string bar = new string('*', 78);

        Console.WriteLine();
        Console.WriteLine(bar);
        Console.WriteLine(AutoReconnectHeadline(config));
        if (config.AutoReconnectEnabled)
        {
            Console.WriteLine(
                $"  Profile:       {config.ProfileName}");
            Console.WriteLine(
                $"  Grace period:  {config.ReconnectGracePeriodSeconds}s (FortiClient's own recovery gets first go)");
            Console.WriteLine(
                $"  Max attempts:  {config.ReconnectMaxAttempts} per outage, backoff {config.ReconnectInitialBackoffSeconds}s-{config.ReconnectMaxBackoffSeconds}s");
            Console.WriteLine(
                "  This monitoring session can CONNECT the tunnel. It can never disconnect it - the poll loop is");
            Console.WriteLine(
                "  only ever handed a connect-only controller - and it never handles credentials.");
            Console.WriteLine(
                $"  One-shot manual control (acts once and exits, does not monitor): --{ConnectFlag} / --{DisconnectFlag}.");
        }
        else
        {
            Console.WriteLine(
                "  Nothing in this run will connect, disconnect or otherwise modify the VPN.");
            Console.WriteLine(
                $"  To enable it: pass --{AutoReconnectFlag} on the command line, or set");
            Console.WriteLine(
                "  \"AutoReconnectEnabled\": true in the config file.");
            Console.WriteLine(
                $"  One-shot manual control (acts once and exits, does not monitor): --{ConnectFlag} / --{DisconnectFlag}.");
        }
        Console.WriteLine(bar);
        Console.WriteLine();
    }

    private static string DescribeDecision(ReconnectDecision decision, WatchdogConfig config) => decision switch
    {
        ReconnectDecision.Connected => "VPN is connected; nothing to do.",
        ReconnectDecision.DisabledByUser => "auto-reconnect is off, so the watchdog is only watching.",
        ReconnectDecision.NoInternet => "the underlying internet is down; reconnecting cannot help yet.",
        ReconnectDecision.WaitingForSelfHeal =>
            $"inside the {config.ReconnectGracePeriodSeconds}s grace period - letting FortiClient's own recovery try first.",
        ReconnectDecision.InBackoff => "backing off after a failed attempt.",
        ReconnectDecision.GaveUp =>
            $"gave up for this outage after {config.ReconnectMaxAttempts} attempts; manual action needed.",
        ReconnectDecision.AlreadyInFlight => "a reconnect attempt is already running.",
        ReconnectDecision.Triggered => "grace period elapsed - triggering a reconnect.",
        _ => "no description available."
    };

    /// <summary>
    /// Opens the shared activity log, or degrades to an in-memory one.
    /// <para>
    /// The path is deliberately fixed and shared with the GUI so one history covers both
    /// front-ends. If it cannot be opened (directory not writable, file locked, corrupt)
    /// the run must still go ahead: this is instrumentation, and losing it is never worth
    /// failing a multi-day monitoring session for. The fallback keeps the rest of the code
    /// free of null checks.
    /// </para>
    /// </summary>
    private static IVpnActivityLog CreateActivityLog(WatchdogConfig config)
    {
        try
        {
            return new SqliteVpnActivityLog(config.ActivityLogPath, config.ActivityLogMaxEntries);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Warning: could not open the activity log at '{config.ActivityLogPath}' " +
                $"({ex.GetType().Name}: {ex.Message}). Falling back to an in-memory log - this run's activity");
            Console.Error.WriteLine(
                "         will not appear in the GUI and will not survive the process. Monitoring is unaffected.");
            return new InMemoryVpnActivityLog(config.ActivityLogMaxEntries);
        }
    }

    /// <summary>
    /// Records one activity entry, swallowing anything the log throws.
    /// <para>
    /// The interface contract already says implementations must not throw, but this is
    /// called from the poll loop and from background reconnect attempts on a process that
    /// has to survive for days: a broken log file must never abort a tick, a reconnect, or
    /// the shutdown summary. Belt and braces on purpose - do not "simplify" it away.
    /// </para>
    /// </summary>
    private static void RecordActivity(
        IVpnActivityLog log,
        VpnActivityKind kind,
        string message,
        string? profileName = null,
        string? detail = null)
    {
        try
        {
            log.Record(kind, message, profileName, detail);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Warning: could not write an activity log entry ({kind}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Collapses the correlator's state machine to what a person reading the history
    /// cares about: is the tunnel UP (true), DOWN (false), or can we not tell (null)?
    /// Unknown and Connecting are measurement gaps, not evidence, and must never produce
    /// a "disconnected" line. Mirrors the GUI's mapping exactly so the shared log reads
    /// the same whichever front-end wrote the entry.
    /// </summary>
    private static bool? IsTunnelUp(VpnState state) => state switch
    {
        VpnState.Connected => true,
        VpnState.Disconnected or VpnState.NetworkUnavailable or VpnState.Recovering => false,
        _ => null
    };

    /// <summary>
    /// The disconnect episode that was open before this tick and closed during it as a
    /// RECOVERY, or null. Found by id rather than by "the completed list grew", because
    /// the correlator caps that list and drops its oldest entry once full - at which
    /// point the count stops growing even though a new correlation was just completed.
    /// </summary>
    private static DisconnectCorrelation? FindRecoveryCompletedThisTick(
        DisconnectCorrelation? openBefore,
        DisconnectCorrelation? openAfter,
        IReadOnlyList<DisconnectCorrelation> completedAfter)
    {
        if (openBefore is null || openAfter?.CorrelationId == openBefore.CorrelationId)
        {
            return null;
        }

        // Newest first: the one that closed this tick is at (or very near) the end.
        for (int i = completedAfter.Count - 1; i >= 0; i--)
        {
            DisconnectCorrelation candidate = completedAfter[i];
            if (candidate.CorrelationId == openBefore.CorrelationId)
            {
                return candidate.RecoverySucceeded == true && candidate.RecoveryDuration.HasValue
                    ? candidate
                    : null;
            }
        }

        return null;
    }

    /// <summary>"12s" - whole seconds, floored at zero so a clock step back can never print a negative recovery.</summary>
    private static string FormatWholeSeconds(TimeSpan duration) =>
        $"{Math.Max(0, Math.Round(duration.TotalSeconds)):0}s";

    /// <summary>The snapshot for one named process, or null if this tick's list has no entry for it at all.</summary>
    private static ProcessSnapshot? FindProcess(IReadOnlyList<ProcessSnapshot> processes, string processName)
    {
        foreach (ProcessSnapshot process in processes)
        {
            if (string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }
        }

        return null;
    }

    /// <summary>Closes the activity log if its implementation holds a resource. Never throws.</summary>
    private static void DisposeActivityLog(IVpnActivityLog log)
    {
        if (log is not IDisposable disposable)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to close the activity log cleanly: {ex.Message}");
        }
    }

    /// <summary>
    /// Rate-limiter for the poll loop's catch-all error reporting.
    /// <para>
    /// A poll tick that fails usually fails for a REASON THAT PERSISTS - the FortiClient
    /// log directory has gone, the database file is locked - and the loop tries again
    /// every PollIntervalMs. At the default 2s that is ~43,000 full stack traces a day,
    /// which fills a redirected log file and hides everything else that happened. So the
    /// first occurrence is reported in full, identical repeats are counted silently, and
    /// the count is summarised at most once per interval. Any CHANGE of error, and the
    /// first clean tick afterwards, flushes what was suppressed - a new or resolved fault
    /// is news and must never be swallowed by a window belonging to the previous one.
    /// </para>
    /// <para>
    /// Touched only from the poll loop thread, so it needs no synchronisation, and it
    /// holds one signature string rather than a growing collection - a process running
    /// for days must not accumulate per-error objects.
    /// </para>
    /// </summary>
    private sealed class PollErrorThrottle
    {
        private readonly TimeSpan _summaryInterval;

        /// <summary>Identity of the error currently being suppressed, or null if the last tick was clean.</summary>
        private string? _signature;

        private DateTimeOffset _windowStartedAt;
        private int _suppressed;

        public PollErrorThrottle(TimeSpan summaryInterval)
        {
            _summaryInterval = summaryInterval;
        }

        /// <summary>Reports one failed tick, in full the first time and as a periodic count after that.</summary>
        public void Report(Exception ex, DateTimeOffset now)
        {
            // Type + message, not the stack: the same fault re-thrown from the same place
            // is the same news, and the full trace is already in the first report.
            string signature = $"{ex.GetType().Name}: {ex.Message}";

            if (signature != _signature)
            {
                FlushSuppressed(now, "before the error changed");

                _signature = signature;
                _windowStartedAt = now;
                _suppressed = 0;

                Console.Error.WriteLine($"[{now:O}] Poll tick error (continuing): {ex}");
                return;
            }

            _suppressed++;

            if (now - _windowStartedAt < _summaryInterval)
            {
                return;
            }

            Console.Error.WriteLine(
                $"[{now:O}] Poll tick error (continuing): the same error repeated {_suppressed} times in the last " +
                $"{(now - _windowStartedAt).TotalMinutes:0.#} minutes - {_signature} " +
                "(further repeats suppressed; the first occurrence above has the stack trace).");

            _windowStartedAt = now;
            _suppressed = 0;
        }

        /// <summary>
        /// A tick completed cleanly. Closes out the current fault so the next occurrence -
        /// of the same error or a different one - is reported in full again.
        /// </summary>
        public void RecordTickSucceeded(DateTimeOffset now)
        {
            if (_signature is null)
            {
                return;
            }

            FlushSuppressed(now, "before it cleared");
            _signature = null;
            _suppressed = 0;
        }

        private void FlushSuppressed(DateTimeOffset now, string why)
        {
            if (_suppressed <= 0)
            {
                return;
            }

            Console.Error.WriteLine(
                $"[{now:O}] Poll tick error (continuing): the same error repeated {_suppressed} more times in the last " +
                $"{(now - _windowStartedAt).TotalMinutes:0.#} minutes {why} - {_signature}");

            _suppressed = 0;
        }
    }

    /// <summary>
    /// Counters for the reconnect attempts THIS process made. Written from the
    /// background attempt tasks and read from either shutdown path (including the
    /// ProcessExit handler, on a different thread), so every access is interlocked.
    /// Fixed-size by design - a process running for days must not accumulate per-attempt
    /// objects.
    /// </summary>
    private sealed class ReconnectStats
    {
        private int _attempts;
        private int _successes;
        private int _failures;

        public int Attempts => Volatile.Read(ref _attempts);
        public int Successes => Volatile.Read(ref _successes);
        public int Failures => Volatile.Read(ref _failures);

        /// <summary>Counts an attempt and returns its 1-based number, for logging.</summary>
        public int StartAttempt() => Interlocked.Increment(ref _attempts);

        public void RecordSuccess() => Interlocked.Increment(ref _successes);

        public void RecordFailure() => Interlocked.Increment(ref _failures);
    }

    /// <summary>
    /// Deliberately synchronous (no async/await) so it can safely run from a
    /// ProcessExit handler, where the runtime does not guarantee async continuations
    /// get a chance to complete. Called from exactly one of the two shutdown paths
    /// per process (guarded by the caller's Interlocked.Exchange), never both.
    /// </summary>
    private static void WriteSessionSummarySync(
        WatchdogConfig config,
        IVpnEventCorrelator correlator,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        VpnState? lastKnownState,
        ReconnectStats reconnectStats,
        bool comUnavailable)
    {
        string summary = BuildSessionSummary(
            config, correlator, startedAt, endedAt, lastKnownState, reconnectStats, comUnavailable);

        Console.WriteLine();
        Console.WriteLine(summary);

        string reportPath = Path.Combine(AppContext.BaseDirectory, $"vpn-watchdog-summary-{endedAt:yyyyMMdd-HHmmss}.txt");
        try
        {
            File.WriteAllText(reportPath, summary);
            Console.WriteLine($"Summary written to: {reportPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write summary report to '{reportPath}': {ex.Message}");
        }
    }

    private static string BuildSessionSummary(
        WatchdogConfig config,
        IVpnEventCorrelator correlator,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        VpnState? lastKnownState,
        ReconnectStats reconnectStats,
        bool comUnavailable)
    {
        IReadOnlyList<DisconnectCorrelation> completed = correlator.GetCompletedCorrelations();
        DisconnectCorrelation? open = correlator.GetOpenCorrelation();
        IReadOnlyList<DisconnectCorrelation> all =
            open is null ? completed : completed.Append(open).ToList();

        bool connectedAtShutdown = lastKnownState == VpnState.Connected;

        TimeSpan totalConnected = completed.Aggregate(
            TimeSpan.Zero, (acc, c) => acc + (c.PreviousConnectedDuration ?? TimeSpan.Zero));
        if (open is not null)
        {
            totalConnected += open.PreviousConnectedDuration ?? TimeSpan.Zero;
        }
        if (connectedAtShutdown)
        {
            totalConnected += correlator.CurrentStateDuration(endedAt);
        }

        TimeSpan totalDisconnected = completed.Aggregate(
            TimeSpan.Zero, (acc, c) => acc + (c.RecoveryDuration ?? TimeSpan.Zero));
        if (!connectedAtShutdown && lastKnownState is not null)
        {
            totalDisconnected += correlator.CurrentStateDuration(endedAt);
        }

        int unexpectedDisconnects = all.Count(c => c.DisconnectClassification == "Unexpected");
        int automaticRecoveries = completed.Count(c => c.RecoverySucceeded == true);
        int failedRecoveries = completed.Count(c => c.RecoverySucceeded == false);
        int stillOpenCount = open is not null ? 1 : 0;

        List<TimeSpan> successfulRecoveryDurations = completed
            .Where(c => c.RecoverySucceeded == true && c.RecoveryDuration.HasValue)
            .Select(c => c.RecoveryDuration!.Value)
            .ToList();

        TimeSpan? averageRecovery = successfulRecoveryDurations.Count > 0
            ? TimeSpan.FromTicks((long)successfulRecoveryDurations.Average(d => d.Ticks))
            : null;
        TimeSpan? longestRecovery = successfulRecoveryDurations.Count > 0
            ? successfulRecoveryDurations.Max()
            : null;

        int internetUpAtDisconnect = all.Count(c => c.InternetStateAtDisconnect == InternetState.Up);
        int internetDownAtDisconnect = all.Count(c => c.InternetStateAtDisconnect == InternetState.Down);

        int reconnectAttemptObserved = all.Count(c => c.ReconnectAttemptDetectedAt is not null);
        int reconnectAttemptNotObserved = all.Count - reconnectAttemptObserved;

        int watchdogAttempts = reconnectStats.Attempts;
        int watchdogSuccesses = reconnectStats.Successes;
        int watchdogFailures = reconnectStats.Failures;
        int watchdogUnresolved = watchdogAttempts - watchdogSuccesses - watchdogFailures;

        string autoReconnectStatus = config.AutoReconnectEnabled
            ? "ENABLED"
            : comUnavailable
                ? "DISABLED (requested, but FortiClient COM was unavailable)"
                : "DISABLED (observe only)";

        var sb = new StringBuilder();
        string bar = new string('=', 76);

        sb.AppendLine(bar);
        sb.AppendLine("VPN Session Summary");
        sb.AppendLine(bar);
        sb.AppendLine($"Profile:                        {config.ProfileName}");
        sb.AppendLine($"Monitoring started:             {startedAt:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Monitoring ended:               {endedAt:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine($"Total time connected:           {FormatTimeSpan(totalConnected)}");
        sb.AppendLine($"Total time disconnected:        {FormatTimeSpan(totalDisconnected)}");
        sb.AppendLine();
        sb.AppendLine($"Unexpected disconnects:         {unexpectedDisconnects}");
        sb.AppendLine($"Automatic recoveries:           {automaticRecoveries}");
        sb.AppendLine($"Failed recoveries:              {failedRecoveries}");
        sb.AppendLine($"Still open at shutdown:         {stillOpenCount}");
        sb.AppendLine();
        sb.AppendLine($"Average recovery time:          {(averageRecovery is { } avg ? FormatTimeSpan(avg) : "n/a (no successful recoveries observed)")}");
        sb.AppendLine($"Longest recovery time:          {(longestRecovery is { } max ? FormatTimeSpan(max) : "n/a (no successful recoveries observed)")}");
        sb.AppendLine();
        sb.AppendLine($"Disconnects w/ internet UP:     {internetUpAtDisconnect}");
        sb.AppendLine($"Disconnects w/ internet DOWN:   {internetDownAtDisconnect}");
        sb.AppendLine();
        sb.AppendLine($"Reconnect attempt observed:     {reconnectAttemptObserved}");
        sb.AppendLine($"Reconnect attempt not observed: {reconnectAttemptNotObserved}");
        sb.AppendLine("  KNOWN LIMITATION: FortiClient's trace log has no distinct \"attempting to");
        sb.AppendLine("  reconnect\" line - only disconnect and eventual connect-success lines exist.");
        sb.AppendLine("  ReconnectAttemptDetectedAt is therefore expected to always be null in this");
        sb.AppendLine("  build, so every disconnect currently shows as \"not observed\" above regardless");
        sb.AppendLine("  of what FortiClient's built-in auto-reconnect actually did behind the scenes.");
        sb.AppendLine();

        // The block above counts what FortiClient did on its own, as seen in its logs.
        // The block below counts what THIS watchdog did - the two are independent and
        // must not be added together.
        sb.AppendLine("Watchdog-initiated reconnects (this process's own actions):");
        sb.AppendLine($"  Auto-reconnect:               {autoReconnectStatus}");
        sb.AppendLine($"  Reconnect attempts made:      {watchdogAttempts}");
        sb.AppendLine($"  Attempts succeeded:           {watchdogSuccesses}");
        sb.AppendLine($"  Attempts failed:              {watchdogFailures}");
        if (watchdogUnresolved > 0)
        {
            sb.AppendLine($"  Attempts unresolved:          {watchdogUnresolved} (still running when the watchdog stopped)");
        }
        sb.AppendLine();
        sb.AppendLine($"Recovery outcome - succeeded:   {automaticRecoveries}");
        sb.AppendLine($"Recovery outcome - failed:      {failedRecoveries}");
        sb.AppendLine($"Recovery outcome - still open:  {stillOpenCount}");
        sb.AppendLine(bar);
        if (config.AutoReconnectEnabled)
        {
            sb.AppendLine("Mode: AUTO-RECONNECT -- this session may ask FortiClient to CONNECT the profile");
            sb.AppendLine("above. It never disconnects the VPN and never handles credentials.");
        }
        else
        {
            sb.AppendLine("Mode: OBSERVE ONLY -- this session never connected, disconnected, or modified the VPN.");
        }
        sb.AppendLine(bar);

        return sb.ToString();
    }

    private static string FormatTimeSpan(TimeSpan ts) =>
        ts.Days > 0
            ? $"{ts.Days}d {ts.Hours:00}h {ts.Minutes:00}m {ts.Seconds:00}s"
            : $"{ts.Hours:00}h {ts.Minutes:00}m {ts.Seconds:00}s";
}
