using System.Collections.Concurrent;
using VpnWatchdog.Core;
using VpnWatchdog.Core.Logging;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// The behaviour EVERY <see cref="IVpnActivityLog"/> implementation must satisfy,
/// written once and run against each one (see the two derived classes lower down).
/// <see cref="SqliteVpnActivityLog"/> is the shared on-disk history and
/// <see cref="InMemoryVpnActivityLog"/> is the fallback its constructor tells the
/// caller to use; the GUI and the CLI pick between them at startup and neither
/// caller adapts its expectations to the choice, so the two must not drift apart.
///
/// <para>
/// Every timestamp is an explicit <see cref="DateTimeOffset"/> - never
/// <c>DateTimeOffset.Now</c> - so ordering and retention are pinned exactly rather
/// than "whichever way the clock happened to fall". Nothing here touches COM,
/// FortiClient, the network or a real VPN; the SQLite tests use a unique temporary
/// file per test and delete it afterwards.
/// </para>
/// </summary>
public abstract class VpnActivityLogContractTests : IDisposable
{
    protected const string Profile = "MLA-DEV-VPN-2-LocalAuth";

    /// <summary>
    /// Whole seconds and a deliberately non-UTC offset. Note that
    /// <see cref="DateTimeOffset"/> equality compares the INSTANT, so an
    /// implementation would be free to normalise to UTC on the way to storage; what
    /// it must never do is shift, truncate or lose the moment itself.
    /// </summary>
    protected static readonly DateTimeOffset T0 = new(2026, 9, 6, 8, 0, 0, TimeSpan.FromHours(-4));

    /// <summary>
    /// Builds the implementation under test with the given retention cap. Every call
    /// must hand back a fresh, EMPTY log - the retention tests depend on it.
    /// </summary>
    protected abstract IVpnActivityLog CreateLog(int maxEntries);

    /// <summary>
    /// How many entries this implementation may hold ABOVE its configured cap before
    /// pruning catches up. Zero for a ring buffer, non-zero for a store that prunes
    /// periodically rather than on every write - see the overrides for why each is
    /// what it is. Retention is asserted against <c>cap + this</c> rather than against
    /// <c>cap</c> so the contract states the bound each implementation actually
    /// promises, instead of quietly assuming they promise the same one.
    /// </summary>
    protected abstract int RetentionHeadroomEntries { get; }

    public virtual void Dispose()
    {
    }

    /// <summary>An ordinary entry, so each test only spells out what it is actually about.</summary>
    protected static VpnActivityEntry EntryAt(DateTimeOffset at, string message) =>
        new(at, VpnActivityKind.VpnDisconnected, message, Profile, null);

    // ==================================================================
    // 1. ROUND-TRIP - what goes in is exactly what comes back out.
    // ==================================================================

    [Fact]
    public void Record_ThenGetRecent_PreservesEveryFieldExactly()
    {
        var sut = CreateLog(100);
        var written = new VpnActivityEntry(
            T0,
            VpnActivityKind.AutoReconnectTriggered,
            "Grace period elapsed - asking FortiClient to reconnect.",
            Profile,
            "attempt 1 of 5");

        sut.Record(written);

        var read = Assert.Single(sut.GetRecent(10));
        Assert.Equal(T0, read.Timestamp);
        Assert.Equal(VpnActivityKind.AutoReconnectTriggered, read.Kind);
        Assert.Equal("Grace period elapsed - asking FortiClient to reconnect.", read.Message);
        Assert.Equal(Profile, read.ProfileName);
        Assert.Equal("attempt 1 of 5", read.Detail);

        // The record's own structural equality, so a field added to VpnActivityEntry
        // later cannot be silently dropped on the way through the store.
        Assert.Equal(written, read);
    }

    [Fact]
    public void Record_WithNullProfileNameAndDetail_ReadsBackAsNullRatherThanEmptyString()
    {
        // Both fields are genuinely optional: an internet-level event has no profile,
        // and most entries carry no detail. A store that round-tripped null as "" would
        // make the log window render an empty column instead of omitting it, and
        // VpnActivityEntry compares the two as different values.
        var sut = CreateLog(100);
        var written = new VpnActivityEntry(T0, VpnActivityKind.InternetLost, "Internet connectivity lost.", null, null);

        sut.Record(written);

        var read = Assert.Single(sut.GetRecent(10));
        Assert.Equal(T0, read.Timestamp);
        Assert.Equal(VpnActivityKind.InternetLost, read.Kind);
        Assert.Equal("Internet connectivity lost.", read.Message);
        Assert.Null(read.ProfileName);
        Assert.Null(read.Detail);
        Assert.Equal(written, read);
    }

