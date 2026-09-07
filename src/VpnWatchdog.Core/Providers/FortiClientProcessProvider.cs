using System.ComponentModel;
using System.Diagnostics;

namespace VpnWatchdog.Core.Providers;

/// <summary>
/// Reports on running FortiClient-related processes by name via
/// System.Diagnostics.Process.GetProcessesByName. Read-only / observe-only:
/// this type never starts, stops, signals, or otherwise controls any process -
/// it only observes whether configured process names are currently running.
/// </summary>
public sealed class FortiClientProcessProvider : IFortiClientProcessProvider
{
    private static readonly IReadOnlyList<string> DefaultProcessNames =
        new[] { "FortiVPN", "FortiSSLVPNdaemon", "FortiTray", "FortiSettings" };

    private readonly IReadOnlyList<string> _processNames;

    /// <param name="processNames">
    /// Process names to observe, WITHOUT the ".exe" suffix (matching
    /// System.Diagnostics.Process naming). When null, defaults to the standard
    /// FortiClient process set: FortiVPN, FortiSSLVPNdaemon, FortiTray, FortiSettings.
    /// </param>
    public FortiClientProcessProvider(IReadOnlyList<string>? processNames = null)
    {
        _processNames = processNames ?? DefaultProcessNames;
    }

    public Task<IReadOnlyList<ProcessSnapshot>> GetProcessesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var snapshots = new List<ProcessSnapshot>(_processNames.Count);

        foreach (var name in _processNames)
        {
            ct.ThrowIfCancellationRequested();
            snapshots.Add(CaptureSnapshot(name, now));
        }

        return Task.FromResult<IReadOnlyList<ProcessSnapshot>>(snapshots);
    }

    /// <summary>
    /// Looks up all processes matching <paramref name="name"/> and captures a
    /// snapshot from the first one found. Every Process instance returned by
    /// GetProcessesByName is disposed before returning - including any beyond
    /// the first that we don't otherwise use - because this provider runs for
    /// days and leaked process handles would accumulate over time.
    /// </summary>
    private static ProcessSnapshot CaptureSnapshot(string name, DateTimeOffset now)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            if (processes.Length == 0)
            {
                return new ProcessSnapshot(
                    ProcessName: name,
                    IsRunning: false,
                    ProcessId: null,
                    StartTime: null,
                    ObservedAt: now);
            }

            var process = processes[0];
            return new ProcessSnapshot(
                ProcessName: name,
                IsRunning: true,
                ProcessId: process.Id,
                StartTime: TryGetStartTime(process),
                ObservedAt: now);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Some Fortinet processes run as SYSTEM/session-0, and querying StartTime
    /// as a normal (non-elevated) user throws Win32Exception "Access is denied"
    /// - confirmed empirically during Phase 1 investigation on this exact
    /// machine. That is expected/benign here, so it is swallowed and reported
    /// as an unknown start time rather than crashing or propagating.
    /// </summary>
    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
