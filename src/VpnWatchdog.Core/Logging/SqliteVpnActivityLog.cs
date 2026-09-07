using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VpnWatchdog.Core.Logging;

/// <summary>
/// SQLite-backed implementation of <see cref="IVpnActivityLog"/> - the human-readable
/// trail of what the watchdog OBSERVED and DID, rendered in the log window.
///
/// <para>
/// Deliberately a SEPARATE store from <see cref="Storage.SqliteVpnEventStore"/> and a
/// separate database file. That one holds raw FortiClient trace events and disconnect
/// correlations for analysis - machine evidence, retained in full. This one holds a
/// capped, plain-English narrative for a person. Different lifetimes, different
/// retention, different readers, so different files: the GUI showing 200 recent lines
/// must never have to scan a multi-day evidence table, and pruning the narrative must
/// never delete evidence.
/// </para>
///
/// <para>
/// The path is FIXED and shared by design (<see cref="WatchdogConfig.DefaultActivityLogPath"/>),
/// so the GUI and the CLI append to ONE history. That makes this a MULTI-PROCESS
/// store as well as a multi-threaded one - see the concurrency notes on <see cref="_gate"/>.
/// </para>
///
/// <para>
/// Nothing here ever holds a credential: exactly the five fields of
/// <see cref="VpnActivityEntry"/> are persisted, nothing is synthesized, and no column
/// exists that could hold a password, token or cookie even if a caller tried.
/// </para>
///
/// <para>
/// Opens a fresh <see cref="SqliteConnection"/> per operation rather than holding one
/// open for the lifetime of the object, matching <see cref="Storage.SqliteVpnEventStore"/>:
/// Microsoft.Data.Sqlite pools connections internally, so this stays cheap while
/// avoiding long-lived-connection locking hazards in a process that may run for days.
/// </para>
/// </summary>
public sealed class SqliteVpnActivityLog : IVpnActivityLog, IDisposable
{
    // ------------------------------------------------------------------
    // THE ONE PROPERTY THAT MATTERS MOST: Record() NEVER THROWS.
    //
    // This class is INSTRUMENTATION. It is called from the monitor loop, from the
    // reconnect path, and from UI event handlers. A failed log write - disk full,
    // file locked by a backup agent, %LOCALAPPDATA% wiped by a cleanup tool,
    // profile on a network share that just went away - must NEVER take down
    // monitoring or abort a reconnect. Every failure below is caught and degraded
    // silently (one Trace line for the whole process lifetime, no more).
    //
    // The single deliberate exception is the CONSTRUCTOR, which DOES throw. That
    // is the caller's one and only chance to notice the store is unusable and fall
    // back to InMemoryVpnActivityLog (below). Once the object exists, nothing it
    // does can escape into the caller.
    // ------------------------------------------------------------------

    /// <summary>
    /// How long SQLite itself waits for a lock held by ANOTHER PROCESS before giving
    /// up. This is what bounds how long <see cref="_gate"/> can be held: without it a
    /// concurrent writer in the other process would fail instantly (SQLITE_BUSY) and
    /// we would drop log lines whenever the GUI and CLI overlapped.
    /// </summary>
    private const int BusyTimeoutMs = 3_000;

    /// <summary>
    /// How long a caller waits to enter <see cref="_gate"/> before giving up on this
    /// one entry. Comfortably above <see cref="BusyTimeoutMs"/> so it only ever fires
    /// if something genuinely pathological is happening.
    /// </summary>
    private static readonly TimeSpan LockAcquireTimeout = TimeSpan.FromMilliseconds(5_000);

    /// <summary>
    /// RETENTION CADENCE. We prune every N inserts, NOT on every insert.
    ///
    /// Why every-N rather than a count check: a cheap "SELECT COUNT(*)" is not
    /// actually cheap in SQLite (it is a full table/index scan), so a per-insert count
    /// check would cost about as much as the prune it is trying to avoid. A counter is
    /// free, and the prune itself is one index seek plus a ranged delete.
    ///
    /// Why 64: the activity log records STATE CHANGES, not polls - during normal
    /// operation 64 entries is many hours - so this collapses the retention write from
    /// "every log line" to "a few times a day", which matters over a multi-day
    /// unattended run. The cost of the laziness is bounded and tiny: the table can
    /// transiently hold at most ActivityLogMaxEntries + 63 rows, so "no unbounded
    /// growth" (invariant 6) still holds exactly.
    /// </summary>
    private const int PruneCheckIntervalInserts = 64;

