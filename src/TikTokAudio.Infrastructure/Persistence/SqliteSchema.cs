using Microsoft.Data.Sqlite;

namespace TikTokAudio.Infrastructure.Persistence;

internal static class SqliteSchema
{
    internal const int ApplicationId = 0x4C415544;
    internal const int CurrentVersion = 2;
    private static readonly string[] CoreTables = ["rule_sets", "playback_plans", "sessions", "event_fingerprints", "checkpoints", "cache_metadata"];

    internal static int Inspect(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var applicationId = Scalar(connection, "PRAGMA application_id;", transaction);
        var version = Scalar(connection, "PRAGMA user_version;", transaction);
        if (version < 0 || version > CurrentVersion) throw new InvalidDataException("Unsupported database schema version.");
        if (applicationId == 0 && version == 0 && Scalar(connection,
                "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';", transaction) == 0) return 0;
        if (applicationId != ApplicationId || version == 0) throw new InvalidDataException("Database identity is not recognized.");
        foreach (var table in CoreTables) Probe(connection, $"SELECT id,payload FROM {table} LIMIT 0;", transaction);
        Probe(connection, "SELECT id,session_id,event_fingerprint,action_type,payload,completed_at FROM interactions LIMIT 0;", transaction);
        if (version >= 2)
        {
            Probe(connection, "SELECT id,payload FROM action_ledger LIMIT 0;", transaction);
            Probe(connection, "SELECT kind,id,version,payload,updated_at FROM documents LIMIT 0;", transaction);
        }
        return checked((int)version);
    }

    internal static void Migrate(SqliteConnection connection, CancellationToken cancellationToken, Action beforeCommit)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var version = Inspect(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        if (version == 0)
        {
            foreach (var table in CoreTables)
                Execute(connection, transaction, $"CREATE TABLE {table} (id TEXT NOT NULL PRIMARY KEY, payload TEXT NOT NULL CHECK(json_valid(payload)));");
            Execute(connection, transaction, """
                CREATE TABLE interactions (
                    id TEXT NOT NULL PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    event_fingerprint TEXT NOT NULL,
                    action_type TEXT NOT NULL,
                    payload TEXT NOT NULL CHECK(json_valid(payload)),
                    completed_at TEXT,
                    UNIQUE(session_id,event_fingerprint,action_type));
                """);
            Execute(connection, transaction, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;");
            version = 1;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (version == 1)
        {
            Execute(connection, transaction, """
                CREATE TABLE action_ledger (id TEXT NOT NULL PRIMARY KEY, payload TEXT NOT NULL CHECK(json_valid(payload)));
                CREATE TABLE documents (
                    kind INTEGER NOT NULL,
                    id TEXT NOT NULL,
                    version INTEGER NOT NULL CHECK(version>0),
                    payload TEXT NOT NULL CHECK(json_valid(payload)),
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY(kind,id));
                PRAGMA user_version=2;
                """);
        }
        cancellationToken.ThrowIfCancellationRequested();
        beforeCommit();
        transaction.Commit();
    }

    internal static void CheckIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal) || reader.Read())
            throw new InvalidDataException("Database integrity check failed.");
    }

    private static long Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Probe(SqliteConnection connection, string sql, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
