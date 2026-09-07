using System.Diagnostics;
using System.Threading;

namespace VpnWatchdog.Core.Reconnect;

/// <summary>
/// A source of elapsed time that only ever moves forward, at the rate real time
/// passes, and that no NTP correction, DST change or hand-set clock can step.
/// <para>
/// The reconnect policy needs one because every duration it cares about (the grace
/// period, the backoff) is a DURATION, and durations must not be derived by
/// subtracting two wall-clock readings: the wall clock is an absolute-time service,
/// not an elapsed-time service, and it is routinely stepped both forwards and
/// backwards underneath a long-running process.
/// </para>
/// <para>
/// Implementations must be callable from any thread and must not throw: the policy
/// reads this on every poll, from whichever thread the watchdog loop happens to be on.
/// </para>
/// </summary>
public interface IMonotonicClock
{
    /// <summary>
    /// Time elapsed since some fixed but arbitrary origin. Only differences between
    /// two readings are meaningful, and such a difference is never negative.
    /// </summary>
    TimeSpan Elapsed { get; }
}

/// <summary>
/// The default <see cref="IMonotonicClock"/>, backed by <see cref="Stopwatch"/> (i.e.
/// the high-resolution performance counter). Deliberately NOT
/// <c>Environment.TickCount64</c>: on Windows that is the biased interrupt time, which
/// can include time the machine spent suspended - precisely the interval this clock
/// exists to make visible.
/// </summary>
public sealed class SystemMonotonicClock : IMonotonicClock
{
    /// <summary>
    /// Origin captured once, so every reading is comparable across the process. Static
    /// and immutable: this type holds no per-instance state and never grows, which
    /// matters in a watchdog that runs for days.
    /// </summary>
    private static readonly long Origin = Stopwatch.GetTimestamp();

    /// <summary>Shared instance - the clock is stateless, so one is enough.</summary>
    public static SystemMonotonicClock Instance { get; } = new();

    /// <inheritdoc />
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(Origin);
}

