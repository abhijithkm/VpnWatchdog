using VpnWatchdog.Core;
using VpnWatchdog.Core.Reconnect;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// Unit tests that drive the real <see cref="ReconnectPolicy"/> directly through
/// explicit <see cref="DateTimeOffset"/> values - never <c>DateTimeOffset.Now</c> -
/// so every rule (explicit opt-in, internet precondition, grace period, backoff,
/// attempt budget, single-flight) is fully deterministic.
///
/// Nothing here touches COM, FortiClient, a real VPN, the network or the clock:
/// the policy is pure decision logic, which is exactly why these safety-critical
/// rules can be pinned down this precisely.
/// </summary>
public class ReconnectPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 9, 0, 0, TimeSpan.FromHours(-4));

    // ------------------------------------------------------------------
    // Config builders - every rule under test is set explicitly rather than
    // relying on the defaults, so each test's intent is visible inline.
    // The one thing deliberately NOT defaulted anywhere is AutoReconnectEnabled:
    // WatchdogConfig.Default has it false, and tests that want reconnect
    // behaviour must opt in, exactly like a real user must.
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

    private static WatchdogConfig DisabledConfig(int gracePeriodSeconds = 90) =>
        WatchdogConfig.Default with
        {
            AutoReconnectEnabled = false,
            ReconnectGracePeriodSeconds = gracePeriodSeconds,
            ReconnectMaxAttempts = 5,
            ReconnectInitialBackoffSeconds = 30,
            ReconnectMaxBackoffSeconds = 300,
        };

    /// <summary>The normal outage shape: VPN down, underlying internet healthy.</summary>
    private static ReconnectDecision EvaluateDuringOutage(ReconnectPolicy sut, DateTimeOffset now) =>
        sut.Evaluate(VpnState.Disconnected, InternetState.Up, now);

    /// <summary>
    /// Drives a fresh policy through a new outage up to the first moment it
    /// triggers: one evaluation at the start of the outage (which must be
    /// WaitingForSelfHeal, so FortiClient's own recovery gets its chance) and
    /// one just after the grace period has elapsed.
    /// </summary>
    private static (ReconnectPolicy Sut, DateTimeOffset TriggeredAt) PolicyAtFirstTrigger(
        WatchdogConfig config, DateTimeOffset outageStart)
    {
        var sut = new ReconnectPolicy(config);

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, outageStart));

        var triggeredAt = outageStart.AddSeconds(config.ReconnectGracePeriodSeconds + 1);
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, triggeredAt));

        return (sut, triggeredAt);
    }

    // ------------------------------------------------------------------
    // 1. Connected always wins - there is nothing to do when the tunnel is up.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(InternetState.Up)]
    [InlineData(InternetState.Down)]
    [InlineData(InternetState.Unknown)]
    public void Evaluate_WhenVpnConnected_ReturnsConnected_RegardlessOfInternetState(InternetState internetState)
    {
        var sut = new ReconnectPolicy(EnabledConfig());

        Assert.Equal(ReconnectDecision.Connected, sut.Evaluate(VpnState.Connected, internetState, T0));
    }

    [Fact]
    public void Evaluate_WhenVpnConnected_ReturnsConnected_EvenAfterFailedAttempts()
    {
        var config = EnabledConfig(maxAttempts: 2, initialBackoffSeconds: 30);
        var (sut, triggeredAt) = PolicyAtFirstTrigger(config, T0);

        sut.RecordAttemptResult(false, triggeredAt);
        sut.RecordAttemptResult(false, triggeredAt.AddMinutes(10)); // budget now exhausted

        Assert.Equal(
            ReconnectDecision.Connected,
            sut.Evaluate(VpnState.Connected, InternetState.Up, triggeredAt.AddMinutes(20)));
    }

    // ------------------------------------------------------------------
    // 2. The explicit opt-in gate: with AutoReconnectEnabled == false the policy
    //    must never do anything, however bad the outage looks.
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_WhenAutoReconnectDisabled_ReturnsDisabledByUser_EvenWhenDisconnectedWithInternetUp()
    {
        var sut = new ReconnectPolicy(DisabledConfig(gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateDuringOutage(sut, T0));
    }

    [Fact]
    public void Evaluate_WhenAutoReconnectDisabled_NeverTriggers_HoweverLongTheOutageLasts()
    {
        var sut = new ReconnectPolicy(DisabledConfig(gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateDuringOutage(sut, T0));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateDuringOutage(sut, T0.AddSeconds(91)));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateDuringOutage(sut, T0.AddHours(6)));
        Assert.Equal(0, sut.AttemptCount);
        Assert.Null(sut.LastAttemptAt);
    }

    [Fact]
    public void Evaluate_WithDefaultConfig_ReturnsDisabledByUser_BecauseAutoReconnectIsOffByDefault()
    {
        // Guards the single most important safety default: an unconfigured
        // watchdog must be observe-only.
        Assert.False(WatchdogConfig.Default.AutoReconnectEnabled);

        var sut = new ReconnectPolicy(WatchdogConfig.Default);

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateDuringOutage(sut, T0));
    }

    // ------------------------------------------------------------------
    // 3. Internet precondition - reconnecting cannot help while the underlying
    //    network is down (or unknown), and crucially the grace-period clock must
    //    not run during that time.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(InternetState.Down)]
    [InlineData(InternetState.Unknown)]
    public void Evaluate_WhenInternetNotUp_ReturnsNoInternet(InternetState internetState)
    {
        var sut = new ReconnectPolicy(EnabledConfig(gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.NoInternet, sut.Evaluate(VpnState.Disconnected, internetState, T0));
    }

    [Fact]
    public void Evaluate_WhenInternetDown_DoesNotStartGracePeriodClock()
    {
        // The whole point of the grace period is to give FortiClient's own
        // recovery a fair chance once it CAN work. Time spent with no internet
        // is not such a chance, so it must not be counted: after internet comes
        // back the full 90s must still be honoured before we intervene.
        var config = EnabledConfig(gracePeriodSeconds: 90);
        var sut = new ReconnectPolicy(config);

        // Internet down for ten minutes - far longer than the grace period.
        Assert.Equal(ReconnectDecision.NoInternet, sut.Evaluate(VpnState.Disconnected, InternetState.Down, T0));
        Assert.Equal(ReconnectDecision.NoInternet, sut.Evaluate(VpnState.Disconnected, InternetState.Down, T0.AddMinutes(5)));
        Assert.Equal(ReconnectDecision.NoInternet, sut.Evaluate(VpnState.Disconnected, InternetState.Down, T0.AddMinutes(10)));

        var internetBackAt = T0.AddMinutes(10).AddSeconds(2);

        // The grace period starts now, not ten minutes ago.
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, internetBackAt));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, internetBackAt.AddSeconds(89)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, internetBackAt.AddSeconds(91)));
    }

    [Fact]
    public void Evaluate_WhenInternetDropsMidGracePeriod_DoesNotTriggerImmediatelyWhenInternetReturns()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90);
        var sut = new ReconnectPolicy(config);

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, T0));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, T0.AddSeconds(60)));

        // Internet drops away 60s in, then returns five minutes later.
        Assert.Equal(ReconnectDecision.NoInternet, sut.Evaluate(VpnState.Disconnected, InternetState.Down, T0.AddSeconds(61)));
        Assert.Equal(ReconnectDecision.NoInternet, sut.Evaluate(VpnState.Disconnected, InternetState.Down, T0.AddMinutes(5)));

        var internetBackAt = T0.AddMinutes(6);

        // The five minutes with no internet were never FortiClient's chance to
        // self-heal, so they must not count towards the grace period: firing the
        // instant connectivity returns is exactly the race this rule prevents.
        // (Whether the remaining grace restarts in full or resumes from the 60s
        // already served is an implementation choice - both are asserted below.)
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, internetBackAt));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, internetBackAt.AddSeconds(29)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, internetBackAt.AddSeconds(91)));
    }

    // ------------------------------------------------------------------
    // 4. Grace period - never race FortiClient's own recovery (measured healing
    //    in 6-70 seconds in the observed data).
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_FirstEvaluationOfNewOutage_ReturnsWaitingForSelfHeal()
    {
        var sut = new ReconnectPolicy(EnabledConfig(gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, T0));
        Assert.Equal(0, sut.AttemptCount);
    }

    [Fact]
    public void Evaluate_JustBeforeGracePeriodElapses_StillReturnsWaitingForSelfHeal_ThenTriggersJustAfter()
    {
        var sut = new ReconnectPolicy(EnabledConfig(gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, T0));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, T0.AddSeconds(89)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, T0.AddSeconds(91)));
    }

    [Fact]
    public void Evaluate_WithLongerConfiguredGracePeriod_HonoursTheConfiguredValue()
    {
        // Proves the boundary tracks configuration rather than a hardcoded 90s.
        var sut = new ReconnectPolicy(EnabledConfig(gracePeriodSeconds: 240));

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, T0));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, T0.AddSeconds(239)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, T0.AddSeconds(241)));
    }

    // ------------------------------------------------------------------
    // 5. Backoff after a failed attempt.
    // ------------------------------------------------------------------

    [Fact]
    public void RecordAttemptResult_AfterFailure_NextImmediateEvaluationReturnsInBackoff()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 30);
        var (sut, triggeredAt) = PolicyAtFirstTrigger(config, T0);

        sut.RecordAttemptResult(false, triggeredAt);

        Assert.Equal(1, sut.AttemptCount);
        Assert.Equal(triggeredAt, sut.LastAttemptAt);
        Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, triggeredAt));
        Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, triggeredAt.AddSeconds(1)));
    }

    [Fact]
    public void Evaluate_AfterInitialBackoffElapses_ReturnsTriggeredAgain()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 30);
        var (sut, triggeredAt) = PolicyAtFirstTrigger(config, T0);

        sut.RecordAttemptResult(false, triggeredAt);

        Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, triggeredAt.AddSeconds(29)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, triggeredAt.AddSeconds(31)));
    }

    [Fact]
    public void Backoff_GrowsExponentiallyAcrossConsecutiveFailures()
    {
        // initial 10s, doubling each consecutive failure, with the cap set far
        // out of the way so growth alone is under test.
        var config = EnabledConfig(
            gracePeriodSeconds: 90, maxAttempts: 10, initialBackoffSeconds: 10, maxBackoffSeconds: 3600);
        var (sut, attemptAt) = PolicyAtFirstTrigger(config, T0);

        var expectedBackoffSeconds = new[] { 10, 20, 40, 80, 160 };
        var expectedAttemptCount = 0;

        foreach (var backoff in expectedBackoffSeconds)
        {
            sut.RecordAttemptResult(false, attemptAt);
            expectedAttemptCount++;

            Assert.Equal(expectedAttemptCount, sut.AttemptCount);
            Assert.Equal(attemptAt, sut.LastAttemptAt);
            Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, attemptAt.AddSeconds(backoff - 1)));

            attemptAt = attemptAt.AddSeconds(backoff + 1);
            Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, attemptAt));
        }
    }

    [Fact]
    public void Backoff_IsCappedAtReconnectMaxBackoffSeconds()
    {
        // initial 30s doubling would be 30, 60, 120, 240, 480 - the 120s cap
        // must clamp everything from the third failure onwards.
        var config = EnabledConfig(
            gracePeriodSeconds: 90, maxAttempts: 10, initialBackoffSeconds: 30, maxBackoffSeconds: 120);
        var (sut, attemptAt) = PolicyAtFirstTrigger(config, T0);

        var expectedBackoffSeconds = new[] { 30, 60, 120, 120, 120 };

        foreach (var backoff in expectedBackoffSeconds)
        {
            sut.RecordAttemptResult(false, attemptAt);

            Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, attemptAt.AddSeconds(backoff - 1)));

            attemptAt = attemptAt.AddSeconds(backoff + 1);
            Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, attemptAt));
        }
    }

    // ------------------------------------------------------------------
    // 6. Attempt budget - stop hammering FortiClient once the budget is spent.
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_AfterMaxAttemptsFailures_ReturnsGaveUp_AndStaysGaveUp()
    {
        var config = EnabledConfig(
            gracePeriodSeconds: 90, maxAttempts: 3, initialBackoffSeconds: 30, maxBackoffSeconds: 300);
        var (sut, attemptAt) = PolicyAtFirstTrigger(config, T0);

        sut.RecordAttemptResult(false, attemptAt);
        Assert.Equal(1, sut.AttemptCount);
        // Budget still available, so once the backoff is over it triggers again.
        attemptAt = attemptAt.AddMinutes(10);
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, attemptAt));

        sut.RecordAttemptResult(false, attemptAt);
        Assert.Equal(2, sut.AttemptCount);
        attemptAt = attemptAt.AddMinutes(10);
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, attemptAt));

        sut.RecordAttemptResult(false, attemptAt);
        Assert.Equal(3, sut.AttemptCount);

        // Budget exhausted - no amount of further waiting may resurrect it.
        Assert.Equal(ReconnectDecision.GaveUp, EvaluateDuringOutage(sut, attemptAt.AddMinutes(10)));
        Assert.Equal(ReconnectDecision.GaveUp, EvaluateDuringOutage(sut, attemptAt.AddHours(6)));
        Assert.Equal(ReconnectDecision.GaveUp, EvaluateDuringOutage(sut, attemptAt.AddDays(2)));
        Assert.Equal(3, sut.AttemptCount);
    }

    // ------------------------------------------------------------------
    // 7. Reset paths - a success, or simply seeing the tunnel healthy again,
    //    must wipe attempt/backoff state so the next outage starts clean.
    // ------------------------------------------------------------------

    [Fact]
    public void RecordAttemptResult_WhenSucceeded_ResetsAttemptCountToZero()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 30);
        var (sut, triggeredAt) = PolicyAtFirstTrigger(config, T0);

        sut.RecordAttemptResult(false, triggeredAt);
        Assert.Equal(1, sut.AttemptCount);

        sut.RecordAttemptResult(true, triggeredAt.AddSeconds(35));

        Assert.Equal(0, sut.AttemptCount);
    }

    [Fact]
    public void RecordAttemptResult_WhenSucceeded_LaterOutageStartsAFreshGracePeriod()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 30);
        var (sut, triggeredAt) = PolicyAtFirstTrigger(config, T0);

        sut.RecordAttemptResult(false, triggeredAt);
        var succeededAt = triggeredAt.AddSeconds(35);
        sut.RecordAttemptResult(true, succeededAt);

        // A brand new outage an hour later: grace period first, not backoff.
        var secondOutageAt = succeededAt.AddHours(1);
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, secondOutageAt));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, secondOutageAt.AddSeconds(89)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, secondOutageAt.AddSeconds(91)));
    }

    [Fact]
    public void Evaluate_WhenVpnReturnsToConnected_ResetsAttemptStateForTheNextOutage()
    {
        var config = EnabledConfig(
            gracePeriodSeconds: 90, maxAttempts: 2, initialBackoffSeconds: 30, maxBackoffSeconds: 300);
        var (sut, attemptAt) = PolicyAtFirstTrigger(config, T0);

        // Burn the entire attempt budget on the first outage.
        sut.RecordAttemptResult(false, attemptAt);
        attemptAt = attemptAt.AddMinutes(10);
        sut.RecordAttemptResult(false, attemptAt);
        Assert.Equal(2, sut.AttemptCount);
        Assert.Equal(ReconnectDecision.GaveUp, EvaluateDuringOutage(sut, attemptAt.AddMinutes(10)));

        // The tunnel comes back by itself (or the user reconnects manually).
        var reconnectedAt = attemptAt.AddMinutes(20);
        Assert.Equal(ReconnectDecision.Connected, sut.Evaluate(VpnState.Connected, InternetState.Up, reconnectedAt));
        Assert.Equal(0, sut.AttemptCount);

        // A later, unrelated outage gets a fresh grace period AND a fresh budget.
        var secondOutageAt = reconnectedAt.AddHours(2);
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, secondOutageAt));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, secondOutageAt.AddSeconds(89)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, secondOutageAt.AddSeconds(91)));

        sut.RecordAttemptResult(false, secondOutageAt.AddSeconds(91));
        Assert.Equal(1, sut.AttemptCount);
    }

    [Fact]
    public void Reset_ClearsAttemptCountAndReturnsOutageToTheGracePeriod()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 30);
        var (sut, triggeredAt) = PolicyAtFirstTrigger(config, T0);

        sut.RecordAttemptResult(false, triggeredAt);
        Assert.Equal(1, sut.AttemptCount);

        sut.Reset();

        Assert.Equal(0, sut.AttemptCount);

        var laterOutageAt = triggeredAt.AddMinutes(30);
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateDuringOutage(sut, laterOutageAt));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, laterOutageAt.AddSeconds(91)));
    }

    // ------------------------------------------------------------------
    // 8. Single-flight - two concurrent reconnect attempts must never run.
    // ------------------------------------------------------------------

    [Fact]
    public void BeginAttempt_SecondConcurrentCall_ReturnsFalse_AndTrueAgainAfterEndAttempt()
    {
        var sut = new ReconnectPolicy(EnabledConfig());

        Assert.True(sut.BeginAttempt());
        Assert.False(sut.BeginAttempt());
        Assert.False(sut.BeginAttempt());

        sut.EndAttempt();

        Assert.True(sut.BeginAttempt());
        Assert.False(sut.BeginAttempt());
    }

    [Fact]
    public void BeginAttempt_UnderParallelCalls_GrantsTheSlotToExactlyOneCaller()
    {
        var sut = new ReconnectPolicy(EnabledConfig());
        var granted = 0;

        Parallel.For(0, 64, _ =>
        {
            if (sut.BeginAttempt())
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(1, granted);
    }

    [Fact]
    public void Evaluate_WhileAttemptInFlight_ReturnsAlreadyInFlight()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 30);
        var (sut, triggeredAt) = PolicyAtFirstTrigger(config, T0);

        Assert.True(sut.BeginAttempt());

        // Would otherwise be Triggered - the in-flight attempt must suppress it.
        Assert.Equal(ReconnectDecision.AlreadyInFlight, EvaluateDuringOutage(sut, triggeredAt));
        Assert.Equal(ReconnectDecision.AlreadyInFlight, EvaluateDuringOutage(sut, triggeredAt.AddSeconds(5)));
    }

    [Fact]
    public void Evaluate_AfterEndAttempt_NoLongerReturnsAlreadyInFlight()
    {
        var config = EnabledConfig(gracePeriodSeconds: 90, initialBackoffSeconds: 30);
        var (sut, triggeredAt) = PolicyAtFirstTrigger(config, T0);

        Assert.True(sut.BeginAttempt());
        Assert.Equal(ReconnectDecision.AlreadyInFlight, EvaluateDuringOutage(sut, triggeredAt));

        sut.RecordAttemptResult(false, triggeredAt.AddSeconds(4));
        sut.EndAttempt();

        Assert.Equal(ReconnectDecision.InBackoff, EvaluateDuringOutage(sut, triggeredAt.AddSeconds(5)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateDuringOutage(sut, triggeredAt.AddSeconds(40)));
    }

    // ------------------------------------------------------------------
    // 9. The policy is an IReconnectPolicy - the watchdog only ever sees the
    //    interface, so the rules must hold through it too.
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_ThroughIReconnectPolicyInterface_AppliesTheSameRules()
    {
        IReconnectPolicy sut = new ReconnectPolicy(EnabledConfig(gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.Connected, sut.Evaluate(VpnState.Connected, InternetState.Up, T0));
        Assert.Equal(ReconnectDecision.NoInternet, sut.Evaluate(VpnState.Disconnected, InternetState.Down, T0));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, sut.Evaluate(VpnState.Disconnected, InternetState.Up, T0.AddSeconds(1)));
        Assert.Equal(ReconnectDecision.Triggered, sut.Evaluate(VpnState.Disconnected, InternetState.Up, T0.AddSeconds(92)));

        sut.RecordAttemptResult(false, T0.AddSeconds(92));
        Assert.Equal(1, sut.AttemptCount);
        Assert.Equal(T0.AddSeconds(92), sut.LastAttemptAt);
        Assert.Equal(ReconnectDecision.InBackoff, sut.Evaluate(VpnState.Disconnected, InternetState.Up, T0.AddSeconds(93)));

        sut.Reset();
        Assert.Equal(0, sut.AttemptCount);
    }
}
