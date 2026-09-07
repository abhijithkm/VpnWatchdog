using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VpnWatchdog.Core;

// ============================================================================
// Enums
// ============================================================================

/// <summary>
/// VPN connection state as inferred from observable evidence (adapter status,
/// assigned IP, log events) - never inferred merely from "a FortiClient process exists".
/// </summary>
public enum VpnState
{
    Unknown,
    Connected,
    Disconnected,
    Connecting,
    NetworkUnavailable,
    Recovering
}

/// <summary>Underlying internet/network availability - independent of VPN state.</summary>
public enum InternetState
{
    Unknown,
    Up,
    Down
}

/// <summary>
/// Classification of a FortiClient trace log line. Only maps to categories that
/// are directly supported by observed log text - never invents semantics for
/// undocumented numeric reason codes.
/// </summary>
public enum LogEventType
{
    Unknown,
    VpnConnected,
    VpnDisconnected,
    VpnDisconnectedUnexpectedly,
    VpnConnectAttempt,
    VpnReconnectAttempt,
    VpnAuthentication,
    VpnError,
    NetworkChange
}

// ============================================================================
// Snapshots (point-in-time observations from each provider)
// ============================================================================

/// <summary>
/// One observation of the Fortinet VPN adapter's state. AdapterName/InterfaceIndex
/// are discovered dynamically each poll by matching InterfaceDescription - never
/// hardcoded, because the OS can renumber/rename adapters across reboots or
/// reconnects.
/// </summary>
public sealed record AdapterSnapshot(
    bool AdapterFound,
    string? AdapterName,
    string? InterfaceDescription,
    int? InterfaceIndex,
    bool IsUp,
    string? IpAddress,
    int? PrefixLength,
    IReadOnlyList<string> GatewayAddresses,
    DateTimeOffset ObservedAt,
    // Cumulative totals straight from the OS (NetworkInterface.GetIPStatistics).
    // Not verified against this specific adapter's actual reconnect behaviour,
    // but interface counters are generally understood to reset if Windows
    // reinitializes the underlying adapter instance - which is exactly why
    // these are raw counters rather than a rate: turning a counter into a rate
    // needs two samples and an elapsed time, and
    // that is a stateful computation a snapshot provider has no business doing.
    // See NetworkThroughputTracker for that half.
    long? BytesReceived = null,
    long? BytesSent = null)
{
    public static AdapterSnapshot NotFound(DateTimeOffset observedAt) =>
        new(false, null, null, null, false, null, null, Array.Empty<string>(), observedAt);
}

/// <summary>
/// One observation of general internet/network availability. Deliberately
/// independent of anything Fortinet-specific - "internet is down" and "VPN is
/// down" are different conditions and must never be conflated.
/// </summary>
public sealed record InternetSnapshot(
    InternetState State,
    bool? DefaultGatewayReachable,
    bool? DnsResolved,
    bool? HttpsReachable,
    string HttpsProbeUrl,
    DateTimeOffset ObservedAt);

/// <summary>One observation of a single FortiClient-related process.</summary>
public sealed record ProcessSnapshot(
    string ProcessName,
    bool IsRunning,
    int? ProcessId,
    DateTimeOffset? StartTime,
    DateTimeOffset ObservedAt);

/// <summary>
/// A single classified line read from a FortiClient trace log. RawLine is kept
/// verbatim (never truncated/rewritten) so downstream analysis can always go
/// back to ground truth. ReasonCode/ReasonText are recorded exactly as observed
/// (e.g. "21" / "Cancelled") - never interpreted beyond what the log states.
/// </summary>
public sealed record LogEvent(
    DateTimeOffset Timestamp,
    string? ProfileName,
    LogEventType EventType,
    string RawLine,
    string? ReasonCode,
    string? ReasonText,
    string SourceFile);