    private readonly string _connectionString;
    private readonly string _readOnlyConnectionString;
    private readonly string _databasePath;
    private readonly int _maxEntries;

    // ------------------------------------------------------------------
    // CONCURRENCY - READ BEFORE "SIMPLIFYING" THIS.
    //
    // Two independent kinds of sharing are in play:
    //
    //   1. ACROSS PROCESSES - the GUI and the CLI append to one file. Handled by
    //      SQLite itself: WAL mode (readers never block the writer) plus a busy
    //      timeout so a concurrent writer WAITS briefly instead of failing.
    //
    //   2. WITHIN THIS PROCESS - the monitor loop, the reconnect path and the UI
    //      thread all log concurrently. Handled by _gate.
    //
    // _gate is acquired with Monitor.TryEnter(LockAcquireTimeout), NOT with a plain
    // `lock (_gate) { ... }`. Do NOT "simplify" it back. Both ends are bounded on
    // purpose:
    //
    //      * the HOLD is bounded because every statement executed inside is bounded -
    //        SQLite waits at most BusyTimeoutMs for the other process, and every read
    //        carries a LIMIT. Never add unbounded work (an un-LIMITed query, a file
    //        scan, a network call, an await) inside this lock.
    //      * the ACQUIRE is bounded because a plain lock would let a stalled writer
    //        park the monitor loop or freeze the UI thread forever. This app runs
    //        unattended for DAYS and must not deadlock (invariant 6); losing one log
    //        line is an acceptable price, stalling monitoring is not.
    // ------------------------------------------------------------------
    private readonly object _gate = new();

    /// <summary>Guarded by <see cref="_gate"/>. Cleared on failure so storage is re-created if the file/directory vanishes mid-run.</summary>
    private bool _schemaEnsured;

    /// <summary>Guarded by <see cref="_gate"/>. Primed so the very first insert prunes (see <see cref="MaybePruneLocked"/>).</summary>
    private int _insertsSincePrune = PruneCheckIntervalInserts;

    /// <summary>0/1 rather than bool so it can be flipped without holding <see cref="_gate"/>.</summary>
    private int _degradedReported;

    private volatile bool _disposed;

    /// <param name="databasePath">
    /// Usually <see cref="WatchdogConfig.ActivityLogPath"/>. Its parent directory is
    /// created if missing - the default lives under %LOCALAPPDATA%\VpnWatchdog\, which
    /// does not exist on a first run.
    /// </param>
    /// <param name="maxEntries">
    /// Usually <see cref="WatchdogConfig.ActivityLogMaxEntries"/>. Retention cap: after
    /// pruning, at most this many rows remain.
    /// </param>
    /// <exception cref="Exception">
    /// THIS is the one member that can throw, deliberately: if the directory cannot be
    /// created or the database cannot be opened, the caller should catch and fall back
    /// to <see cref="InMemoryVpnActivityLog"/> rather than run with no trail at all.
    /// After construction, no member throws.
    /// </exception>
    public SqliteVpnActivityLog(string databasePath, int maxEntries)
    {
        _databasePath = databasePath;

        // A cap of 0 (or negative) would delete every row immediately after writing it,
        // leaving a log window that is permanently empty and a user convinced the app is
        // broken. Clamp instead of throwing: a nonsensical cap is a configuration typo,
        // not a reason to refuse to keep a trail.
        _maxEntries = Math.Max(1, maxEntries);

        // Built rather than concatenated: this path comes from %LOCALAPPDATA%, so it
        // embeds a Windows account name that may contain characters the connection-
        // string grammar treats as syntax. The builder quotes them correctly. (Same
        // reasoning as SqliteVpnEventStore's own connection string.)
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Microsoft.Data.Sqlite retries SQLITE_BUSY for this long; belt to the
            // PRAGMA busy_timeout braces applied per connection below.
            DefaultTimeout = BusyTimeoutMs / 1000
        }.ToString();

