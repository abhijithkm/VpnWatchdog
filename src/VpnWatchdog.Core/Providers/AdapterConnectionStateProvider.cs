using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VpnWatchdog.Core.Providers;

/// <summary>
/// Determines the Fortinet VPN adapter's current state using only
/// System.Net.NetworkInformation.NetworkInterface - no PowerShell, no
/// Process.Start, no external tool invocation, no network I/O. The target
/// adapter is always located dynamically each call by a case-insensitive
/// substring match of <c>adapterDescriptionPattern</c> against each
/// interface's Description; this class never hardcodes an adapter name or
/// interface index. Disambiguating between adapters (e.g. the Fortinet SSL
/// VPN adapter vs. the plain Fortinet Virtual Ethernet adapter) is entirely
/// the caller's responsibility via the pattern it supplies - this provider
/// applies no additional business logic beyond the plain Contains match.
/// </summary>
public sealed class AdapterConnectionStateProvider : IConnectionStateProvider
{
    public Task<AdapterSnapshot> GetAdapterSnapshotAsync(string adapterDescriptionPattern, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.Now;

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            // Enumeration itself failed - nothing to report, but do not crash the caller's poll loop.
            return Task.FromResult(AdapterSnapshot.NotFound(now));
        }

        NetworkInterface? match = null;
        foreach (var nic in interfaces)
        {
            try
            {
                if (nic.Description.Contains(adapterDescriptionPattern, StringComparison.OrdinalIgnoreCase))
                {
                    match = nic;
                    break;
                }
            }
            catch
            {
                // A single malformed adapter's properties should never abort the whole scan.
            }
        }

        if (match is null)
        {
            return Task.FromResult(AdapterSnapshot.NotFound(now));
        }

        try
        {
            return Task.FromResult(BuildSnapshot(match, now));
        }
        catch
        {
            // Reading the matched adapter's properties blew up (e.g. mid-teardown) -
            // treat as not-found for this poll rather than propagating.
            return Task.FromResult(AdapterSnapshot.NotFound(now));
        }
    }

    private static AdapterSnapshot BuildSnapshot(NetworkInterface nic, DateTimeOffset observedAt)
    {
        var isUp = nic.OperationalStatus == OperationalStatus.Up;

        IPInterfaceProperties? ipProps;
        try
        {
            ipProps = nic.GetIPProperties();
        }
        catch
        {
            ipProps = null;
        }

        string? ipAddress = null;
        int? prefixLength = null;
        var gatewayAddresses = new List<string>();
        int? interfaceIndex = null;

        if (ipProps is not null)
        {
            try
            {
                var firstIPv4 = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (firstIPv4 is not null)
                {
                    ipAddress = firstIPv4.Address.ToString();
                    prefixLength = firstIPv4.PrefixLength;
                }
            }
            catch
            {
                // Skip malformed unicast address info; leave ipAddress/prefixLength null.
            }

            try
            {
                foreach (var gateway in ipProps.GatewayAddresses)
                {
                    try
                    {
                        gatewayAddresses.Add(gateway.Address.ToString());
                    }
                    catch
                    {
                        // Skip a single malformed gateway entry, keep the rest.
                    }
                }
            }
            catch
            {
                // Skip the whole gateway collection if it is itself unreadable.
            }

            try
            {
                interfaceIndex = ipProps.GetIPv4Properties()?.Index;
            }
            catch
            {
                // IPv4 properties are unavailable (e.g. adapter is down) - leave null.
                interfaceIndex = null;
            }
        }

        long? bytesReceived = null;
        long? bytesSent = null;
        try
        {
            IPInterfaceStatistics stats = nic.GetIPStatistics();
            bytesReceived = stats.BytesReceived;
            bytesSent = stats.BytesSent;
        }
        catch
        {
            // Not every adapter/driver supports interface statistics - leave both
            // null rather than reporting a fabricated zero, so a consumer can tell
            // "no data yet" apart from "genuinely zero bytes so far".
        }

        return new AdapterSnapshot(
            AdapterFound: true,
            AdapterName: nic.Name,
            InterfaceDescription: nic.Description,
            InterfaceIndex: interfaceIndex,
            IsUp: isUp,
            IpAddress: ipAddress,
            PrefixLength: prefixLength,
            GatewayAddresses: gatewayAddresses,
            ObservedAt: observedAt,
            BytesReceived: bytesReceived,
            BytesSent: bytesSent);
    }
}