    [Fact]
    public void Record_WithEveryActivityKind_RoundTripsThatKind()
    {
        // The SQLite store persists the enum by NAME, so a kind added to the middle of
        // VpnActivityKind must not need a schema change to survive. Sweeping the whole
        // enum means a new member is covered the moment it is declared.
        var kinds = Enum.GetValues<VpnActivityKind>();
        var sut = CreateLog(kinds.Length + 10);

        for (var i = 0; i < kinds.Length; i++)
        {
            sut.Record(new VpnActivityEntry(T0.AddSeconds(i), kinds[i], $"entry for {kinds[i]}", Profile, null));
        }

        var recent = sut.GetRecent(kinds.Length + 10);

        Assert.Equal(kinds.Length, recent.Count);
        // Enumerable.Reverse spelled out: with C# 14 first-class spans, `array.Reverse()`
        // binds to MemoryExtensions.Reverse(Span<T>) - an in-place void - not LINQ.
        Assert.Equal(Enumerable.Reverse(kinds).ToArray(), recent.Select(entry => entry.Kind).ToArray());
    }

    [Fact]
    public void Record_ThroughTheConvenienceOverload_LandsAnEntryInTheSameStore()
    {
        // The default interface method stamps the entry "now", so its timestamp is
        // deliberately not asserted here - only that the overload actually reaches the
        // same store as the explicit one.
        var sut = CreateLog(100);

        sut.Record(VpnActivityKind.MonitoringStarted, "Monitoring started.", Profile);

        var read = Assert.Single(sut.GetRecent(10));
        Assert.Equal(VpnActivityKind.MonitoringStarted, read.Kind);
        Assert.Equal("Monitoring started.", read.Message);
        Assert.Equal(Profile, read.ProfileName);
        Assert.Null(read.Detail);
    }

    // ==================================================================
    // 2. ORDERING - newest first, because that is what a log window shows.
    // ==================================================================

    [Fact]
    public void GetRecent_ReturnsEntriesNewestFirst()
    {
        var sut = CreateLog(100);

        sut.Record(EntryAt(T0, "oldest"));
        sut.Record(EntryAt(T0.AddSeconds(30), "middle"));
        sut.Record(EntryAt(T0.AddMinutes(5), "newest"));

        Assert.Equal(
            new[] { "newest", "middle", "oldest" },
            sut.GetRecent(10).Select(entry => entry.Message).ToArray());
    }

    [Fact]
    public void GetRecent_OnAnEmptyLog_ReturnsAnEmptyList()
    {
        Assert.Empty(CreateLog(100).GetRecent(10));
    }

    // ==================================================================
    // 3. THE max PARAMETER - a cap on what is RETURNED, distinct from the
    //    retention cap on what is KEPT.
    // ==================================================================

    [Fact]
    public void GetRecent_CapsTheNumberReturnedAtMax_AndReturnsTheNewestOnes()
    {
        var sut = CreateLog(100);

        for (var i = 0; i < 10; i++)
        {
            sut.Record(EntryAt(T0.AddSeconds(i), $"entry-{i:D2}"));
        }

        Assert.Equal(
            new[] { "entry-09", "entry-08", "entry-07" },
            sut.GetRecent(3).Select(entry => entry.Message).ToArray());
    }

    [Fact]
    public void GetRecent_WithMaxOfOne_ReturnsOnlyTheNewestEntry()
    {
        var sut = CreateLog(100);

        sut.Record(EntryAt(T0, "older"));
        sut.Record(EntryAt(T0.AddSeconds(1), "newer"));

        var read = Assert.Single(sut.GetRecent(1));
        Assert.Equal("newer", read.Message);
    }

