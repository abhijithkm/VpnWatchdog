using VpnWatchdog.Core;
using VpnWatchdog.Core.Logging;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// <see cref="IVpnActivityLog.Clear"/> - the one destructive operation on the activity
/// trail, reachable only from the "Clear Logs" button behind a confirmation. Two things
/// must hold for both implementations: clearing empties the history and leaves the store
/// USABLE (monitoring keeps logging after the click), and it NEVER throws, however broken
/// the store underneath has become. The button is pressed on the UI thread, and the
/// contract says a failure is reported as <c>false</c> so the UI can say so - not as an
/// exception that would take the window down.
///
/// <para>
/// Every timestamp is an explicit <see cref="DateTimeOffset"/>, never
/// <c>DateTimeOffset.Now</c>. Each SQLite test gets its own uniquely named temporary
/// directory and deletes it in a <c>finally</c>. Nothing here touches COM, FortiClient,
/// the network or a real VPN.
/// </para>
/// </summary>
public class ActivityLogClearTests
{
    private const string Profile = "MLA-DEV-VPN-2-LocalAuth";

    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 8, 0, 0, TimeSpan.FromHours(-4));

    /// <summary>An ordinary entry, so each test only spells out what it is actually about.</summary>
    private static VpnActivityEntry EntryAt(DateTimeOffset at, string message) =>
        new(at, VpnActivityKind.VpnDisconnected, message, Profile, null);

    /// <summary>
    /// Runs <paramref name="test"/> against a database path inside a fresh, uniquely named
    /// temporary directory, then deletes that directory whatever happened. The tests that
    /// break the store may leave a directory, a garbage file or nothing at all at the path,
    /// so the cleanup is recursive; it is best-effort because these files live under the OS
    /// temp directory and failing a test over cleanup would be pure noise. The log instance
    /// must be disposed inside <paramref name="test"/> (a <c>using</c>), so the pooled native
    /// handles are released before the delete runs.
    /// </summary>
    private static void WithTempDatabasePath(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "VpnWatchdogTests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "activity-log.db");

        try
        {
            test(databasePath);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Releases the native file handles Microsoft.Data.Sqlite's connection pool holds for
    /// <paramref name="databasePath"/>, so the file can be deleted or replaced underneath a
    /// LIVE log. The pool is keyed by connection string, so disposing a throwaway instance on
    /// the same path clears it for the instance under test too - which must stay alive,
    /// because breaking the store under a live object is the whole point. Without this the
    /// delete fails with a sharing violation.
    /// </summary>
    private static void ReleasePooledHandles(string databasePath)
    {
        using var throwaway = new SqliteVpnActivityLog(databasePath, maxEntries: 1_000);
    }

    // ==================================================================
    // 1. SQLITE - THE HAPPY PATH. Clear empties the shared on-disk history and
    //    the store keeps working afterwards.
    // ==================================================================

    [Fact]
    public void Clear_OnAPopulatedSqliteStore_ReturnsTrue_EmptiesIt_AndLeavesItUsable()
    {
        WithTempDatabasePath(databasePath =>
        {
            using var sut = new SqliteVpnActivityLog(databasePath, maxEntries: 100);
            sut.Record(EntryAt(T0, "first"));
            sut.Record(EntryAt(T0.AddSeconds(1), "second"));
            sut.Record(EntryAt(T0.AddSeconds(2), "third"));
            Assert.Equal(3, sut.GetRecent(10).Count);

            Assert.True(sut.Clear());

            Assert.Empty(sut.GetRecent(10));

            // The store is still live: monitoring carries on logging after the click, and
            // what comes back is exactly what was logged since - no resurrected history.
            sut.Record(EntryAt(T0.AddSeconds(3), "after clear"));

            var read = Assert.Single(sut.GetRecent(10));
            Assert.Equal(T0.AddSeconds(3), read.Timestamp);
            Assert.Equal(VpnActivityKind.VpnDisconnected, read.Kind);
            Assert.Equal("after clear", read.Message);
            Assert.Equal(Profile, read.ProfileName);
            Assert.Null(read.Detail);
        });
    }

    [Fact]
    public void Clear_OnAFreshSqliteStore_ReturnsTrue_AndClearingAgainIsStillTrue()
    {
        // Nothing to delete is still a successful clear, and clicking the button twice must
        // not turn the second click into an error.
        WithTempDatabasePath(databasePath =>
        {
            using var sut = new SqliteVpnActivityLog(databasePath, maxEntries: 100);

            Assert.True(sut.Clear());
            Assert.True(sut.Clear());
            Assert.Empty(sut.GetRecent(10));
        });
    }

    [Fact]
    public void Clear_FromOneLogOnASharedPath_EmptiesWhatAnotherLogOnTheSamePathReads()
    {
        // The GUI and the CLI append to ONE fixed file by design, so clearing from the GUI
        // must clear the history the CLI shows - there is no "my half" of the log.
        WithTempDatabasePath(databasePath =>
        {
            using var gui = new SqliteVpnActivityLog(databasePath, maxEntries: 100);
            using var cli = new SqliteVpnActivityLog(databasePath, maxEntries: 100);

            gui.Record(EntryAt(T0, "from the gui"));
            cli.Record(EntryAt(T0.AddSeconds(1), "from the cli"));
            Assert.Equal(2, cli.GetRecent(10).Count);

            Assert.True(gui.Clear());

            Assert.Empty(cli.GetRecent(10));
            Assert.Empty(gui.GetRecent(10));

            cli.Record(EntryAt(T0.AddSeconds(2), "after clear"));

            var read = Assert.Single(gui.GetRecent(10));
            Assert.Equal("after clear", read.Message);
        });
    }

    // ==================================================================
    // 2. SQLITE - MUST NOT THROW, WHATEVER HAPPENED TO THE FILE.
    //    These break the store UNDERNEATH a live log, which is the realistic
    //    multi-day failure: a cleanup tool wiping %LOCALAPPDATA%, a user deleting
    //    the file by hand to "reset the log", a stray file landing on the path.
    // ==================================================================

    [Fact]
    public void Clear_WhenTheDatabaseFileHasBeenDeleted_ReturnsTrueWithoutThrowingOrRecreatingIt()
    {
        // The user deleted the file, then pressed Clear: from their point of view the log is
        // empty, which is exactly what they asked for. That is a success, not an error - and
        // a clear must not resurrect the file they just removed.
        WithTempDatabasePath(databasePath =>
        {
            using var sut = new SqliteVpnActivityLog(databasePath, maxEntries: 100);
            sut.Record(EntryAt(T0, "written while the store was healthy"));

            ReleasePooledHandles(databasePath);
            File.Delete(databasePath);

            bool? result = null;
            var thrown = Xunit.Record.Exception(() => result = sut.Clear());

            Assert.True(thrown is null, "Clear must never throw into the UI thread. Threw: " + thrown);
            Assert.True(result);
            Assert.False(File.Exists(databasePath));
            Assert.Empty(sut.GetRecent(10));
        });
    }

    [Fact]
    public void Clear_WhenTheDatabaseFileIsNotASqliteDatabase_ReturnsFalseRatherThanThrowing()
    {
        // A file IS there, so there is something to clear, but SQLite cannot make sense of
        // it. This is the case the contract's "return false so the UI can say so" exists
        // for: the user needs to hear "could not clear", not see a crash dialog.
        WithTempDatabasePath(databasePath =>
        {
            using var sut = new SqliteVpnActivityLog(databasePath, maxEntries: 100);
            sut.Record(EntryAt(T0, "written while the store was healthy"));

            ReleasePooledHandles(databasePath);

            // Well past one SQLite page of garbage, so the header check itself fails rather
            // than the file being mistaken for a short, empty database.
            File.WriteAllText(databasePath, new string('x', 8_192));

            bool? result = null;
            var thrown = Xunit.Record.Exception(() => result = sut.Clear());

            Assert.True(thrown is null, "Clear must never throw into the UI thread. Threw: " + thrown);
            Assert.False(result);
        });
    }

    [Fact]
    public void Clear_WhenTheDatabasePathBecomesADirectory_DoesNotThrow()
    {
        // The path itself is unusable - SQLite can never open a directory. Whether the
        // answer is "nothing there to clear" (true) or "could not clear" (false) is the
        // implementation's call; what is not negotiable is that it answers rather than throws.
        WithTempDatabasePath(databasePath =>
        {
            using var sut = new SqliteVpnActivityLog(databasePath, maxEntries: 100);
            sut.Record(EntryAt(T0, "written while the store was healthy"));

            ReleasePooledHandles(databasePath);
            File.Delete(databasePath);
            Directory.CreateDirectory(databasePath);

            // Twice on purpose, as the sibling Record tests do: a first failure marks the
            // schema unknown, so a second call can take a different path to the same answer.
            var thrown = Xunit.Record.Exception(() =>
            {
                sut.Clear();
                sut.Clear();
            });

            Assert.True(
                thrown is null,
                "Clear must never throw into the UI thread, however unusable the path has become. Threw: " + thrown);
        });
    }

    [Fact]
    public void Clear_AfterDispose_ReturnsFalseRatherThanThrowing()
    {
        // Shutdown ordering: the log may already be disposed when a late button click lands.
        // A disposed store cannot be cleared, which the contract spells as false.
        WithTempDatabasePath(databasePath =>
        {
            var sut = new SqliteVpnActivityLog(databasePath, maxEntries: 100);
            sut.Record(EntryAt(T0, "before dispose"));
            sut.Dispose();

            bool? result = null;
            var thrown = Xunit.Record.Exception(() => result = sut.Clear());

            Assert.True(thrown is null, "Clear must never throw, not even after Dispose. Threw: " + thrown);
            Assert.False(result);
        });
    }

    // ==================================================================
    // 3. IN-MEMORY - the fallback the GUI and CLI use when the SQLite store
    //    cannot be opened. Same button, same expectations.
    // ==================================================================

    [Fact]
    public void Clear_OnAPopulatedInMemoryLog_ReturnsTrue_EmptiesIt_AndKeepsNewestFirstOrderingAfterwards()
    {
        // Capacity 4 with 6 writes, so the ring has wrapped and its write pointer sits
        // mid-buffer. A Clear that zeroed the count but not the pointer would still pass an
        // "is it empty" check and only show up later as mis-ordered reads; recording again
        // after the clear is what catches that.
        var sut = new InMemoryVpnActivityLog(capacity: 4);

        for (var i = 0; i < 6; i++)
        {
            sut.Record(EntryAt(T0.AddSeconds(i), $"before-{i}"));
        }

        Assert.Equal(4, sut.GetRecent(100).Count);

        Assert.True(sut.Clear());

        Assert.Empty(sut.GetRecent(100));
        Assert.Empty(sut.GetRecent(1));

        sut.Record(EntryAt(T0.AddMinutes(1), "oldest"));
        sut.Record(EntryAt(T0.AddMinutes(2), "middle"));
        sut.Record(EntryAt(T0.AddMinutes(3), "newest"));

        var recent = sut.GetRecent(100);

        Assert.Equal(3, recent.Count);
        Assert.Equal(new[] { "newest", "middle", "oldest" }, recent.Select(entry => entry.Message).ToArray());
        Assert.DoesNotContain(recent, entry => entry.Message.StartsWith("before-", StringComparison.Ordinal));
    }

    [Fact]
    public void Clear_OnAnEmptyInMemoryLog_ReturnsTrue_AndClearingAgainIsStillTrue()
    {
        var sut = new InMemoryVpnActivityLog(capacity: 10);

        Assert.True(sut.Clear());
        Assert.True(sut.Clear());
        Assert.Empty(sut.GetRecent(10));
    }

    [Fact]
    public void Clear_ThroughTheIVpnActivityLogInterface_BehavesTheSameForBothImplementations()
    {
        // The GUI holds an IVpnActivityLog and does not know which store it got at startup,
        // so the button must behave identically through the interface for either one.
        WithTempDatabasePath(databasePath =>
        {
            using var sqlite = new SqliteVpnActivityLog(databasePath, maxEntries: 100);
            var logs = new IVpnActivityLog[] { sqlite, new InMemoryVpnActivityLog(capacity: 100) };

            foreach (var log in logs)
            {
                log.Record(EntryAt(T0, "before"));
                Assert.Single(log.GetRecent(10));

                Assert.True(log.Clear());
                Assert.Empty(log.GetRecent(10));

                log.Record(EntryAt(T0.AddSeconds(1), "after"));
                var read = Assert.Single(log.GetRecent(10));
                Assert.Equal("after", read.Message);
            }
        });
    }
}

/// <summary>
/// <see cref="VpnActivityKindExtensions.Severity"/> is the ONE mapping every surface
/// colours and icons an event by - the main window's "Last Event" line, the log dialog,
/// the tray. It lives in the contract precisely so the surfaces cannot drift apart, which
/// also means a change to it repaints every screen at once. These spot-checks make such a
/// change a deliberate one.
/// </summary>
public class VpnActivitySeverityMappingTests
{
    [Theory]
    // Green: the tunnel, or what it depends on, is back.
    [InlineData(VpnActivityKind.VpnConnected, VpnActivitySeverity.Success)]
    [InlineData(VpnActivityKind.AutoReconnectSucceeded, VpnActivitySeverity.Success)]
    [InlineData(VpnActivityKind.InternetRestored, VpnActivitySeverity.Success)]
    // Amber: something dropped, or we are about to intervene.
    [InlineData(VpnActivityKind.VpnDisconnected, VpnActivitySeverity.Warning)]
    [InlineData(VpnActivityKind.VpnDisconnectedUnexpectedly, VpnActivitySeverity.Warning)]
    [InlineData(VpnActivityKind.InternetLost, VpnActivitySeverity.Warning)]
    [InlineData(VpnActivityKind.AutoReconnectTriggered, VpnActivitySeverity.Warning)]
    // Red: an intervention failed, or FortiClient is not there to ask.
    [InlineData(VpnActivityKind.AutoReconnectFailed, VpnActivitySeverity.Error)]
    [InlineData(VpnActivityKind.AutoReconnectGaveUp, VpnActivitySeverity.Error)]
    [InlineData(VpnActivityKind.FortiClientNotRunning, VpnActivitySeverity.Error)]
    // Grey: housekeeping the user should see but not be alarmed by.
    [InlineData(VpnActivityKind.MonitoringStarted, VpnActivitySeverity.Info)]
    [InlineData(VpnActivityKind.MonitoringStopped, VpnActivitySeverity.Info)]
    [InlineData(VpnActivityKind.AutoReconnectArmed, VpnActivitySeverity.Info)]
    public void Severity_MapsTheKindToTheSeverityEveryUiColoursBy(VpnActivityKind kind, VpnActivitySeverity expected)
    {
        Assert.Equal(expected, kind.Severity());
    }

    [Fact]
    public void Severity_IsADefinedValueForEveryActivityKind()
    {
        // A kind added later must not fall through to an undefined severity; the mapping's
        // default arm is Info, and this pins that every member resolves to SOMETHING a UI
        // can look up a colour for.
        foreach (var kind in Enum.GetValues<VpnActivityKind>())
        {
            var severity = kind.Severity();

            Assert.True(
                Enum.IsDefined(severity),
                $"{kind}.Severity() returned {severity}, which is not a defined VpnActivitySeverity.");
        }
    }
}
