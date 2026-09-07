using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VpnWatchdog.Core.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IVpnEventStore"/>. Read-only/observe-only
/// persistence for adapter/state snapshots, classified log lines, and disconnect
/// correlations - no field anywhere holds a password, token, cookie, or secret.
///
/// Opens a fresh <see cref="SqliteConnection"/> per operation rather than holding one
/// open for the lifetime of the object; Microsoft.Data.Sqlite pools connections
/// internally, so this stays cheap while avoiding long-lived-connection locking
/// hazards in a process that may run for days.
/// </summary>
public sealed class SqliteVpnEventStore : IVpnEventStore, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaEnsured;
    private bool _disposed;

    public SqliteVpnEventStore(string databasePath)
    {
        // Built rather than concatenated: databasePath comes from a JSON config
        // file (CLI's appsettings.json, or the GUI's own settings), so it is
        // NOT purely developer-chosen the way it might look - a value
        // containing ';' concatenated raw would inject extra connection-string
        // keywords (Mode, Cache, Password, Pooling...) instead of naming a
        // file. Same reasoning as SqliteVpnActivityLog's connection string.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    private const string CreateSchemaSql = """
        CREATE TABLE IF NOT EXISTS VpnStateSnapshots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            observed_at TEXT NOT NULL,
            state TEXT NOT NULL,
            adapter_found INTEGER NOT NULL,
            adapter_name TEXT,
            interface_description TEXT,
            interface_index INTEGER,
            is_up INTEGER NOT NULL,
            ip_address TEXT,
            prefix_length INTEGER
        );

        CREATE TABLE IF NOT EXISTS LogEvents (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            profile_name TEXT,
            event_type TEXT NOT NULL,
            raw_line TEXT NOT NULL,
            reason_code TEXT,
            reason_text TEXT,
            source_file TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Correlations (
            correlation_id TEXT PRIMARY KEY,
            profile_name TEXT NOT NULL,
            disconnected_at TEXT NOT NULL,
            classification TEXT NOT NULL,
            reason_code TEXT,
            reason_text TEXT,
            internet_state_at_disconnect TEXT NOT NULL,
            forti_vpn_running INTEGER NOT NULL,
            forti_sslvpn_running INTEGER NOT NULL,
            previous_connected_duration_sec REAL,
            reconnect_attempt_at TEXT,
            reconnected_at TEXT,
            recovery_duration_sec REAL,
            recovery_succeeded INTEGER
        );
        """;

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = CreateSchemaSql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static void AddParam(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public async Task SaveVpnStateSnapshotAsync(VpnStateSnapshot snapshot, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO VpnStateSnapshots
                (observed_at, state, adapter_found, adapter_name, interface_description,
                 interface_index, is_up, ip_address, prefix_length)
            VALUES
                (@observed_at, @state, @adapter_found, @adapter_name, @interface_description,
                 @interface_index, @is_up, @ip_address, @prefix_length);
            """;

        var adapter = snapshot.Adapter;
        AddParam(cmd, "@observed_at", snapshot.ObservedAt.ToString("o"));
        AddParam(cmd, "@state", snapshot.State.ToString());
        AddParam(cmd, "@adapter_found", adapter.AdapterFound ? 1 : 0);
        AddParam(cmd, "@adapter_name", adapter.AdapterName);
        AddParam(cmd, "@interface_description", adapter.InterfaceDescription);
        AddParam(cmd, "@interface_index", (object?)adapter.InterfaceIndex);
        AddParam(cmd, "@is_up", adapter.IsUp ? 1 : 0);
        AddParam(cmd, "@ip_address", adapter.IpAddress);
        AddParam(cmd, "@prefix_length", (object?)adapter.PrefixLength);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveLogEventAsync(LogEvent logEvent, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LogEvents
                (timestamp, profile_name, event_type, raw_line, reason_code, reason_text, source_file)
            VALUES
                (@timestamp, @profile_name, @event_type, @raw_line, @reason_code, @reason_text, @source_file);
            """;

        AddParam(cmd, "@timestamp", logEvent.Timestamp.ToString("o"));
        AddParam(cmd, "@profile_name", logEvent.ProfileName);
        AddParam(cmd, "@event_type", logEvent.EventType.ToString());
        AddParam(cmd, "@raw_line", logEvent.RawLine);
        AddParam(cmd, "@reason_code", logEvent.ReasonCode);
        AddParam(cmd, "@reason_text", logEvent.ReasonText);
        AddParam(cmd, "@source_file", logEvent.SourceFile);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpsertCorrelationAsync(DisconnectCorrelation correlation, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Correlations
                (correlation_id, profile_name, disconnected_at, classification, reason_code, reason_text,
                 internet_state_at_disconnect, forti_vpn_running, forti_sslvpn_running,
                 previous_connected_duration_sec, reconnect_attempt_at, reconnected_at,
                 recovery_duration_sec, recovery_succeeded)
            VALUES
                (@correlation_id, @profile_name, @disconnected_at, @classification, @reason_code, @reason_text,
                 @internet_state_at_disconnect, @forti_vpn_running, @forti_sslvpn_running,
                 @previous_connected_duration_sec, @reconnect_attempt_at, @reconnected_at,
                 @recovery_duration_sec, @recovery_succeeded)
            ON CONFLICT(correlation_id) DO UPDATE SET
                profile_name = excluded.profile_name,
                disconnected_at = excluded.disconnected_at,
                classification = excluded.classification,
                reason_code = excluded.reason_code,
                reason_text = excluded.reason_text,
                internet_state_at_disconnect = excluded.internet_state_at_disconnect,
                forti_vpn_running = excluded.forti_vpn_running,
                forti_sslvpn_running = excluded.forti_sslvpn_running,
                previous_connected_duration_sec = excluded.previous_connected_duration_sec,
                reconnect_attempt_at = excluded.reconnect_attempt_at,
                reconnected_at = excluded.reconnected_at,
                recovery_duration_sec = excluded.recovery_duration_sec,
                recovery_succeeded = excluded.recovery_succeeded;
            """;

        AddParam(cmd, "@correlation_id", correlation.CorrelationId);
        AddParam(cmd, "@profile_name", correlation.ProfileName);
        AddParam(cmd, "@disconnected_at", correlation.DisconnectedAt.ToString("o"));
        AddParam(cmd, "@classification", correlation.DisconnectClassification.ToString());
        AddParam(cmd, "@reason_code", correlation.DisconnectReasonCode);
        AddParam(cmd, "@reason_text", correlation.DisconnectReasonText);
        AddParam(cmd, "@internet_state_at_disconnect", correlation.InternetStateAtDisconnect.ToString());
        AddParam(cmd, "@forti_vpn_running", correlation.FortiVpnProcessRunningAtDisconnect ? 1 : 0);
        AddParam(cmd, "@forti_sslvpn_running", correlation.FortiSslVpnDaemonRunningAtDisconnect ? 1 : 0);
        AddParam(cmd, "@previous_connected_duration_sec",
            correlation.PreviousConnectedDuration.HasValue ? correlation.PreviousConnectedDuration.Value.TotalSeconds : (object?)null);
        AddParam(cmd, "@reconnect_attempt_at",
            correlation.ReconnectAttemptDetectedAt.HasValue ? correlation.ReconnectAttemptDetectedAt.Value.ToString("o") : (object?)null);
        AddParam(cmd, "@reconnected_at",
            correlation.ReconnectedAt.HasValue ? correlation.ReconnectedAt.Value.ToString("o") : (object?)null);
        AddParam(cmd, "@recovery_duration_sec",
            correlation.RecoveryDuration.HasValue ? correlation.RecoveryDuration.Value.TotalSeconds : (object?)null);
        AddParam(cmd, "@recovery_succeeded",
            correlation.RecoverySucceeded.HasValue ? (correlation.RecoverySucceeded.Value ? 1 : 0) : (object?)null);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DisconnectCorrelation>> GetAllCorrelationsAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_id, profile_name, disconnected_at, classification, reason_code, reason_text,
                   internet_state_at_disconnect, forti_vpn_running, forti_sslvpn_running,
                   previous_connected_duration_sec, reconnect_attempt_at, reconnected_at,
                   recovery_duration_sec, recovery_succeeded
            FROM Correlations;
            """;

        var results = new List<DisconnectCorrelation>();

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new DisconnectCorrelation(
                CorrelationId: reader.GetString(0),
                ProfileName: reader.GetString(1),
                DisconnectedAt: ParseTimestamp(reader.GetString(2)),
                DisconnectClassification: reader.GetString(3),
                DisconnectReasonCode: reader.IsDBNull(4) ? null : reader.GetString(4),
                DisconnectReasonText: reader.IsDBNull(5) ? null : reader.GetString(5),
                // TryParse, not Parse: a row written by a future schema version (or one
                // hand-edited/corrupted) must degrade to Unknown, never throw and take
                // this entire read with it.
                InternetStateAtDisconnect: Enum.TryParse(reader.GetString(6), out InternetState internetState)
                    ? internetState
                    : InternetState.Unknown,
                FortiVpnProcessRunningAtDisconnect: reader.GetInt64(7) != 0,
                FortiSslVpnDaemonRunningAtDisconnect: reader.GetInt64(8) != 0,
                PreviousConnectedDuration: reader.IsDBNull(9) ? null : TimeSpan.FromSeconds(reader.GetDouble(9)),
                ReconnectAttemptDetectedAt: reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
                ReconnectedAt: reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
                RecoveryDuration: reader.IsDBNull(12) ? null : TimeSpan.FromSeconds(reader.GetDouble(12)),
                RecoverySucceeded: reader.IsDBNull(13) ? null : reader.GetInt64(13) != 0));
        }

        return results;
    }

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.ParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Connection-per-operation means nothing long-lived is held BY THIS CLASS, but
    /// the Microsoft.Data.Sqlite pool is: it keeps native file handles to the database
    /// open for as long as the process runs, which on Windows blocks anyone from
    /// deleting, moving, or backing up the file. Release them explicitly rather than
    /// waiting for a GC finalizer that may never run - same reasoning, and the same
    /// pattern, as <see cref="Logging.SqliteVpnActivityLog.Dispose"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // Never opened - it exists only to name the pool to clear.
            using var identifiesThePool = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(identifiesThePool);
        }
        catch
        {
            // Failure to release must never propagate out of Dispose.
        }

        _schemaLock.Dispose();
    }
}
