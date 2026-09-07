using System.Runtime.CompilerServices;
using VpnWatchdog.Core;

namespace VpnWatchdog.Tests.Fakes;

/// <summary>
/// In-memory test double for <see cref="IConnectionStateProvider"/>. A test sets
/// <see cref="NextSnapshot"/> to whatever <see cref="AdapterSnapshot"/> it wants
/// returned on the *next* call - no real Windows networking APIs
/// (<c>System.Net.NetworkInformation</c>) are touched, so this is fully hermetic.
/// </summary>
public sealed class FakeConnectionStateProvider : IConnectionStateProvider
{
    /// <summary>
    /// The snapshot <see cref="GetAdapterSnapshotAsync"/> returns on its next
    /// call. A test mutates this between <c>Ingest</c> calls to script a
    /// scenario (e.g. connected -> adapter down -> adapter back up).
    /// </summary>
    public AdapterSnapshot NextSnapshot { get; set; } = AdapterSnapshot.NotFound(DateTimeOffset.UnixEpoch);

    /// <summary>Number of times <see cref="GetAdapterSnapshotAsync"/> has been called.</summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// The <c>adapterDescriptionPattern</c> passed on the most recent call - lets a
    /// test assert the caller is discovering the adapter dynamically rather than
    /// hardcoding an adapter name or index.
    /// </summary>
    public string? LastPatternRequested { get; private set; }

    public Task<AdapterSnapshot> GetAdapterSnapshotAsync(string adapterDescriptionPattern, CancellationToken ct)
    {
        CallCount++;
        LastPatternRequested = adapterDescriptionPattern;
        return Task.FromResult(NextSnapshot);
    }
}

/// <summary>
/// In-memory test double for <see cref="IInternetStateProvider"/>. A test sets
/// <see cref="NextSnapshot"/> to control exactly what internet-reachability
/// evidence is returned on the next call.
/// </summary>
public sealed class FakeInternetStateProvider : IInternetStateProvider
{
    public InternetSnapshot NextSnapshot { get; set; } =
        new(InternetState.Unknown, null, null, null, string.Empty, DateTimeOffset.UnixEpoch);

    public int CallCount { get; private set; }

    public Task<InternetSnapshot> GetCurrentStateAsync(CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(NextSnapshot);
    }
}

/// <summary>
/// In-memory test double for <see cref="IFortiClientProcessProvider"/>. A test
/// sets <see cref="NextSnapshot"/> to the list of process observations it wants
/// returned on the next call (e.g. flip a process's <c>IsRunning</c> to false to
/// simulate it disappearing).
/// </summary>
public sealed class FakeFortiClientProcessProvider : IFortiClientProcessProvider
{
    public IReadOnlyList<ProcessSnapshot> NextSnapshot { get; set; } = Array.Empty<ProcessSnapshot>();

    public int CallCount { get; private set; }

    public Task<IReadOnlyList<ProcessSnapshot>> GetProcessesAsync(CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(NextSnapshot);
    }
}

/// <summary>
/// In-memory test double for <see cref="IFortiClientLogMonitor"/>. A test
/// enqueues the <see cref="LogEvent"/>s it wants "read from the log" onto
/// <see cref="PendingEvents"/>; <see cref="TailNewEventsAsync"/> drains and
/// yields whatever is queued at the time of the call, then completes - it does
/// not loop forever waiting for more, so a test can safely enumerate it to
/// completion with <c>await foreach</c>. No real file I/O of any kind occurs.
/// </summary>
public sealed class FakeLogMonitor : IFortiClientLogMonitor
{
    /// <summary>Events a test queues up to be "discovered" on the next tail call.</summary>
    public Queue<LogEvent> PendingEvents { get; } = new();

    /// <summary>Number of times <see cref="TailNewEventsAsync"/> has been invoked.</summary>
    public int CallCount { get; private set; }

    public async IAsyncEnumerable<LogEvent> TailNewEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        CallCount++;
        while (PendingEvents.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var next = PendingEvents.Dequeue();

            // Yield control back to the caller like a real async source would,
            // without introducing any real I/O or timing dependency.
            await Task.Yield();

            yield return next;
        }
    }
}
