using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace VpnWatchdog.Core.Reconnect;

// The whole watchdog is Windows-only by construction (FortiClient, WMI adapter
// queries, FortiClient trace logs). VpnWatchdog.Core targets plain net8.0 rather
// than net8.0-windows, so the platform-compatibility analyser is silenced here
// instead of annotating the type - annotating would force every caller (console
// host, WinForms host, tests) to carry the same attribute for no benefit.
#pragma warning disable CA1416 // Validate platform compatibility

/// <summary>
/// Thrown when FortiClient's COM automation server cannot be reached at all -
/// not registered (FortiClient not installed), not runnable, or answering with
/// a shape we cannot bind to. Callers should treat this as "reconnect capability
/// is unavailable on this machine" and degrade to observe-only, as distinct from
/// "the reconnect attempt failed", which surfaces as a plain
/// <see cref="InvalidOperationException"/> from <see cref="FortiClientComReconnectController.ReconnectAsync"/>.
/// <para>
/// Because callers use this type to switch auto-reconnect off for the rest of the
/// run, it must only ever be raised for conditions that are genuinely PERMANENT on
/// this machine. A transient call failure is not one of them - see the three-way
/// split documented on <see cref="FortiClientComReconnectController"/>'s
/// <c>ClassifyCallFailure</c>.
/// </para>
/// </summary>
public sealed class FortiClientComUnavailableException : InvalidOperationException
{
    public FortiClientComUnavailableException(string message) : base(message) { }
    public FortiClientComUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Drives FortiClient's registered COM automation interface to bring a saved VPN
/// profile back up.
/// <para>
/// This is the mechanism that was empirically validated on the target machine:
/// ProgID <c>FCCOMInt.XVPN</c> (out-of-process server <c>fccomint.exe</c>), with
/// <c>FortiClient.VPN</c> (in-process <c>fccomintdll.dll</c>) exposing the same
/// method set as a fallback. <c>Connect(tunnelName)</c> brought the tunnel up in
/// roughly four seconds, silently, without elevation and <b>without this process
/// ever supplying or seeing a credential</b> - FortiClient uses its own saved
/// password.
/// </para>
/// <para>
/// Binding is late-bound on purpose (<see cref="Type.GetTypeFromProgID(string, bool)"/> +
/// <see cref="Activator.CreateInstance(Type)"/> + <c>InvokeMember</c>) so the build
/// stays free of any generated interop assembly and so a machine without
/// FortiClient still compiles and runs - it just reports the capability as
/// unavailable at call time.
/// </para>
/// <para>
/// CREDENTIALS: this class never calls <c>SendXAuthResponse</c> and has no field,
/// parameter or config surface for a password, token or secret. That is
/// deliberate and must stay that way.
/// </para>
/// </summary>
public sealed class FortiClientComReconnectController : IReconnectController, IManualVpnControl, IDisposable
{
    // ------------------------------------------------------------------
    // WHERE Disconnect LIVES, AND WHY THAT PLACEMENT IS THE SAFETY BOUNDARY.
    //
    // This class does now wrap the COM member void Disconnect(string tunnelName),
    // but it is exposed EXCLUSIVELY through IManualVpnControl.DisconnectAsync -
    // never through IReconnectController.
    //
    // The automatic side of the app (polling, correlation, reconnect policy, the
    // watchdog loop) is only ever handed an IReconnectController. That interface
    // has no Disconnect member, so the automatic path cannot tear down the user's
    // tunnel - not "does not by convention", but CANNOT: it does not compile. The
    // guarantee is enforced by the type system, and it is only worth anything for
    // as long as it stays that way, so:
    //
    //   * NEVER add Disconnect (or anything that reaches it) to IReconnectController.
    //   * NEVER hand an IManualVpnControl - or this concrete type - to timer,
    //     poll, correlation or reconnect-policy code. Manual control is reachable
    //     only from an explicit user action (a button click, an explicit CLI
    //     command).
    //   * NEVER implement reconnect as disconnect-then-connect. ReconnectAsync
    //     issues Connect and nothing else. Routing an automatic recovery through
    //     a disconnect would smuggle a teardown into the automatic path and
    //     destroy the whole guarantee.
    //
    // Note also that a manual disconnect while auto-reconnect is armed would just
    // be undone by the watchdog after the grace period, so callers of
    // DisconnectAsync must switch auto-reconnect off first, visibly - see the
    // remarks on DisconnectAsync.
    // ------------------------------------------------------------------

    /// <summary>Out-of-process automation server (fccomint.exe) - the validated primary.</summary>
    public const string DefaultProgId = "FCCOMInt.XVPN";

