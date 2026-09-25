using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Persistence;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class T080SqliteSafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshDatabaseHasApplicationIdentityAndCurrentVersion()
    {
        using var scope = new DatabaseScope();
        Assert.Equal(OperationStatus.Succeeded, (await scope.Store.InitializeAsync()).Status);
        using var connection = scope.Connect();
        Assert.Equal(0x4C415544L, Scalar(connection, "PRAGMA application_id;"));
        Assert.Equal(2L, Scalar(connection, "PRAGMA user_version;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionOneMigrationIsAtomicAndPreservesData(bool failSecondMigrationStatement)
    {
        using var scope = new DatabaseScope();
        scope.CreateVersionOne();
        using (var connection = scope.Connect())
        {
            Execute(connection, "INSERT INTO rule_sets VALUES('preserved', '{\"RuleSetId\":\"preserved\",\"Version\":1,\"UpdatedAtUtc\":\"2026-09-25T00:00:00+00:00\"}');");
            if (failSecondMigrationStatement) Execute(connection, "CREATE TABLE documents(sentinel TEXT); INSERT INTO documents VALUES('untouched');");
        }

        var result = await scope.Store.InitializeAsync();

        Assert.Equal(failSecondMigrationStatement ? OperationStatus.Failed : OperationStatus.Succeeded, result.Status);
        using var verify = scope.Connect();
        Assert.Equal(failSecondMigrationStatement ? 1L : 2L, Scalar(verify, "PRAGMA user_version;"));
        Assert.Equal(1L, Scalar(verify, "SELECT count(*) FROM rule_sets WHERE id='preserved';"));
        Assert.Equal(failSecondMigrationStatement ? 0L : 1L,
            Scalar(verify, "SELECT count(*) FROM sqlite_schema WHERE name='action_ledger';"));
        if (failSecondMigrationStatement) Assert.Equal("untouched", Scalar(verify, "SELECT sentinel FROM documents;"));
        else Assert.Equal(1, (await scope.Store.LoadRulesAsync("preserved"))!.Version);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("future")]
    [InlineData("corrupt")]
    [InlineData("missing-table")]
    public async Task RejectedDatabasesAreNotModified(string kind)
    {
        using var scope = new DatabaseScope();
        scope.Paths.EnsureDirectories();
        if (kind == "corrupt") File.WriteAllBytes(scope.Paths.DatabasePath, Encoding.UTF8.GetBytes("not a sqlite database: synthetic data"));
        else
        {
            if (kind == "missing-table") scope.CreateVersionOne();
            using var connection = scope.Connect();
            Execute(connection, kind switch
            {
                "foreign" => "CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('keep');",
                "future" => "PRAGMA application_id=1279350084; PRAGMA user_version=99;",
                _ => "DROP TABLE checkpoints;"
            });
        }
        var before = SHA256.HashData(File.ReadAllBytes(scope.Paths.DatabasePath));

        var result = await scope.Store.InitializeAsync();

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(scope.Paths.DatabasePath)));
        Assert.False(File.Exists(scope.Paths.DatabasePath + "-journal"));
    }

    [Fact]
    public async Task DamagedDataPageWithReadableSchemaIsRejectedBeforeMigration()
    {
        using var scope = new DatabaseScope();
        scope.CreateVersionOne();
        long pageOffset;
        using (var connection = scope.Connect())
        {
            Execute(connection, "INSERT INTO rule_sets VALUES('preserved','{}');");
            var pageSize = (long)Scalar(connection, "PRAGMA page_size;")!;
            var rootPage = (long)Scalar(connection, "SELECT rootpage FROM sqlite_schema WHERE name='rule_sets';")!;
            pageOffset = (rootPage - 1) * pageSize;
            Assert.True(pageOffset > 0);
        }
        using (var file = new FileStream(scope.Paths.DatabasePath, FileMode.Open, FileAccess.Write))
        {
            file.Position = pageOffset;
            file.WriteByte(0x7f); // Invalid b-tree page type; sqlite_schema remains intact.
        }
        var before = SHA256.HashData(File.ReadAllBytes(scope.Paths.DatabasePath));

        Assert.Equal(OperationStatus.Failed, (await scope.Store.InitializeAsync()).Status);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(scope.Paths.DatabasePath)));
        using var verify = scope.Connect();
        Assert.Equal(1L, Scalar(verify, "PRAGMA user_version;"));
        Assert.Equal(0L, Scalar(verify, "SELECT count(*) FROM sqlite_schema WHERE name='action_ledger';"));
    }

    [Fact]
    public async Task CancelledInitializationDoesNotCreateData()
    {
        using var scope = new DatabaseScope();
        Assert.Equal(OperationStatus.Cancelled, (await scope.Store.InitializeAsync(new CancellationToken(true))).Status);
        Assert.False(Directory.Exists(scope.Paths.RootDirectory));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("ACCESS_TOKEN")]
    [InlineData("refreshToken")]
    [InlineData("API-key")]
    [InlineData("Cookie")]
    [InlineData("client_secret")]
    [InlineData("Authorization")]
    public async Task CredentialKeysNestedInArraysAreRejectedWithoutEchoingValues(string key)
    {
        using var scope = new DatabaseScope();
        await scope.InitializeAsync();
        var json = "{\"items\":[{\"" + key + "\":\"synthetic-private-value\"}]}";

        var result = await scope.Store.SaveDocumentAsync(Document(json));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.DoesNotContain("synthetic-private-value", result.ToString());
        Assert.Null(await scope.Store.LoadDocumentAsync(LocalDocumentKind.Settings, "settings"));
    }

    [Fact]
    public async Task JsonDepthAndUtf8ByteLimitsAreEnforced()
    {
        using var scope = new DatabaseScope(maxPayloadBytes: 512);
        await scope.InitializeAsync();
        var deep = new string('[', 33) + "0" + new string(']', 33);
        Assert.Equal(OperationStatus.Failed, (await scope.Store.SaveDocumentAsync(Document(deep))).Status);
        var unicode = "{\"text\":\"" + new string('越', 200) + "\"}";
        Assert.True(unicode.Length < 512);
        Assert.True(Encoding.UTF8.GetByteCount(unicode) > 512);
        Assert.Equal(OperationStatus.Failed, (await scope.Store.SaveDocumentAsync(Document(unicode))).Status);
        Assert.Equal(OperationStatus.Succeeded, (await scope.Store.SaveDocumentAsync(Document("{\"text\":\"xin chào\"}"))).Status);
    }

    [Theory]
    [InlineData("rules")]
    [InlineData("document")]
    [InlineData("interaction")]
    [InlineData("ledger")]
    public async Task ExternallyOversizedPayloadsAreRejectedOnRead(string kind)
    {
        using var scope = new DatabaseScope(maxPayloadBytes: 512);
        await scope.InitializeAsync();
        var id = Guid.NewGuid();
        using (var connection = scope.Connect())
        {
            using var command = connection.CreateCommand();
            command.CommandText = kind switch
            {
                "rules" => "INSERT INTO rule_sets VALUES($id,$payload);",
                "document" => "INSERT INTO documents VALUES(0,$id,1,$payload,'2026-09-25T00:00:00.0000000+00:00');",
                "interaction" => "INSERT INTO interactions(id,session_id,event_fingerprint,action_type,payload) VALUES($id,'session','event','voice',$payload);",
                _ => "INSERT INTO action_ledger VALUES($id,$payload);"
            };
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$payload", "{\"oversized\":\"" + new string('x', 10000) + "\"}");
            command.ExecuteNonQuery();
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            switch (kind)
            {
                case "rules": await scope.Store.LoadRulesAsync(id.ToString("D")); break;
                case "document": await scope.Store.LoadDocumentAsync(LocalDocumentKind.Settings, id.ToString("D")); break;
                case "interaction": await scope.Store.LoadInteractionAsync(id); break;
                default: await scope.Store.LoadActionLedgerAsync(id); break;
            }
        });
        Assert.DoesNotContain("oversized", error.Message);
    }

    [Fact]
    public async Task MalformedStoredDatesDoNotEscapeAsRawExceptions()
    {
        using var scope = new DatabaseScope();
        await scope.InitializeAsync();
        Assert.Equal(OperationStatus.Succeeded, (await scope.Store.SaveDocumentAsync(Document("{}"))).Status);
        using (var connection = scope.Connect()) Execute(connection, "UPDATE documents SET updated_at='synthetic-sensitive-date';");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.LoadDocumentAsync(LocalDocumentKind.Settings, "settings"));
        Assert.DoesNotContain("synthetic-sensitive-date", error.ToString());
    }

    [Fact]
    public async Task SqlLikeIdentifiersAreDataNotCommands()
    {
        using var scope = new DatabaseScope();
        await scope.InitializeAsync();
        var rules = new RuleSetSnapshot("x'); DROP TABLE rule_sets; --", 1, Now);
        Assert.Equal(OperationStatus.Succeeded, (await scope.Store.SaveRulesAsync(rules)).Status);
        Assert.Equal(rules, await scope.Store.LoadRulesAsync(rules.RuleSetId));
        Assert.Null(await scope.Store.LoadRulesAsync("different"));
    }

    [Fact]
    public async Task ExternalWriteLockFailsWithinConfiguredTimeoutAndDoesNotCommit()
    {
        using var scope = new DatabaseScope();
        await scope.InitializeAsync();
        using (var connection = scope.Connect())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var result = await scope.Store.SaveRulesAsync(new RuleSetSnapshot("locked", 1, Now));
            Assert.Equal(OperationStatus.Failed, result.Status);
        }
        Assert.Null(await scope.Store.LoadRulesAsync("locked"));
        Assert.Equal(OperationStatus.Succeeded, (await scope.Store.SaveRulesAsync(new RuleSetSnapshot("after", 1, Now))).Status);
    }

    [Fact]
    public async Task CheckpointSnapshotsMarkersBeforeReturningToCaller()
    {
        using var scope = new DatabaseScope();
        await scope.InitializeAsync();
        var markers = new HashSet<string>(StringComparer.Ordinal) { "original" };
        var checkpoint = new PlaybackCheckpoint(BasePlaybackMode.TtsScript, new PlanRevision(1), null, null, null,
            new AudioCursor(20, 48000), 1, 5, markers);
        Task<OperationResult> write;
        using (var connection = scope.Connect())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            write = scope.Store.SavePlaybackCheckpointAsync(checkpoint);
            markers.Clear();
            markers.Add("late-change");
        }
        Assert.Equal(OperationStatus.Succeeded, (await write).Status);
        var stored = await scope.Store.LoadPlaybackCheckpointAsync(BasePlaybackMode.TtsScript);
        Assert.Equal("original", Assert.Single(stored!.ConsumedMarkerIds));
    }

    [Fact]
    public async Task BusyStoreRejectsSecondWriteAndReadWithoutQueuing()
    {
        using var scope = new DatabaseScope(timeoutSeconds: 30);
        await scope.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        Task<OperationResult> first;
        using (var connection = scope.Connect())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            first = scope.Store.SaveRulesAsync(new RuleSetSnapshot("first", 1, Now), cancellation.Token);
            Assert.Equal(OperationStatus.Failed, (await scope.Store.SaveRulesAsync(new RuleSetSnapshot("second", 1, Now))).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Store.LoadRulesAsync("first"));
            cancellation.Cancel();
        }
        Assert.Equal(OperationStatus.Cancelled, (await first).Status);
        Assert.Null(await scope.Store.LoadRulesAsync("first"));
        Assert.Null(await scope.Store.LoadRulesAsync("second"));
    }

    [Fact]
    public async Task CancellationBeforeCommitLeavesNoPartialWrite()
    {
        using var scope = new DatabaseScope();
        await scope.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        Task<OperationResult> write;
        using (var connection = scope.Connect())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            write = scope.Store.SaveRulesAsync(new RuleSetSnapshot("cancelled", 1, Now), cancellation.Token);
            cancellation.Cancel();
        }
        Assert.Equal(OperationStatus.Cancelled, (await write).Status);
        Assert.Null(await scope.Store.LoadRulesAsync("cancelled"));
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(31, 1024)]
    [InlineData(1, 0)]
    [InlineData(1, 16777217)]
    public void ExplicitResourceLimitsMustBeBounded(int timeout, int bytes)
    {
        using var scope = new DatabaseScope();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteStateStore(scope.Paths, new(timeout, bytes)));
    }

    private static LocalStateDocument Document(string json) => new(LocalDocumentKind.Settings, "settings", 1, json, Now);
    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class DatabaseScope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "t080-safety-" + Guid.NewGuid().ToString("N"));
        internal DatabaseScope(int maxPayloadBytes = 65536, int timeoutSeconds = 1)
        {
            Paths = LocalDataPaths.ForDevelopment(_root);
            Store = new SqliteStateStore(Paths, new(timeoutSeconds, maxPayloadBytes));
        }
        internal LocalDataPaths Paths { get; }
        internal SqliteStateStore Store { get; }
        internal async Task InitializeAsync() => Assert.Equal(OperationStatus.Succeeded, (await Store.InitializeAsync()).Status);
        internal SqliteConnection Connect()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Paths.DatabasePath, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
        internal void CreateVersionOne()
        {
            Paths.EnsureDirectories();
            using var connection = Connect();
            foreach (var table in new[] { "rule_sets", "playback_plans", "sessions", "event_fingerprints", "checkpoints", "cache_metadata" })
                Execute(connection, $"CREATE TABLE {table}(id TEXT NOT NULL PRIMARY KEY,payload TEXT NOT NULL CHECK(json_valid(payload)));");
            Execute(connection, """
                CREATE TABLE interactions(id TEXT NOT NULL PRIMARY KEY,session_id TEXT NOT NULL,event_fingerprint TEXT NOT NULL,
                action_type TEXT NOT NULL,payload TEXT NOT NULL CHECK(json_valid(payload)),completed_at TEXT,UNIQUE(session_id,event_fingerprint,action_type));
                PRAGMA application_id=1279350084;
                PRAGMA user_version=1;
                """);
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
                !Path.GetFileName(full).StartsWith("t080-safety-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(full)[12..], "N", out _)) throw new InvalidOperationException("Unsafe test cleanup target.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