    [Fact]
    public void GetRecent_WithMaxLargerThanTheLog_ReturnsEverythingItHas()
    {
        var sut = CreateLog(100);

        sut.Record(EntryAt(T0, "only-one"));

        Assert.Equal(new[] { "only-one" }, sut.GetRecent(1000).Select(entry => entry.Message).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetRecent_WithANonPositiveMax_ReturnsAnEmptyListRatherThanThrowing(int max)
    {
        var sut = CreateLog(100);

        sut.Record(EntryAt(T0, "present"));

        Assert.Empty(sut.GetRecent(max));
    }

    // ==================================================================
    // 4. RETENTION - the property that lets this run for DAYS.
    //    The watchdog logs on every state change over an unattended multi-day
    //    run; without a hard bound on what is KEPT the store would grow without
    //    limit, which invariant 6 forbids outright.
    // ==================================================================

    [Fact]
    public void Record_FarBeyondTheCap_KeepsOnlyTheNewestEntriesAndPrunesTheOldest()
    {
        const int maxEntries = 20;
        var sut = CreateLog(maxEntries);

        // 25x the cap, in strictly increasing timestamp order so "oldest" is
        // unambiguous however the implementation orders internally.
        for (var i = 0; i < 500; i++)
        {
            sut.Record(EntryAt(T0.AddSeconds(i), $"entry-{i:D3}"));
        }

        var recent = sut.GetRecent(10_000);

        AssertWithinRetentionBound(recent.Count, maxEntries, writes: 500);

        // Whatever survived must be the newest CONTIGUOUS run, newest first: that is
        // one assertion covering ordering, "the newest survive" and "the oldest were
        // the ones pruned", without depending on exactly where pruning fell.
        for (var i = 0; i < recent.Count; i++)
        {
            Assert.Equal($"entry-{499 - i:D3}", recent[i].Message);
        }

        Assert.DoesNotContain(recent, entry => entry.Message == "entry-000");
    }

    [Fact]
    public void Record_ContinuingLongAfterPruningHasStarted_DoesNotGrowTheStore()
    {
        // The single most important retention property: the bound is a BOUND, not a
        // one-off trim. A store that pruned once and then kept growing would pass the
        // test above and still fill a disk over a multi-day run.
        const int maxEntries = 20;
        var sut = CreateLog(maxEntries);

        for (var i = 0; i < 500; i++)
        {
            sut.Record(EntryAt(T0.AddSeconds(i), $"entry-{i:D4}"));
        }

        var afterFirstRun = sut.GetRecent(10_000).Count;
        AssertWithinRetentionBound(afterFirstRun, maxEntries, writes: 500);

        for (var i = 500; i < 2_000; i++)
        {
            sut.Record(EntryAt(T0.AddSeconds(i), $"entry-{i:D4}"));
        }

        var recent = sut.GetRecent(10_000);

        // Four times the writes, same bound - that is what "bounded" means.
        AssertWithinRetentionBound(recent.Count, maxEntries, writes: 2_000);
        Assert.Equal("entry-1999", recent[0].Message);
    }

    /// <summary>
    /// Asserts the store is holding no more than its configured cap plus the headroom
    /// this implementation documents, with a failure message that says which half of
    /// the retention contract broke.
    /// </summary>
    private void AssertWithinRetentionBound(int retained, int maxEntries, int writes)
    {
        var bound = maxEntries + RetentionHeadroomEntries;

        Assert.True(
            retained <= bound,
            $"Retention is what keeps a multi-day unattended run from growing without bound (invariant 6). " +
            $"After {writes} writes with a cap of {maxEntries} the store must hold at most {bound} entries " +
            $"(cap + {RetentionHeadroomEntries} documented pruning headroom), but it held {retained}.");

        Assert.True(
            retained > 0,
            $"After {writes} writes the store must still hold the most recent entries - pruning must remove the " +
            $"oldest, not empty the log. It held {retained}.");
    }

    // ==================================================================
    // 5. MUST NOT THROW - logging is instrumentation.
    //    A log line that cannot be written is a nuisance; monitoring that stops
    //    because a log line could not be written is the failure this whole app
    //    exists to prevent.
    // ==================================================================

    [Fact]
    public void Record_WithANullEntry_DoesNotThrow()
    {
        // The contract says non-null, but "never throws" is absolute: a null-forgiving
        // caller must not be able to bring down the monitor loop with a
        // NullReferenceException.
        var sut = CreateLog(100);

        var thrown = Xunit.Record.Exception(() => sut.Record(null!));

        Assert.True(thrown is null, "Record must swallow even a null entry rather than throw. Threw: " + thrown);
        Assert.Empty(sut.GetRecent(10));
    }

    // ==================================================================
    // 6. THREAD SAFETY - the log is written from the poll loop, the reconnect
    //    path and the UI thread at the same time.
    // ==================================================================

    [Fact]
    public void Record_FromManyThreadsAtOnce_NeitherThrowsNorLosesEntriesBeyondTheCap()
    {
        const int maxEntries = 200;
        const int writers = 8;
        const int perWriter = 50;
        const int totalWrites = writers * perWriter; // 400, comfortably past the cap

        var sut = CreateLog(maxEntries);
        var failures = new ConcurrentQueue<Exception>();

        Parallel.For(0, writers, writer =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                try
                {
                    sut.Record(new VpnActivityEntry(
                        T0.AddSeconds((writer * perWriter) + i),
                        VpnActivityKind.VpnDisconnected,
                        $"writer-{writer}-entry-{i:D2}",
                        Profile,
                        null));
                }
                catch (Exception ex)
                {
                    // Collected rather than rethrown so one failing thread does not
                    // mask how many others also failed.
                    failures.Enqueue(ex);
                }
            }
        });

        Assert.True(
            failures.IsEmpty,
            "Record must never throw into its caller, however many threads are logging at once. Threw: " +
            string.Join(" | ", failures.Select(ex => ex.ToString())));

        var recent = sut.GetRecent(totalWrites * 2);

        // At least the cap survived: fewer would mean writes were silently DROPPED
        // under contention, which is a different (and worse) failure than pruning.
        Assert.True(
            recent.Count >= maxEntries,
            $"{totalWrites} concurrent writes against a cap of {maxEntries} must leave at least the cap behind; " +
            $"only {recent.Count} survived, so entries were lost to contention rather than to retention.");

        AssertWithinRetentionBound(recent.Count, maxEntries, totalWrites);

        // Every surviving entry is a distinct write: no interleaving produced a
        // duplicated or half-written row.
        Assert.Equal(recent.Count, recent.Select(entry => entry.Message).Distinct().Count());
    }

