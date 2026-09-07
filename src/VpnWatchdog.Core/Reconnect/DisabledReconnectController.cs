namespace VpnWatchdog.Core.Reconnect;

/// <summary>
/// Observe-only fallback used when FortiClient's COM automation interface is not
/// available on this machine (FortiClient not installed, or the COM server not
/// registered). Everything still monitors; only the reconnect action is inert.
/// Reports the tunnel as not-connected rather than throwing, so callers can keep
/// polling without special-casing, but every call that claims to ACT - automatic
/// ReconnectAsync and the user-initiated ConnectAsync/DisconnectAsync - fails
/// loudly; silently pretending to have acted would be worse than an honest error.
/// </summary>
public sealed class DisabledReconnectController : IReconnectController, IManualVpnControl
{
    private readonly string _reason;

    public DisabledReconnectController(string? reason = null) =>
        _reason = reason ?? "FortiClient COM automation is unavailable on this machine.";

    public Task ReconnectAsync(string profileName, CancellationToken ct) =>
        throw new NotSupportedException(
            $"Cannot reconnect '{profileName}': {_reason} Monitoring continues in observe-only mode.");

    // Manual (user-initiated) control - same honesty rule as ReconnectAsync: without
    // the COM server there is no way to drive the tunnel, so say so instead of
    // returning a completed Task the caller would read as "done".
    public Task ConnectAsync(string profileName, CancellationToken ct) =>
        throw new NotSupportedException(
            $"Cannot connect '{profileName}': {_reason} Manual VPN control is not possible; monitoring continues in observe-only mode.");

    public Task DisconnectAsync(string profileName, CancellationToken ct) =>
        throw new NotSupportedException(
            $"Cannot disconnect '{profileName}': {_reason} Manual VPN control is not possible; monitoring continues in observe-only mode.");

    public Task<bool> IsConnectedAsync(string profileName, CancellationToken ct) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<string>> GetTunnelListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