    /// <summary>In-process automation server (fccomintdll.dll) - same method set, used if the primary is not registered.</summary>
    public const string FallbackProgId = "FortiClient.VPN";

    // ------------------------------------------------------------------
    // HRESULT CLASSIFICATION - the three-way split. Getting this wrong is not
    // cosmetic: callers switch auto-reconnect OFF FOR THE WHOLE RUN when they see
    // FortiClientComUnavailableException, so mislabelling one transient blip as
    // "unavailable" silently kills recovery for days.
    //
    //   UNAVAILABLE (stop trying)      -> FortiClientComUnavailableException.
    //       The automation server cannot be reached or created AT ALL on this
    //       machine: the class is not registered, the ProgID does not resolve,
    //       the server cannot be launched, or it answers with a shape we cannot
    //       bind to. Permanent for this run; degrade to observe-only.
    //
    //   STALE (rebind and retry once)  -> release the RCW, recreate, call again.
    //       We are holding a proxy to something that is gone - the out-of-process
    //       server was restarted underneath us, which does happen across a
    //       multi-day run. Exactly one retry, then classify whatever comes back.
    //
    //   FAILED (count it, back off)    -> plain InvalidOperationException.
    //       The server is there and answered, but THIS CALL did not succeed.
    //       The policy must count the attempt and back off; auto-reconnect stays
    //       armed so the next attempt can still work.
    // ------------------------------------------------------------------

    // STALE: "the RCW you are holding is pointing at something that is gone" - the
    // out-of-process server can be restarted underneath us during a multi-day run,
    // which is exactly the case the single retry exists for. E_FAIL is kept in this
    // set because one rebind is cheap and was worth having in practice - but note
    // it is deliberately NOT in the unavailable set below: a generic E_FAIL that
    // survives the rebind is a failed attempt, not a missing capability.
    private const int RpcEDisconnected = unchecked((int)0x80010108); // RPC_E_DISCONNECTED
    private const int EFail = unchecked((int)0x80004005);            // E_FAIL
    private const int CoEObjNotConnected = unchecked((int)0x800401FD); // CO_E_OBJNOTCONNECTED
    private const int RpcSServerUnavailable = unchecked((int)0x800706BA); // RPC_S_SERVER_UNAVAILABLE

    // UNAVAILABLE: the ONLY HRESULTs that genuinely mean "this machine cannot host
    // the call at all". Everything else - including E_FAIL and a stale HRESULT that
    // survived the rebind - is a failed attempt. Do not widen this list casually.
    private const int RegDbEClassNotReg = unchecked((int)0x80040154);    // REGDB_E_CLASSNOTREG - not installed/registered
    private const int CoEClassString = unchecked((int)0x800401F3);       // CO_E_CLASSSTRING - ProgID does not map to a CLSID
    private const int ENoInterface = unchecked((int)0x80004002);         // E_NOINTERFACE - not the shape we can bind to
    private const int CoEServerExecFailure = unchecked((int)0x80080005); // CO_E_SERVER_EXEC_FAILURE - server cannot be launched

    private static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Default bound on how long a call will wait to enter <see cref="_comGate"/>.
    /// Generous compared with every measured COM call (Connect ~4s, Disconnect ~5s,
    /// IsConnected effectively instant), so it only ever fires when the gate holder
    /// is genuinely wedged rather than merely slow.
    /// </summary>
    private static readonly TimeSpan DefaultComGateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Dispose gets a much SHORTER bound than a normal call: it runs on a shutdown
    /// path or on the WinForms UI thread (form close), where blocking is visible to
    /// the user as a frozen window. Two seconds is long enough to win the gate in
    /// the ordinary "nothing in flight" case and short enough that a wedged server
    /// cannot hang the close.
    /// </summary>
    private static readonly TimeSpan DisposeGateTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Guards creation, release and invocation of the COM instance. All COM work
    /// is serialised through this so a background poll can never release the RCW
    /// out from under an in-flight call (which would surface as
    /// InvalidComObjectException during a long run).
    /// <para>
    /// IMPORTANT - this gate is ALWAYS entered with a bounded
    /// <see cref="Monitor.TryEnter(object, TimeSpan, ref bool)"/>, never with a
    /// plain <c>lock</c>. Do not "simplify" it back. See the comment on
    /// <see cref="InvokeCom"/> for the failure this bound exists to prevent.
    /// </para>
    /// </summary>
    private readonly object _comGate = new();

