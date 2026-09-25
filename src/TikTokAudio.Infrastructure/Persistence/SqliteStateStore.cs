using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokAudio.Application.Contracts;

namespace TikTokAudio.Infrastructure.Persistence;

/// <summary>Stores local state only. Initialization and reads never replay audio or platform actions.</summary>
public sealed partial class SqliteStateStore : IStateStore
{
    private readonly LocalDataPaths _paths;
    private readonly SqliteStateStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _initialized;
    private const string BoundedPayload = "CASE WHEN length(CAST(payload AS BLOB)) <= $max THEN payload ELSE NULL END";
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };
    private static readonly HashSet<string> SecretKeys = new(StringComparer.Ordinal)
    {
        "password", "passwd", "pwd", "cookie", "cookies", "token", "accesstoken", "refreshtoken",
        "idtoken", "authorization", "apikey", "apisecret", "clientsecret", "secret", "credentials", "credential"
    };

    public SqliteStateStore(LocalDataPaths paths, SqliteStateStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _paths = paths;
        _options = options;
    }

    public async Task<OperationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!await AdmitAsync(cancellationToken).ConfigureAwait(false))
            return cancellationToken.IsCancellationRequested ? OperationResult.Cancelled() : OperationResult.Failed("State store is busy.");
        var committing = false;
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDatabasePaths();
                // Reject foreign/future/corrupt files using a read-only connection before any
                // writable SQLite pragma, migration, or journal can alter an existing database.
                if (File.Exists(_paths.DatabasePath))
                {
                    using var existing = Open(SqliteOpenMode.ReadOnly);
                    SqliteSchema.Inspect(existing);
                    SqliteSchema.CheckIntegrity(existing);
                }
                _paths.EnsureDirectories();
                cancellationToken.ThrowIfCancellationRequested();
                using var connection = Open(SqliteOpenMode.ReadWriteCreate);
                SqliteSchema.Inspect(connection);
                SqliteSchema.Migrate(connection, cancellationToken, () => committing = true);
                _initialized = true;
                return OperationResult.Succeeded();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return OperationResult.Cancelled(); }
        catch (Exception error) when (IsStorageError(error))
        {
            _initialized = false;
            return committing ? OperationResult.Unknown("Migration commit outcome is uncertain; initialize again to inspect it.") : OperationResult.Failed(SafeError(error));
        }
        finally { _gate.Release(); }
    }

    private async Task<OperationResult> WriteAsync(Action<SqliteConnection, SqliteTransaction, CancellationToken> action,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return OperationResult.Cancelled();
        if (!_initialized) return OperationResult.Failed("Initialize the state store first.");
        if (!await AdmitAsync(cancellationToken).ConfigureAwait(false))
            return cancellationToken.IsCancellationRequested ? OperationResult.Cancelled() : OperationResult.Failed("State store is busy.");
        var committing = false;
        try
        {
            // Microsoft.Data.Sqlite's async methods perform synchronous SQLite I/O. One
            // bounded worker per instance keeps it off the caller's UI thread without a queue.
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var connection = OpenInitialized(SqliteOpenMode.ReadWrite);
                using var transaction = connection.BeginTransaction(deferred: false);
                action(connection, transaction, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                committing = true;
                transaction.Commit();
                return OperationResult.Succeeded();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return committing ? OperationResult.Unknown("Commit outcome is uncertain.") : OperationResult.Cancelled(); }
        catch (Exception error) when (IsStorageError(error))
        {
            return committing ? OperationResult.Unknown("Commit outcome is uncertain.") : OperationResult.Failed(SafeError(error));
        }
        finally { _gate.Release(); }
    }

    private async Task<T?> ReadAsync<T>(Func<SqliteConnection, T?> read, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_initialized) throw new InvalidOperationException("Initialize the state store first.");
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("State store is busy.");
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var connection = OpenInitialized(SqliteOpenMode.ReadOnly);
                var result = read(connection);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (IsStorageError(error)) { throw new InvalidDataException(SafeError(error)); }
        finally { _gate.Release(); }
    }

    private async Task<bool> AdmitAsync(CancellationToken token)
    {
        try { return await _gate.WaitAsync(0, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    private SqliteConnection OpenInitialized(SqliteOpenMode mode)
    {
        var connection = Open(mode);
        try
        {
            if (SqliteSchema.Inspect(connection) != SqliteSchema.CurrentVersion)
                throw new InvalidDataException("State store schema changed; explicit initialization is required.");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        ValidateDatabasePaths();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath, Mode = mode, Cache = SqliteCacheMode.Private,
            Pooling = false, DefaultTimeout = _options.BusyTimeoutSeconds
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private void ValidateDatabasePaths()
    {
        _paths.ValidatePath(_paths.DatabasePath);
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" }) _paths.ValidatePath(_paths.DatabasePath + suffix);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void SavePayload(SqliteConnection connection, SqliteTransaction transaction, string table, string id, string payload, bool immutable = false)
    {
        Id(id);
        if (immutable)
        {
            using var existing = Command(connection, transaction, $"SELECT {BoundedPayload} FROM {table} WHERE id=$id;", ("$id", id), ("$max", _options.MaxPayloadBytes));
            if (ReadScalarPayload(existing) is string current)
            {
                if (!string.Equals(current, payload, StringComparison.Ordinal)) throw new InvalidDataException("An immutable state identifier already has different content.");
                return;
            }
        }
        using var command = Command(connection, transaction,
            $"INSERT INTO {table}(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload;",
            ("$id", id), ("$payload", payload));
        command.ExecuteNonQuery();
    }

    private T? LoadPayload<T>(SqliteConnection connection, string table, string id) where T : class
    {
        Id(id);
        using var command = Command(connection, null, $"SELECT {BoundedPayload} FROM {table} WHERE id=$id;", ("$id", id), ("$max", _options.MaxPayloadBytes));
        return ReadScalarPayload(command) is string payload ? Decode<T>(payload) : null;
    }

    private static string? ReadScalarPayload(SqliteCommand command) => command.ExecuteScalar() switch
    {
        null => null,
        string payload => payload,
        _ => throw new InvalidDataException("Stored payload exceeds the configured bound or is invalid.")
    };

    private static string ReadPayload(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? throw new InvalidDataException("Stored payload exceeds the configured bound or is invalid.") : reader.GetString(ordinal);

    private string Encode<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        CheckPayload(json);
        return json;
    }

    private T Decode<T>(string json)
    {
        CheckPayload(json);
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidDataException("Stored state is null.");
    }

    private void CheckPayload(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > _options.MaxPayloadBytes) throw new ArgumentException("State payload exceeds the configured bound.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        CheckKeys(document.RootElement);
    }

    private static void CheckKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var key = string.Concat(property.Name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
                if (SecretKeys.Contains(key)) throw new ArgumentException("Credential fields must use the Windows secure-storage boundary.");
                CheckKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckKeys(item);
    }

    private static void Id(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw new ArgumentException("Invalid state identifier.");
    }
    private static string Key(Guid id) => id != Guid.Empty ? id.ToString("D") : throw new ArgumentException("A nonempty identifier is required.");
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Defined<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException("Undefined state enumeration value.");
    }
    private static bool IsStorageError(Exception error) => error is SqliteException or IOException or UnauthorizedAccessException
        or InvalidDataException or FormatException or ArgumentException or JsonException or NotSupportedException
        or InvalidOperationException or System.Security.SecurityException;
    private static string SafeError(Exception error) => error is SqliteException sqlite
        ? $"SQLite state operation failed (code {sqlite.SqliteErrorCode})." : $"Local state operation failed ({error.GetType().Name}).";
}