    [Fact]
    public void GetRecent_WhileOtherThreadsAreRecording_NeitherThrowsNorReturnsATornEntry()
    {
        // The GUI paints its log window from the UI thread while the poll loop writes.
        const int maxEntries = 500;
        var sut = CreateLog(maxEntries);
        var failures = new ConcurrentQueue<Exception>();

        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    if (worker % 2 == 0)
                    {
                        sut.Record(new VpnActivityEntry(
                            T0.AddSeconds((worker * 40) + i),
                            VpnActivityKind.AutoReconnectSucceeded,
                            $"worker-{worker}-entry-{i:D2}",
                            Profile,
                            "detail"));
                    }
                    else
                    {
                        foreach (var entry in sut.GetRecent(50))
                        {
                            // Every entry handed out must be fully formed - never a
                            // null slot glimpsed mid-write.
                            Assert.NotNull(entry);
                            Assert.NotNull(entry.Message);
                            Assert.Equal(VpnActivityKind.AutoReconnectSucceeded, entry.Kind);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }
        });

        Assert.True(
            failures.IsEmpty,
            "Reading the log while it is being written must not throw or expose a partially written entry. " +
            "Threw: " + string.Join(" | ", failures.Select(ex => ex.ToString())));
    }
}

/// <summary>
/// The in-memory ring buffer: the fallback the GUI and CLI use when
/// <see cref="SqliteVpnActivityLog"/>'s constructor reports the file is unusable.
/// </summary>
public sealed class InMemoryVpnActivityLogTests : VpnActivityLogContractTests
{
    protected override IVpnActivityLog CreateLog(int maxEntries) => new InMemoryVpnActivityLog(maxEntries);

    /// <summary>Zero: a ring buffer overwrites in place, so the cap is exact at every instant.</summary>
    protected override int RetentionHeadroomEntries => 0;