/// <summary>The correlator's fused view of VPN state at one point in time.</summary>
public sealed record VpnStateSnapshot(
    VpnState State,
    AdapterSnapshot Adapter,
    DateTimeOffset ObservedAt);

// ============================================================================
// Correlation - the core evidence record Phase 2 exists to produce
// ============================================================================

/// <summary>
/// One full disconnect->(attempted recovery)->(reconnect or not) episode.
/// This is the primary evidence artifact for the Phase 2 report and for the
/// eventual Phase 3 decision. A correlation is "open" (RecoverySucceeded == null,
/// ReconnectedAt == null) until either a reconnect is observed or the monitor
/// decides enough time has passed to call it a non-recovery (a policy decision
/// made by the correlator, not fabricated here).
/// </summary>
public sealed record DisconnectCorrelation(
    string CorrelationId,
    string ProfileName,
    DateTimeOffset DisconnectedAt,
    string DisconnectClassification, // e.g. "Unexpected", "Cancelled", "Unknown" - derived only from observed log text/reason
    string? DisconnectReasonCode,
    string? DisconnectReasonText,
    InternetState InternetStateAtDisconnect,
    bool FortiVpnProcessRunningAtDisconnect,
    bool FortiSslVpnDaemonRunningAtDisconnect,
    TimeSpan? PreviousConnectedDuration,
    DateTimeOffset? ReconnectAttemptDetectedAt,
    DateTimeOffset? ReconnectedAt,
    TimeSpan? RecoveryDuration,
    bool? RecoverySucceeded);

// ============================================================================
// Provider interfaces - each is independently mockable for tests/simulation
// ============================================================================

/// <summary>Determines current VPN connection state from adapter/IP evidence.</summary>
public interface IConnectionStateProvider
{
    /// <param name="adapterDescriptionPattern">
    /// Substring/regex used to dynamically identify the Fortinet VPN adapter by
    /// its InterfaceDescription. Never a hardcoded adapter name or ifIndex.
    /// </param>
    Task<AdapterSnapshot> GetAdapterSnapshotAsync(string adapterDescriptionPattern, CancellationToken ct);
}

/// <summary>Determines general internet/network availability, independent of VPN state.</summary>
public interface IInternetStateProvider
{
    Task<InternetSnapshot> GetCurrentStateAsync(CancellationToken ct);
}

/// <summary>Reports on running FortiClient-related processes.</summary>
public interface IFortiClientProcessProvider
{
    Task<IReadOnlyList<ProcessSnapshot>> GetProcessesAsync(CancellationToken ct);
}

/// <summary>
/// Efficiently tails FortiClient trace logs for new lines only (tracked by byte
/// offset per file), classifies them, and yields them as they appear. Must
/// detect and recover safely from log rotation/truncation.
/// </summary>
public interface IFortiClientLogMonitor
{
    IAsyncEnumerable<LogEvent> TailNewEventsAsync(CancellationToken ct);
}

/// <summary>
/// Fuses all signals into the VPN state machine and produces/updates
/// DisconnectCorrelation records. Pure logic - takes snapshots in, does not
/// itself poll or read files.
/// </summary>
public interface IVpnEventCorrelator
{
    VpnStateSnapshot Ingest(
        AdapterSnapshot adapter,
        InternetSnapshot internet,
        IReadOnlyList<ProcessSnapshot> processes,
        IReadOnlyList<LogEvent> newLogEvents,
        DateTimeOffset now);

    /// <summary>Correlations that have reached a terminal outcome (reconnected, or declared non-recovering).</summary>
    IReadOnlyList<DisconnectCorrelation> GetCompletedCorrelations();

    /// <summary>The in-progress correlation, if a disconnect is currently unresolved.</summary>
    DisconnectCorrelation? GetOpenCorrelation();

    /// <summary>How long the VPN has been in its current state.</summary>
    TimeSpan CurrentStateDuration(DateTimeOffset now);
}

