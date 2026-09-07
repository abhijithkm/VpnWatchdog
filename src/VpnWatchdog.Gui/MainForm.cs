using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using VpnWatchdog.Core;
using VpnWatchdog.Core.Correlation;
using VpnWatchdog.Core.Diagnostics;
using VpnWatchdog.Core.Logging;
using VpnWatchdog.Core.Providers;
using VpnWatchdog.Core.Reconnect;
using VpnWatchdog.Core.Storage;
using VpnWatchdog.Core.Updates;

namespace VpnWatchdog.Gui;

/// <summary>
/// Compact front-end for the same observe-only VpnWatchdog.Core the CLI uses.
/// Monitoring (the "● Monitoring" toggle) controls ONLY this app's own polling
/// loop - it never connects, disconnects, or otherwise controls the real
/// FortiClient VPN.
/// <para>
/// The single contextual action button (Connect / Disconnect) is the sole
/// exception, and it is driven exclusively by a direct user click. It talks to
/// <see cref="IManualVpnControl"/>; the automatic poll/policy path is only ever
/// handed an <see cref="IReconnectController"/> and therefore cannot reach
/// Disconnect at all. Do not widen that anywhere in this file.
/// </para>
/// </summary>
public partial class MainForm : Form
{
    // A floor under the configured poll interval, so a typo in the settings file
    // cannot turn the 2s observation loop into a busy spin.
    private const int MinPollIntervalMs = 500;

    private const string NoUptime = "--:--:--";
    private const string NoThroughput = "↓ -- ↑ --";

    // FortiClient's VPN engine processes. Either one running is what "FortiClient
    // is running" means here; FortiTray/FortiSettings are UI and prove nothing
    // about whether a tunnel can be brought up.
    private static readonly string[] EngineProcessNames = { "FortiVPN", "FortiSSLVPNdaemon" };

    private GuiSettings _settings;
    private WatchdogConfig _config;

    private IConnectionStateProvider? _adapterProvider;
    private IInternetStateProvider? _internetProvider;
    private IFortiClientProcessProvider? _processProvider;
    private IFortiClientLogMonitor? _logMonitor;
    private IVpnEventCorrelator? _correlator;
    private IVpnEventStore? _store;
    private NetworkThroughputTracker? _throughputTracker;

    private IReconnectController? _reconnectController;
    private ReconnectPolicy? _reconnectPolicy;
    private CancellationTokenSource? _reconnectCts;

    private bool _monitoring;
    private bool _pollInFlight;

    // Reconnect UI/coordination state. All of these are read and written on the
    // UI thread only - the background attempt marshals back through
    // RunOnUiThread before touching them.
    private bool _reconnectInFlight;
    private int _reconnectAttemptNumber;
    private ReconnectDecision _lastReconnectDecision = ReconnectDecision.DisabledByUser;

    // Bumped on every Start/Stop so a reconnect attempt left over from a previous
    // monitoring session can never write its result into the current one.
    private int _reconnectGeneration;

    // Manual (user-initiated) VPN control. Held as the concrete controller because
    // this field owns the COM object's lifetime; it is only ever handed OUT as an
    // IManualVpnControl, so nothing else in the form can reach ReconnectAsync
    // through it by accident. Created lazily on the first manual click - never by
    // StartMonitoring, and creating it never starts monitoring.
    private FortiClientComReconnectController? _manualController;
    private CancellationTokenSource? _manualCts;

    // UI-thread-only, like the reconnect state above: single-flight guard plus a
    // generation counter so a manual result from a torn-down controller cannot
    // write into the current UI.
    private bool _manualInFlight;
    private int _manualGeneration;

    // A message pinned onto the hero sub-line. Without this the 2s poll would wipe
    // "Disconnecting..." or "Auto-reconnect switched off" almost immediately, and
    // the user would never see why the checkbox changed.
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(20);
    private string? _noticeText;
    private Color _noticeColor = Color.Gray;
    private DateTimeOffset _noticeUntil;

    // The shared activity trail. Built once for the life of the form rather than
    // per monitoring session, so Start/Stop cycles are themselves recorded instead
    // of being the thing that loses the record. Held as the interface because the
    // GUI does not care whether it ended up on SQLite or in memory.
    private readonly IVpnActivityLog _activityLog;

    // Last condition actually written to the activity log. The poll runs every 2s,
    // so without this the log would fill with one line per tick; with it the log
    // records TRANSITIONS only. null means "nothing observed yet this session" -
    // the first observation seeds it silently, because arriving at a condition we
    // have never seen before is not a transition.
    private readonly record struct ObservedCondition(bool? VpnConnected, bool? InternetUp, bool? FortiClientRunning);
    private ObservedCondition _logged;

    // Which action the primary button offers (see the "Which manual action" region).
    // _actionStamp is bumped every time OBSERVED evidence decides; the async COM
    // probe captures it and drops its own answer if the stamp moved on, so a slow
    // probe can never overwrite a fresher observation.
    private enum ManualAction { Connect, Disconnect }
    private ManualAction _manualAction = ManualAction.Connect;
    private bool _manualActionSettling;
    private bool _actionProbeInFlight;
    private int _actionStamp;

    // Set from the same probe round-trip as the manual-action resolution, below.
    // True only when FortiClient answered with a NON-EMPTY tunnel list that does
    // not contain the configured profile name - a strong, concrete signal that
    // whoever is running this build has never changed ProfileName away from the
    // shipped default. An EMPTY list is deliberately treated as "no evidence
    // either way" (COM unavailable, or this machine genuinely has zero profiles)
    // rather than a mismatch, so a machine without FortiClient never shows a
    // confusing "profile not found" nudge on top of the COM-unavailable state.
    private bool _profileMismatch;

    // Presentation state, UI thread only.
    private VpnState _shownState = VpnState.Unknown;
    private string? _idleTunnelHint;
    private bool _suppressCheckboxEvents;
    private bool _exitRequested;

    // Set by BeginUpdateCheck; read only by RenderVersionLabel and
    // LblVersion_Click. Independent of monitoring/VPN state entirely - this is
    // "is a newer release of this app itself available", nothing to do with the
    // tunnel.
    private string? _updateAvailableVersionTag;
    private string? _updateReleaseUrl;
    private bool _updateCheckInFlight;

    /// <summary>
    /// The base config with the live checkbox state folded in. AutoReconnectEnabled
    /// is a record property, so the only way to change it is to build a new record.
    /// </summary>
    private WatchdogConfig CurrentConfig => _config with { AutoReconnectEnabled = chkAutoReconnect.Checked };

    public MainForm()
    {
        InitializeComponent();

        Icon appIcon = LoadEmbeddedAppIcon();
        Icon = appIcon;
        trayIcon.Icon = appIcon;

        _settings = GuiSettings.Load();
        _config = _settings.ToWatchdogConfig();
        _activityLog = CreateActivityLog(_config);

        // Re-applied on every launch, not just when the checkbox changes: this is
        // what self-heals a stale Run-key entry if the exe was moved/reinstalled
        // to a new path since it was last set.
        if (_settings.AutoStartWithWindows)
        {
            WindowsStartupRegistration.Enable();
        }

        pollTimer.Interval = Math.Max(MinPollIntervalMs, _config.PollIntervalMs);

        // Reflect the saved switch without recording it: nothing has changed yet,
        // and MonitoringStarted already says which mode the session began in.
        SyncAutoReconnectCheckbox(recordChange: false);

        RenderVersionLabel();
        RenderIdle();
        RenderActionButton();

        // Independent of Start/Stop and of StartMonitoringOnLaunch: this has
        // nothing to do with VPN monitoring, so it always runs. One check now,
        // then updateCheckTimer re-checks periodically for a session that stays
        // open for days.
        BeginUpdateCheck();
        updateCheckTimer.Start();
    }

    /// <summary>
    /// Loads the app icon baked into the assembly (see the EmbeddedResource entry
    /// in the .csproj) rather than a loose file next to the exe, so the window
    /// titlebar/Alt+Tab/tray glyph can never go missing if someone copies just the
    /// .exe elsewhere. Falls back to the default WinForms icon rather than
    /// throwing - a wrong/missing icon is cosmetic, never worth crashing the app.
    /// </summary>
    private static Icon LoadEmbeddedAppIcon()
    {
        try
        {
            using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream("AppIcon.ico");
            if (stream is null)
            {
                return SystemIcons.Shield;
            }

            return new Icon(stream);
        }
        catch
        {
            return SystemIcons.Shield;
        }
    }