    private readonly string _progId;
    private readonly TimeSpan _verifyTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _comGateTimeout;

    private Type? _comType;
    private object? _comInstance;

    /// <summary>
    /// 0 = live, 1 = Dispose has begun. An int rather than a bool so Dispose can
    /// latch it with <see cref="Interlocked.Exchange(ref int, int)"/> and be both
    /// idempotent and safe to call concurrently with an in-flight COM call -
    /// WITHOUT having to hold <see cref="_comGate"/> to make that decision.
    /// </summary>
    private int _disposedFlag;

    /// <param name="progId">
    /// COM ProgID to bind to. Defaults to the validated <see cref="DefaultProgId"/>;
    /// if that ProgID is not registered, <see cref="FallbackProgId"/> is tried.
    /// </param>
    /// <param name="verifyTimeout">
    /// How long to wait for the tunnel to actually come up after Connect() is
    /// issued. Defaults to 30 seconds (the observed connect took about 4).
    /// </param>
    /// <param name="pollInterval">How often to re-check IsConnected while verifying. Defaults to 1 second.</param>
    /// <param name="comGateTimeout">
    /// How long a call may wait to enter the COM gate before giving up and
    /// reporting the capability as unavailable. Defaults to
    /// <see cref="DefaultComGateTimeout"/> (30s). Injectable so a host with a
    /// tighter shutdown budget - or a test - can bound it differently; there is no
    /// unbounded option on purpose.
    /// </param>
    public FortiClientComReconnectController(
        string progId = DefaultProgId,
        TimeSpan? verifyTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? comGateTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progId);

        var verify = verifyTimeout ?? DefaultVerifyTimeout;
        if (verify <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(verifyTimeout), verify, "Verify timeout must be positive.");
        }