/// <summary>
/// Persists snapshots/correlations. Must never be asked to store credentials,
/// tokens, cookies, or secrets - the data model has no field for them.
/// </summary>
public interface IVpnEventStore
{
    Task SaveVpnStateSnapshotAsync(VpnStateSnapshot snapshot, CancellationToken ct);
    Task SaveLogEventAsync(LogEvent logEvent, CancellationToken ct);
    Task UpsertCorrelationAsync(DisconnectCorrelation correlation, CancellationToken ct);
    Task<IReadOnlyList<DisconnectCorrelation>> GetAllCorrelationsAsync(CancellationToken ct);
}

/// <summary>
/// Triggers a VPN reconnect. The working implementation drives FortiClient's
/// registered COM automation interface (ProgID FCCOMInt.XVPN), which was
/// validated to connect the saved profile silently, without elevation and
/// without this app ever handling credentials.
/// </summary>
public interface IReconnectController
{
    /// <summary>Ask FortiClient to connect the named tunnel.</summary>
    Task ReconnectAsync(string profileName, CancellationToken ct);

    /// <summary>FortiClient's own view of whether the tunnel is connected.</summary>
    Task<bool> IsConnectedAsync(string profileName, CancellationToken ct);

    /// <summary>Tunnel profiles FortiClient knows about (used to validate configuration).</summary>
    Task<IReadOnlyList<string>> GetTunnelListAsync(CancellationToken ct);
}

/// <summary>
/// Explicit, user-initiated tunnel control. Deliberately a SEPARATE interface from
/// <see cref="IReconnectController"/>: the automatic watchdog loop is only ever
/// handed an <see cref="IReconnectController"/>, so it is structurally incapable of
/// disconnecting the tunnel - the guarantee is enforced by the type system rather
/// than by a comment asking future code to behave.
/// <para>
/// Every method here must be reachable ONLY from a direct user action (a button
/// click, an explicit CLI command). Never call it from polling, correlation or
/// reconnect-policy code.
/// </para>
/// </summary>
public interface IManualVpnControl
{
    /// <summary>Ask FortiClient to connect the named tunnel, because the user asked for it now.</summary>
    Task ConnectAsync(string profileName, CancellationToken ct);

    /// <summary>
    /// Ask FortiClient to disconnect the named tunnel, because the user asked for it
    /// now. Callers MUST ensure auto-reconnect is switched off first, otherwise the
    /// watchdog would simply bring the tunnel back up after the grace period and
    /// appear to fight the user.
    /// </summary>
    Task DisconnectAsync(string profileName, CancellationToken ct);
}

/// <summary>Outcome of one pass of the reconnect decision logic.</summary>
public enum ReconnectDecision
{
    /// <summary>Nothing to do - VPN is connected.</summary>
    Connected,
    /// <summary>Auto-reconnect is switched off by the user.</summary>
    DisabledByUser,
    /// <summary>Underlying internet is down - reconnecting cannot help yet.</summary>
    NoInternet,
    /// <summary>Within the grace period, giving FortiClient's own recovery a chance first.</summary>
    WaitingForSelfHeal,
    /// <summary>Backing off after a failed attempt.</summary>
    InBackoff,
    /// <summary>Max attempts exhausted for this outage.</summary>
    GaveUp,
    /// <summary>A reconnect is already running.</summary>
    AlreadyInFlight,
    /// <summary>A reconnect was triggered.</summary>
    Triggered
}

/// <summary>
/// Decides whether a reconnect should be attempted right now. Pure policy -
/// takes state in, returns a decision, never performs I/O itself, so the rules
/// (grace period, backoff, max attempts, internet precondition) are fully
/// unit-testable without touching COM or a real VPN.
/// </summary>
public interface IReconnectPolicy
{
    ReconnectDecision Evaluate(VpnState vpnState, InternetState internetState, DateTimeOffset now);

    /// <summary>Record the result of an attempt so backoff/attempt counting can advance.</summary>
    void RecordAttemptResult(bool succeeded, DateTimeOffset now);