    /// <summary>
    /// Opens the shared activity trail, degrading to an in-memory one if the
    /// SQLite store cannot be opened (read-only profile, locked file, corrupt db).
    /// Losing history is a nuisance; refusing to start the GUI over it would be a
    /// regression, so this can never throw.
    /// </summary>
    private static IVpnActivityLog CreateActivityLog(WatchdogConfig config)
    {
        try
        {
            // The default path is under %LOCALAPPDATA% and will not exist on a
            // first run. Creating it here is belt and braces with the store's own
            // handling - an unwritable path still falls through to memory below.
            string? directory = Path.GetDirectoryName(config.ActivityLogPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return new SqliteVpnActivityLog(config.ActivityLogPath, config.ActivityLogMaxEntries);
        }
        catch
        {
            // Deliberately broad: whatever went wrong, the GUI still runs and the
            // log window still works - it just does not survive a restart.
            return new InMemoryVpnActivityLog(Math.Max(1, config.ActivityLogMaxEntries));
        }
    }

    private void MainForm_Load(object? sender, EventArgs e)
    {
        EnsureContentFits();
        ShowLastEvent(TryGetMostRecentEntry());

        if (_settings.StartMonitoringOnLaunch)
        {
            // The first poll comes back Unknown and resolves the action button
            // itself, so no separate probe is needed on this path.
            StartMonitoring();
        }
        else
        {
            // Nothing has been observed yet, so which action belongs on the button
            // is genuinely unknown. Ask FortiClient rather than guessing.
            BeginResolveManualAction();
        }
    }

    /// <summary>
    /// Every row is AutoSize, so after the first layout the root table knows exactly
    /// how tall the content came out at this DPI. Grow the window to fit if it did
    /// not - never shrink, and never let a row be clipped off the bottom.
    /// </summary>
    private void EnsureContentFits()
    {
        int needed = rootLayout.GetPreferredSize(new Size(rootLayout.Width, 0)).Height;
        if (needed > ClientSize.Height)
        {
            ClientSize = new Size(ClientSize.Width, needed);
        }
    }

    private void BtnMonitorToggle_Click(object? sender, EventArgs e)
    {
        if (_monitoring)
        {
            StopMonitoring();
        }
        else
        {
            StartMonitoring();
        }
    }

    private void StartMonitoring()
    {
        if (_monitoring) return;

        try
        {
            _adapterProvider = new AdapterConnectionStateProvider();
            _internetProvider = new InternetStateProvider(_config.HttpsProbeUrl);
            _processProvider = new FortiClientProcessProvider();
            _logMonitor = new FortiClientLogMonitor(_config.FortiClientLogDirectory, _config.ProfileName);
            _correlator = new VpnEventCorrelator(_config.ProfileName, _config.OpenCorrelationTimeoutMinutes);
            _store = new SqliteVpnEventStore(_config.DatabasePath);
            _throughputTracker = new NetworkThroughputTracker();

            // Reconnect side. The controller can only ever ask FortiClient to CONNECT;
            // the policy decides whether asking is allowed right now, and stays fully
            // inert while the Auto-reconnect checkbox is unchecked.
            _reconnectController = FortiClientComReconnectController.FromConfig(_config);
            _reconnectPolicy = new ReconnectPolicy(CurrentConfig);
            _reconnectCts = new CancellationTokenSource();
        }
        catch (Exception ex)
        {
            // A bad database path or log directory is a configuration problem, not a
            // reason to crash. Tear down whatever half got built and stay stopped.
            _adapterProvider = null;
            _internetProvider = null;
            _processProvider = null;
            _logMonitor = null;
            _correlator = null;
            _store = null;
            _throughputTracker = null;
            DisposeReconnect();

            MessageBox.Show(this,
                $"Monitoring could not start.\r\n\r\n{ex.Message}",
                "VPN Watchdog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _reconnectInFlight = false;
        _reconnectAttemptNumber = 0;
        _lastReconnectDecision = ReconnectDecision.DisabledByUser;
        _reconnectGeneration++;

        // Fresh session: forget what the previous one had recorded, so a change
        // that happened while nobody was watching is not reported now as if we
        // had seen it happen.
        _logged = default;

        _monitoring = true;
        _shownState = VpnState.Unknown;
        _idleTunnelHint = null;

        RenderMonitoringIndicator();
        RenderHero();

        RecordActivity(VpnActivityKind.MonitoringStarted, "Monitoring started",
            chkAutoReconnect.Checked ? "auto-reconnect armed" : "observe only");

        // Not just for the action button: this is also how the profile-mismatch
        // nudge gets its first chance to fire when StartMonitoringOnLaunch is set,
        // which otherwise never calls this probe at all - the poll loop resolves
        // Connected/Disconnected on its own, but nothing else asks FortiClient
        // whether it has even heard of this profile name.
        BeginResolveManualAction();

        pollTimer.Interval = Math.Max(MinPollIntervalMs, _config.PollIntervalMs);
        pollTimer.Start();
        // Render an immediate first frame rather than waiting for the first tick.
        _ = PollOnceAsync();
    }

    private void StopMonitoring(string? detail = null)
    {
        if (!_monitoring) return;

        pollTimer.Stop();
        _monitoring = false;

        // Provider/correlator instances are cheap and stateless enough to just
        // drop and recreate on the next Start - no explicit disposal needed
        // (SqliteVpnEventStore opens/closes a connection per call, nothing to
        // leak by discarding the reference).
        _adapterProvider = null;
        _internetProvider = null;
        _processProvider = null;
        _logMonitor = null;
        _correlator = null;
        _store = null;
        _throughputTracker = null;

        RecordActivity(VpnActivityKind.MonitoringStopped, "Monitoring stopped", detail);

        DisposeReconnect();

        // We have stopped watching, so the tunnel's state is no longer something
        // we know. The hero says so, and FortiClient is asked which manual action
        // still makes sense instead of leaving a stale button on screen.
        _shownState = VpnState.Unknown;
        RenderIdle();
        BeginResolveManualAction();
    }

    /// <summary>
    /// Tears down the reconnect side. Unlike the observe-only providers the COM
    /// controller owns a real out-of-process FortiClient COM object, so it MUST be
    /// disposed or fccomint.exe leaks for the life of the process.
    /// </summary>
    private void DisposeReconnect()
    {
        // Invalidate any in-flight attempt's result before tearing anything down.
        _reconnectGeneration++;

        try { _reconnectCts?.Cancel(); } catch { /* already disposed - nothing to cancel */ }
        _reconnectCts?.Dispose();
        _reconnectCts = null;

        // Field cleared FIRST, then the reference handed off: see
        // ReleaseComControllerOffUiThread for why disposal must not happen here.
        IDisposable? reconnectController = _reconnectController as IDisposable;
        _reconnectController = null;
        ReleaseComControllerOffUiThread(reconnectController);

        _reconnectPolicy = null;
        _reconnectInFlight = false;
        _reconnectAttemptNumber = 0;
        _lastReconnectDecision = ReconnectDecision.DisabledByUser;

        DisposeManualControl();
    }

    /// <summary>
    /// Tears down the manual controller's COM object. Deliberately does NOT re-enable
    /// the action button from here: that is done in the operation's own finally, so a
    /// teardown mid-operation can never leave it dead. The generation bump only
    /// suppresses the stale result text.
    /// </summary>
    private void DisposeManualControl()
    {
        _manualGeneration++;
        _noticeText = null;

        try { _manualCts?.Cancel(); } catch { /* already disposed - nothing to cancel */ }
        _manualCts?.Dispose();
        _manualCts = null;

        FortiClientComReconnectController? manualController = _manualController;
        _manualController = null;
        ReleaseComControllerOffUiThread(manualController);
    }

    /// <summary>
    /// Releases a FortiClient COM controller on a background thread, never on the
    /// UI thread.
    /// <para>
    /// WHY THIS IS NOT A PLAIN <c>Dispose()</c>: the controller serialises all COM
    /// work through an internal gate, and Dispose has to take that gate. A COM call
    /// can be in flight for seconds (Connect verifies for up to the verify timeout;
    /// Disconnect was measured taking ~5s to settle), so a Dispose issued from the
    /// WinForms UI thread blocks the message pump for exactly that long and the
    /// window visibly FREEZES while it is being closed. A try/catch does not help:
    /// a blocked lock is not an exception. Handing the reference to the thread pool
    /// lets the form close immediately while the release completes behind it.
    /// </para>
    /// <para>
    /// Callers MUST null their field before calling this (every call site does), so
    /// this local is the only surviving reference: nothing can hand the instance out
    /// again - <see cref="EnsureManualVpnControl"/> would build a fresh one rather
    /// than resurrect this one - and no second teardown can queue the same instance
    /// twice. Dispose is idempotent as well, so even a double hand-off would be
    /// harmless; the single-ownership rule is what makes it unreachable.
    /// </para>
    /// <para>
    /// If the process exits before the release runs, nothing leaks either: the
    /// out-of-process COM server shuts down once its client process is gone. This
    /// exists so a long-running session's Start/Stop cycles do not accumulate
    /// fccomint.exe instances.
    /// </para>
    /// </summary>
    private static void ReleaseComControllerOffUiThread(IDisposable? controller)
    {
        if (controller is null) return;

        _ = Task.Run(() =>
        {
            try { controller.Dispose(); }
            catch { /* releasing COM must never surface as an unobserved crash */ }
        });
    }

    // ------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------

    private void BtnSettings_Click(object? sender, EventArgs e)
    {
        // A clone goes in so Cancel cannot leave half-edited values behind; the
        // dialog's Result comes back only on OK.
        using var dialog = new SettingsForm(_settings.Clone());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ApplySettings(dialog.Result);
    }

    /// <summary>
    /// The hero sub-line only behaves like a button while it is showing the
    /// profile-mismatch nudge (see RenderHero) - re-checking the flag here rather
    /// than unhooking/rehooking the event keeps this a single, permanent
    /// subscription instead of one added and removed on every render.
    /// </summary>
    private void LblHeroSub_Click(object? sender, EventArgs e)
    {
        if (!_profileMismatch) return;

        BtnSettings_Click(sender, e);
    }

    private void ApplySettings(GuiSettings updated)
    {
        _settings = updated;
        _settings.Save();
        _config = _settings.ToWatchdogConfig();
        pollTimer.Interval = Math.Max(MinPollIntervalMs, _config.PollIntervalMs);

        if (_settings.AutoStartWithWindows)
        {
            WindowsStartupRegistration.Enable();
        }
        else
        {
            WindowsStartupRegistration.Disable();
        }

        // The checkbox is the live source of truth for CurrentConfig, so it is
        // synced BEFORE any restart below builds a policy from it. Changing it via
        // the dialog is still the user changing what this app may do, hence
        // recordChange: true.
        SyncAutoReconnectCheckbox(recordChange: true);

        if (_monitoring)
        {
            // A new profile or interval only takes effect in fresh providers and a
            // fresh correlator, so restart cleanly rather than patching in place.
            StopMonitoring("settings changed");
            StartMonitoring();
        }
        else
        {
            // The lazily built manual controller carries the OLD verify timeout;
            // drop it so the next click builds one from the new config.
            DisposeManualControl();
            RenderIdle();
            BeginResolveManualAction();
        }
    }

    /// <summary>
    /// Makes the Auto-reconnect checkbox agree with <see cref="_settings"/>. With
    /// recordChange the normal CheckedChanged path runs (log line, policy rebuild);
    /// without it the box is set silently, for the initial load.
    /// </summary>
    private void SyncAutoReconnectCheckbox(bool recordChange)
    {
        bool wanted = _settings.AutoReconnectEnabled;
        if (chkAutoReconnect.Checked == wanted)
        {
            RenderMode();
            return;
        }

        if (recordChange)
        {
            chkAutoReconnect.Checked = wanted;
            return;
        }

        _suppressCheckboxEvents = true;
        try
        {
            chkAutoReconnect.Checked = wanted;
        }
        finally
        {
            _suppressCheckboxEvents = false;
        }
        RenderMode();
    }

    private void ChkAutoReconnect_CheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressCheckboxEvents) return;

        bool armed = chkAutoReconnect.Checked;
        RenderMode();

        // Persist first, so the switch survives a restart even if the log write
        // below has nothing to say. Save never throws; a false return is not worth
        // a dialog - the setting still applies for this session.
        if (_settings.AutoReconnectEnabled != armed)
        {
            _settings.AutoReconnectEnabled = armed;
            _settings.Save();
        }

        // Recorded before the monitoring check below: arming the switch while
        // stopped is still the user changing what this app is allowed to do, and
        // a disarm here is often the explanation for the manual disconnect that
        // follows it in the log (RequestManualDisconnect clears the box first).
        RecordActivity(
            armed ? VpnActivityKind.AutoReconnectArmed : VpnActivityKind.AutoReconnectDisarmed,
            armed ? "Auto-reconnect armed" : "Auto-reconnect disarmed",
            _monitoring ? null : "monitoring not running");

        if (!_monitoring) return;

        // AutoReconnectEnabled lives on an immutable record, so the policy is
        // rebuilt from a fresh config to make the new checkbox state take effect
        // on the very next poll rather than at the next Start.
        _reconnectPolicy = new ReconnectPolicy(CurrentConfig);

        if (!_reconnectInFlight)
        {
            _reconnectAttemptNumber = 0;
            _lastReconnectDecision = ReconnectDecision.DisabledByUser;
        }

        RenderHero();
    }

    // ------------------------------------------------------------------
    // Poll loop
    // ------------------------------------------------------------------

    private async void PollTimer_Tick(object? sender, EventArgs e) => await PollOnceAsync();

    private async Task PollOnceAsync()
    {
        if (_pollInFlight || !_monitoring) return;
        if (_adapterProvider is null || _internetProvider is null || _processProvider is null
            || _logMonitor is null || _correlator is null || _store is null)
        {
            return;
        }

        _pollInFlight = true;
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;

            AdapterSnapshot adapter = await _adapterProvider.GetAdapterSnapshotAsync(_config.AdapterDescriptionPattern, CancellationToken.None);
            InternetSnapshot internet = await _internetProvider.GetCurrentStateAsync(CancellationToken.None);
            IReadOnlyList<ProcessSnapshot> processes = await _processProvider.GetProcessesAsync(CancellationToken.None);

            var newLogEvents = new List<LogEvent>();
            await foreach (LogEvent logEvent in _logMonitor.TailNewEventsAsync(CancellationToken.None))
            {
                newLogEvents.Add(logEvent);
            }

            DisconnectCorrelation? openBefore = _correlator.GetOpenCorrelation();
            int completedBefore = _correlator.GetCompletedCorrelations().Count;

            VpnStateSnapshot snapshot = _correlator.Ingest(adapter, internet, processes, newLogEvents, now);

            DisconnectCorrelation? openAfter = _correlator.GetOpenCorrelation();
            IReadOnlyList<DisconnectCorrelation> completedAfter = _correlator.GetCompletedCorrelations();

            await _store.SaveVpnStateSnapshotAsync(snapshot, CancellationToken.None);
            foreach (LogEvent logEvent in newLogEvents)
            {
                await _store.SaveLogEventAsync(logEvent, CancellationToken.None);
            }

            bool openedNew = openAfter is not null && (openBefore is null || openBefore.CorrelationId != openAfter.CorrelationId);
            if (openedNew)
            {
                await _store.UpsertCorrelationAsync(openAfter!, CancellationToken.None);
            }

            // A correlation that closed THIS tick as recovered carries the
            // evidence-based disconnect->connected duration for the log line.
            TimeSpan? recovery = null;
            if (completedAfter.Count > completedBefore)
            {
                for (int i = completedBefore; i < completedAfter.Count; i++)
                {
                    await _store.UpsertCorrelationAsync(completedAfter[i], CancellationToken.None);
                    if (completedAfter[i].RecoverySucceeded == true && completedAfter[i].RecoveryDuration is TimeSpan recovered)
                    {
                        recovery = recovered;
                    }
                }
            }

            bool fortiRunning = IsFortiClientRunning(processes, out string runningEngines);

            // Independent of everything else observed this tick: the tracker just
            // wants the adapter's raw byte counters and "now", and returns null
            // (never a fabricated zero) until it has two trustworthy samples.
            ThroughputSample? throughput = _throughputTracker?.Update(adapter.BytesReceived, adapter.BytesSent, now);

            RenderState(snapshot, internet, processes, runningEngines, fortiRunning, throughput, now);
            RecordObservedTransitions(snapshot.State, internet.State, fortiRunning, runningEngines, recovery);

            // Reconnect decision comes last, so it can layer its own status on top
            // of the freshly rendered state without ever delaying observation.
            EvaluateReconnect(snapshot.State, internet.State, now);
            RenderHero();
        }
        catch
        {
            // A single tick's transient failure must never crash the GUI - just
            // skip this frame and try again on the next tick.
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private static bool IsFortiClientRunning(IReadOnlyList<ProcessSnapshot> processes, out string runningEngines)
    {
        var running = new List<string>(EngineProcessNames.Length);
        foreach (ProcessSnapshot process in processes)
        {
            if (process.IsRunning && Array.IndexOf(EngineProcessNames, process.ProcessName) >= 0)
            {
                running.Add(process.ProcessName);
            }
        }

        runningEngines = string.Join(", ", running);
        return running.Count > 0;
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    private void RenderState(
        VpnStateSnapshot snapshot,
        InternetSnapshot internet,
        IReadOnlyList<ProcessSnapshot> processes,
        string runningEngines,
        bool fortiRunning,
        ThroughputSample? throughput,
        DateTimeOffset now)
    {
        _shownState = snapshot.State;

        // Internet
        switch (internet.State)
        {
            case InternetState.Up:
                SetIndicator(lblInternetValue, "Connected", Palette.Green);
                break;
            case InternetState.Down:
                SetIndicator(lblInternetValue, "Disconnected", Palette.Red);
                break;
            default:
                SetIndicator(lblInternetValue, "Unknown", Palette.Muted, hollow: true);
                break;
        }
        toolTip.SetToolTip(lblInternetValue,
            $"Probe: {internet.HttpsProbeUrl}\n" +
            $"Gateway reachable: {YesNo(internet.DefaultGatewayReachable)}\n" +
            $"DNS resolved: {YesNo(internet.DnsResolved)}\n" +
            $"HTTPS reachable: {YesNo(internet.HttpsReachable)}");

        // VPN adapter. The address is shown only while the correlator calls the
        // tunnel Connected - an adapter that is up without a tunnel has nothing
        // worth printing.
        AdapterSnapshot adapter = snapshot.Adapter;
        if (adapter.AdapterFound && adapter.IsUp)
        {
            SetIndicator(lblAdapterValue, "Up", Palette.Green);
        }
        else
        {
            SetIndicator(lblAdapterValue, "Down", Palette.Red);
        }
        lblAdapterExtra.Text = snapshot.State == VpnState.Connected && !string.IsNullOrEmpty(adapter.IpAddress)
            ? adapter.PrefixLength is int prefix ? $"{adapter.IpAddress}/{prefix}" : adapter.IpAddress
            : string.Empty;
        toolTip.SetToolTip(lblAdapterValue, adapter.AdapterFound
            ? $"Adapter: {adapter.AdapterName ?? "(unnamed)"}\n" +
              $"Interface index: {adapter.InterfaceIndex?.ToString() ?? "-"}\n" +
              $"Gateway: {(adapter.GatewayAddresses.Count > 0 ? string.Join(", ", adapter.GatewayAddresses) : "-")}"
            : $"No adapter matching \"{_config.AdapterDescriptionPattern}\" was found.");

        // FortiClient engine processes
        if (fortiRunning)
        {
            SetIndicator(lblFortiValue, "Running", Palette.Green);
        }
        else
        {
            SetIndicator(lblFortiValue, "Not running", Palette.Red);
        }
        lblFortiExtra.Text = runningEngines;
        toolTip.SetToolTip(lblFortiValue, DescribeProcesses(processes));

        // Network throughput. null (not a zero) means "not enough samples yet" -
        // shown as the same placeholder as before monitoring started, rather than
        // a momentarily-misleading "0 B/s" on the very first tick after Start.
        if (throughput is { } rate)
        {
            lblNetworkValue.Text = $"↓ {FormatRate(rate.DownloadBytesPerSecond)} ↑ {FormatRate(rate.UploadBytesPerSecond)}";
            lblNetworkExtra.Text = $"{FormatBytes(rate.TotalBytesReceived)} ↓ / {FormatBytes(rate.TotalBytesSent)} ↑ total";
            toolTip.SetToolTip(lblNetworkValue,
                "Live send/receive rate on the VPN adapter. \"total\" is the adapter's\n" +
                "own cumulative counter from Windows, which may not start from zero\n" +
                "when this monitoring session started - it is not reset by this app.");
        }
        else
        {
            lblNetworkValue.Text = NoThroughput;
            lblNetworkExtra.Text = string.Empty;
        }

        // Uptime is the correlator's view of how long Connected has held, not the
        // adapter's - the two agree, and the correlator's is what the log uses.
        lblUptimeValue.Text = snapshot.State == VpnState.Connected && _correlator is not null
            ? FormatUpTime(_correlator.CurrentStateDuration(now))
            : NoUptime;

        RenderManualAction(snapshot.State);
    }

    /// <summary>Resets every observation-driven surface to "nothing known" for the stopped state.</summary>
    private void RenderIdle()
    {
        RenderMonitoringIndicator();

        SetIndicator(lblInternetValue, "Unknown", Palette.Muted, hollow: true);
        SetIndicator(lblAdapterValue, "Unknown", Palette.Muted, hollow: true);
        SetIndicator(lblFortiValue, "Unknown", Palette.Muted, hollow: true);
        lblAdapterExtra.Text = string.Empty;
        lblFortiExtra.Text = string.Empty;
        toolTip.SetToolTip(lblInternetValue, "Start monitoring to observe the internet connection.");
        toolTip.SetToolTip(lblAdapterValue, "Start monitoring to observe the VPN adapter.");
        toolTip.SetToolTip(lblFortiValue, "Start monitoring to observe FortiClient's processes.");

        lblNetworkValue.Text = NoThroughput;
        lblNetworkExtra.Text = string.Empty;
        toolTip.SetToolTip(lblNetworkValue, "Start monitoring to observe network throughput.");

        lblUptimeValue.Text = NoUptime;

        RenderHero();
    }

    private void RenderMonitoringIndicator()
    {
        lblMonitoring.Text = _monitoring ? "● Monitoring" : "○ Not monitoring";
        lblMonitoring.ForeColor = _monitoring ? Palette.Green : Palette.Muted;

        // The button's TEXT is the verb for the action a click performs right now
        // ("Stop" while running, "Start" while idle) - never the current state,
        // which the label to its left already shows. A button whose label just
        // repeats the status next to it forces the user to infer the verb.
        btnMonitorToggle.Text = _monitoring ? "Stop" : "Start";
        btnMonitorToggle.ForeColor = _monitoring ? Palette.Red : Palette.Blue;
    }

    private void RenderMode()
    {
        lblMode.Text = chkAutoReconnect.Checked ? "Mode: Auto-reconnect" : "Mode: Observe Only";
    }

    /// <summary>
    /// The hero is the one place that says what is going on. Glyph, colour, title
    /// and tint all follow the observed state; the sub-line carries the reconnect
    /// decision - or a pinned manual notice, which outranks it because the user is
    /// standing there waiting for that answer.
    /// </summary>
    private void RenderHero()
    {
        string glyph;
        Color colour;
        Color tint;
        Color edge;
        string title;
        string sub;
        Color subColour = Palette.Muted;

        if (!_monitoring)
        {
            glyph = Glyphs.Unknown;
            colour = Palette.Muted;
            tint = Palette.GreyTint;
            edge = Palette.GreyEdge;
            title = "Not monitoring";
            sub = _idleTunnelHint ?? "Monitoring is stopped";
        }
        else
        {
            switch (_shownState)
            {
                case VpnState.Connected:
                    glyph = Glyphs.Check;
                    colour = Palette.Green;
                    tint = Palette.GreenTint;
                    edge = Palette.GreenEdge;
                    title = "VPN Connected";
                    sub = "FortiClient reports connected";
                    break;

                case VpnState.Connecting:
                case VpnState.Recovering:
                    glyph = Glyphs.Sync;
                    colour = Palette.Blue;
                    tint = Palette.BlueTint;
                    edge = Palette.BlueEdge;
                    title = "Reconnecting...";
                    sub = ReconnectSubStatus(recovering: true) ?? "Attempting to reconnect";
                    break;

                case VpnState.NetworkUnavailable:
                    // Auto-reconnect cannot fire here (the policy answers NoInternet),
                    // so the sub-line says the only honest thing: we are waiting.
                    glyph = Glyphs.Wifi;
                    colour = Palette.Amber;
                    tint = Palette.AmberTint;
                    edge = Palette.AmberEdge;
                    title = "Internet Unavailable";
                    sub = "Waiting for network...";
                    break;

                case VpnState.Disconnected:
                    glyph = Glyphs.Cross;
                    colour = Palette.Red;
                    tint = Palette.RedTint;
                    edge = Palette.RedEdge;
                    title = "VPN Disconnected";
                    sub = ReconnectSubStatus(recovering: false) ?? "Auto-reconnect is off";
                    break;

                default: // Unknown - first tick, nothing observed yet
                    glyph = Glyphs.Unknown;
                    colour = Palette.Muted;
                    tint = Palette.GreyTint;
                    edge = Palette.GreyEdge;
                    title = "Checking...";
                    sub = "Waiting for the first observation";
                    break;
            }
        }

        // A pinned notice is something happening right now (the user just clicked
        // something and is watching for the result) and always wins. Failing that,
        // a profile mismatch outranks the ordinary state text: knowing FortiClient
        // has never heard of this profile name is more useful than "Checking..." or
        // "Auto-reconnect is off", and it is the one thing on this screen the user
        // can actually go fix.
        bool subIsClickable = false;
        if (TryGetNotice(out string noticeText, out Color noticeColour))
        {
            sub = noticeText;
            subColour = noticeColour;
        }
        else if (_profileMismatch)
        {
            sub = $"Profile \"{_config.ProfileName}\" not found on this FortiClient - click to fix in Settings";
            subColour = Palette.Amber;
            subIsClickable = true;
        }

        lblHeroGlyph.Text = glyph;
        lblHeroGlyph.ForeColor = colour;
        lblHeroTitle.Text = title;
        lblHeroTitle.ForeColor = colour;
        lblHeroProfile.Text = _config.ProfileName;
        lblHeroSub.Text = sub;
        lblHeroSub.ForeColor = subColour;
        lblHeroSub.Cursor = subIsClickable ? Cursors.Hand : Cursors.Default;
        toolTip.SetToolTip(lblHeroSub, subIsClickable
            ? "FortiClient does not know this profile name. Open Settings and set the profile name to one of yours."
            : "");
        pnlHero.SetTint(tint, edge);

        // The window may be hidden in the tray, so the tooltip carries the headline.
        string trayText = $"VPN Watchdog - {title}";
        trayIcon.Text = trayText.Length > 63 ? trayText[..63] : trayText;
    }

    /// <summary>
    /// The reconnect decision as one short line, or null when the decision has
    /// nothing to add (Connected). Wording is deliberately about what the user will
    /// see happen next, not about the policy's internals.
    /// </summary>
    private string? ReconnectSubStatus(bool recovering)
    {
        if (_reconnectInFlight)
        {
            return $"Reconnecting... (attempt {_reconnectAttemptNumber})";
        }

        return _lastReconnectDecision switch
        {
            ReconnectDecision.Triggered or ReconnectDecision.AlreadyInFlight
                => $"Reconnecting... (attempt {Math.Max(1, _reconnectAttemptNumber)})",
            ReconnectDecision.WaitingForSelfHeal => "Waiting to reconnect...",
            ReconnectDecision.InBackoff => "Retrying shortly...",
            ReconnectDecision.GaveUp => $"Gave up after {_reconnectPolicy?.AttemptCount ?? 0} attempts",
            ReconnectDecision.NoInternet => "Waiting for network...",
            // While Recovering with the switch off, FortiClient's own recovery is
            // the only thing that could be reconnecting - say so rather than imply
            // this app is doing it.
            ReconnectDecision.DisabledByUser => recovering ? "Waiting for FortiClient to recover" : "Auto-reconnect is off",
            _ => null,
        };
    }

    private static void SetIndicator(Label label, string word, Color colour, bool hollow = false)
    {
        label.Text = (hollow ? "○ " : "● ") + word;
        label.ForeColor = colour;
    }

    private static string DescribeProcesses(IReadOnlyList<ProcessSnapshot> processes)
    {
        if (processes.Count == 0) return "No FortiClient processes observed.";

        var lines = new List<string>(processes.Count);
        foreach (ProcessSnapshot process in processes)
        {
            lines.Add($"{process.ProcessName}.exe: {(process.IsRunning ? "running" : "not running")}");
        }
        return string.Join("\n", lines);
    }

    private static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => "unknown" };

    private static string FormatUpTime(TimeSpan ts) =>
        ts.TotalHours >= 100
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";

    private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>"1.2 MB/s" style, auto-scaled. A negative input (should not happen -
    /// the tracker guards against it - but a format helper must never throw over
    /// a defensive Math.Max(0, ...) elsewhere going slightly wrong) clamps to 0.</summary>
    private static string FormatRate(double bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";

    private static string FormatBytes(double bytes)
    {
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Whole units read as whole numbers ("12 B/s", not "12.0 B/s"); anything
        // scaled up gets one decimal place, which is all a live rate needs.
        return unit == 0 ? $"{value:0} {ByteUnits[unit]}" : $"{value:0.0} {ByteUnits[unit]}";
    }

    private static string VersionText()
    {
        Version? version = typeof(MainForm).Assembly.GetName().Version;
        return version is null ? "v1.0.0" : $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    // ------------------------------------------------------------------
    // Update check - entirely independent of VPN monitoring/state. A single
    // anonymous GET against GitHub's public release API; see
    // VpnWatchdog.Core.Updates.GitHubUpdateChecker for the guarantee that this
    // can never throw and never blocks noticeably.
    // ------------------------------------------------------------------

    private void UpdateCheckTimer_Tick(object? sender, EventArgs e) => BeginUpdateCheck();

    private void BeginUpdateCheck()
    {
        // One at a time: the periodic timer and the constructor's initial call
        // could otherwise overlap on a slow network.
        if (_updateCheckInFlight) return;
        _updateCheckInFlight = true;

        _ = Task.Run(async () =>
        {
            string? tag = null;
            string? url = null;
            try
            {
                using var checker = new GitHubUpdateChecker();
                Version current = typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0);
                UpdateCheckResult result = await checker.CheckForUpdateAsync(current, CancellationToken.None).ConfigureAwait(false);
                if (result.Outcome == UpdateCheckOutcome.UpdateAvailable)
                {
                    tag = result.LatestVersionTag;
                    url = result.ReleaseUrl;
                }
            }
            catch
            {
                // A background version check must never crash the form.
            }
            finally
            {
                RunOnUiThread(() =>
                {
                    _updateCheckInFlight = false;

                    // Only a genuine change is worth a repaint - the far more common
                    // case, "still up to date", must stay a total no-op.
                    if (_updateAvailableVersionTag != tag || _updateReleaseUrl != url)
                    {
                        _updateAvailableVersionTag = tag;
                        _updateReleaseUrl = url;
                        RenderVersionLabel();
                    }
                });
            }
        });
    }

    /// <summary>
    /// Ordinary muted version text, unless an update is known to be available, in
    /// which case it becomes a clickable accent-coloured line naming the newer
    /// version - reusing the exact "colour + hand cursor + tooltip + click"
    /// pattern the profile-mismatch nudge on the hero uses, for the same reason:
    /// a plain-text label that happens to be clickable is not a control anyone
    /// would think to click.
    /// </summary>
    private void RenderVersionLabel()
    {
        bool hasUpdate = _updateAvailableVersionTag is not null && _updateReleaseUrl is not null;

        lblVersion.Text = hasUpdate
            ? $"{VersionText()} → {_updateAvailableVersionTag} available"
            : VersionText();
        lblVersion.ForeColor = hasUpdate ? Palette.Blue : Palette.Muted;
        lblVersion.Cursor = hasUpdate ? Cursors.Hand : Cursors.Default;
        toolTip.SetToolTip(lblVersion, hasUpdate
            ? "A newer version is available - click to open the release page"
            : "");
    }

    private void LblVersion_Click(object? sender, EventArgs e)
    {
        if (_updateReleaseUrl is not { Length: > 0 } url) return;

        try
        {
            // UseShellExecute: this is a browser URL, not an executable - letting
            // Windows hand it to whatever the user's default browser is, exactly
            // like clicking a link anywhere else.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Nothing sensible to do if the shell can't open a URL; definitely
            // not worth a MessageBox over.
        }
    }

    // ------------------------------------------------------------------
    // Activity log: transitions only
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes the activity log's VPN/internet/FortiClient lines. Only genuine
    /// transitions are recorded - this runs on every 2s tick, and a line per tick
    /// would bury the handful of events that actually matter under thousands that
    /// do not.
    /// </summary>
    private void RecordObservedTransitions(
        VpnState vpnState,
        InternetState internetState,
        bool fortiRunning,
        string runningEngines,
        TimeSpan? recovery)
    {
        // Unknown/Connecting deliberately map to null: neither is evidence that the
        // tunnel is up or down, and treating them as "down" would manufacture a
        // disconnect line out of a state that only means "we cannot tell yet".
        bool? connected = vpnState switch
        {
            VpnState.Connected => true,
            VpnState.Disconnected or VpnState.NetworkUnavailable or VpnState.Recovering => false,
            _ => null
        };

        if (connected is bool isConnected)
        {
            if (_logged.VpnConnected is bool wasConnected && wasConnected != isConnected)
            {
                if (isConnected)
                {
                    string detail = $"Profile: {_config.ProfileName}";
                    if (recovery is TimeSpan recovered)
                    {
                        // Disconnect -> connected as the correlator measured it. This
                        // is NOT the time our reconnect attempt took; it is how long
                        // the outage lasted, whoever ended it.
                        detail += $" - Recovery: {Math.Max(0, (int)Math.Round(recovered.TotalSeconds))}s";
                    }
                    RecordActivity(VpnActivityKind.VpnConnected, "VPN connected", detail);
                }
                else
                {
                    // "Unexpected" comes only from FortiClient's own log text; the
                    // reason code is shown verbatim, never interpreted.
                    DisconnectCorrelation? open = _correlator?.GetOpenCorrelation();
                    if (open is not null && string.Equals(open.DisconnectClassification, "Unexpected", StringComparison.Ordinal))
                    {
                        RecordActivity(VpnActivityKind.VpnDisconnectedUnexpectedly, "VPN disconnected unexpectedly",
                            string.IsNullOrEmpty(open.DisconnectReasonCode)
                                ? "Unexpected disconnect"
                                : $"Unexpected disconnect - Reason: {open.DisconnectReasonCode}");
                    }
                    else
                    {
                        RecordActivity(VpnActivityKind.VpnDisconnected, "VPN disconnected", $"State: {vpnState}");
                    }
                }
            }

            _logged = _logged with { VpnConnected = isConnected };
        }

        bool? internetUp = internetState switch
        {
            InternetState.Up => true,
            InternetState.Down => false,
            _ => null
        };

        if (internetUp is bool isUp)
        {
            if (_logged.InternetUp is bool wasUp && wasUp != isUp)
            {
                RecordActivity(
                    isUp ? VpnActivityKind.InternetRestored : VpnActivityKind.InternetLost,
                    isUp ? "Internet connection restored" : "Internet connection lost");
            }

            _logged = _logged with { InternetUp = isUp };
        }

        if (_logged.FortiClientRunning is bool wasRunning && wasRunning != fortiRunning)
        {
            RecordActivity(
                fortiRunning ? VpnActivityKind.FortiClientRunning : VpnActivityKind.FortiClientNotRunning,
                fortiRunning ? "FortiClient is running" : "FortiClient is not running",
                fortiRunning ? runningEngines : $"{string.Join(", ", EngineProcessNames)} not found");
        }

        _logged = _logged with { FortiClientRunning = fortiRunning };
    }

    // ------------------------------------------------------------------
    // Auto-reconnect (opt-in). The watchdog may ask FortiClient to CONNECT a
    // profile that has stayed down; it never disconnects, never touches the
    // registry, and never handles credentials - FortiClient uses its own saved
    // ones.
    // ------------------------------------------------------------------

    private void EvaluateReconnect(VpnState vpnState, InternetState internetState, DateTimeOffset now)
    {
        ReconnectPolicy? policy = _reconnectPolicy;
        IReconnectController? controller = _reconnectController;
        if (policy is null || controller is null) return;

        ReconnectDecision decision = policy.Evaluate(vpnState, internetState, now);

        // GaveUp is returned on EVERY poll once the budget is spent, so it is
        // recorded on the edge only - otherwise one exhausted outage would write a
        // line every 2 seconds for as long as it lasted.
        if (decision == ReconnectDecision.GaveUp && _lastReconnectDecision != ReconnectDecision.GaveUp)
        {
            RecordActivity(VpnActivityKind.AutoReconnectGaveUp, "Auto-reconnect gave up",
                $"after {policy.AttemptCount} attempt(s)");

            // The one moment this app most needs to reach someone who is not
            // looking at the screen: every automated attempt is exhausted and the
            // tunnel is still down. A balloon tip works even while minimized to
            // tray, which is the whole point of a "watchdog" - see
            // ShowGaveUpNotification for why this never throws into the poll loop.
            ShowGaveUpNotification(policy.AttemptCount);
        }

        _lastReconnectDecision = decision;

        // Tunnel is healthy again - clear attempt/backoff state so the next
        // outage starts from a clean slate.
        if (decision == ReconnectDecision.Connected && !_reconnectInFlight)
        {
            if (policy.AttemptCount > 0) policy.Reset();
            _reconnectAttemptNumber = 0;
        }

        // _reconnectInFlight is the UI-thread half of single-flight; BeginAttempt
        // is the policy's own half. Both must agree before an attempt starts.
        if (decision == ReconnectDecision.Triggered && !_reconnectInFlight && policy.BeginAttempt())
        {
            StartReconnectAttempt(policy, controller);
        }
    }

    /// <summary>
    /// A Windows notification-area balloon for the one moment this app most needs
    /// to reach someone who is not looking at the screen: every automated attempt
    /// is exhausted and the tunnel is still down. Deliberately does not force the
    /// tray icon visible to show this - <see cref="NotifyIcon.ShowBalloonTip"/>
    /// only has anywhere to anchor a balloon while the icon is already showing
    /// (i.e. the window is minimized to tray), which is exactly the case where a
    /// balloon is useful; when the window is open on-screen, the hero sub-line
    /// already says "Gave up after N attempts" and a balloon would be redundant.
    /// </summary>
    private void ShowGaveUpNotification(int attemptCount)
    {
        try
        {
            trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
            trayIcon.BalloonTipTitle = "VPN Watchdog";
            trayIcon.BalloonTipText =
                $"Gave up reconnecting '{_config.ProfileName}' after {attemptCount} attempt(s). " +
                "The tunnel is still down - try FortiClient's own tray icon to reconnect manually.";
            trayIcon.ShowBalloonTip(10_000);
        }
        catch
        {
            // A failed notification must never be worth taking down the poll loop.
        }
    }

    /// <summary>
    /// Runs one reconnect attempt off the poll loop. The poll timer must keep
    /// ticking while FortiClient works (the validated COM Connect took ~4s), so
    /// this deliberately does not await anything on the UI thread.
    /// </summary>
    private void StartReconnectAttempt(ReconnectPolicy policy, IReconnectController controller)
    {
        _reconnectInFlight = true;
        _reconnectAttemptNumber++;

        string profileName = CurrentConfig.ProfileName;
        CancellationToken ct = _reconnectCts?.Token ?? CancellationToken.None;
        int generation = _reconnectGeneration;

        // Captured for the background thread: _reconnectAttemptNumber is UI-thread
        // state and must not be read from the task below.
        int attemptNumber = _reconnectAttemptNumber;

        RecordActivity(VpnActivityKind.AutoReconnectTriggered, "Attempting VPN recovery",
            $"attempt {attemptNumber}");

        _ = Task.Run(async () =>
        {
            bool succeeded = false;
            Exception? failure = null;
            try
            {
                await controller.ReconnectAsync(profileName, ct).ConfigureAwait(false);
                succeeded = true;
            }
            catch (Exception ex)
            {
                // A COM fault, a cancellation, or a controller disposed by Stop is
                // just a failed attempt - it must never surface as a crash. The
                // policy's backoff/max-attempts rules take it from here. The
                // exception is kept only to describe the attempt in the log.
                succeeded = false;
                failure = ex;
            }
            finally
            {
                try { policy.RecordAttemptResult(succeeded, DateTimeOffset.Now); } catch { }
                try { policy.EndAttempt(); } catch { }

                // Recorded here rather than in the UI callback below: this is off
                // the UI thread, and the attempt is worth recording even when its
                // result is about to be discarded as belonging to a stopped session.
                // The exception message is a COM/verify description - never a
                // credential, this app has none to leak.
                if (succeeded)
                {
                    RecordActivity(VpnActivityKind.AutoReconnectSucceeded, "Auto-reconnect succeeded",
                        $"attempt {attemptNumber}");
                }
                else
                {
                    RecordActivity(VpnActivityKind.AutoReconnectFailed, "Auto-reconnect attempt failed",
                        failure is OperationCanceledException
                            ? $"attempt {attemptNumber} cancelled"
                            : $"attempt {attemptNumber}: {failure?.Message ?? "no reason reported"}");
                }

                RunOnUiThread(() =>
                {
                    // Ignore results from a monitoring session that has since stopped.
                    if (generation != _reconnectGeneration) return;

                    _reconnectInFlight = false;

                    // RecordAttemptResult(true) resets the policy's own outage
                    // bookkeeping, so the displayed attempt number resets with it.
                    if (succeeded)
                    {
                        _reconnectAttemptNumber = 0;
                        SetNotice($"Reconnected automatically (attempt {attemptNumber})", Palette.Green);
                    }

                    _lastReconnectDecision = succeeded
                        ? ReconnectDecision.Connected
                        : ReconnectDecision.InBackoff;
                    RenderHero();
                });
            }
        });
    }

    // ------------------------------------------------------------------
    // Manual VPN control - user-initiated ONLY.
    //
    // Everything in this region runs from the action button's Click and from
    // nothing else. This is the only place in the GUI that touches
    // IManualVpnControl: no timer, poll, correlation or reconnect-policy code may
    // call into it. That is what keeps the automatic watchdog structurally
    // incapable of disconnecting the tunnel - the poll path only ever sees
    // IReconnectController, which has no Disconnect on it to reach.
    // ------------------------------------------------------------------

    /// <summary>
    /// The manual control surface, created on first use and handed back narrowed to
    /// <see cref="IManualVpnControl"/> so callers cannot reach anything else through
    /// it. Manual actions must work whether or not monitoring is running, so this
    /// deliberately does not use (or create) StartMonitoring's controller and never
    /// starts monitoring as a side effect. Its COM object is released by
    /// <see cref="DisposeManualControl"/> along with the rest of the COM side.
    /// </summary>
    private IManualVpnControl EnsureManualVpnControl()
    {
        _manualController ??= FortiClientComReconnectController.FromConfig(_config);
        _manualCts ??= new CancellationTokenSource();
        return _manualController;
    }

    private void BtnAction_Click(object? sender, EventArgs e)
    {
        if (_manualInFlight || _manualActionSettling) return;

        if (_manualAction == ManualAction.Disconnect)
        {
            RequestManualDisconnect();
        }
        else
        {
            RequestManualConnect();
        }
    }

    private void RequestManualConnect()
    {
        // No confirmation prompt here: bringing the tunnel up is what the user
        // came for, and it is undone by the same button once it reads Disconnect.
        RunManualOperation(
            actionName: "Connect",
            successText: "FortiClient reports connected",
            successColor: Palette.Green,
            requestedKind: VpnActivityKind.ManualConnectRequested,
            succeededKind: VpnActivityKind.ManualConnectSucceeded,
            failedKind: VpnActivityKind.ManualConnectFailed,
            operation: static (control, profile, token) => control.ConnectAsync(profile, token));
    }

    private void RequestManualDisconnect()
    {
        string profileName = CurrentConfig.ProfileName;

        // Dropping someone's tunnel on a stray click is exactly the kind of thing
        // that must never happen silently, so: confirm first, defaulting to No.
        DialogResult answer = MessageBox.Show(
            this,
            $"Disconnect the VPN profile \"{profileName}\"?\r\n\r\n" +
            "Auto-reconnect will be switched OFF first - otherwise this watchdog would " +
            "see the drop and bring the tunnel straight back up.\r\n\r\n" +
            "You can re-arm it whenever you like with the Auto-reconnect checkbox.",
            "Disconnect VPN",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        // Disarm BEFORE disconnecting, never after. Assigning Checked raises
        // ChkAutoReconnect_CheckedChanged, which rebuilds ReconnectPolicy from a
        // config with AutoReconnectEnabled false, so the very next poll decides
        // DisabledByUser. This is the entire answer to "the watchdog fights the
        // user", which is why it runs before the disconnect and stays done even if
        // the disconnect below then fails.
        bool wasArmed = chkAutoReconnect.Checked;
        if (wasArmed)
        {
            chkAutoReconnect.Checked = false;
        }

        // An auto-reconnect attempt that is ALREADY in flight would still bring the
        // tunnel back up behind the user, so cancel that one too. The token source
        // is replaced rather than left cancelled, so re-arming the checkbox later in
        // this same monitoring session still works. The generation is deliberately
        // NOT bumped: the cancelled attempt must be allowed to land and clear
        // _reconnectInFlight, or auto-reconnect would stay wedged for the session.
        if (_reconnectInFlight && _reconnectCts is not null)
        {
            CancellationTokenSource stale = _reconnectCts;
            _reconnectCts = new CancellationTokenSource();
            try { stale.Cancel(); } catch { /* already disposed - nothing to cancel */ }
            stale.Dispose();
        }

        RunManualOperation(
            actionName: "Disconnect",
            successText: wasArmed
                ? "Disconnected. Auto-reconnect switched off."
                : "Disconnected (manual)",
            successColor: Palette.Red,
            requestedKind: VpnActivityKind.ManualDisconnectRequested,
            succeededKind: VpnActivityKind.ManualDisconnectSucceeded,
            failedKind: VpnActivityKind.ManualDisconnectFailed,
            operation: static (control, profile, token) => control.DisconnectAsync(profile, token),
            requestDetail: wasArmed ? "auto-reconnect switched off first" : null);
    }

    /// <summary>
    /// Runs one manual operation off the UI thread. Both take several seconds -
    /// Connect waits for the tunnel to actually come up, and Disconnect has to wait
    /// out FortiClient reporting a stale "still connected" - so nothing here may be
    /// awaited on the UI thread. Same shape as <see cref="StartReconnectAttempt"/>:
    /// fire a Task, marshal the result back through <see cref="RunOnUiThread"/>, and
    /// ignore results whose generation has since been torn down.
    /// </summary>
    private void RunManualOperation(
        string actionName,
        string successText,
        Color successColor,
        VpnActivityKind requestedKind,
        VpnActivityKind succeededKind,
        VpnActivityKind failedKind,
        Func<IManualVpnControl, string, CancellationToken, Task> operation,
        string? requestDetail = null)
    {
        // Belt and braces with the disabled button: a second click that slips in
        // before the repaint must not start an overlapping operation.
        if (_manualInFlight) return;

        // Recorded after that guard and after the disconnect confirmation, so the
        // log holds exactly the requests that were really made.
        RecordActivity(requestedKind, $"Manual {actionName.ToLowerInvariant()} requested", requestDetail);

        IManualVpnControl control;
        CancellationToken ct;
        try
        {
            control = EnsureManualVpnControl();
            ct = _manualCts?.Token ?? CancellationToken.None;
        }
        catch (Exception ex)
        {
            // Even building the controller can fail (FortiClient COM not
            // registered). Report it and leave the button usable.
            RecordActivity(failedKind, $"Manual {actionName.ToLowerInvariant()} failed", ex.Message);
            ShowManualFailure(actionName, ex);
            return;
        }

        string profileName = CurrentConfig.ProfileName;
        int generation = _manualGeneration;

        _manualInFlight = true;
        RenderActionButton();
        SetNotice($"{actionName}ing...", Palette.Blue);

        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await operation(control, profileName, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A COM fault, a verify timeout, or a controller disposed by Stop
                // or by closing the form. None of them may surface as a crash.
                failure = ex;
            }
            finally
            {
                // Recorded off the UI thread, and regardless of generation: the
                // operation really happened, so the trail must say so even if its
                // result is about to be discarded as belonging to a dead session.
                if (failure is null)
                {
                    RecordActivity(succeededKind, $"Manual {actionName.ToLowerInvariant()} succeeded");
                }
                else
                {
                    RecordActivity(failedKind,
                        failure is OperationCanceledException
                            ? $"Manual {actionName.ToLowerInvariant()} cancelled"
                            : $"Manual {actionName.ToLowerInvariant()} failed",
                        failure.Message);
                }

                RunOnUiThread(() =>
                {
                    // Unconditional, and before the generation check: an operation
                    // that outlived its controller must still hand the button back,
                    // so an error can never leave it permanently disabled.
                    _manualInFlight = false;
                    RenderActionButton();

                    // The tunnel has just been driven somewhere new, and the poll
                    // may not run for another 2s (or at all, if monitoring is off),
                    // so settle which action belongs on the button from FortiClient's
                    // own answer rather than from what was there before the click.
                    BeginResolveManualAction();

                    // Result belongs to a controller that has since been torn down -
                    // say nothing rather than describing a dead session.
                    if (generation != _manualGeneration)
                    {
                        RenderHero();
                        return;
                    }

                    if (failure is null)
                    {
                        SetNotice(successText, successColor);
                    }
                    else if (failure is OperationCanceledException)
                    {
                        SetNotice($"{actionName} cancelled", Palette.Muted);
                    }
                    else
                    {
                        ShowManualFailure(actionName, failure);
                    }
                    RenderHero();
                });
            }
        });
    }

    // ------------------------------------------------------------------
    // Which manual action the button offers.
    //
    // Connect and Disconnect share one button, so the user is offered the action
    // that makes sense right now instead of two buttons of which one is always
    // wrong. Nothing here initiates a state change: it only decides what to show,
    // and the read-only probe below asks FortiClient through
    // IReconnectController.IsConnectedAsync - never through IManualVpnControl,
    // which stays reachable from button clicks alone.
    // ------------------------------------------------------------------

    /// <summary>
    /// Chooses the action for an observed VPN state.
    /// <para>
    /// While a transition is in progress the CURRENT action stays put and is merely
    /// disabled behind a "Reconnecting..." caption - it shows the state being LEFT
    /// (Connecting is on its way out of Disconnected, Recovering on its way out of
    /// Connected). Flipping halfway through would make the button jump back and
    /// forth for the few seconds FortiClient takes to settle, which reads as a
    /// broken window rather than as progress.
    /// </para>
    /// </summary>
    private void RenderManualAction(VpnState state)
    {
        switch (state)
        {
            case VpnState.Connected:
                ShowManualAction(ManualAction.Disconnect, enabled: true);
                break;

            case VpnState.Disconnected:
            case VpnState.NetworkUnavailable:
                // NetworkUnavailable is still "the tunnel is not up" - offering
                // Connect is honest; whether it will succeed is FortiClient's call.
                ShowManualAction(ManualAction.Connect, enabled: true);
                break;

            case VpnState.Connecting:
                ShowManualAction(ManualAction.Connect, enabled: false);
                break;

            case VpnState.Recovering:
                ShowManualAction(ManualAction.Disconnect, enabled: false);
                break;

            default: // Unknown - we genuinely do not know, so do not guess.
                BeginResolveManualAction();
                break;
        }
    }

    private void ShowManualAction(ManualAction action, bool enabled)
    {
        // Every call from observed evidence invalidates any in-flight COM probe:
        // what we can see now is fresher than an answer that was requested before.
        _actionStamp++;

        _manualAction = action;
        _manualActionSettling = !enabled;
        RenderActionButton();
    }

    /// <summary>
    /// Paints the one action button from <see cref="_manualAction"/>, whether a
    /// transition is settling, and whether a manual operation is running. A manual
    /// operation in flight owns the disabled state - never hand the button back
    /// behind its back, or the click it is already busy with could start twice.
    /// </summary>
    private void RenderActionButton()
    {
        bool busy = _manualInFlight;
        bool enable = !busy && !_manualActionSettling;

        string text;
        Color back;
        if (busy)
        {
            text = _manualAction == ManualAction.Connect ? "Connecting..." : "Disconnecting...";
            back = Palette.Disabled;
        }
        else if (_manualActionSettling)
        {
            text = "Reconnecting...";
            back = Palette.Disabled;
        }
        else if (_manualAction == ManualAction.Connect)
        {
            text = "Connect";
            back = Palette.Blue;
        }
        else
        {
            text = "Disconnect";
            back = Palette.Red;
        }

        btnAction.Text = text;
        btnAction.BackColor = back;
        btnAction.ForeColor = enable ? Color.White : Palette.Muted;
        btnAction.FlatAppearance.MouseOverBackColor = enable ? ControlPaint.Dark(back, 0.08f) : back;
        btnAction.FlatAppearance.MouseDownBackColor = enable ? ControlPaint.Dark(back, 0.16f) : back;
        btnAction.Enabled = enable;

        toolTip.SetToolTip(btnAction, enable
            ? (_manualAction == ManualAction.Connect ? ConnectTooltip : DisconnectTooltip)
            : string.Empty);

        // WinForms sends no mouse messages to a disabled control, so the explanation
        // for a disabled button has to live on the panel behind it (see Designer).
        toolTip.SetToolTip(pnlAction, enable ? string.Empty : busy ? BusyTooltip : TransitionTooltip);
    }

    private const string ConnectTooltip =
        "Asks FortiClient to connect this profile right now.\nWorks whether or not monitoring is running.";

    private const string DisconnectTooltip =
        "Drops this VPN profile, after asking you to confirm.\nAuto-reconnect is switched off first, so the\nwatchdog cannot bring the tunnel straight back up.";

    private const string TransitionTooltip =
        "A VPN state change is in progress.\nThis button becomes available again once it settles.";

    private const string BusyTooltip = "A manual VPN operation is in progress.";

    /// <summary>
    /// Resolves an unknown tunnel state by asking FortiClient itself, then shows the
    /// matching action.
    /// <para>
    /// Deliberately event-driven - form load, the end of a manual operation, a
    /// settings change, and a poll that came back Unknown - and never a timer of its
    /// own: an idle COM poll would spin FortiClient's out-of-process automation
    /// server forever for a cosmetic detail. The probe is single-flighted, and in
    /// practice the correlator only reports Unknown on its very first tick, so this
    /// settles almost at once.
    /// </para>
    /// <para>
    /// The probe is read-only (IsConnectedAsync) and is typed as
    /// <see cref="IReconnectController"/> throughout, so this path cannot reach a
    /// disconnect even by accident.
    /// </para>
    /// </summary>
    private void BeginResolveManualAction()
    {
        // One probe at a time: the answer would be the same, and each one can have
        // to start the COM server.
        if (_actionProbeInFlight) return;

        // Reuse whichever controller already exists rather than paying for a second
        // COM object; both are narrowed to the read-only interface here.
        IReconnectController? existing = _reconnectController ?? _manualController;

        string profileName = _config.ProfileName;
        int stamp = _actionStamp;

        _actionProbeInFlight = true;

        _ = Task.Run(async () =>
        {
            bool? connected = null;
            bool profileMismatch = false;

            // Non-null only when this probe had to create its own controller and is
            // therefore the one that has to release it.
            FortiClientComReconnectController? owned = null;
            try
            {
                IReconnectController probe = existing ?? (owned = FortiClientComReconnectController.FromConfig(_config));
                connected = await probe.IsConnectedAsync(profileName, CancellationToken.None).ConfigureAwait(false);

                // Piggy-backed on the same probe/round-trip rather than a second COM
                // call: a non-empty tunnel list that does not contain the configured
                // profile is the strongest evidence available that ProfileName still
                // holds the shipped-default value on a machine whose FortiClient has
                // never heard of that profile - exactly the "shared the .exe with a
                // teammate and it just says Unknown forever" failure mode.
                IReadOnlyList<string> knownTunnels =
                    await probe.GetTunnelListAsync(CancellationToken.None).ConfigureAwait(false);
                profileMismatch = knownTunnels.Count > 0 &&
                    !knownTunnels.Any(t => string.Equals(t, profileName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // FortiClient COM missing, blocked, or torn down under us. Leaving
                // the button as it is is the right degradation: the fail-safe
                // default is Connect, which cannot drop anyone's tunnel. An empty
                // tunnel list from this path is "no evidence", not a mismatch.
                connected = null;
            }
            finally
            {
                // Already on a background thread, so this cannot freeze the UI even
                // if the controller's COM gate is busy, and nothing else can be
                // holding `owned` - this task created it and never published it.
                try { owned?.Dispose(); } catch { /* releasing COM must never crash */ }

                RunOnUiThread(() =>
                {
                    _actionProbeInFlight = false;

                    // Independent of which VPN state is currently shown - a config
                    // mismatch does not go stale the way an IsConnected answer does,
                    // so it is applied even if the stamp moved on below.
                    if (_profileMismatch != profileMismatch)
                    {
                        _profileMismatch = profileMismatch;
                        RenderHero();
                    }

                    // Observed evidence arrived while we were asking - it is fresher
                    // than this answer, so drop ours rather than flipping the button
                    // back to a state the poll has already moved past.
                    if (stamp != _actionStamp) return;

                    if (connected is bool isConnected)
                    {
                        ShowManualAction(isConnected ? ManualAction.Disconnect : ManualAction.Connect, enabled: true);

                        // While stopped the poll is not painting the hero, so this is
                        // the only honest thing it can say about the tunnel.
                        if (!_monitoring)
                        {
                            _idleTunnelHint = isConnected
                                ? "FortiClient reports connected"
                                : "FortiClient reports not connected";
                            RenderHero();
                        }
                    }
                });
            }
        });
    }

    // ------------------------------------------------------------------
    // Activity log
    // ------------------------------------------------------------------

    private void BtnViewLogs_Click(object? sender, EventArgs e)
    {
        // Modal: it is a read-only window onto history, and keeping it modal means
        // there is never a second copy of it drifting out of date behind the form.
        // The poll timer keeps ticking underneath, so monitoring is unaffected.
        using (var dialog = new ActivityLogForm(_activityLog, _config.ProfileName))
        {
            dialog.ShowDialog(this);
        }

        // The dialog can clear history, so the "Last Event" line re-reads rather
        // than keeps describing an entry that no longer exists.
        ShowLastEvent(TryGetMostRecentEntry());
    }

    /// <summary>
    /// Appends one line to the activity trail and mirrors it onto the "Last Event"
    /// row, so the main window and the log dialog always agree on what happened last.
    /// <para>
    /// Swallows everything. The trail is instrumentation: a failure to write a line
    /// must never break the poll loop, abort a reconnect attempt, or throw on the UI
    /// thread. <see cref="IVpnActivityLog"/> implementations already promise not to
    /// throw - this is the caller's half of that promise, because the call sites
    /// include both of those places.
    /// </para>
    /// <para>
    /// Every entry carries the watched profile: this GUI follows exactly one, so
    /// stamping it makes an exported line self-describing. <see cref="_config"/> is
    /// used rather than <see cref="CurrentConfig"/> because this is also called from
    /// background threads, and CurrentConfig reads a control.
    /// </para>
    /// </summary>
    private void RecordActivity(VpnActivityKind kind, string message, string? detail = null)
    {
        VpnActivityEntry entry;
        try
        {
            entry = new VpnActivityEntry(DateTimeOffset.Now, kind, message, _config.ProfileName, detail);
            _activityLog.Record(entry);
        }
        catch
        {
            // Instrumentation only - never worth taking anything else down for.
            return;
        }

        RunOnUiThread(() => ShowLastEvent(entry));
    }

    private VpnActivityEntry? TryGetMostRecentEntry()
    {
        try
        {
            IReadOnlyList<VpnActivityEntry> recent = _activityLog.GetRecent(1);
            return recent.Count > 0 ? recent[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private void ShowLastEvent(VpnActivityEntry? entry)
    {
        if (entry is null)
        {
            lblLastEventValue.Text = "No activity recorded yet";
            lblLastEventValue.ForeColor = Palette.Muted;
            lblLastEventTime.Text = string.Empty;
            toolTip.SetToolTip(lblLastEventValue, string.Empty);
            return;
        }

        lblLastEventValue.Text = entry.Message;
        lblLastEventValue.ForeColor = SeverityColour(entry.Kind.Severity());
        lblLastEventTime.Text = entry.Timestamp.ToLocalTime().ToString("ddd d MMM, HH:mm:ss");
        toolTip.SetToolTip(lblLastEventValue,
            string.IsNullOrEmpty(entry.Detail) ? entry.Message : $"{entry.Message}\n{entry.Detail}");
    }

    private static Color SeverityColour(VpnActivitySeverity severity) => severity switch
    {
        VpnActivitySeverity.Success => Palette.Green,
        VpnActivitySeverity.Warning => Palette.Amber,
        VpnActivitySeverity.Error => Palette.Red,
        _ => Palette.Text,
    };

    // ------------------------------------------------------------------
    // Hero notice (manual results, auto-reconnect success)
    // ------------------------------------------------------------------

    /// <summary>
    /// Pins a message onto the hero sub-line. While a manual operation is in
    /// flight the notice never expires; afterwards it lingers a few seconds so the
    /// 2s poll cannot wipe the result - or the explanation of why the Auto-reconnect
    /// checkbox just changed - before the user has read it.
    /// </summary>
    private void SetNotice(string text, Color color)
    {
        _noticeText = text;
        _noticeColor = color;
        _noticeUntil = DateTimeOffset.Now + NoticeDuration;

        lblHeroSub.Text = text;
        lblHeroSub.ForeColor = color;
    }

    /// <summary>A still-current notice, if one owns the sub-line right now.</summary>
    private bool TryGetNotice(out string text, out Color color)
    {
        text = string.Empty;
        color = Palette.Muted;

        if (_noticeText is null) return false;

        if (!_manualInFlight && DateTimeOffset.Now >= _noticeUntil)
        {
            _noticeText = null;
            return false;
        }

        text = _noticeText;
        color = _noticeColor;
        return true;
    }

    /// <summary>Surfaces a failed manual operation in both the hero and a dialog.</summary>
    private void ShowManualFailure(string actionName, Exception ex)
    {
        SetNotice($"{actionName} failed", Palette.Red);

        MessageBox.Show(
            this,
            $"Could not {actionName.ToLowerInvariant()} \"{CurrentConfig.ProfileName}\".\r\n\r\n{ex.Message}",
            $"{actionName} VPN failed",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { /* form closed mid-attempt */ }
        catch (InvalidOperationException) { /* handle destroyed mid-attempt */ }
    }

    // ------------------------------------------------------------------
    // Tray / lifetime
    // ------------------------------------------------------------------

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        Hide();
        trayIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        trayIcon.Visible = false;
    }

    /// <summary>The tray menu's Exit: the one way past MinimizeToTrayOnClose.</summary>
    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // The X button hides rather than quits when the user asked for that - a
        // watchdog is supposed to keep watching. Windows shutdown and the tray's
        // Exit still close for real.
        if (e.CloseReason == CloseReason.UserClosing && _settings.MinimizeToTrayOnClose && !_exitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        pollTimer.Stop();

        // Closing while monitoring is still a stop, and a trail that shows a start
        // with no matching stop reads as a crash to whoever inspects it later.
        if (_monitoring)
        {
            RecordActivity(VpnActivityKind.MonitoringStopped, "Monitoring stopped", "application closing");
        }

        _monitoring = false;
        trayIcon.Visible = false;

        // Release the FortiClient COM objects on exit too - closing the window
        // while monitoring must not leak fccomint.exe.
        DisposeReconnect();
    }

    private void MainForm_FormClosed(object? sender, FormClosedEventArgs e)
    {
        // Releases the SQLite pool's file handles. Anything a background finally
        // block records after this is dropped by the store, which is the right
        // outcome for a process that is exiting.
        try { (_activityLog as IDisposable)?.Dispose(); } catch { /* nothing left to protect */ }
    }

    // ------------------------------------------------------------------
    // Look: palette, glyphs, rounded hero
    // ------------------------------------------------------------------

    /// <summary>Windows 11 utility palette. Semantic colours match the log dialog's.</summary>
    private static class Palette
    {
        public static readonly Color Surface = Color.FromArgb(0xF9, 0xF9, 0xF9);
        public static readonly Color Border = Color.FromArgb(0xE5, 0xE5, 0xE5);
        public static readonly Color ButtonBorder = Color.FromArgb(0xD1, 0xD1, 0xD1);
        public static readonly Color Hover = Color.FromArgb(0xED, 0xED, 0xED);
        public static readonly Color Pressed = Color.FromArgb(0xE0, 0xE0, 0xE0);
        public static readonly Color Disabled = Color.FromArgb(0xE1, 0xE1, 0xE1);
        public static readonly Color Text = Color.FromArgb(0x1A, 0x1A, 0x1A);
        public static readonly Color Muted = Color.FromArgb(0x6E, 0x6E, 0x6E);

        public static readonly Color Green = Color.FromArgb(0x10, 0x7C, 0x10);
        public static readonly Color Red = Color.FromArgb(0xC4, 0x2B, 0x1C);
        public static readonly Color Amber = Color.FromArgb(0xC1, 0x9C, 0x00);
        public static readonly Color Blue = Color.FromArgb(0x00, 0x67, 0xC0);

        public static readonly Color GreenTint = Color.FromArgb(0xEA, 0xF6, 0xEA);
        public static readonly Color GreenEdge = Color.FromArgb(0xB9, 0xE0, 0xB9);
        public static readonly Color RedTint = Color.FromArgb(0xFC, 0xEC, 0xEA);
        public static readonly Color RedEdge = Color.FromArgb(0xF0, 0xBC, 0xB6);
        public static readonly Color BlueTint = Color.FromArgb(0xE8, 0xF1, 0xFB);
        public static readonly Color BlueEdge = Color.FromArgb(0xB6, 0xD3, 0xF2);
        public static readonly Color AmberTint = Color.FromArgb(0xFB, 0xF5, 0xE1);
        public static readonly Color AmberEdge = Color.FromArgb(0xE8, 0xD6, 0x8A);
        public static readonly Color GreyTint = Color.FromArgb(0xF1, 0xF1, 0xF1);
        public static readonly Color GreyEdge = Color.FromArgb(0xDE, 0xDE, 0xDE);
    }

    /// <summary>
    /// Font glyphs instead of image resources: Segoe Fluent Icons on Windows 11,
    /// Segoe MDL2 Assets on Windows 10, and plain characters if neither is present
    /// so the window never shows a tofu box. Both icon fonts share these code points.
    /// </summary>
    private static class Glyphs
    {
        private static readonly string? IconFamily = PickIconFamily();

        public static string Shield => Pick(0xEA18, "◆");   // shield / black diamond
        public static string Check => Pick(0xE73E, "✓");    // check mark
        public static string Cross => Pick(0xE711, "✕");    // cancel / multiplication x
        public static string Sync => Pick(0xE895, "↻");     // sync / clockwise arrow
        public static string Wifi => Pick(0xE701, "⚠");     // wifi / warning sign
        public static string Unknown => Pick(0xE9CE, "?");        // unknown
        public static string Settings => Pick(0xE713, "⚙"); // settings / gear
        public static string Info => Pick(0xE946, "i");           // info

        public static Font Font(float points) =>
            new(IconFamily ?? "Segoe UI", points, FontStyle.Regular, GraphicsUnit.Point);

        private static string Pick(int codePoint, string fallback) => IconFamily is null ? fallback : ((char)codePoint).ToString();

        private static string? PickIconFamily()
        {
            try
            {
                using var installed = new InstalledFontCollection();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FontFamily family in installed.Families)
                {
                    names.Add(family.Name);
                }

                if (names.Contains("Segoe Fluent Icons")) return "Segoe Fluent Icons";
                if (names.Contains("Segoe MDL2 Assets")) return "Segoe MDL2 Assets";
            }
            catch
            {
                // Enumerating fonts is cosmetic; the text fallbacks are always fine.
            }
            return null;
        }
    }

    /// <summary>
    /// A TableLayoutPanel that paints itself as a rounded, tinted, hairline-bordered
    /// card. Children inherit the tint through the ambient BackColor, so a state
    /// change is one <see cref="SetTint"/> call. The corner radius is scaled with the
    /// DPI so it stays 6 logical px everywhere.
    /// </summary>
    private sealed class RoundedTablePanel : TableLayoutPanel
    {
        private Color _edge = Palette.GreyEdge;

        public RoundedTablePanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Palette.GreyTint;
        }

        public void SetTint(Color tint, Color edge)
        {
            if (BackColor == tint && _edge == edge) return;
            _edge = edge;
            BackColor = tint;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The corners must show the surface behind the card, so that is painted
            // first and the rounded card on top of it.
            using (var surface = new SolidBrush(Parent?.BackColor ?? Palette.Surface))
            {
                e.Graphics.FillRectangle(surface, ClientRectangle);
            }

            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            int radius = Math.Max(1, LogicalToDeviceUnits(6));
            using GraphicsPath path = RoundedRectangle(rect, radius);

            SmoothingMode previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(BackColor))
            {
                e.Graphics.FillPath(fill, path);
            }
            using (var pen = new Pen(_edge))
            {
                e.Graphics.DrawPath(pen, path);
            }
            e.Graphics.SmoothingMode = previous;
        }

        private static GraphicsPath RoundedRectangle(Rectangle rect, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
