using System.Net.Http;
using System.Net.NetworkInformation;

namespace VpnWatchdog.Core.Providers;

/// <summary>
/// Determines general internet/network availability via three independent,
/// individually-fault-tolerant checks: default gateway reachability (ping),
/// DNS resolution, and HTTPS reachability. Deliberately knows nothing about
/// Fortinet/VPN - this is "is the network itself up", not "is the tunnel up".
///
/// The HTTPS probe target is fully configurable via the constructor (never
/// hardcoded here) so callers control whether it points at a public,
/// non-company-internal endpoint (e.g. https://www.microsoft.com/robots.txt).
/// </summary>
public sealed class InternetStateProvider : IInternetStateProvider
{
    private const int GatewayPingTimeoutMs = 800;
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HttpsTimeout = TimeSpan.FromSeconds(3);

    // Single shared, lazily-created HttpClient reused across all calls for the
    // lifetime of the process - this is a long-running watchdog, and creating
    // a new HttpClient per poll would leak sockets (SNAT port exhaustion).
    private static readonly Lazy<HttpClient> SharedHttpClient = new(() => new HttpClient
    {
        Timeout = HttpsTimeout
    });

    private readonly string _httpsProbeUrl;

    public InternetStateProvider(string httpsProbeUrl)
    {
        _httpsProbeUrl = httpsProbeUrl ?? throw new ArgumentNullException(nameof(httpsProbeUrl));
    }

    public async Task<InternetSnapshot> GetCurrentStateAsync(CancellationToken ct)
    {
        var observedAt = DateTimeOffset.Now;

        try
        {
            var gatewayTask = CheckDefaultGatewayReachableAsync(ct);
            var dnsTask = CheckDnsResolvedAsync(ct);
            var httpsTask = CheckHttpsReachableAsync(ct);

            await Task.WhenAll(gatewayTask, dnsTask, httpsTask).ConfigureAwait(false);

            bool? gatewayReachable = gatewayTask.Result;
            bool? dnsResolved = dnsTask.Result;
            bool? httpsReachable = httpsTask.Result;

            // Each check always yields a definite true/false here (it swallows
            // its own exceptions internally) - Unknown is reserved for the
            // outer catch below, not for this combination step. Any reading
            // that isn't affirmatively "Up" (including the canonical
            // all-three-false case) is treated as Down.
            InternetState state = (httpsReachable == true) || (dnsResolved == true && gatewayReachable == true)
                ? InternetState.Up
                : InternetState.Down;

            return new InternetSnapshot(
                state,
                gatewayReachable,
                dnsResolved,
                httpsReachable,
                _httpsProbeUrl,
                observedAt);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Each individual check already catches its own exceptions and
            // reports false rather than throwing. Reaching here means
            // something unexpected prevented the checks from running at all
            // (should be rare) - report Unknown rather than guessing.
            return new InternetSnapshot(
                InternetState.Unknown,
                null,
                null,
                null,
                _httpsProbeUrl,
                observedAt);
        }
    }

    /// <summary>
    /// Finds any up, non-loopback, non-tunnel interface with a configured
    /// default gateway and pings it. True only if at least one gateway
    /// replies successfully.
    /// </summary>
    private static async Task<bool> CheckDefaultGatewayReachableAsync(CancellationToken ct)
    {
        try
        {
            var gatewayIps = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(nic => nic.GetIPProperties()?.GatewayAddresses ?? Enumerable.Empty<GatewayIPAddressInformation>())
                .Select(g => g.Address)
                .Where(addr => addr is not null)
                .Distinct()
                .ToList();

            foreach (var gatewayIp in gatewayIps)
            {
                ct.ThrowIfCancellationRequested();

                var ping = new Ping();
                var pingTask = ping.SendPingAsync(gatewayIp, GatewayPingTimeoutMs);
                // Dispose only once the send itself has finished, whichever
                // branch below observes that - never dispose while a
                // SendPingAsync could still be in flight.
                _ = pingTask.ContinueWith(_ => ping.Dispose(), TaskScheduler.Default);

                var delayTask = Task.Delay(GatewayPingTimeoutMs + 200, ct);
                var completed = await Task.WhenAny(pingTask, delayTask).ConfigureAwait(false);

                if (completed == pingTask)
                {
                    var reply = await pingTask.ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        return true;
                    }
                }
                else
                {
                    // The delay won: either our backstop window elapsed (rare -
                    // SendPingAsync carries its own shorter internal timeout)
                    // or the caller's token fired. Surface real cancellation;
                    // otherwise move on to the next candidate gateway.
                    ct.ThrowIfCancellationRequested();
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the host portion of the configured HTTPS probe URL, racing
    /// resolution against a timeout since DNS APIs don't reliably honor a
    /// CancellationToken for cancelling the underlying lookup.
    /// </summary>
    private async Task<bool> CheckDnsResolvedAsync(CancellationToken ct)
    {
        try
        {
            var host = new Uri(_httpsProbeUrl).Host;

            var resolveTask = System.Net.Dns.GetHostAddressesAsync(host, ct);
            var delayTask = Task.Delay(DnsTimeout, ct);

            var completed = await Task.WhenAny(resolveTask, delayTask).ConfigureAwait(false);
            if (completed != resolveTask)
            {
                return false;
            }

            var addresses = await resolveTask.ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Issues a GET against the configured HTTPS probe URL using the shared
    /// HttpClient. Any HTTP response at all (including 4xx/5xx) counts as
    /// reachable - we only care that the TLS/HTTP conversation succeeded.
    /// </summary>
    private async Task<bool> CheckHttpsReachableAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SharedHttpClient.Value
                .GetAsync(_httpsProbeUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