    /// <summary>Reset attempt/backoff state (called when the tunnel is healthy again).</summary>
    void Reset();

    int AttemptCount { get; }
    DateTimeOffset? LastAttemptAt { get; }
}

// ============================================================================
// Activity log - the human-readable record of what happened and what we did
// ============================================================================

/// <summary>
/// One kind of noteworthy activity. Deliberately separate from
/// <see cref="LogEventType"/>: that classifies lines parsed out of FortiClient's
/// own trace log, whereas this records what the WATCHDOG observed or did, in
/// terms a person reading a log window would recognise.
/// </summary>
public enum VpnActivityKind
{
    MonitoringStarted,
    MonitoringStopped,

    VpnConnected,
    VpnDisconnected,
    /// <summary>
    /// A drop FortiClient's own trace log flagged as unexpected, or one with no
    /// preceding user/manual action. Only used when the evidence supports it -
    /// never inferred from an undocumented reason code alone.
    /// </summary>
    VpnDisconnectedUnexpectedly,

    InternetLost,
    InternetRestored,

    FortiClientRunning,
    FortiClientNotRunning,

    AutoReconnectArmed,
    AutoReconnectDisarmed,
    AutoReconnectTriggered,
    AutoReconnectSucceeded,
    AutoReconnectFailed,
    AutoReconnectGaveUp,

    ManualConnectRequested,
    ManualConnectSucceeded,
    ManualConnectFailed,
    ManualDisconnectRequested,
    ManualDisconnectSucceeded,
    ManualDisconnectFailed,
}

/// <summary>
/// One line in the activity log. <paramref name="Message"/> is already
/// human-readable - the log window renders it as-is rather than re-deriving
/// wording from the kind, so history stays accurate even if wording changes later.
/// Never holds a credential: there is no field for one, and callers must not smuggle
/// one into Message or Detail.
/// </summary>
public sealed record VpnActivityEntry(
    DateTimeOffset Timestamp,
    VpnActivityKind Kind,
    string Message,
    string? ProfileName,
    string? Detail);

/// <summary>
/// Append-only activity trail, shared by the GUI and the CLI so one history covers
/// both. Implementations must be safe to call from any thread and must never throw
/// into the caller: logging is instrumentation, and a failure to write a log line
/// must never take down monitoring or abort a reconnect.
/// </summary>
public interface IVpnActivityLog
{
    /// <summary>Append one entry. Must not throw - swallow and degrade instead.</summary>
    void Record(VpnActivityEntry entry);

    /// <summary>
    /// Most recent entries, newest first, capped at <paramref name="max"/>.
    /// Returns an empty list rather than throwing if the store is unreadable.
    /// </summary>
    IReadOnlyList<VpnActivityEntry> GetRecent(int max);

    /// <summary>
    /// Delete ALL history. Destructive and user-initiated only (a "Clear Logs"
    /// button behind a confirmation) - never called automatically. Must not throw;
    /// return false if the store could not be cleared so the UI can say so.
    /// </summary>
    bool Clear();

    /// <summary>Convenience for the common case of an entry stamped "now".</summary>
    void Record(VpnActivityKind kind, string message, string? profileName = null, string? detail = null)
        => Record(new VpnActivityEntry(DateTimeOffset.Now, kind, message, profileName, detail));
}

/// <summary>
/// Display severity derived from an entry's kind, so every UI (main window
/// "Last Event" line, the log dialog) colours and icons events identically.
/// Pure mapping - lives with the contract so it cannot drift between surfaces.
/// </summary>
public enum VpnActivitySeverity { Info, Success, Warning, Error }

