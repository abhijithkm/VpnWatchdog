using System.Reflection;
using VpnWatchdog.Core;
using VpnWatchdog.Core.Correlation;
using VpnWatchdog.Core.Reconnect;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// Tests for the user-initiated manual VPN controls and, above all, for the
/// SEPARATION GUARANTEE that keeps them out of the automatic watchdog loop.
///
/// <para>
/// Nothing here touches COM, FortiClient, the network, a real VPN or the clock.
/// The type-system tests are pure reflection over the compiled
/// <c>VpnWatchdog.Core</c> assembly (no instance of the COM-backed controller is
/// ever created), and the policy tests drive the real <see cref="ReconnectPolicy"/>
/// through explicit <see cref="DateTimeOffset"/> values - never
/// <c>DateTimeOffset.Now</c>.
/// </para>
///
/// <para>
/// <b>Why the reflection tests exist.</b> The rule that the automatic loop cannot
/// disconnect the tunnel is enforced by the type system: polling, correlation and
/// reconnect-policy code is only ever handed an <see cref="IReconnectController"/>,
/// and that interface has no disconnect member, so a teardown from the automatic
/// path does not fail at runtime - it does not compile. A guarantee like that is
/// worth exactly as much as the shape of the interface it rests on, and nothing
/// else in the build would notice if someone later "helpfully" added
/// <c>DisconnectAsync</c> to <see cref="IReconnectController"/> for symmetry. These
/// tests are the thing that notices, and they are meant to fail loudly.
/// </para>
/// </summary>
public class ManualVpnControlTests
{
    private const string Profile = "MLA-DEV-VPN-2-LocalAuth";

    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 14, 0, 0, TimeSpan.FromHours(-4));

    /// <summary>Everything a type declares itself: public/non-public, instance/static, no inherited members.</summary>
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    /// <summary>The compiled Core assembly, reached through a contract type rather than by name.</summary>
    private static readonly Assembly CoreAssembly = typeof(IReconnectController).Assembly;

    // ==================================================================
    // 1. TYPE-SYSTEM SEPARATION
    //    The most important tests in this file: they pin the shape of the
    //    interface the automatic loop consumes.
    // ==================================================================

    [Fact]
    public void IReconnectController_DeclaresNoMemberWhoseNameContainsDisconnect()
    {
        // The interface handed to the polling/correlation/policy code must be
        // structurally incapable of expressing a disconnect. This is scoped to the
        // interface on purpose: a name-substring rule cannot be applied to
        // implementations, where honest names like ReconnectPolicy.DisconnectedSince
        // (a read-only diagnostic) legitimately contain the word.
        var offending = typeof(IReconnectController)
            .GetMembers(AllDeclared)
            .Select(member => member.Name)
            .Where(name => name.Contains("Disconnect", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offending.Length == 0,
            "IReconnectController must never declare a disconnect member: the automatic watchdog loop is " +
            "handed this interface and nothing else, which is what makes it structurally incapable of tearing " +
            "down the user's tunnel. Move the capability to IManualVpnControl instead. Found: " +
            string.Join(", ", offending));
    }

    [Fact]
    public void IReconnectController_DeclaresExactlyReconnectIsConnectedAndGetTunnelList()
    {
        // Pins the whole automatic-side contract, not just the word "Disconnect",
        // so a differently-named teardown (TearDownAsync, StopAsync, CloseTunnelAsync)
        // is caught too. Changing this list is a deliberate change to the safety
        // boundary and should be made consciously, here, in one place.
        var declared = typeof(IReconnectController)
            .GetMembers(AllDeclared)
            .Select(member => member.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "GetTunnelListAsync", "IsConnectedAsync", "ReconnectAsync" },
            declared);
    }

    [Fact]
    public void IReconnectController_InheritsNoOtherInterface_SoItCannotReachManualControlTransitively()
    {
        // Declaring `IReconnectController : IManualVpnControl` would hand the
        // automatic loop a disconnect without ever adding a member to
        // IReconnectController itself - the same regression by a different route.
        Assert.False(
            typeof(IManualVpnControl).IsAssignableFrom(typeof(IReconnectController)),
            "IReconnectController must not extend IManualVpnControl - that would give the automatic loop a disconnect.");

        Assert.Empty(typeof(IReconnectController).GetInterfaces());
    }

    [Fact]
    public void IManualVpnControl_DeclaresExactlyConnectAsyncAndDisconnectAsync()
    {
        var declared = typeof(IManualVpnControl)
            .GetMembers(AllDeclared)
            .Select(member => member.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "ConnectAsync", "DisconnectAsync" }, declared);
    }

    [Theory]
    [InlineData(nameof(IManualVpnControl.ConnectAsync))]
    [InlineData(nameof(IManualVpnControl.DisconnectAsync))]
    public void IManualVpnControl_MethodTakesProfileNameAndCancellationToken_AndReturnsTask(string methodName)
    {
        var method = typeof(IManualVpnControl).GetMethod(methodName);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method!.ReturnType);

        var parameters = method.GetParameters();

        Assert.Equal(
            new[] { typeof(string), typeof(CancellationToken) },
            parameters.Select(p => p.ParameterType).ToArray());
        Assert.Equal("profileName", parameters[0].Name);
        Assert.Equal("ct", parameters[1].Name);
    }

    [Fact]
    public void IManualVpnControl_InheritsNoOtherInterface()
    {
        Assert.False(
            typeof(IReconnectController).IsAssignableFrom(typeof(IManualVpnControl)),
            "IManualVpnControl must stay a separate capability rather than a superset of IReconnectController.");

        Assert.Empty(typeof(IManualVpnControl).GetInterfaces());
    }

    [Fact]
    public void FortiClientComReconnectController_ImplementsBothIReconnectControllerAndIManualVpnControl()
    {
        // One object, two capabilities, handed out as two different types: the
        // automatic loop receives it as IReconnectController and therefore cannot
        // see DisconnectAsync at all; only explicit user-initiated code takes the
        // IManualVpnControl view. Reflection only - no instance is constructed, so
        // no COM binding is attempted.
        var controller = typeof(FortiClientComReconnectController);

        Assert.True(
            typeof(IReconnectController).IsAssignableFrom(controller),
            "FortiClientComReconnectController must still serve the automatic loop as an IReconnectController.");
        Assert.True(
            typeof(IManualVpnControl).IsAssignableFrom(controller),
            "FortiClientComReconnectController must expose the user-initiated controls through IManualVpnControl.");
    }

    [Fact]
    public void FortiClientComReconnectController_ProvidesItsOwnImplementationsOfTheManualControlMembers()
    {
        // Guards against the interface being declared on the class while the
        // members are inherited from somewhere else or otherwise not really wired
        // up. Works whether the implementation is implicit or explicit.
        var map = typeof(FortiClientComReconnectController).GetInterfaceMap(typeof(IManualVpnControl));

        Assert.Equal(2, map.TargetMethods.Length);
        Assert.All(
            map.TargetMethods,
            target => Assert.Equal(typeof(FortiClientComReconnectController), target.DeclaringType));
    }

    [Fact]
    public void DisabledReconnectController_ImplementsBothIReconnectControllerAndIManualVpnControl()
    {
        var controller = typeof(DisabledReconnectController);

        Assert.True(typeof(IReconnectController).IsAssignableFrom(controller));
        Assert.True(typeof(IManualVpnControl).IsAssignableFrom(controller));
    }

    [Fact]
    public void IManualVpnControl_IsTheOnlyInterfaceInCoreThatCanExpressADisconnect()
    {
        // Broadens the guarantee across the whole contract surface: adding a
        // disconnect to some *other* interface and then handing that to the
        // watchdog loop would defeat the separation just as effectively as
        // amending IReconnectController.
        var offending = CoreAssembly
            .GetTypes()
            .Where(type => type.IsInterface && type != typeof(IManualVpnControl))
            .SelectMany(type => type.GetMembers(AllDeclared).Select(member => $"{type.Name}.{member.Name}"))
            .Where(qualifiedName => qualifiedName.Contains("Disconnect", StringComparison.OrdinalIgnoreCase))
            .OrderBy(qualifiedName => qualifiedName, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offending.Length == 0,
            "IManualVpnControl must remain the only interface in VpnWatchdog.Core that can express a disconnect. Found: " +
            string.Join(", ", offending));
    }

    [Fact]
    public void ReconnectDecision_HasNoOutcomeThatExpressesADisconnect()
    {
        // The policy's vocabulary is the other half of the guarantee: the watchdog
        // may decide to bring the tunnel up, and has no way to say "take it down".
        var offending = Enum.GetNames<ReconnectDecision>()
            .Where(name => name.Contains("Disconnect", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            offending.Length == 0,
            "ReconnectDecision must not be able to express a disconnect outcome. Found: " + string.Join(", ", offending));
    }

    [Fact]
    public void AutomaticSideTypes_NeverMentionIManualVpnControlInAnyDeclaredMember()
    {
        // The manual capability must be unreachable from timer/poll/correlation/
        // policy code. Checking the declared members of those types (fields
        // included, so an injected dependency is caught too) is the closest a unit
        // test can get to asserting "this code path cannot call Disconnect".
        var automaticSideTypes = new[]
        {
            typeof(IReconnectController),
            typeof(IReconnectPolicy),
            typeof(ReconnectPolicy),
            typeof(IVpnEventCorrelator),
            typeof(VpnEventCorrelator),
        };

        var offending = automaticSideTypes
            .SelectMany(type => DeclaredMemberTypes(type).Select(memberType => (type, memberType)))
            .Where(pair => pair.memberType == typeof(IManualVpnControl))
            .Select(pair => pair.type.Name)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offending.Length == 0,
            "Automatic (timer/poll/correlation/policy) types must never take, hold or hand out an IManualVpnControl. Found: " +
            string.Join(", ", offending));
    }

    // ==================================================================
    // 2. DisabledReconnectController - the quiet-read / loud-write split.
    //    Reads degrade silently so callers can keep polling; anything that
    //    CLAIMS TO HAVE ACTED fails loudly, because silently pretending to
    //    have connected or disconnected a tunnel is the worst outcome.
    // ==================================================================

    [Fact]
    public async Task DisabledReconnectController_ConnectAsync_ThrowsNotSupportedException()
    {
        IManualVpnControl sut = new DisabledReconnectController();

        // ThrowsAsync awaits the returned task, and also catches the exception if
        // the implementation throws synchronously at the call site.
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.ConnectAsync(Profile, CancellationToken.None));

        Assert.Contains(Profile, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledReconnectController_DisconnectAsync_ThrowsNotSupportedException()
    {
        IManualVpnControl sut = new DisabledReconnectController();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.DisconnectAsync(Profile, CancellationToken.None));

        Assert.Contains(Profile, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledReconnectController_ReconnectAsync_ThrowsNotSupportedException()
    {
        IReconnectController sut = new DisabledReconnectController();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.ReconnectAsync(Profile, CancellationToken.None));
    }

    [Fact]
    public async Task DisabledReconnectController_WriteMethods_SurfaceTheConfiguredReasonToTheUser()
    {
        const string reason = "FortiClient is not installed on this machine.";
        var sut = new DisabledReconnectController(reason);

        var connectFailure = await Assert.ThrowsAsync<NotSupportedException>(
            () => ((IManualVpnControl)sut).ConnectAsync(Profile, CancellationToken.None));
        var disconnectFailure = await Assert.ThrowsAsync<NotSupportedException>(
            () => ((IManualVpnControl)sut).DisconnectAsync(Profile, CancellationToken.None));

        Assert.Contains(reason, connectFailure.Message, StringComparison.Ordinal);
        Assert.Contains(reason, disconnectFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledReconnectController_IsConnectedAsync_ReturnsFalseWithoutThrowing()
    {
        IReconnectController sut = new DisabledReconnectController();

        Assert.False(await sut.IsConnectedAsync(Profile, CancellationToken.None));
    }

    [Fact]
    public async Task DisabledReconnectController_GetTunnelListAsync_ReturnsEmptyListWithoutThrowing()
    {
        IReconnectController sut = new DisabledReconnectController();

        var tunnels = await sut.GetTunnelListAsync(CancellationToken.None);

        Assert.NotNull(tunnels);
        Assert.Empty(tunnels);
    }

    // ==================================================================
    // 3. POLICY INTERACTION - the fight problem, expressed as policy behaviour.
    //
    //    A manual disconnect must switch auto-reconnect OFF first, visibly.
    //    These tests prove what that switch actually buys: once it is off, the
    //    real ReconnectPolicy will never bring the tunnel back up behind the
    //    user's back, however long the tunnel stays down.
    //
    //    AutoReconnectEnabled is fixed at construction (ReconnectPolicy reads it
    //    from WatchdogConfig in its constructor), so "switching auto-reconnect
    //    off" means building a policy from a config whose flag is false - which is
    //    exactly what the manual disconnect path has to do.
    // ==================================================================

    private static WatchdogConfig ConfigWithAutoReconnect(bool enabled, int gracePeriodSeconds = 90) =>
        WatchdogConfig.Default with
        {
            AutoReconnectEnabled = enabled,
            ReconnectGracePeriodSeconds = gracePeriodSeconds,
            ReconnectMaxAttempts = 5,
            ReconnectInitialBackoffSeconds = 30,
            ReconnectMaxBackoffSeconds = 300,
        };

    /// <summary>The shape a manual disconnect leaves behind: tunnel down, internet perfectly healthy.</summary>
    private static ReconnectDecision EvaluateAfterManualDisconnect(ReconnectPolicy sut, DateTimeOffset now) =>
        sut.Evaluate(VpnState.Disconnected, InternetState.Up, now);

    [Fact]
    public void Evaluate_WhenAutoReconnectWasSwitchedOffForAManualDisconnect_NeverTriggers_HoweverLongTheTunnelStaysDown()
    {
        // The user asked to disconnect, so auto-reconnect was switched off first.
        // From here the watchdog must stay out of the way permanently: the tunnel
        // is down and the internet is up, which is precisely the situation it would
        // otherwise fix, and it must not.
        var sut = new ReconnectPolicy(ConfigWithAutoReconnect(enabled: false, gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, T0));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, T0.AddSeconds(89)));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, T0.AddSeconds(91)));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, T0.AddMinutes(5)));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, T0.AddHours(1)));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, T0.AddHours(9)));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, T0.AddDays(3)));

        // Not one attempt was made or even scheduled - no backoff state was built up
        // that could fire later.
        Assert.Equal(0, sut.AttemptCount);
        Assert.Null(sut.LastAttemptAt);
        Assert.Null(sut.DisconnectedSince);
    }

    [Fact]
    public void Evaluate_WithAutoReconnectStillEnabled_ReachesTriggeredAfterTheGracePeriod()
    {
        // The converse of the test above, and the reason it means anything: given
        // the identical situation, a policy whose switch is ON does reconnect. So
        // the DisabledByUser result really is the switch doing its job, not a
        // policy that is inert for some unrelated reason.
        var sut = new ReconnectPolicy(ConfigWithAutoReconnect(enabled: true, gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateAfterManualDisconnect(sut, T0));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateAfterManualDisconnect(sut, T0.AddSeconds(89)));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateAfterManualDisconnect(sut, T0.AddSeconds(91)));
    }

    [Fact]
    public void Evaluate_TwoPoliciesDifferingOnlyInTheAutoReconnectSwitch_DivergeAtEveryInstantOfTheSameOutage()
    {
        // Same config, same clock, same VPN/internet inputs, one flag apart - so
        // the difference in outcome is attributable to the switch and nothing else.
        var afterManualDisconnect = new ReconnectPolicy(ConfigWithAutoReconnect(enabled: false, gracePeriodSeconds: 90));
        var autoReconnectStillArmed = new ReconnectPolicy(ConfigWithAutoReconnect(enabled: true, gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(afterManualDisconnect, T0));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateAfterManualDisconnect(autoReconnectStillArmed, T0));

        var afterGracePeriod = T0.AddSeconds(91);

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(afterManualDisconnect, afterGracePeriod));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateAfterManualDisconnect(autoReconnectStillArmed, afterGracePeriod));

        // An hour later the armed policy has spent its budget and given up, while
        // the switched-off policy is still saying the same single thing.
        autoReconnectStillArmed.RecordAttemptResult(false, afterGracePeriod);

        var muchLater = T0.AddHours(1);

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(afterManualDisconnect, muchLater));
        Assert.Equal(ReconnectDecision.Triggered, EvaluateAfterManualDisconnect(autoReconnectStillArmed, muchLater));
        Assert.Equal(0, afterManualDisconnect.AttemptCount);
        Assert.Equal(1, autoReconnectStillArmed.AttemptCount);
    }

    [Fact]
    public void Evaluate_WhenAutoReconnectIsSwitchedOffMidGracePeriod_TheReconnectThatWasPendingNeverHappens()
    {
        // The fight problem in its exact form. The watchdog is already counting out
        // the grace period on an observed drop; the user then hits Disconnect,
        // which switches auto-reconnect off before issuing the COM Disconnect. From
        // that moment the watchdog must not complete the reconnect it was about to
        // make.
        var config = ConfigWithAutoReconnect(enabled: true, gracePeriodSeconds: 90);
        var armed = new ReconnectPolicy(config);

        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateAfterManualDisconnect(armed, T0));
        Assert.Equal(ReconnectDecision.WaitingForSelfHeal, EvaluateAfterManualDisconnect(armed, T0.AddSeconds(30)));

        // The user disconnects at T0+30s, so the switch goes off and the watchdog
        // runs on the new config from here.
        var switchedOff = new ReconnectPolicy(config with { AutoReconnectEnabled = false });
        var switchedOffAt = T0.AddSeconds(30);

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(switchedOff, switchedOffAt));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(switchedOff, T0.AddSeconds(91)));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(switchedOff, T0.AddMinutes(30)));
        Assert.Equal(0, switchedOff.AttemptCount);

        // And for contrast: had the switch been left armed, that same T0+91s
        // evaluation is exactly where the watchdog would have undone the user's
        // disconnect. This is the reason a manual disconnect must turn the switch
        // off first rather than just issuing the COM call.
        Assert.Equal(ReconnectDecision.Triggered, EvaluateAfterManualDisconnect(armed, T0.AddSeconds(91)));
    }

    [Fact]
    public void Evaluate_AfterAManualDisconnect_StaysDisabledEvenIfTheTunnelBrieflyComesBackAndDropsAgain()
    {
        // A Connected evaluation resets the policy's outage bookkeeping, so this
        // checks the reset path cannot quietly re-arm the user's switch.
        var sut = new ReconnectPolicy(ConfigWithAutoReconnect(enabled: false, gracePeriodSeconds: 90));

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, T0));
        Assert.Equal(ReconnectDecision.Connected, sut.Evaluate(VpnState.Connected, InternetState.Up, T0.AddMinutes(10)));

        var secondOutageAt = T0.AddMinutes(20);

        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, secondOutageAt));
        Assert.Equal(ReconnectDecision.DisabledByUser, EvaluateAfterManualDisconnect(sut, secondOutageAt.AddSeconds(91)));
        Assert.Equal(0, sut.AttemptCount);
    }

    // ==================================================================
    // Reflection helper
    // ==================================================================

    /// <summary>
    /// Every type mentioned by a type's own declarations: method parameter and
    /// return types, constructor parameter types, property and field types, with
    /// generic arguments and element types flattened out (so <c>Task&lt;X&gt;</c>
    /// and <c>X[]</c> both surface <c>X</c>).
    /// </summary>
    private static IEnumerable<Type> DeclaredMemberTypes(Type type)
    {
        var methodTypes = type.GetMethods(AllDeclared)
            .SelectMany(method => method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType));

        var constructorTypes = type.GetConstructors(AllDeclared)
            .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType));

        var propertyTypes = type.GetProperties(AllDeclared).Select(property => property.PropertyType);
        var fieldTypes = type.GetFields(AllDeclared).Select(field => field.FieldType);

        return methodTypes
            .Concat(constructorTypes)
            .Concat(propertyTypes)
            .Concat(fieldTypes)
            .SelectMany(Flatten);
    }

    /// <summary>Expands a type into itself plus its generic arguments and element type, recursively.</summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.IsGenericType)
        {
            foreach (var inner in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return inner;
            }
        }

        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var inner in Flatten(elementType))
            {
                yield return inner;
            }
        }
    }
}