/// <summary>
/// Pure, deterministic reconnect decision logic. No COM, no I/O, no timers, no
/// threads of its own: every time value arrives via the <c>now</c> parameter, so
/// the whole policy is unit-testable by simply advancing a fake clock.
/// <para>
/// <b>Rules, applied strictly in this order</b> (first match wins):
/// </para>
/// <list type="number">
///   <item><description>
///     VPN is <see cref="VpnState.Connected"/> -&gt; <see cref="ReconnectDecision.Connected"/>.
///     This is the healthy state, so it also resets everything (attempt count,
///     backoff, disconnected-since marker) - the next outage starts fresh.
///   </description></item>
///   <item><description>
///     Auto-reconnect not enabled -&gt; <see cref="ReconnectDecision.DisabledByUser"/>.
///     Auto-reconnect is opt-in and off by default; nothing below this line can
///     override the user's switch.
///   </description></item>
///   <item><description>
///     Internet is not <see cref="InternetState.Up"/> -&gt; <see cref="ReconnectDecision.NoInternet"/>.
///     Reconnecting cannot help while the underlying network is down, and the
///     grace-period clock is <b>cleared</b> here rather than left running: the
///     grace period must measure time since the VPN dropped <i>with working
///     internet</i>, otherwise a long ISP outage would burn the whole grace window
///     and we'd fire the instant connectivity returned - exactly when FortiClient
///     is about to recover by itself. The attempt budget and backoff survive,
///     because they belong to the outage rather than to the grace window.
///   </description></item>
///   <item><description>
///     An attempt is already in flight (see <see cref="BeginAttempt"/>) -&gt;
///     <see cref="ReconnectDecision.AlreadyInFlight"/>. Single-flight is absolute.
///   </description></item>
///   <item><description>
///     First evaluation of a new outage -&gt; record <c>now</c> as the
///     disconnected-since marker and return <see cref="ReconnectDecision.WaitingForSelfHeal"/>.
///   </description></item>
///   <item><description>
///     Still inside the grace period -&gt; <see cref="ReconnectDecision.WaitingForSelfHeal"/>.
///     <b>Why the grace period exists:</b> FortiClient has its own built-in
///     recovery, measured in the Phase 2 evidence healing drops in roughly
///     6-70 seconds. Intervening sooner would race that recovery - two connect
///     paths driving the same tunnel at once produces flapping, spurious auth
///     traffic and a worse outcome than doing nothing. So we always let
///     FortiClient try first and only step in once it has demonstrably failed.
///   </description></item>
///   <item><description>
///     Attempt budget exhausted -&gt; <see cref="ReconnectDecision.GaveUp"/>.
///   </description></item>
///   <item><description>
///     A previous attempt happened and we are still inside its exponential
///     backoff -&gt; <see cref="ReconnectDecision.InBackoff"/>.
///   </description></item>
///   <item><description>
///     Otherwise -&gt; <see cref="ReconnectDecision.Triggered"/>.
///   </description></item>
/// </list>
/// <para>
/// Backoff is exponential from <see cref="WatchdogConfig.ReconnectInitialBackoffSeconds"/>,
/// doubling after each failed attempt, capped at
/// <see cref="WatchdogConfig.ReconnectMaxBackoffSeconds"/>.
/// </para>
/// <para>
/// <b>Clocks.</b> The caller's <c>now</c> stays authoritative - it is the only way the
/// tests can drive the policy, and it is what the watchdog loop already has to hand -
/// but a wall clock is not a reliable elapsed-time source on a laptop that suspends
/// nightly and gets NTP-corrected, so every marker also carries a reading from an
/// <see cref="IMonotonicClock"/> and the two are cross-checked:
/// <list type="bullet">
///   <item><description>
///     <b>Backwards steps</b> (NTP correction, DST/timezone change, hand-set clock)
///     would otherwise produce a negative - or merely too small - elapsed value, making
///     every "&lt; grace" / "&lt; backoff" comparison true and stalling the watchdog for
///     the entire duration of the jump. Instead the marker's wall stamp is rebuilt from
///     the monotonic reading, which cannot be stepped, so the wait carries on from where
///     it really is. A backward jump must never be able to stall recovery.
///   </description></item>
///   <item><description>
///     <b>Suspend/resume</b> collapses the grace window: the machine sleeps for eight
///     hours mid-grace and on resume <c>now - disconnectedSince</c> is instantly eight
///     hours, so the watchdog fires at the worst possible moment - the network stack is
///     still coming up and FortiClient's own recovery has not had one second of working
///     network to try. So an interval in which we did not evaluate at all (see
///     <see cref="MaxObservationGap"/>) does not count as time FortiClient was given:
///     the grace window restarts. See rule 6 for the exact scope of that restart.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// This type never handles, stores or logs credentials, and never expresses a
/// "disconnect" decision - the watchdog may bring the tunnel up, never take it down.
/// </para>
/// </summary>
public sealed class ReconnectPolicy : IReconnectPolicy
{
    /// <summary>
    /// Longest gap between two consecutive <see cref="Evaluate"/> calls that can still
    /// be treated as continuous observation. The watchdog polls every couple of seconds
    /// (<see cref="WatchdogConfig.PollIntervalMs"/> defaults to 2000), so a gap of
    /// minutes does not mean "we watched and nothing happened", it means we were not
    /// watching at all - the machine was suspended or hibernating, the loop was wedged,
    /// or the wall clock leapt forward. Generous on purpose: the cost of a false
    /// positive is one extra grace period of delay, so we only act on gaps that are
    /// orders of magnitude beyond any scheduling hiccup.
    /// </summary>
    private static readonly TimeSpan MaxObservationGap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far a wall-clock delta may fall behind the monotonic delta over the same
    /// interval before we call it a backward step rather than ordinary drift. Real
    /// drift between the performance counter and the system clock is milliseconds per
    /// hour; anything past this is a correction that was applied to us.
    /// </summary>
    private static readonly TimeSpan BackwardStepTolerance = TimeSpan.FromSeconds(5);

    private readonly bool _autoReconnectEnabled;
    private readonly TimeSpan _gracePeriod;
    private readonly int _maxAttempts;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly IMonotonicClock _monotonicClock;

    /// <summary>Guards the mutable decision state. Held only for O(1) arithmetic - never across I/O.</summary>
    private readonly object _gate = new();

    private int _attemptCount;
    private Instant? _lastAttempt;
    private Instant? _disconnectedSince;