public static class VpnActivityKindExtensions
{
    public static VpnActivitySeverity Severity(this VpnActivityKind kind) => kind switch
    {
        VpnActivityKind.VpnConnected => VpnActivitySeverity.Success,
        VpnActivityKind.AutoReconnectSucceeded => VpnActivitySeverity.Success,
        VpnActivityKind.ManualConnectSucceeded => VpnActivitySeverity.Success,
        VpnActivityKind.InternetRestored => VpnActivitySeverity.Success,
        VpnActivityKind.FortiClientRunning => VpnActivitySeverity.Success,

        VpnActivityKind.VpnDisconnected => VpnActivitySeverity.Warning,
        VpnActivityKind.VpnDisconnectedUnexpectedly => VpnActivitySeverity.Warning,
        VpnActivityKind.InternetLost => VpnActivitySeverity.Warning,
        VpnActivityKind.AutoReconnectTriggered => VpnActivitySeverity.Warning,
        VpnActivityKind.ManualDisconnectSucceeded => VpnActivitySeverity.Warning,

        VpnActivityKind.AutoReconnectFailed => VpnActivitySeverity.Error,
        VpnActivityKind.AutoReconnectGaveUp => VpnActivitySeverity.Error,
        VpnActivityKind.ManualConnectFailed => VpnActivitySeverity.Error,
        VpnActivityKind.ManualDisconnectFailed => VpnActivitySeverity.Error,
        VpnActivityKind.FortiClientNotRunning => VpnActivitySeverity.Error,

        _ => VpnActivitySeverity.Info,
    };
}

// ============================================================================
// Configuration
// ============================================================================

public sealed record WatchdogConfig(
    string ProfileName,
    string AdapterDescriptionPattern,
    string HttpsProbeUrl,
    int PollIntervalMs,
    int HeartbeatIntervalMs,
    int OpenCorrelationTimeoutMinutes,
    string FortiClientLogDirectory,
    string DatabasePath,
    bool AutoReconnectEnabled,
    int ReconnectGracePeriodSeconds,
    int ReconnectMaxAttempts,
    int ReconnectInitialBackoffSeconds,
    int ReconnectMaxBackoffSeconds,
    int ReconnectVerifyTimeoutSeconds,
    string ActivityLogPath,
    int ActivityLogMaxEntries)
{
    public static WatchdogConfig Default => new(
        ProfileName: "MLA-DEV-VPN-2-LocalAuth",
        AdapterDescriptionPattern: "Fortinet Virtual Ethernet Adapter",
        HttpsProbeUrl: "https://www.microsoft.com/robots.txt",
        PollIntervalMs: 2000,
        HeartbeatIntervalMs: 30000,
        OpenCorrelationTimeoutMinutes: 30,
        FortiClientLogDirectory: @"C:\Program Files\Fortinet\FortiClient\logs\trace",
        DatabasePath: "vpn-watchdog.db",

        // Auto-reconnect is OFF by default - it must be an explicit, deliberate
        // opt-in, because no log signal reliably distinguishes a deliberate manual
        // disconnect from an unexpected drop (reason code 21 "Cancelled" was
        // observed in both cases), so an explicit switch is the only honest control.
        AutoReconnectEnabled: false,

        // FortiClient has its own built-in recovery which was measured healing in
        // 6-70 seconds. Wait this long before intervening so we never race it.
        ReconnectGracePeriodSeconds: 90,

        ReconnectMaxAttempts: 5,
        ReconnectInitialBackoffSeconds: 30,
        ReconnectMaxBackoffSeconds: 300,
        ReconnectVerifyTimeoutSeconds: 30,

        // A FIXED, well-known path rather than one derived from DatabasePath, so the
        // GUI and the CLI append to ONE shared history instead of each keeping a
        // private half of the story the user would have to piece together.
        ActivityLogPath: DefaultActivityLogPath,

        ActivityLogMaxEntries: 5000);

    /// <summary>
    /// %LOCALAPPDATA%\VpnWatchdogctivity-log.db - user-scoped and writable
    /// without elevation, unlike a path next to the executable under Program Files.
    /// </summary>
    public static string DefaultActivityLogPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VpnWatchdog",
        "activity-log.db");
}