        var poll = pollInterval ?? DefaultPollInterval;
        if (poll <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), poll, "Poll interval must be positive.");
        }

        var gate = comGateTimeout ?? DefaultComGateTimeout;
        if (gate <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(comGateTimeout), gate, "COM gate timeout must be positive.");
        }

        _progId = progId;
        _verifyTimeout = verify;
        _pollInterval = poll;
        _comGateTimeout = gate;
    }

    /// <summary>Builds a controller using the verify timeout from <see cref="WatchdogConfig"/>.</summary>
    public static FortiClientComReconnectController FromConfig(WatchdogConfig config, string progId = DefaultProgId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new FortiClientComReconnectController(
            progId,
            TimeSpan.FromSeconds(Math.Max(1, config.ReconnectVerifyTimeoutSeconds)));
    }

    /// <summary>The ProgID this instance was configured with (the fallback is only tried if this one is unregistered).</summary>
    public string ProgId => _progId;

    /// <summary>True once <see cref="Dispose"/> has been entered. Read without taking the COM gate.</summary>
    private bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

    /// <summary>
    /// Asks FortiClient to connect the named tunnel, then verifies it actually
    /// came up.
    /// <para>
    /// IDEMPOTENT: if FortiClient already reports the tunnel connected, this
    /// returns success immediately without issuing <c>Connect</c> at all - see
    /// <see cref="ConnectAndVerifyAsync"/>.
    /// </para>
    /// <para>
    /// <c>Connect</c> returns void and behaves fire-and-forget: it hands the request
    /// to FortiClient and returns immediately, with the tunnel appearing a few
    /// seconds later. So the call alone proves nothing - we poll
    /// <c>IsConnected</c> until it reports true or the verify timeout expires, and
    /// throw on timeout so the caller's policy counts this as a failed attempt
    /// (and backs off) rather than silently believing it worked.
    /// </para>
    /// </summary>
    /// <exception cref="FortiClientComUnavailableException">FortiClient COM automation could not be used at all.</exception>
    /// <exception cref="InvalidOperationException">This attempt failed: the call was rejected, or the connect was issued but the tunnel was not up within the verify timeout.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public Task ReconnectAsync(string profileName, CancellationToken ct) =>
        ConnectAndVerifyAsync(profileName, ct);

    /// <summary>
    /// <see cref="IManualVpnControl"/>: brings the named tunnel up because the user
    /// asked for it now (a button click, an explicit CLI command).
    /// <para>
    /// This is the exact same COM operation as <see cref="ReconnectAsync"/> - the
    /// already-connected short-circuit, then <c>Connect</c> followed by polling
    /// <c>IsConnected</c> until it reports true or the verify timeout expires - and
    /// shares its implementation. The two methods exist separately because they sit
    /// on different interfaces: the automatic watchdog loop only ever sees
    /// <see cref="IReconnectController"/>, while this one is reachable only from an
    /// explicit user action.
    /// </para>
    /// </summary>
    /// <exception cref="FortiClientComUnavailableException">FortiClient COM automation could not be used at all.</exception>
    /// <exception cref="InvalidOperationException">This attempt failed: the call was rejected, or the connect was issued but the tunnel was not up within the verify timeout.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public Task ConnectAsync(string profileName, CancellationToken ct) =>
        ConnectAndVerifyAsync(profileName, ct);

    /// <summary>
    /// <see cref="IManualVpnControl"/>: takes the named tunnel down because the user
    /// asked for it now, then verifies it actually went down.
    /// <para>
    /// <c>Disconnect</c> returns void and, like <c>Connect</c>, is NOT instant: it
    /// returns <b>before</b> FortiClient has finished tearing the tunnel down, and
    /// <c>IsConnected</c> keeps reading a stale <c>true</c> for several seconds
    /// afterwards. Trusting the return - or taking a single reading after it - is a
    /// real observed bug, not a theoretical one, so this polls <c>IsConnected</c>
    /// until it reports false or the verify timeout expires, and throws on timeout
    /// rather than reporting a teardown that did not happen.
    /// </para>
    /// <para>
    /// NOTE the deliberate asymmetry with the connect path: there is no
    /// "already disconnected, nothing to do" short-circuit here. A user who asks for
    /// a disconnect gets the request ISSUED to FortiClient every time, because the
    /// adapter/IP evidence the rest of the app reasons from can read disconnected
    /// while the tunnel is still up, and silently skipping the teardown on that
    /// basis would leave the tunnel running while telling the user it was down.
    /// </para>
    /// <para>
    /// CALLER CONTRACT: auto-reconnect must already have been switched off, visibly,
    /// before this is called. Otherwise the watchdog observes the drop, waits out the
    /// grace period and brings the tunnel straight back up, appearing to fight the
    /// user. Never disconnect while leaving auto-reconnect armed.
    /// </para>
    /// </summary>
    /// <exception cref="FortiClientComUnavailableException">FortiClient COM automation could not be used at all.</exception>
    /// <exception cref="InvalidOperationException">This attempt failed: the call was rejected, or the disconnect was issued but the tunnel was still up at the verify timeout.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task DisconnectAsync(string profileName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ct.ThrowIfCancellationRequested();

        // Blocking, apartment-threaded COM call - never run it on the caller's
        // thread, which for the WinForms host is the STA UI thread.
        await Task.Run(() => InvokeCom("Disconnect", profileName), ct).ConfigureAwait(false);

        if (await WaitForConnectedStateAsync(profileName, expectConnected: false, ct).ConfigureAwait(false))
        {
            return;
        }

        throw new InvalidOperationException(
            string.Format(
                CultureInfo.InvariantCulture,
                "FortiClient accepted Disconnect(\"{0}\") but the tunnel was still reported connected after {1:0.#}s. " +
                "Treat this as a failed disconnect - the tunnel may still be up.",
                profileName,
                _verifyTimeout.TotalSeconds));
    }

    /// <summary>
    /// The shared Connect-then-verify operation behind both
    /// <see cref="ReconnectAsync"/> (automatic) and <see cref="ConnectAsync"/>
    /// (user-initiated). They are the same COM work; only the interface they are
    /// reached through differs.
    /// </summary>
    private async Task ConnectAndVerifyAsync(string profileName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ct.ThrowIfCancellationRequested();

        // ASK FORTICLIENT FIRST, AND DO NOTHING IF IT SAYS THE TUNNEL IS ALREADY UP.
        //
        // The watchdog decides "disconnected" from adapter/IP evidence, and that
        // evidence can produce a FALSE NEGATIVE: a one-tick flap was actually
        // observed live - both the adapter reading and the internet probe glitched
        // for a single poll about six seconds after a successful reconnect, on a
        // tunnel that was perfectly healthy throughout. Without this guard the
        // watchdog answers a false negative by re-issuing Connect on a working
        // tunnel, repeatedly.
        //
        // FortiClient's own view is authoritative here, so consult it before acting.
        // This also makes the whole operation idempotent: connecting an
        // already-connected profile is a no-op that reports success.
        bool alreadyConnected;
        try
        {
            alreadyConnected = await IsConnectedAsync(profileName, ct).ConfigureAwait(false);
        }
        catch (FortiClientComUnavailableException)
        {
            // The capability itself is gone - Connect could not have worked either,
            // and the caller needs to see this to degrade to observe-only.
            throw;
        }
        catch (InvalidOperationException)
        {
            // A transient failure of the PROBE must not abort the ATTEMPT. Before
            // this guard existed, a flaky IsConnected could not stop a reconnect,
            // and it must not start doing so now: the probe is a guard, not the
            // operation. Fall through and issue Connect as we always did.
            alreadyConnected = false;
        }

        if (alreadyConnected)
        {
            return;
        }

        // Blocking, apartment-threaded COM call - never run it on the caller's
        // thread, which for the WinForms host is the STA UI thread.
        await Task.Run(() => InvokeCom("Connect", profileName), ct).ConfigureAwait(false);

        if (await WaitForConnectedStateAsync(profileName, expectConnected: true, ct).ConfigureAwait(false))
        {
            return;
        }

        throw new InvalidOperationException(
            string.Format(
                CultureInfo.InvariantCulture,
                "FortiClient accepted Connect(\"{0}\") but the tunnel was still not reported connected after {1:0.#}s. " +
                "Treat this as a failed reconnect attempt.",
                profileName,
                _verifyTimeout.TotalSeconds));
    }

    /// <summary>
    /// Polls <c>IsConnected</c> until it reports <paramref name="expectConnected"/>
    /// or the verify timeout expires. Both Connect and Disconnect hand the request
    /// to FortiClient and return before it has taken effect - and after Disconnect
    /// the reading stays stale-true for several seconds - so neither call proves
    /// anything on its own and both must be confirmed the same way.
    /// </summary>
    /// <returns><c>true</c> if the tunnel reached the expected state in time; <c>false</c> on timeout.</returns>
    private async Task<bool> WaitForConnectedStateAsync(string profileName, bool expectConnected, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await IsConnectedAsync(profileName, ct).ConfigureAwait(false) == expectConnected)
            {
                return true;
            }

            var remaining = _verifyTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>FortiClient's own view of whether the named tunnel is up.</summary>
    /// <exception cref="FortiClientComUnavailableException">FortiClient COM automation could not be used at all.</exception>
    /// <exception cref="InvalidOperationException">The automation server rejected this call - a failed attempt, not a missing capability.</exception>
    public Task<bool> IsConnectedAsync(string profileName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        return Task.Run(
            () =>
            {
                var raw = InvokeCom("IsConnected", profileName);
                try
                {
                    // VARIANT_BOOL can arrive as bool or as a short; Convert
                    // handles both, and a null result converts to false.
                    return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
                {
                    // UNAVAILABLE, not FAILED: the server answered, but with a shape
                    // this binding cannot read. That is a property of the automation
                    // interface on this machine, not of this one attempt, so retrying
                    // it every backoff for days would be pointless noise.
                    throw Unavailable("IsConnected", ex);
                }
            },
            ct);
    }

    /// <summary>
    /// Tunnel profiles FortiClient knows about. <c>GetTunnelList</c> returns a
    /// VARIANT; in testing it came back as a string[], but the shape is not
    /// contractual, so an array, a general IEnumerable and a lone string are all
    /// handled, and anything else yields an empty list rather than an exception -
    /// this is used to validate configuration, not to make a safety decision.
    /// </summary>
    public Task<IReadOnlyList<string>> GetTunnelListAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        return Task.Run<IReadOnlyList<string>>(
            () => CoerceToStringList(InvokeCom("GetTunnelList")),
            ct);
    }

    // ------------------------------------------------------------------
    // COM plumbing
    // ------------------------------------------------------------------

    /// <summary>
    /// Invokes a member on the cached COM instance. If the call fails with a
    /// stale/disconnected-object HRESULT (the out-of-process server was restarted
    /// under us), the instance is dropped, recreated once, and the call retried
    /// exactly once - then we give up.
    /// </summary>
    /// <remarks>
    /// WHY THE GATE IS ENTERED WITH A TIMEOUT AND NOT WITH <c>lock</c>.
    /// <para>
    /// Everything below this line is a BLOCKING, CROSS-PROCESS RPC into
    /// fccomint.exe - <c>Activator.CreateInstance</c> and <c>InvokeMember</c> both.
    /// Neither a <see cref="CancellationToken"/> nor <c>Task.WaitAsync</c> can
    /// interrupt them, and neither can interrupt <c>Monitor.Enter</c>: those only
    /// abandon the AWAIT, while the worker thread stays parked inside the call, and
    /// inside the gate.
    /// </para>
    /// <para>
    /// So if fccomint.exe goes live-but-hung - suspended mid FortiClient
    /// auto-update, STA stuck behind a modal dialog, half-alive after a laptop
    /// resume - a plain <c>lock</c> is never released. The traced consequence is not
    /// "one slow call": the reconnect task never reaches its <c>finally</c>, so
    /// <c>ReconnectPolicy.EndAttempt()</c> never runs, the single-flight latch stays
    /// set forever, and every later <c>Evaluate</c> returns <c>AlreadyInFlight</c>.
    /// Auto-reconnect is then silently dead for the rest of a multi-day run while
    /// the banner still reads ARMED.
    /// </para>
    /// <para>
    /// A bounded <see cref="Monitor.TryEnter(object, TimeSpan, ref bool)"/> cannot
    /// un-hang the wedged thread, but it guarantees that every OTHER caller unwinds
    /// - through its own <c>finally</c>, releasing the latch - instead of joining
    /// the pile-up. Do not simplify this back into a <c>lock</c>.
    /// </para>
    /// </remarks>
    private object? InvokeCom(string memberName, params object?[] args)
    {
        var gateTaken = false;
        try
        {
            Monitor.TryEnter(_comGate, _comGateTimeout, ref gateTaken);
            if (!gateTaken)
            {
                // Reported as UNAVAILABLE on purpose. Whatever is holding the gate is
                // stuck inside an uninterruptible COM call, so every subsequent call
                // would wedge the same way; telling the caller the capability is gone
                // is the honest answer, and - the point of the whole fix - it makes
                // the caller unwind to its finally and release the single-flight latch.
                throw GateUnavailable(memberName);
            }

            try
            {
                return InvokeOnce(memberName, args);
            }
            catch (COMException ex) when (IsStaleInstance(ex))
            {
                // STALE: drop the proxy, rebind, and try exactly once more.
                ReleaseInstanceUnsafe();

                try
                {
                    return InvokeOnce(memberName, args);
                }
                catch (Exception retryEx) when (IsCallFailure(retryEx))
                {
                    ReleaseInstanceUnsafe();
                    throw ClassifyCallFailure(memberName, retryEx);
                }
            }
            catch (Exception ex) when (IsCallFailure(ex))
            {
                throw ClassifyCallFailure(memberName, ex);
            }
        }
        finally
        {
            // Released on EVERY path, including the throw above - Monitor.Exit must
            // only run if TryEnter actually took the gate, which is what the
            // ref-bool overload guarantees even against an async abort.
            if (gateTaken)
            {
                Monitor.Exit(_comGate);
            }
        }
    }

    private object? InvokeOnce(string memberName, object?[] args)
    {
        var (type, instance) = GetOrCreateInstanceUnsafe();

        try
        {
            return type.InvokeMember(
                memberName,
                BindingFlags.InvokeMethod,
                binder: null,
                target: instance,
                args: args,
                culture: CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Surface the real COMException/MissingMemberException so the
            // stale-object and classification checks above can see the HRESULT.
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

    /// <summary>Lazily creates and caches the COM instance. Caller must hold <see cref="_comGate"/>.</summary>
    private (Type Type, object Instance) GetOrCreateInstanceUnsafe()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (_comType is not null && _comInstance is not null)
        {
            return (_comType, _comInstance);
        }

        var type = ResolveType();

        object instance;
        try
        {
            instance = Activator.CreateInstance(type)
                ?? throw new FortiClientComUnavailableException(
                    $"FortiClient COM automation is unavailable: creating an instance of ProgID '{_progId}' returned null.");
        }
        catch (FortiClientComUnavailableException)
        {
            throw;
        }
        catch (COMException ex) when (!IsUnavailableHResult(ex.HResult))
        {
            // FAILED, not UNAVAILABLE: the class IS registered (ResolveType found it)
            // and this particular activation did not work - FortiClient mid-restart,
            // mid-upgrade, or momentarily refusing to launch its server. Counting this
            // as a lost attempt keeps auto-reconnect armed for the next one; calling it
            // "unavailable" would end recovery for the rest of the run over a blip.
            throw AttemptFailed($"creating an instance of ProgID '{_progId}'", ex);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MemberAccessException
                                      or TargetInvocationException or NotSupportedException)
        {
            throw new FortiClientComUnavailableException(
                $"FortiClient COM automation is unavailable: could not start the automation server for ProgID '{_progId}' " +
                $"({ex.GetType().Name}: {ex.Message}). FortiClient may not be installed, may not be running, or its COM " +
                "server may be blocked. The watchdog can continue in observe-only mode.",
                ex);
        }

        _comType = type;
        _comInstance = instance;
        return (type, instance);
    }

    private Type ResolveType()
    {
        var type = Type.GetTypeFromProgID(_progId, throwOnError: false);

        if (type is null && !string.Equals(_progId, FallbackProgId, StringComparison.OrdinalIgnoreCase))
        {
            // Same method set, in-process server - used when the out-of-process
            // one is not registered on this machine.
            type = Type.GetTypeFromProgID(FallbackProgId, throwOnError: false);
        }

        // UNAVAILABLE, unambiguously: neither ProgID resolves to a CLSID, so there is
        // nothing on this machine to call and no amount of retrying will change that.
        return type ?? throw new FortiClientComUnavailableException(
            $"FortiClient COM automation is unavailable: ProgID '{_progId}' is not registered on this machine" +
            (string.Equals(_progId, FallbackProgId, StringComparison.OrdinalIgnoreCase)
                ? "."
                : $" (nor is the fallback '{FallbackProgId}').") +
            " FortiClient is probably not installed. The watchdog can continue in observe-only mode.");
    }

    /// <summary>STALE: the RCW points at a server instance that is gone; worth exactly one rebind and retry.</summary>
    private static bool IsStaleInstance(COMException ex) =>
        ex.HResult is RpcEDisconnected or EFail or CoEObjNotConnected or RpcSServerUnavailable;

    /// <summary>
    /// UNAVAILABLE: the small, closed set of HRESULTs that genuinely mean the
    /// automation server cannot be reached or created on this machine. Anything
    /// outside this set is a failed attempt - see <see cref="ClassifyCallFailure"/>.
    /// </summary>
    private static bool IsUnavailableHResult(int hresult) =>
        hresult is RegDbEClassNotReg or CoEClassString or ENoInterface or CoEServerExecFailure;

    /// <summary>The exception shapes a late-bound call can fail with and that we classify rather than let escape raw.</summary>
    private static bool IsCallFailure(Exception ex) =>
        ex is COMException or InvalidCastException or MissingMemberException;

    /// <summary>
    /// Turns a failed call into the RIGHT exception type - the whole point being
    /// that callers switch auto-reconnect off for the run on
    /// <see cref="FortiClientComUnavailableException"/>, so that type must be
    /// reserved for conditions that are actually permanent:
    /// <list type="bullet">
    /// <item><description>
    /// UNAVAILABLE (stop trying): a COMException carrying one of the
    /// <see cref="IsUnavailableHResult"/> codes - the class is not registered, the
    /// ProgID does not map to a CLSID, the server will not launch, or we cannot bind
    /// to its shape. Also an InvalidCastException or MissingMemberException, which
    /// mean the object is not the interface we know how to drive; that is a property
    /// of this installation, not of this attempt.
    /// </description></item>
    /// <item><description>
    /// FAILED (count it, back off): every other COMException, E_FAIL included, and a
    /// stale HRESULT that survived the rebind-and-retry. The server is there and
    /// answered; this one call did not work. It must surface as a plain
    /// InvalidOperationException so the policy records a failed attempt and backs
    /// off, leaving auto-reconnect armed for the next try.
    /// </description></item>
    /// </list>
    /// </summary>
    private Exception ClassifyCallFailure(string memberName, Exception inner) =>
        inner is COMException com && !IsUnavailableHResult(com.HResult)
            ? AttemptFailed($"the call to '{memberName}' on ProgID '{_progId}'", com)
            : Unavailable(memberName, inner);

    private FortiClientComUnavailableException Unavailable(string memberName, Exception inner) =>
        new(
            $"FortiClient COM automation is unavailable: the call to '{memberName}' on ProgID '{_progId}' failed " +
            $"({inner.GetType().Name}: 0x{inner.HResult:X8} {inner.Message}). FortiClient may not be installed or running, " +
            "or its automation interface may have changed. The watchdog can continue in observe-only mode.",
            inner);

    private FortiClientComUnavailableException GateUnavailable(string memberName) =>
        new(
            $"FortiClient COM automation is unavailable: '{memberName}' waited {_comGateTimeout.TotalSeconds:0.#}s for the " +
            "COM gate and an earlier call is still inside FortiClient's automation server without returning. A cross-process " +
            "COM call cannot be cancelled or interrupted, so the wait is bounded deliberately rather than blocking forever - " +
            "this attempt is abandoned so the caller can release its in-flight latch. The watchdog can continue in " +
            "observe-only mode.");

    /// <summary>
    /// FAILED, not UNAVAILABLE: this ONE call did not succeed, the capability is
    /// still there. A plain <see cref="InvalidOperationException"/> on purpose -
    /// that is what tells the policy to count the attempt and back off, instead of
    /// giving up on reconnect for the rest of the run.
    /// </summary>
    private static InvalidOperationException AttemptFailed(string what, Exception inner) =>
        new(
            $"FortiClient's COM automation did not complete {what} " +
            $"({inner.GetType().Name}: 0x{inner.HResult:X8} {inner.Message}). The automation server is present, so treat " +
            "this as a FAILED ATTEMPT - count it, back off and try again later - not as a missing capability.",
            inner);

    /// <summary>
    /// Best-effort conversion of the GetTunnelList VARIANT into a string list.
    /// Never throws - an unexpected shape yields an empty list.
    /// </summary>
    private static IReadOnlyList<string> CoerceToStringList(object? raw)
    {
        if (raw is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            // A lone string must be checked before IEnumerable, or it would be
            // enumerated one char at a time.
            if (raw is string single)
            {
                return string.IsNullOrWhiteSpace(single)
                    ? Array.Empty<string>()
                    : new[] { single.Trim() };
            }

            var results = new List<string>();

            switch (raw)
            {
                case string[] strings:
                    AddAll(results, strings);
                    break;

                case Array array:
                    // SAFEARRAY of VARIANT, of BSTR, or a jagged/multi-dim shape.
                    foreach (var item in array)
                    {
                        AddOne(results, item);
                    }
                    break;

                case IEnumerable enumerable:
                    foreach (var item in enumerable)
                    {
                        AddOne(results, item);
                    }
                    break;

                default:
                    return Array.Empty<string>();
            }

            return results;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or IndexOutOfRangeException)
        {
            // The tunnel list is informational (config validation); an odd shape
            // must not take the watchdog down.
            return Array.Empty<string>();
        }

        static void AddAll(List<string> target, IEnumerable<string?> items)
        {
            foreach (var item in items)
            {
                AddOne(target, item);
            }
        }

        static void AddOne(List<string> target, object? item)
        {
            var text = item as string ?? item?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                target.Add(text.Trim());
            }
        }
    }

    // ------------------------------------------------------------------
    // Lifetime
    // ------------------------------------------------------------------

    /// <summary>
    /// Releases the cached COM instance. This process is expected to run for
    /// days, so the out-of-process server (fccomint.exe) must not be leaked.
    /// <para>
    /// NEVER BLOCKS INDEFINITELY, and never takes the gate with a plain
    /// <c>lock</c>. Dispose runs on a shutdown path or - for the WinForms host - on
    /// the STA UI thread during form close. Waiting there for an in-flight,
    /// uninterruptible cross-process COM call would defeat the CLI's bounded drain
    /// and freeze the GUI window on close, which is exactly what a user reads as a
    /// hung application. So the gate is attempted for
    /// <see cref="DisposeGateTimeout"/> only; if a call still holds it, the explicit
    /// release is SKIPPED and the RCW is left to the CLR's finalizer. Leaving one
    /// RCW to finalization is a bounded, self-correcting cost; freezing the UI is
    /// not.
    /// </para>
    /// <para>
    /// Idempotent and safe to call concurrently with an in-flight call: the
    /// disposed flag is latched with <see cref="Interlocked"/> BEFORE the gate is
    /// attempted, so a second Dispose returns immediately and new calls fail fast
    /// with <see cref="ObjectDisposedException"/> while the in-flight one is left to
    /// finish on its own.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            // Already disposed (or being disposed on another thread) - do nothing.
            return;
        }

        var gateTaken = false;
        try
        {
            Monitor.TryEnter(_comGate, DisposeGateTimeout, ref gateTaken);
            if (!gateTaken)
            {
                // A call is wedged inside the automation server. Do NOT wait it out:
                // FinalReleaseComObject on an RCW another thread is actively using
                // would be wrong anyway, and blocking here is the failure this bound
                // exists to prevent. The finalizer will release the RCW.
                return;
            }

            ReleaseInstanceUnsafe();
        }
        finally
        {
            if (gateTaken)
            {
                Monitor.Exit(_comGate);
            }
        }
    }

    /// <summary>Drops the cached RCW so the next call rebinds. Caller must hold <see cref="_comGate"/>.</summary>
    private void ReleaseInstanceUnsafe()
    {
        var instance = _comInstance;
        _comInstance = null;
        _comType = null;

        if (instance is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidComObjectException)
        {
            // Already dead or already released - nothing useful to do, and a
            // failure to release must never propagate out of Dispose.
        }
    }
}

#pragma warning restore CA1416