    /// <summary>
    /// The last moment we looked at the world, on both clocks. Only used to tell
    /// "we watched for five minutes and nothing changed" apart from "we were not
    /// running for five minutes" - see <see cref="MaxObservationGap"/>.
    /// </summary>
    private Instant? _lastEvaluation;

    /// <summary>
    /// True once the grace period has actually been served for the CURRENT outage.
    /// Cleared with <see cref="_disconnectedSince"/>, always as a pair.
    /// <para>
    /// It exists so the grace obligation is discharged exactly once per outage: under a
    /// sane clock it changes nothing (an elapsed grace period can never un-elapse while
    /// time moves forward), but it stops a later clock anomaly from re-opening a window
    /// that was already honoured and postponing recovery again and again.
    /// </para>
    /// </summary>
    private bool _graceServed;

    private TimeSpan _currentBackoff;

    /// <summary>
    /// Single-flight latch, manipulated only via <see cref="Interlocked"/> so that
    /// <see cref="BeginAttempt"/> is genuinely race-free across threads.
    /// 0 = idle, 1 = an attempt is in flight.
    /// </summary>
    private int _inFlight;

    public ReconnectPolicy(WatchdogConfig config)
        : this(config, SystemMonotonicClock.Instance)
    {
    }

    /// <summary>
    /// Test/diagnostic seam: takes the elapsed-time source explicitly. Production code
    /// uses the single-argument constructor and gets <see cref="SystemMonotonicClock"/>.
    /// </summary>
    public ReconnectPolicy(WatchdogConfig config, IMonotonicClock monotonicClock)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(monotonicClock);

        _autoReconnectEnabled = config.AutoReconnectEnabled;
        _monotonicClock = monotonicClock;

        // Defensive clamping: a nonsensical config must degrade to "wait longer /
        // try less", never to "hammer the tunnel".
        _gracePeriod = TimeSpan.FromSeconds(Math.Max(0, config.ReconnectGracePeriodSeconds));
        _maxAttempts = Math.Max(0, config.ReconnectMaxAttempts);

        var initialSeconds = Math.Max(0, config.ReconnectInitialBackoffSeconds);
        var maxSeconds = Math.Max(initialSeconds, Math.Max(0, config.ReconnectMaxBackoffSeconds));