        // A SEPARATE connection string for GetRecent, identical except Mode=ReadWrite
        // (no Create). GetRecent's own comment promises "a read must not create a
        // database" - that promise is only true if the connection literally cannot
        // create the file. Sharing _connectionString (ReadWriteCreate) for reads would
        // silently resurrect a file/table the user just deleted the instant a read
        // opened a connection to it, before any query even ran.
        _readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = BusyTimeoutMs / 1000
        }.ToString();

        // Fail fast and loudly HERE, while the caller can still choose the in-memory
        // fallback. Not wrapped in try/catch on purpose.
        EnsureStorage();
    }

    private const string CreateSchemaSql = """
        CREATE TABLE IF NOT EXISTS ActivityLog (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            kind TEXT NOT NULL,
            message TEXT NOT NULL,
            profile_name TEXT,
            detail TEXT
        );
        """;

    // AUTOINCREMENT matters more here than in the sibling store: this is the only table
    // that DELETES rows, and plain rowids can be reused after a delete. Ordering by a
    // reused id would interleave a new entry among old ones. AUTOINCREMENT guarantees
    // ids only ever increase, so "ORDER BY id DESC" is exactly "newest first", forever.

    /// <summary>
    /// Creates the parent directory, the database and the table if any are missing.
    /// Callers other than the constructor must hold <see cref="_gate"/>.
    /// </summary>
    private void EnsureStorage()
    {
        // _schemaEnsured says "we have already created this once in this process" - it
        // is NOT proof the file is still there. A cleanup tool wiping %LOCALAPPDATA%, or
        // (in tests) a directory deleted out from under a live instance, removes the file
        // without ever touching this flag, so trusting it alone would skip recreation
        // forever and silently drop every write that follows. Check the filesystem too.
        if (_schemaEnsured && File.Exists(_databasePath))
        {
            return;
        }

        // Must precede opening the connection: SQLite will happily create the .db FILE
        // but not the DIRECTORY above it, and %LOCALAPPDATA%\VpnWatchdog\ does not exist
        // before the first run.
        var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory); // idempotent - no exists-check needed
        }

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = CreateSchemaSql;
        cmd.ExecuteNonQuery();

        _schemaEnsured = true;
    }

    /// <summary>
    /// Opens a pooled connection and applies the per-connection pragmas. Synchronous on
    /// purpose - see the note on <see cref="Record(VpnActivityEntry)"/>.
    /// </summary>
    /// <param name="connectionString">
    /// Defaults to the write connection string (Mode=ReadWriteCreate). GetRecent passes
    /// <see cref="_readOnlyConnectionString"/> (Mode=ReadWrite) explicitly so a read can
    /// never bring a deleted database back into existence - see the call site.
    /// </param>
    private SqliteConnection OpenConnection(string? connectionString = null)
    {
        var conn = new SqliteConnection(connectionString ?? _connectionString);
        try
        {
            conn.Open();

            using (var pragma = conn.CreateCommand())
            {
                // busy_timeout is a PER-CONNECTION setting and pooled connections may be
                // reset, so it is re-applied on every open. This is what turns "another
                // process is mid-write" from a lost log line into a short wait.
                pragma.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
                pragma.ExecuteNonQuery();
            }

            // WAL and synchronous are best-effort: both are OPTIMISATIONS, and a
            // filesystem that refuses them (a network share cannot do WAL's shared-memory
            // file) still gives a perfectly correct rollback-journal database - just with
            // more blocking. Failing to open the log because a pragma was refused would
            // be a far worse outcome than running it slightly slower.
            TryExecutePragma(conn, "PRAGMA journal_mode = WAL;");

            // synchronous = NORMAL is safe under WAL: it cannot corrupt the database, it
            // only means an OS crash or power loss may lose the most recently committed
            // entries. For an instrumentation trail written on every state change across
            // a multi-day run, trading the last few lines for far fewer fsyncs is the
            // right way round. (Never make this trade in the evidence store.)
            TryExecutePragma(conn, "PRAGMA synchronous = NORMAL;");

            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static void TryExecutePragma(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Deliberately ignored - see caller.
        }
    }

    private static void AddParam(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>
    /// Appends one entry. Never throws, whatever happens.
    ///
    /// <para>
    /// Synchronous ADO calls throughout, even though the sibling event store is async:
    /// <see cref="IVpnActivityLog"/> is a synchronous contract, and the GUI calls it from
    /// the UI thread. Wrapping async calls in .Result/.Wait() to satisfy that contract is
    /// the classic WinForms deadlock. Microsoft.Data.Sqlite's async methods are synchronous
    /// underneath anyway, so calling the sync API is both the honest and the safe choice.
    /// </para>
    /// </summary>
    public void Record(VpnActivityEntry entry)
    {
        // Contract says non-null, but "never throws" is absolute - a null-forgiving caller
        // must not be able to bring down the monitor loop with a NullReferenceException.
        if (entry is null || _disposed)
        {
            return;
        }

        var taken = false;
        try
        {
            Monitor.TryEnter(_gate, LockAcquireTimeout, ref taken);
            if (!taken)
            {
                // Bounded wait expired. Drop the line rather than park the caller - the
                // caller may be the monitor loop or a reconnect in progress.
                ReportDegradedOnce("timed out acquiring the activity-log lock");
                return;
            }

            // Re-create storage if it disappeared mid-run (a cleanup tool wiping
            // %LOCALAPPDATA%, a user deleting the file to "reset the log"). Over a
            // days-long run this is a real scenario, and without it every subsequent
            // Record would fail silently forever.
            EnsureStorage();

            using var conn = OpenConnection();
            InsertLocked(conn, entry);
            MaybePruneLocked(conn);
        }
        catch (Exception ex)
        {
            // Reached only with _gate held: the sole statement before acquisition is
            // TryEnter, and ReportDegradedOnce cannot throw.
            _schemaEnsured = false; // force a full re-setup on the next attempt
            ReportDegradedOnce(ex.Message);
        }
        finally
        {
            if (taken)
            {
                Monitor.Exit(_gate);
            }
        }
    }

    private void InsertLocked(SqliteConnection conn, VpnActivityEntry entry)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ActivityLog
                (timestamp, kind, message, profile_name, detail)
            VALUES
                (@timestamp, @kind, @message, @profile_name, @detail);
            """;

        // "o" is the round-trip format and PRESERVES THE OFFSET, so an entry written
        // before a DST change still renders at the wall-clock time the user saw. Same
        // format as SqliteVpnEventStore, so both files read alike by hand.
        AddParam(cmd, "@timestamp", entry.Timestamp.ToString("o", CultureInfo.InvariantCulture));

        // Stored as the enum's NAME, not its numeric value: readable when someone opens
        // the file with any SQLite browser, and immune to a future insertion into the
        // middle of VpnActivityKind silently rewriting the meaning of old history.
        AddParam(cmd, "@kind", entry.Kind.ToString());

        // Column is NOT NULL; coerce rather than lose the entry to a constraint failure.
        AddParam(cmd, "@message", entry.Message ?? string.Empty);

        // ProfileName/Detail round-trip null as NULL and "" as "" - distinct, as records compare them.
        AddParam(cmd, "@profile_name", entry.ProfileName);
        AddParam(cmd, "@detail", entry.Detail);

        cmd.ExecuteNonQuery();

        _insertsSincePrune++;
    }

    /// <summary>
    /// Enforces the retention cap, at most once per <see cref="PruneCheckIntervalInserts"/>
    /// inserts. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void MaybePruneLocked(SqliteConnection conn)
    {
        if (_insertsSincePrune < PruneCheckIntervalInserts)
        {
            return;
        }

        // Reset first: if the delete throws, we do not want to retry it on every single
        // subsequent insert - the next scheduled prune will pick the work up anyway.
        _insertsSincePrune = 0;

        using var cmd = conn.CreateCommand();

        // Find the id of the OLDEST ROW WE KEEP (offset max-1 in newest-first order) and
        // delete everything below it. One seek down the primary-key index plus a ranged
        // delete - no COUNT(*), no "NOT IN (SELECT ...)" scan of the whole table.
        //
        // When the table holds max rows or fewer the subquery yields no row, the
        // comparison is NULL, and nothing is deleted. That is also why the first insert
        // after construction prunes (_insertsSincePrune is primed): a file left oversized
        // by an earlier run - or by the cap being lowered in config - gets trimmed at
        // once instead of 64 entries later.
        cmd.CommandText = """
            DELETE FROM ActivityLog
            WHERE id < (SELECT id FROM ActivityLog ORDER BY id DESC LIMIT 1 OFFSET @keep_offset);
            """;
        AddParam(cmd, "@keep_offset", _maxEntries - 1);

        cmd.ExecuteNonQuery();

        // Two processes pruning concurrently is harmless: the statement is idempotent and
        // whichever runs second simply finds nothing left to delete.
    }

    /// <summary>
    /// Most recent entries, newest first, capped at <paramref name="max"/>. Returns an
    /// empty list rather than throwing if the store is missing or unreadable.
    /// </summary>
    public IReadOnlyList<VpnActivityEntry> GetRecent(int max)
    {
        if (max <= 0 || _disposed)
        {
            return Array.Empty<VpnActivityEntry>();
        }

        var results = new List<VpnActivityEntry>();

        var taken = false;
        try
        {
            Monitor.TryEnter(_gate, LockAcquireTimeout, ref taken);
            if (!taken)
            {
                // The UI thread asked for the log window's contents; it gets an empty
                // window rather than a frozen one.
                return Array.Empty<VpnActivityEntry>();
            }

            // Deliberately does NOT call EnsureStorage: a read must not create a database.
            // Opened on _readOnlyConnectionString (Mode=ReadWrite, no Create) rather than
            // the write path's _connectionString: that mode still allows recovering a WAL
            // left behind by another process (unlike a true read-only open), but - unlike
            // ReadWriteCreate - it cannot bring a deleted file back into existence merely
            // by being opened. If the file or table is missing, Open()/the query throws
            // and we return empty, which is the documented behaviour and now actually true.
            using var conn = OpenConnection(_readOnlyConnectionString);
            using var cmd = conn.CreateCommand();

            // ORDER BY id, not BY timestamp: timestamps are offset-bearing strings, and
            // lexicographic order across differing UTC offsets is NOT chronological order.
            // id is the append order, which for a log is what "recent" actually means.
            cmd.CommandText = """
                SELECT timestamp, kind, message, profile_name, detail
                FROM ActivityLog
                ORDER BY id DESC
                LIMIT @max;
                """;
            AddParam(cmd, "@max", max);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // A row written by a NEWER build may carry a kind this build has no name
                // for. Skip that row instead of throwing away the whole window - the other
                // rows are still perfectly good history.
                if (!Enum.TryParse<VpnActivityKind>(reader.GetString(1), ignoreCase: false, out var kind))
                {
                    continue;
                }

                results.Add(new VpnActivityEntry(
                    Timestamp: ParseTimestamp(reader.GetString(0)),
                    Kind: kind,
                    Message: reader.GetString(2),
                    ProfileName: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Detail: reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }
        catch (Exception ex)
        {
            // Rows already read are the newest ones, so a partial result is still the most
            // recent slice of history and strictly better than showing nothing.
            ReportDegradedOnce(ex.Message);
        }
        finally
        {
            if (taken)
            {
                Monitor.Exit(_gate);
            }
        }

        return results;
    }

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.ParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Writes ONE diagnostic line for the whole lifetime of the process, then goes quiet.
    /// If the disk is full or the file is locked, every subsequent entry fails the same
    /// way; a message per failure would flood the debug output of a days-long run and
    /// bury whatever the developer was actually looking at.
    /// </summary>
    private void ReportDegradedOnce(string reason)
    {
        // Cannot throw: it is called from inside catch blocks, where an exception would
        // escape Record() and violate the one property this class must guarantee.
        try
        {
            if (Interlocked.Exchange(ref _degradedReported, 1) == 0)
            {
                Trace.WriteLine(
                    $"[VpnWatchdog] Activity log degraded (further messages suppressed): {reason}");
            }
        }
        catch
        {
            // A misbehaving TraceListener must not be able to break logging either.
        }
    }

    /// <summary>
    /// Deletes every row. User-initiated only - the "Clear Logs" button behind a
    /// confirmation dialog. Same never-throw rule as everything else here: returns
    /// false (and the UI says "could not clear") rather than surfacing an exception
    /// from a store that may be locked by the other process at that instant.
    /// </summary>
    public bool Clear()
    {
        if (_disposed)
        {
            return false;
        }

        var taken = false;
        try
        {
            Monitor.TryEnter(_gate, LockAcquireTimeout, ref taken);
            if (!taken)
            {
                ReportDegradedOnce("timed out acquiring the activity-log lock for Clear");
                return false;
            }

            // If the file/table is already gone there is nothing to clear - that is a
            // success from the user's point of view, not an error.
            if (!File.Exists(_databasePath))
            {
                return true;
            }

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ActivityLog;";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _schemaEnsured = false;
            ReportDegradedOnce(ex.Message);
            return false;
        }
        finally
        {
            if (taken)
            {
                Monitor.Exit(_gate);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var taken = false;
        try
        {
            // Bounded, like every other acquisition here - Dispose must not hang shutdown.
            Monitor.TryEnter(_gate, LockAcquireTimeout, ref taken);

            // Connection-per-operation means nothing long-lived is held HERE, but the
            // Microsoft.Data.Sqlite pool is: it keeps native file handles to the database
            // open. On Windows those handles block anyone from deleting or rotating the
            // file, so release them explicitly rather than waiting for a GC finalizer.
            // The connection below is never opened - it exists only to name the pool.
            using var identifiesThePool = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(identifiesThePool);
        }
        catch
        {
            // Failure to release must never propagate out of Dispose.
        }
        finally
        {
            if (taken)
            {
                Monitor.Exit(_gate);
            }
        }
    }
}

/// <summary>
/// In-memory <see cref="IVpnActivityLog"/> backed by a bounded ring buffer.
///
/// <para>
/// Two jobs. First, the FALLBACK when <see cref="SqliteVpnActivityLog"/>'s constructor
/// reports that the file cannot be opened at all (read-only profile, locked-down
/// %LOCALAPPDATA%, corrupt database): the session still gets a live log window, it just
/// does not survive a restart - far better than the app refusing to start, and far
/// better than a silently empty log. Second, tests, which want the same semantics with
/// no file and no cleanup.
/// </para>
///
/// <para>
/// The ring buffer is what makes it safe for a multi-day run: memory use is fixed at
/// construction and one entry can never push total usage up (invariant 6, no unbounded
/// growth). Like the SQLite implementation it never throws and holds no credential.
/// </para>
/// </summary>
public sealed class InMemoryVpnActivityLog : IVpnActivityLog
{
    private readonly object _gate = new();
    private readonly VpnActivityEntry[] _buffer;

    private int _next;  // slot the next entry goes into
    private int _count; // live entries, <= _buffer.Length

    /// <param name="capacity">
    /// Entries retained before the oldest is overwritten. Defaults to 5000, matching
    /// WatchdogConfig.Default's ActivityLogMaxEntries so the fallback behaves like the
    /// store it stands in for.
    /// </param>
    public InMemoryVpnActivityLog(int capacity = 5000)
    {
        // Clamped rather than validated, for the same reason as the SQLite cap: a bad
        // number is a config typo, and a log that refuses to construct is worse than a
        // log that keeps one entry.
        _buffer = new VpnActivityEntry[Math.Max(1, capacity)];
    }

    public void Record(VpnActivityEntry entry)
    {
        if (entry is null)
        {
            return;
        }

        // A plain lock is fine HERE - unlike the SQLite implementation, everything inside
        // is a couple of array writes with no I/O, no other process, and nothing that can
        // block. It cannot be held long enough to be worth a timeout.
        lock (_gate)
        {
            _buffer[_next] = entry;
            _next = (_next + 1) % _buffer.Length;

            if (_count < _buffer.Length)
            {
                _count++;
            }
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _next = 0;
            _count = 0;
        }
        return true;
    }

    public IReadOnlyList<VpnActivityEntry> GetRecent(int max)
    {
        if (max <= 0)
        {
            return Array.Empty<VpnActivityEntry>();
        }

        lock (_gate)
        {
            var take = Math.Min(max, _count);
            var results = new List<VpnActivityEntry>(take);

            // Walk backwards from the most recently written slot: newest first, as the
            // contract requires and as the log window renders it.
            for (var i = 1; i <= take; i++)
            {
                var index = (_next - i + _buffer.Length) % _buffer.Length;
                results.Add(_buffer[index]);
            }

            return results;
        }
    }
}
