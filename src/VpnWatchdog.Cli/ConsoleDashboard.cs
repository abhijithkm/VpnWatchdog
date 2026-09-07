using System.Linq;
using VpnWatchdog.Core;

namespace VpnWatchdog.Cli;

/// <summary>
/// Renders a live, read-only status view of the watchdog to the console. Re-renders
/// only when the meaningfully-displayed state has changed, or when at least
/// <see cref="WatchdogConfig.HeartbeatIntervalMs"/> has elapsed since the last render -
/// this avoids clearing/redrawing the screen on every single poll tick (e.g. every
/// 2 seconds) when nothing of substance has happened.
/// </summary>
public sealed class ConsoleDashboard
{
    private const int LabelColumnWidth = 24;

    private string? _lastRenderedSignature;
    private DateTimeOffset _lastRenderTime = DateTimeOffset.MinValue;

    public void Render(
        WatchdogConfig config,
        VpnStateSnapshot vpnState,
        InternetSnapshot internet,
        IReadOnlyList<ProcessSnapshot> processes,
        IVpnEventCorrelator correlator,
        DateTimeOffset now)
    {
        IReadOnlyList<DisconnectCorrelation> completed = correlator.GetCompletedCorrelations();
        DisconnectCorrelation? open = correlator.GetOpenCorrelation();

        int unexpectedDisconnects = completed.Count(c => c.DisconnectClassification == "Unexpected")
            + (open is not null && open.DisconnectClassification == "Unexpected" ? 1 : 0);
        int automaticRecoveries = completed.Count(c => c.RecoverySucceeded == true);
        int failedRecoveries = completed.Count(c => c.RecoverySucceeded == false);

        bool fortiVpnRunning = IsProcessRunning(processes, "FortiVPN");
        bool sslVpnDaemonRunning = IsProcessRunning(processes, "SSLVPN");

        string adapterDisplay = vpnState.Adapter.AdapterFound && vpnState.Adapter.AdapterName is not null
            ? vpnState.Adapter.AdapterName
            : "(not found)";

        string vpnIpDisplay = vpnState.Adapter.IpAddress is not null
            ? $"{vpnState.Adapter.IpAddress}/{(vpnState.Adapter.PrefixLength?.ToString() ?? "?")}"
            : "(none)";

        // The signature deliberately excludes the ever-ticking "current state duration"
        // value - including it would defeat the whole point of change-detection, since
        // it changes every poll. Only fields whose change is actually newsworthy count.
        string signature = string.Join(
            '|',
            config.ProfileName,
            vpnState.State,
            adapterDisplay,
            vpnIpDisplay,
            internet.State,
            fortiVpnRunning,
            sslVpnDaemonRunning,
            unexpectedDisconnects,
            automaticRecoveries,
            failedRecoveries);

        bool isFirstRender = _lastRenderedSignature is null;
        bool contentChanged = signature != _lastRenderedSignature;
        bool heartbeatElapsed = (now - _lastRenderTime).TotalMilliseconds >= config.HeartbeatIntervalMs;

        if (!isFirstRender && !contentChanged && !heartbeatElapsed)
        {
            return;
        }

        _lastRenderedSignature = signature;
        _lastRenderTime = now;

        TimeSpan duration = correlator.CurrentStateDuration(now);
        string durationDisplay = FormatHms(duration);

        // Console.Clear() throws IOException ("The handle is invalid") when stdout is
        // redirected/piped rather than a real console buffer (e.g. output captured to a
        // file or another process) - skip it in that case rather than losing every render.
        if (!Console.IsOutputRedirected)
        {
            Console.Clear();
        }

        // config.AutoReconnectEnabled is the EFFECTIVE value by the time the dashboard
        // runs: Program downgrades it to false when FortiClient COM turns out to be
        // unavailable, so this header can never promise a capability we lack.
        Console.WriteLine(config.AutoReconnectEnabled
            ? "VPN WATCHDOG -- AUTO-RECONNECT ARMED (connect only; never disconnects)"
            : "VPN WATCHDOG -- OBSERVE MODE (auto-reconnect off; watching only)");
        Console.WriteLine();
        PrintField("Profile:", config.ProfileName);
        PrintField("VPN:", vpnState.State.ToString());
        PrintField("Adapter:", adapterDisplay);
        PrintField("VPN IP:", vpnIpDisplay);
        PrintField("Internet:", internet.State.ToString());
        PrintField("FortiVPN:", fortiVpnRunning ? "RUNNING" : "NOT RUNNING");
        PrintField("SSLVPN daemon:", sslVpnDaemonRunning ? "RUNNING" : "NOT RUNNING");
        PrintField("Current state duration:", durationDisplay);
        Console.WriteLine();
        Console.WriteLine("Events this session:");
        Console.WriteLine($"  Unexpected disconnects: {unexpectedDisconnects}");
        Console.WriteLine($"  Automatic recoveries:   {automaticRecoveries}");
        Console.WriteLine($"  Failed recoveries:      {failedRecoveries}");
        Console.WriteLine();

        string bar = new string('=', 78);
        Console.WriteLine(bar);
        Console.WriteLine(config.AutoReconnectEnabled
            ? "Mode: AUTO-RECONNECT -- may ask FortiClient to CONNECT after the grace period."
            : "Mode: OBSERVE ONLY -- this run will not connect or modify the VPN.");
        Console.WriteLine("      It never disconnects the VPN and never handles credentials.");
        Console.WriteLine(bar);
    }

    private static void PrintField(string label, string value)
    {
        string padded = label.Length >= LabelColumnWidth ? label + " " : label.PadRight(LabelColumnWidth);
        Console.WriteLine(padded + value);
    }

    private static bool IsProcessRunning(IReadOnlyList<ProcessSnapshot> processes, string nameContains)
    {
        foreach (ProcessSnapshot process in processes)
        {
            if (process.IsRunning && process.ProcessName.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Formats as hh:mm:ss, with the hour component reflecting TOTAL elapsed hours
    /// (not wrapped at 24) since this watchdog may run for multiple days at a stretch.</summary>
    private static string FormatHms(TimeSpan duration)
    {
        int totalHours = (int)duration.TotalHours;
        return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