        _initialBackoff = TimeSpan.FromSeconds(initialSeconds);
        _maxBackoff = TimeSpan.FromSeconds(maxSeconds);
        _currentBackoff = _initialBackoff;
    }

    /// <inheritdoc />
    public int AttemptCount
    {
        get { lock (_gate) { return _attemptCount; } }
    }

    /// <inheritdoc />
    public DateTimeOffset? LastAttemptAt
    {
        get { lock (_gate) { return _lastAttempt?.Wall; } }
    }

    /// <summary>
    /// The backoff that must elapse after the most recent failed attempt before
    /// another one is allowed. Exposed for diagnostics/tests only.
    /// </summary>
    public TimeSpan CurrentBackoff
    {
        get { lock (_gate) { return _currentBackoff; } }
    }

    /// <summary>
    /// When the current outage was first observed with the internet up, or null if
    /// there is no outage in progress. Exposed for diagnostics/tests only.
    /// </summary>
    public DateTimeOffset? DisconnectedSince
    {
        get { lock (_gate) { return _disconnectedSince?.Wall; } }
    }

    /// <summary>True while an attempt started by <see cref="BeginAttempt"/> has not yet been ended.</summary>
    public bool IsAttemptInFlight => Volatile.Read(ref _inFlight) != 0;

    /// <inheritdoc />
    public ReconnectDecision Evaluate(VpnState vpnState, InternetState internetState, DateTimeOffset now)
    {
        // Read the monotonic clock once, before taking the lock, and pair it with the
        // caller's wall-clock stamp: both readings must describe the same instant for
        // the cross-checks below to mean anything.
        var here = new Instant(now, _monotonicClock.Elapsed);

        lock (_gate)
        {
            // Was the interval since our previous look actually observed? Computed
            // before _lastEvaluation is overwritten, and used only by rule 6.
            var unobservedGap = _lastEvaluation is { } previous && here.Wall - previous.Wall > MaxObservationGap;
            _lastEvaluation = here;

            // 1. Healthy: nothing to do, and the outage bookkeeping is cleared so
            //    the next drop starts from a clean slate.
            if (vpnState == VpnState.Connected)
            {
                ResetCore();
                return ReconnectDecision.Connected;
            }

            // 2. The user's opt-in switch outranks every recovery rule below.
            if (!_autoReconnectEnabled)
            {
                return ReconnectDecision.DisabledByUser;
            }

            // 3. No internet: reconnecting cannot help. The grace clock is cleared
            //    rather than left running, so time spent with no internet can never
            //    be counted as FortiClient's chance to self-heal. When connectivity
            //    returns the full grace period is served again from scratch -
            //    deliberately the conservative choice, since firing the instant the
            //    network comes back is exactly the race this rule exists to prevent.
            //    (It is also the safety net after a resume: the network stack is
            //    rarely up on the first poll, and one such poll re-arms the full
            //    grace window.) Attempt count and backoff are NOT cleared: the
            //    attempt budget belongs to the outage, not to the grace window.
            if (internetState != InternetState.Up)
            {
                ClearGraceWindow();
                return ReconnectDecision.NoInternet;
            }

            // 4. Single-flight: never allow two reconnects at once.
            if (Volatile.Read(ref _inFlight) != 0)
            {
                return ReconnectDecision.AlreadyInFlight;
            }

            // 5. First sighting of this outage with internet up - start the clock
            //    and give FortiClient's own recovery the first move.
            if (_disconnectedSince is null)
            {
                StartGraceWindow(here);
                return ReconnectDecision.WaitingForSelfHeal;
            }

            // 6. Still inside the grace period: FortiClient self-heals in ~6-70s,
            //    so intervening now would race its own recovery.
            if (!_graceServed)
            {
                // A gap in which we did not evaluate at all is not a gap in which
                // FortiClient was given a chance: we never saw the internet up for
                // any of it. The laptop suspending mid-grace is the everyday case -
                // on resume the raw wall-clock difference is hours, which would fire
                // a reconnect at the exact instant the network stack is coming back
                // and FortiClient's own recovery has had no working network at all.
                // So the window restarts rather than being counted as served.
                if (unobservedGap)
                {
                    StartGraceWindow(here);
                    return ReconnectDecision.WaitingForSelfHeal;
                }

                var (sinceDisconnect, graceMarker) = ElapsedSince(_disconnectedSince.Value, here);
                _disconnectedSince = graceMarker; // unchanged unless the clock stepped backwards

                if (sinceDisconnect < _gracePeriod)
                {
                    return ReconnectDecision.WaitingForSelfHeal;
                }

                _graceServed = true;
            }

            // 7. Attempt budget exhausted for this outage.
            if (_attemptCount >= _maxAttempts)
            {
                return ReconnectDecision.GaveUp;
            }

            // 8. Exponential backoff after a previous attempt. Note the deliberate
            //    asymmetry with rule 6: an unobserved gap does NOT restart the
            //    backoff. The backoff exists to space out OUR attempts, and if hours
            //    have passed since the last one the tunnel really has been down for
            //    all of them - making the user wait another backoff on top would just
            //    delay the recovery they are waiting for.
            if (_lastAttempt is { } lastAttempt)
            {
                var (sinceAttempt, attemptMarker) = ElapsedSince(lastAttempt, here);
                _lastAttempt = attemptMarker; // unchanged unless the clock stepped backwards

                if (sinceAttempt < _currentBackoff)
                {
                    return ReconnectDecision.InBackoff;
                }
            }

            // 9. Grace elapsed, budget left, not backing off, nothing in flight.
            return ReconnectDecision.Triggered;
        }
    }

    /// <inheritdoc />
    public void RecordAttemptResult(bool succeeded, DateTimeOffset now)
    {
        var here = new Instant(now, _monotonicClock.Elapsed);

        lock (_gate)
        {
            _lastAttempt = here;

            if (succeeded)
            {
                // Tunnel is back: forget the whole outage.
                ResetCore();
                return;
            }

            _attemptCount++;

            // The wait served after failure N is initial * 2^(N-1): the FIRST
            // failure waits the configured initial backoff, and only the second
            // and later failures double. Doubling on the first failure would
            // silently make the real initial wait twice what the config says.
            _currentBackoff = _attemptCount <= 1
                ? _initialBackoff
                : DoubleCapped(_currentBackoff);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            ResetCore();
        }
    }

    /// <summary>
    /// Atomically claims the single-flight slot. Returns false (claiming nothing)
    /// if an attempt is already running. Used by the coordinator so that a slow
    /// COM reconnect can never be overlapped by the next poll tick.
    /// Always pair a <c>true</c> result with <see cref="EndAttempt"/> in a finally block.
    /// </summary>
    public bool BeginAttempt() => Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0;

    /// <summary>Releases the single-flight slot. Idempotent and safe to call from a finally block.</summary>
    public void EndAttempt() => Interlocked.Exchange(ref _inFlight, 0);

    /// <summary>Clears all outage state. Caller must hold <see cref="_gate"/>.</summary>
    private void ResetCore()
    {
        _attemptCount = 0;
        _lastAttempt = null;
        ClearGraceWindow();
        _currentBackoff = _initialBackoff;
    }

    /// <summary>
    /// Starts (or restarts) the grace window at <paramref name="at"/>. Caller must hold
    /// <see cref="_gate"/>. The marker and the "already served" flag are only ever set
    /// together - a restarted window that kept the flag would skip the grace entirely.
    /// </summary>
    private void StartGraceWindow(Instant at)
    {
        _disconnectedSince = at;
        _graceServed = false;
    }

    /// <summary>Ends the grace window: no outage is being timed. Caller must hold <see cref="_gate"/>.</summary>
    private void ClearGraceWindow()
    {
        _disconnectedSince = null;
        _graceServed = false;
    }

    /// <summary>
    /// Elapsed time from <paramref name="marker"/> to <paramref name="here"/>, together
    /// with the marker to keep (normally the one passed in).
    /// <para>
    /// A wall-clock delta that has gone negative, or that has fallen measurably behind
    /// the monotonic delta over the same interval, means the wall clock was stepped
    /// BACKWARDS under us - an NTP correction, a DST/timezone change, someone setting
    /// the clock. Subtracting two such timestamps yields a negative (or simply
    /// understated) TimeSpan, every "&lt; grace" / "&lt; backoff" comparison then stays
    /// true, and the watchdog waits out the whole jump doing nothing: a backward step
    /// must never be able to stall recovery. So the marker's WALL stamp is rebuilt from
    /// the monotonic reading - the one thing that cannot be stepped - and the wait
    /// carries on from where it really is, neither stalled nor silently restarted.
    /// </para>
    /// </summary>
    private static (TimeSpan Elapsed, Instant Marker) ElapsedSince(Instant marker, Instant here)
    {
        var wall = here.Wall - marker.Wall;
        var monotonic = here.Monotonic - marker.Monotonic;

        // Defensive: a monotonic source is contractually non-decreasing, but this
        // policy must degrade to "wait", never to "hammer", if a substitute misbehaves.
        if (monotonic < TimeSpan.Zero)
        {
            monotonic = TimeSpan.Zero;
        }

        if (wall < TimeSpan.Zero || wall < monotonic - BackwardStepTolerance)
        {
            return (monotonic, new Instant(here.Wall - monotonic, marker.Monotonic));
        }

        return (wall, marker);
    }

    /// <summary>Doubles a backoff, saturating at the configured maximum (no overflow).</summary>
    private TimeSpan DoubleCapped(TimeSpan current)
    {
        if (current >= _maxBackoff)
        {
            return _maxBackoff;
        }

        // Compare in ticks against the cap before doubling, so the arithmetic can
        // never overflow even after an implausible number of failures.
        var halfCapTicks = _maxBackoff.Ticks / 2;
        return current.Ticks >= halfCapTicks
            ? _maxBackoff
            : TimeSpan.FromTicks(current.Ticks * 2);
    }

    /// <summary>
    /// One instant recorded from BOTH clocks: the wall-clock stamp the caller supplied
    /// (what diagnostics and the public properties report, and what the decision rules
    /// measure with) and the monotonic reading taken at the same moment (what proves
    /// whether that wall-clock stamp can still be trusted).
    /// </summary>
    private readonly record struct Instant(DateTimeOffset Wall, TimeSpan Monotonic);
}