    [Fact]
    public void Record_BeyondTheCapacity_KeepsExactlyTheCapacityAndNoMore()
    {
        // Sharper than the shared contract allows, because a ring buffer can be held to
        // the exact number: its memory footprint is fixed at construction.
        var sut = new InMemoryVpnActivityLog(capacity: 10);

        for (var i = 0; i < 25; i++)
        {
            sut.Record(EntryAt(T0.AddSeconds(i), $"entry-{i:D2}"));
        }

        var recent = sut.GetRecent(1000);

        Assert.Equal(10, recent.Count);
        Assert.Equal(
            new[]
            {
                "entry-24", "entry-23", "entry-22", "entry-21", "entry-20",
                "entry-19", "entry-18", "entry-17", "entry-16", "entry-15",
            },
            recent.Select(entry => entry.Message).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_WithANonsensicalCapacity_ClampsInsteadOfThrowing(int capacity)
    {
        // A bad number is a configuration typo. A log that refuses to construct is worse
        // than a log that keeps one entry - the fallback has to be able to construct,
        // because it is what the caller falls back TO.
        var sut = new InMemoryVpnActivityLog(capacity);

        sut.Record(EntryAt(T0, "kept"));

        var read = Assert.Single(sut.GetRecent(10));
        Assert.Equal("kept", read.Message);
    }
}

/// <summary>
/// The SQLite log: the FIXED, shared on-disk history the GUI and the CLI both append
/// to. Adds the tests only a store with an outside world can have - the ones proving a
/// broken, missing or vanished database degrades instead of taking monitoring down.
/// </summary>
public sealed class SqliteVpnActivityLogTests : VpnActivityLogContractTests
{
    private readonly string _root;
    private readonly List<SqliteVpnActivityLog> _created = new();

    public SqliteVpnActivityLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "VpnWatchdogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    protected override IVpnActivityLog CreateLog(int maxEntries) =>
        CreateLogAt(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"), maxEntries);

    /// <summary>
    /// 63: the store prunes every 64 inserts rather than on every write, because a
    /// per-insert <c>COUNT(*)</c> is a full scan in SQLite and would cost about as much
    /// as the prune it was trying to avoid. Its own documentation states the table can
    /// therefore transiently hold at most <c>ActivityLogMaxEntries + 63</c> rows - still
    /// a hard bound, which is all invariant 6 requires. This number is pinned here on
    /// purpose: if the cadence changes, the promised bound changes with it and someone
    /// should have to say so.
    /// </summary>
    protected override int RetentionHeadroomEntries => 63;

    private SqliteVpnActivityLog CreateLogAt(string databasePath, int maxEntries)
    {
        var log = new SqliteVpnActivityLog(databasePath, maxEntries);
        _created.Add(log);
        return log;
    }

    /// <summary>
    /// Releases the native file handles Microsoft.Data.Sqlite keeps in its connection
    /// pool for this database, so the test can then delete or replace the file.
    /// <para>
    /// Disposing a THROWAWAY instance is the way to do it: the pool is keyed by
    /// connection string, so clearing it through a second instance on the same path also
    /// releases the handles held for the instance under test - which must stay alive,
    /// because the whole point is to break the store underneath a live log. Without this
    /// the delete fails with a sharing violation.
    /// </para>
    /// </summary>
    private static void ReleasePooledHandles(string databasePath)
    {
        using var throwaway = new SqliteVpnActivityLog(databasePath, maxEntries: 1_000);
    }

    public override void Dispose()
    {
        foreach (var log in _created)
        {
            // Disposing releases the pooled handles, which is what makes the directory
            // below deletable.
            log.Dispose();
        }

        // Best effort on purpose: these files live under the OS temp directory, and
        // failing a test over cleanup would be pure noise.
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        base.Dispose();
    }

    // ==================================================================
    // 7. CONSTRUCTION IS THE ONE PLACE FAILURE SURFACES.
    //    The constructor throws DELIBERATELY when the store cannot be opened:
    //    that is the caller's single chance to notice and fall back to
    //    InMemoryVpnActivityLog rather than run with no trail at all. Every
    //    other member must stay silent (section 8).
    // ==================================================================

    [Fact]
    public void Constructor_WhenThePathIsADirectory_ThrowsSoTheCallerCanFallBackToTheInMemoryLog()
    {
        var databasePath = Path.Combine(_root, "i-am-a-directory");
        Directory.CreateDirectory(databasePath);

        Assert.ThrowsAny<Exception>(() => new SqliteVpnActivityLog(databasePath, maxEntries: 100).Dispose());
    }

    [Fact]
    public void Constructor_WhenTheParentDirectoryCannotBeCreated_ThrowsSoTheCallerCanFallBackToTheInMemoryLog()
    {
        // The parent of the requested path is an existing FILE, so the directory can
        // never be created - the shape of a real misconfigured ActivityLogPath.
        var blockingFile = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blockingFile, "a file, so nothing can be created underneath it");

        Assert.ThrowsAny<Exception>(
            () => new SqliteVpnActivityLog(Path.Combine(blockingFile, "nested", "activity-log.db"), maxEntries: 100).Dispose());
    }

    [Fact]
    public void Constructor_WhenTheFileIsNotASqliteDatabase_ThrowsSoTheCallerCanFallBackToTheInMemoryLog()
    {
        var databasePath = Path.Combine(_root, "corrupt.db");
        File.WriteAllText(databasePath, "this is not a SQLite database");

        Assert.ThrowsAny<Exception>(() => new SqliteVpnActivityLog(databasePath, maxEntries: 100).Dispose());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_WithANonsensicalCap_ClampsInsteadOfDeletingEveryEntryItWrites(int maxEntries)
    {
        // A cap of zero would delete each row immediately after writing it, leaving a
        // permanently empty log window and a user convinced the app is broken.
        var sut = CreateLogAt(Path.Combine(_root, "clamped.db"), maxEntries);

        sut.Record(EntryAt(T0, "kept"));

        var read = Assert.Single(sut.GetRecent(10));
        Assert.Equal("kept", read.Message);
    }

    // ==================================================================
    // 8. MUST NOT THROW - THE MOST IMPORTANT TESTS IN THIS FILE.
    //    Once the object exists, nothing it does may escape into the caller.
    //    These break the store UNDERNEATH a live log, which is the realistic
    //    multi-day failure: a cleanup tool wiping %LOCALAPPDATA%, a user
    //    deleting the file to "reset the log", a roaming profile going away.
    // ==================================================================

    [Fact]
    public void Record_WhenTheDirectoryVanishesAndCannotBeRecreated_DoesNotThrow()
    {
        var directory = Path.Combine(_root, "gone");
        var databasePath = Path.Combine(directory, "activity-log.db");

        var sut = CreateLogAt(databasePath, maxEntries: 100);
        sut.Record(EntryAt(T0, "written while the store was healthy"));

        // Break it: the directory disappears and a FILE takes its place, so it can
        // never be re-created either.
        ReleasePooledHandles(databasePath);
        Directory.Delete(directory, recursive: true);
        File.WriteAllText(directory, "a file now stands where the log directory used to be");

        // Twice on purpose. The first call fails opening the database (the schema is
        // still believed to be in place); that failure marks the schema unknown, so the
        // second call fails EARLIER, trying to re-create the directory. Both paths must
        // swallow, and only calling twice exercises both.
        var thrown = Xunit.Record.Exception(() =>
        {
            sut.Record(EntryAt(T0.AddSeconds(1), "written while the store was broken"));
            sut.Record(EntryAt(T0.AddSeconds(2), "written while the store was still broken"));
        });

        Assert.True(
            thrown is null,
            "Record must degrade silently when the store becomes unusable mid-run: losing the history is a " +
            "nuisance, losing monitoring is the failure this app exists to prevent. Threw: " + thrown);
    }

    [Fact]
    public void Record_WhenTheDatabasePathBecomesADirectory_DoesNotThrow()
    {
        var databasePath = Path.Combine(_root, "becomes-a-directory.db");

        var sut = CreateLogAt(databasePath, maxEntries: 100);
        sut.Record(EntryAt(T0, "written while the store was healthy"));

        ReleasePooledHandles(databasePath);
        File.Delete(databasePath);
        Directory.CreateDirectory(databasePath); // SQLite can never open a directory

        var thrown = Xunit.Record.Exception(() =>
        {
            sut.Record(EntryAt(T0.AddSeconds(1), "written while the path was a directory"));
            sut.Record(EntryAt(T0.AddSeconds(2), "and again"));
        });

        Assert.True(
            thrown is null,
            "Record must never throw into its caller, however broken the store is. Threw: " + thrown);
    }

    [Fact]
    public void GetRecent_WhenTheStoreBecameUnusable_ReturnsAnEmptyListRatherThanThrowing()
    {
        var databasePath = Path.Combine(_root, "unreadable.db");

        var sut = CreateLogAt(databasePath, maxEntries: 100);
        sut.Record(EntryAt(T0, "written while the store was healthy"));

        ReleasePooledHandles(databasePath);
        File.Delete(databasePath);
        Directory.CreateDirectory(databasePath);

        IReadOnlyList<VpnActivityEntry>? recent = null;
        var thrown = Xunit.Record.Exception(() => recent = sut.GetRecent(50));

        Assert.True(
            thrown is null,
            "GetRecent must hand back an empty list rather than throw when the store cannot be read - the GUI " +
            "calls it to paint the log window and must not be brought down by it. Threw: " + thrown);
        Assert.NotNull(recent);
        Assert.Empty(recent);
    }

    [Fact]
    public void GetRecent_WhenTheDatabaseFileHasBeenDeleted_ReturnsAnEmptyListWithoutRecreatingIt()
    {
        // A read must never create a database - otherwise merely opening the log window
        // would resurrect a file the user just deleted.
        var databasePath = Path.Combine(_root, "deleted.db");

        var sut = CreateLogAt(databasePath, maxEntries: 100);
        sut.Record(EntryAt(T0, "written while the store was healthy"));

        ReleasePooledHandles(databasePath);
        File.Delete(databasePath);

        Assert.Empty(sut.GetRecent(50));
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public void GetRecent_OnAFreshlyCreatedDatabase_ReturnsAnEmptyList()
    {
        // First ever run: the table exists but there is genuinely nothing to show. That
        // is an empty list, not an exception.
        var sut = CreateLogAt(Path.Combine(_root, "never-written.db"), maxEntries: 100);

        Assert.Empty(sut.GetRecent(50));
    }

    [Fact]
    public void Record_AfterTheStoreIsRepaired_StartsWritingAgain()
    {
        // Degrading silently must not mean giving up permanently: over a days-long run
        // the directory may well come back, and the trail should resume by itself.
        var directory = Path.Combine(_root, "temporarily-gone");
        var databasePath = Path.Combine(directory, "activity-log.db");

        var sut = CreateLogAt(databasePath, maxEntries: 100);
        sut.Record(EntryAt(T0, "before"));

        ReleasePooledHandles(databasePath);
        Directory.Delete(directory, recursive: true);

        sut.Record(EntryAt(T0.AddSeconds(1), "while gone"));

        var read = Assert.Single(sut.GetRecent(50));
        Assert.Equal("while gone", read.Message);
    }

    // ==================================================================
    // 9. ONE SHARED HISTORY - the GUI and the CLI point at the same fixed path
    //    deliberately, so a second reader must see what the first writer wrote.
    // ==================================================================

    [Fact]
    public void GetRecent_FromASecondLogOnTheSamePath_SeesEntriesWrittenByTheFirst()
    {
        var databasePath = Path.Combine(_root, "shared.db");

        var writer = CreateLogAt(databasePath, maxEntries: 100);
        writer.Record(new VpnActivityEntry(T0, VpnActivityKind.ManualConnectRequested, "Connect requested by user.", Profile, null));

        var reader = CreateLogAt(databasePath, maxEntries: 100);

        var read = Assert.Single(reader.GetRecent(50));
        Assert.Equal(T0, read.Timestamp);
        Assert.Equal(VpnActivityKind.ManualConnectRequested, read.Kind);
        Assert.Equal("Connect requested by user.", read.Message);
        Assert.Equal(Profile, read.ProfileName);
        Assert.Null(read.Detail);
    }

    [Fact]
    public void Record_FromTwoLogsOnTheSamePath_InterleavesIntoOneNewestFirstHistory()
    {
        var databasePath = Path.Combine(_root, "interleaved.db");

        var gui = CreateLogAt(databasePath, maxEntries: 100);
        var cli = CreateLogAt(databasePath, maxEntries: 100);

        gui.Record(EntryAt(T0, "first"));
        cli.Record(EntryAt(T0.AddSeconds(1), "second"));
        gui.Record(EntryAt(T0.AddSeconds(2), "third"));

        Assert.Equal(
            new[] { "third", "second", "first" },
            cli.GetRecent(50).Select(entry => entry.Message).ToArray());
    }

    [Fact]
    public void Record_SurvivesTheProcessThatWroteItGoingAway()
    {
        // The whole reason this store is on disk: history has to outlive a restart.
        var databasePath = Path.Combine(_root, "durable.db");

        var first = new SqliteVpnActivityLog(databasePath, maxEntries: 100);
        first.Record(new VpnActivityEntry(T0, VpnActivityKind.MonitoringStopped, "Monitoring stopped.", Profile, "shutdown"));
        first.Dispose();

        var second = CreateLogAt(databasePath, maxEntries: 100);

        var read = Assert.Single(second.GetRecent(50));
        Assert.Equal(VpnActivityKind.MonitoringStopped, read.Kind);
        Assert.Equal("Monitoring stopped.", read.Message);
        Assert.Equal("shutdown", read.Detail);
    }
}
