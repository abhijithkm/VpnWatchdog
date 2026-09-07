using VpnWatchdog.Core;
using VpnWatchdog.Core.Reconnect;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// Drives the real <see cref="ReconnectPolicy"/> through the clock anomalies a laptop
/// produces over a multi-day run: the wall clock being stepped BACKWARDS (an NTP
/// correction, a hand-set clock) and leaping FORWARDS across a suspend/resume. Every
/// duration the policy honours - the grace period, the backoff - must keep meaning
/// "this much real time", whatever the wall clock did in between: a backward step must
/// never stall recovery, and a forward leap must never collapse the grace period.
///
/// <para>
/// The policy pairs the caller's wall-clock <c>now</c> with a reading from an
/// <see cref="IMonotonicClock"/> and cross-checks the two, so these tests use its
/// documented seam - the constructor that takes the clock - to hand it a monotonic
/// clock the test advances by hand. That is what makes "real time passed" and "the wall
/// clock was stepped" two separately controllable events; everything else is the
/// production policy, unchanged. One test goes through the single-argument constructor
/// and the production <see cref="SystemMonotonicClock"/> as well, so the recovery is
/// shown not to depend on the fake.
/// </para>
///
/// <para>
/// Every wall-clock value is an explicit <see cref="DateTimeOffset"/>, never
/// <c>DateTimeOffset.Now</c>. Nothing here touches COM, FortiClient, the network or a
/// real VPN.
/// </para>
/// </summary>
public class ClockRobustnessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 3, 0, 0, TimeSpan.FromHours(-4));

    // ------------------------------------------------------------------
    // Config builder - the same shape as ReconnectPolicyTests, and like there
    // AutoReconnectEnabled is opted into explicitly because WatchdogConfig.Default
    // has it off. Nothing in this file may pass without that opt-in.
    // ------------------------------------------------------------------

    private static WatchdogConfig EnabledConfig(
        int gracePeriodSeconds = 90,
        int maxAttempts = 5,
        int initialBackoffSeconds = 30,
        int maxBackoffSeconds = 300) =>
        WatchdogConfig.Default with
        {
            AutoReconnectEnabled = true,
            ReconnectGracePeriodSeconds = gracePeriodSeconds,
            ReconnectMaxAttempts = maxAttempts,
            ReconnectInitialBackoffSeconds = initialBackoffSeconds,
            ReconnectMaxBackoffSeconds = maxBackoffSeconds,
        };

    /// <summary>The normal outage shape: VPN down, underlying internet healthy.</summary>
    private static ReconnectDecision EvaluateDuringOutage(ReconnectPolicy sut, DateTimeOffset now) =>
        sut.Evaluate(VpnState.Disconnected, InternetState.Up, now);

    // ------------------------------------------------------------------
    // Test doubles for the two clocks a real machine has.
    // ------------------------------------------------------------------

    /// <summary>
    /// A monotonic clock the test advances by hand, standing in for the Stopwatch-backed
    /// production clock. Starts from an arbitrary non-zero origin on purpose: the contract
    /// says only DIFFERENCES between readings are meaningful, so nothing may depend on the
    /// first reading being zero.
    /// </summary>
    private sealed class FakeMonotonicClock : IMonotonicClock
    {
        public TimeSpan Elapsed { get; private set; } = TimeSpan.FromHours(37);

        public void Advance(TimeSpan by) => Elapsed += by;
    }

    /// <summary>
    /// The wall clock and the monotonic clock driven together, so each test reads as a
    /// sequence of things that happened to the machine rather than as arithmetic on two
    /// counters that happen to agree.
    /// </summary>
    private sealed class Machine
    {
        public FakeMonotonicClock MonotonicClock { get; } = new();

        /// <summary>What the wall clock reads right now - the <c>now</c> the watchdog loop would hand the policy.</summary>
        public DateTimeOffset Now { get; private set; } = T0;

        /// <summary>Real time passes with the machine running: both clocks advance in lockstep.</summary>
        public void Run(TimeSpan duration)
        {
            MonotonicClock.Advance(duration);
            Now += duration;
        }

        /// <summary>
        /// The wall clock is stepped - an NTP correction, someone setting the clock. No real
        /// time passes and the monotonic clock does not move: that is the whole point of
        /// having one.
        /// </summary>
        public void StepWallClock(TimeSpan by) => Now += by;

        /// <summary>
        /// The machine suspends for <paramref name="duration"/>: nothing runs, so nothing
        /// evaluates. On resume the wall clock always covers the whole interval. Whether the
        /// monotonic source does too depends on which counter backs it (Windows exposes both
        /// kinds), so tests that suspend run both ways - the documented outcome must not
        /// hinge on that detail.
        /// </summary>
        public void Suspend(TimeSpan duration, bool monotonicClockCountsSuspendedTime)
        {
            Now += duration;
            if (monotonicClockCountsSuspendedTime)
            {
                MonotonicClock.Advance(duration);
            }
        }
    }

    /// <summary>
    /// Drives a fresh policy on <paramref name="machine"/> through a new outage to its first
    /// trigger and records that attempt as FAILED, so the backoff is running. Returns the
    /// wall-clock time of the attempt.
    /// </summary>
    private static DateTimeOffset FailFirstAttempt(ReconnectPolicy sut, Machine machine, WatchdogConfig config)
    {
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));

        machine.Run(TimeSpan.FromSeconds(config.ReconnectGracePeriodSeconds + 1));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, machine.Now));

        var attemptAt = machine.Now;
        sut.RecordAttemptResult(false, attemptAt);
        Assert.Equal(1, sut.AttemptCount);
        Assert.Equal(attemptAt, sut.LastAttemptAt);

        return attemptAt;
    }

    // ==================================================================
    // 1. BACKWARD STEP AFTER A FAILED ATTEMPT.
    //    Naive "now - LastAttemptAt" goes negative, every "< backoff" comparison
    //    stays true, and the watchdog sits in InBackoff until the wall clock has
    //    caught up with where it was - the entire duration of the jump. The
    //    policy documents the opposite: "the marker's WALL stamp is rebuilt from
    //    the monotonic reading ... and the wait carries on from where it really
    //    is, neither stalled nor silently restarted."
    // ==================================================================

    [Fact]
    public void Evaluate_AfterFailedAttempt_WhenWallClockStepsBackwards_KeepsTheBackoffRunningFromWhereItReallyIs()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 30);
        var machine = new Machine();
        var sut = new ReconnectPolicy(config, machine.MonotonicClock);

        FailFirstAttempt(sut, machine, config);

        // Ten real seconds into the 30s backoff an NTP correction sets the clock back an hour.
        machine.Run(TimeSpan.FromSeconds(10));
        machine.StepWallClock(TimeSpan.FromHours(-1));

        // Still backing off - correctly, 10s of the 30s have elapsed - but the marker has been
        // re-anchored: LastAttemptAt now reads "10 seconds ago" ON THE STEPPED CLOCK, rather
        // than an hour in the future. That re-anchoring is what stops the stall.
        Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, machine.Now));
        Assert.Equal(machine.Now.AddSeconds(-10), sut.LastAttemptAt);

        // 29 real seconds after the attempt: inside the 30s, so InBackoff. A policy that had
        // RESTARTED the backoff at the step would also say InBackoff here, which is why the
        // assertion after this one is the decisive one.
        machine.Run(TimeSpan.FromSeconds(19));
        Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, machine.Now));

        // 31 real seconds after the attempt: the backoff has genuinely elapsed. A stalled
        // policy would stay InBackoff for another ~59 minutes; a restarted one for another
        // 9 seconds. Neither is acceptable - the tunnel has been down for all of it.
        machine.Run(TimeSpan.FromSeconds(2));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, machine.Now));
        Assert.Equal(1, sut.AttemptCount);
    }

    [Fact]
    public void Evaluate_AfterFailedAttempt_WhenWallClockStepsBackwards_WithTheProductionMonotonicClock_RecoversWithinOneBackoff()
    {
        // The same anomaly through the single-argument constructor the watchdog actually
        // uses, so the recovery is shown to hold with SystemMonotonicClock and not only with
        // the fake. Real time barely passes between two statements here, so on the stepped
        // clock the backoff is measured from the step itself: the policy can be held to
        // "fires one backoff after the step", against the stall's "fires one HOUR after".
        // A 5-minute backoff keeps every assertion true even if this test is paused for a
        // few minutes under a debugger.
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 300);
        var sut = new ReconnectPolicy(config);

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, T0));

        var attemptAt = T0.AddSeconds(91);
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, attemptAt));
        sut.RecordAttemptResult(false, attemptAt);
        Assert.Equal(attemptAt, sut.LastAttemptAt);

        var steppedNow = attemptAt.AddHours(-1);

        Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, steppedNow));

        // Re-anchored onto the stepped clock: at, or by the few microseconds this test took
        // just before, the stepped "now" - not at the attempt time an hour ahead of it.
        Assert.NotNull(sut.LastAttemptAt);
        Assert.InRange(sut.LastAttemptAt.Value, steppedNow.AddMinutes(-1), steppedNow);

        // One backoff after the step it fires. Without the re-anchoring it would have stayed
        // InBackoff until steppedNow + 1h + 300s.
        Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, steppedNow.AddSeconds(299)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, steppedNow.AddSeconds(301)));
    }

    // ==================================================================
    // 2. BACKWARD STEP INSIDE THE GRACE WINDOW.
    //    Same failure shape as above with "now - DisconnectedSince": the policy
    //    would report WaitingForSelfHeal until the wall clock caught up. The grace
    //    period protects FortiClient's own 6-70s recovery, not an hour of it.
    // ==================================================================

    [Fact]
    public void Evaluate_DuringGracePeriod_WhenWallClockStepsBackwards_StillTriggersOnceTheRealGracePeriodHasElapsed()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90);
        var machine = new Machine();
        var sut = new ReconnectPolicy(config, machine.MonotonicClock);

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));
        Assert.Equal(T0, sut.DisconnectedSince);

        machine.Run(TimeSpan.FromSeconds(60));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));

        // Sixty real seconds into the grace period the clock is set back an hour.
        machine.StepWallClock(TimeSpan.FromHours(-1));

        // Still waiting - correctly, 60s of the 90s have been served - and the marker now
        // reads "60 seconds ago" on the stepped clock: rebuilt from the monotonic reading.
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));
        Assert.Equal(machine.Now.AddSeconds(-60), sut.DisconnectedSince);

        // 89 real seconds since the drop: still inside the grace period.
        machine.Run(TimeSpan.FromSeconds(29));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));

        // 91 real seconds since the drop: FortiClient has had its full chance. A stalled
        // policy would wait another ~59 minutes here; one that restarted the window at the
        // step would wait another 59 seconds. It must do neither.
        machine.Run(TimeSpan.FromSeconds(2));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, machine.Now));
    }

    [Fact]
    public void Evaluate_AfterGracePeriodServed_WhenWallClockStepsBackwards_DoesNotReopenTheGracePeriod()
    {
        // The grace obligation is discharged once per outage. A clock anomaly arriving after
        // it has been served must not put the policy back into WaitingForSelfHeal - that would
        // be a second, unearned grace period on top of the first.
        var config = EnabledConfig(gracePeriodSeconds: 90);
        var machine = new Machine();
        var sut = new ReconnectPolicy(config, machine.MonotonicClock);

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));
        machine.Run(TimeSpan.FromSeconds(91));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, machine.Now));

        machine.StepWallClock(TimeSpan.FromHours(-1));

        // Nothing was attempted yet and the grace period has been served, so the only
        // acceptable answer is still Triggered.
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, machine.Now));
        Assert.Equal(0, sut.AttemptCount);
    }

    // ==================================================================
    // 3. LARGE FORWARD JUMP INSIDE THE GRACE WINDOW - the laptop resumes after
    //    eight hours asleep.
    //
    //    The policy does NOT protect the grace window with monotonic elapsed time
    //    here (a monotonic clock that counts suspended time would agree with the
    //    wall clock that eight hours passed). It documents a different rule:
    //    "an interval in which we did not evaluate at all (see MaxObservationGap)
    //    does not count as time FortiClient was given: the grace window restarts."
    //    MaxObservationGap is five minutes between consecutive evaluations. So the
    //    behaviour asserted below is a RESTART of the full grace period at resume -
    //    not a clamp, and not "resume from the 30s already served".
    // ==================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_DuringGracePeriod_AfterAnEightHourSuspend_RestartsTheGracePeriodOnResumeInsteadOfFiring(
        bool monotonicClockCountsSuspendedTime)
    {
        var config = EnabledConfig(gracePeriodSeconds: 90);
        var machine = new Machine();
        var sut = new ReconnectPolicy(config, machine.MonotonicClock);

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));
        machine.Run(TimeSpan.FromSeconds(30));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));

        // Lid closed 30s into the grace period; opened again eight hours later.
        machine.Suspend(TimeSpan.FromHours(8), monotonicClockCountsSuspendedTime);
        var resumedAt = machine.Now;

        // Raw subtraction says the outage is eight hours old and fires now - at the exact
        // moment the network stack is coming back and FortiClient's own recovery has had no
        // working network at all. The documented behaviour is that the window restarts here.
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, resumedAt));
        Assert.Equal(resumedAt, sut.DisconnectedSince);

        // Restarted in FULL: the 30s served before the suspend are not carried over. Had they
        // been, 61s after resume would total 91s and fire.
        machine.Run(TimeSpan.FromSeconds(61));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));

        // 89s since resume: still inside the fresh window.
        machine.Run(TimeSpan.FromSeconds(28));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, machine.Now));

        // 91s of working network since resume: FortiClient has had its chance, now we step in.
        machine.Run(TimeSpan.FromSeconds(2));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, machine.Now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_AfterFailedAttempt_AfterAnEightHourSuspend_DoesNotRestartTheBackoff_TriggersOnResume(
        bool monotonicClockCountsSuspendedTime)
    {
        // The documented asymmetry with the grace-window restart above: "an unobserved gap
        // does NOT restart the backoff. The backoff exists to space out OUR attempts, and if
        // hours have passed since the last one the tunnel really has been down for all of
        // them". So with the internet up on resume the policy fires at once, rather than
        // making the user sit through another backoff - or another grace period - first.
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 30);
        var machine = new Machine();
        var sut = new ReconnectPolicy(config, machine.MonotonicClock);

        FailFirstAttempt(sut, machine, config);

        machine.Run(TimeSpan.FromSeconds(5));
        Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, machine.Now));

        machine.Suspend(TimeSpan.FromHours(8), monotonicClockCountsSuspendedTime);

        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, machine.Now));
        Assert.Equal(1, sut.AttemptCount);
    }
}
