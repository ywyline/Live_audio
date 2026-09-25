using System.Globalization;
using Microsoft.Data.Sqlite;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Persistence;

public sealed partial class SqliteStateStore
{
    public Task<RuleSetSnapshot?> LoadRulesAsync(string ruleSetId, CancellationToken cancellationToken = default) =>
        ReadAsync(connection => LoadPayload<RuleSetSnapshot>(connection, "rule_sets", ruleSetId), cancellationToken);

    public Task<OperationResult> SaveRulesAsync(RuleSetSnapshot rules, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, _) =>
        {
            ArgumentNullException.ThrowIfNull(rules);
            if (rules.Version < 0) throw new ArgumentException("Invalid rules version.");
            SavePayload(connection, transaction, "rule_sets", rules.RuleSetId, Encode(rules with { UpdatedAtUtc = rules.UpdatedAtUtc.ToUniversalTime() }));
        }, cancellationToken);

    public Task<PlaybackPlanSnapshot?> LoadPlanAsync(string planId, CancellationToken cancellationToken = default) =>
        ReadAsync(connection => LoadPayload<PlaybackPlanSnapshot>(connection, "playback_plans", planId), cancellationToken);

    public Task<OperationResult> SavePlanAsync(PlaybackPlanSnapshot plan, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, _) =>
        {
            ArgumentNullException.ThrowIfNull(plan);
            Defined(plan.Mode);
            if (plan.Revision.Value < 0) throw new ArgumentException("Invalid plan revision.");
            SavePayload(connection, transaction, "playback_plans", plan.PlanId, Encode(plan with { UpdatedAtUtc = plan.UpdatedAtUtc.ToUniversalTime() }));
        }, cancellationToken);

    public Task<SessionState?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ReadAsync(connection => LoadPayload<SessionState>(connection, "sessions", Key(sessionId)), cancellationToken);

    public Task<OperationResult> SaveSessionAsync(SessionState session, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, _) =>
        {
            ArgumentNullException.ThrowIfNull(session);
            Id(session.RoomId);
            SavePayload(connection, transaction, "sessions", Key(session.SessionId), Encode(session with { StartedAtUtc = session.StartedAtUtc.ToUniversalTime() }));
        }, cancellationToken);

    public Task<EventDeduplicationRecord?> FindEventAsync(string fingerprint, CancellationToken cancellationToken = default) =>
        ReadAsync(connection => LoadPayload<EventDeduplicationRecord>(connection, "event_fingerprints", fingerprint), cancellationToken);

    public Task<OperationResult> RecordEventAsync(EventDeduplicationRecord record, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, _) =>
        {
            ArgumentNullException.ThrowIfNull(record);
            Id(record.Fingerprint);
            Defined(record.Quality);
            using var command = Command(connection, transaction,
                "INSERT INTO event_fingerprints(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO NOTHING;",
                ("$id", record.Fingerprint), ("$payload", Encode(record with { ReceivedAtUtc = record.ReceivedAtUtc.ToUniversalTime() })));
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<OperationResult> ReserveInteractionAsync(InteractionReservation reservation, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, _) =>
        {
            ArgumentNullException.ThrowIfNull(reservation);
            var id = Key(reservation.ReservationId);
            var session = Key(reservation.SessionId);
            Id(reservation.EventFingerprint);
            Id(reservation.ActionType);
            var payload = Encode(reservation with { ReservedAtUtc = reservation.ReservedAtUtc.ToUniversalTime() });
            using var existing = Command(connection, transaction, $"SELECT {BoundedPayload} FROM interactions WHERE id=$id;", ("$id", id), ("$max", _options.MaxPayloadBytes));
            if (ReadScalarPayload(existing) is string current)
            {
                if (current != payload) throw new InvalidDataException("Reservation identifier already has different content.");
                return;
            }
            using var command = Command(connection, transaction, """
                INSERT INTO interactions(id,session_id,event_fingerprint,action_type,payload)
                VALUES($id,$session,$fingerprint,$action,$payload);
                """, ("$id", id), ("$session", session), ("$fingerprint", reservation.EventFingerprint),
                ("$action", reservation.ActionType), ("$payload", payload));
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<StoredInteraction?> LoadInteractionAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
        ReadAsync(connection =>
        {
            using var command = Command(connection, null, $"SELECT {BoundedPayload},completed_at FROM interactions WHERE id=$id;", ("$id", Key(reservationId)), ("$max", _options.MaxPayloadBytes));
            using var reader = command.ExecuteReader();
            return reader.Read() ? new StoredInteraction(Decode<InteractionReservation>(ReadPayload(reader, 0)),
                reader.IsDBNull(1) ? null : DateTimeOffset.ParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture)) : null;
        }, cancellationToken);

    public Task<OperationResult> CompleteInteractionAsync(Guid reservationId, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, _) =>
        {
            var id = Key(reservationId);
            using var read = Command(connection, transaction, $"SELECT {BoundedPayload} FROM interactions WHERE id=$id;", ("$id", id), ("$max", _options.MaxPayloadBytes));
            if (ReadScalarPayload(read) is not string payload) throw new InvalidDataException("Reservation does not exist.");
            if (completedAtUtc < Decode<InteractionReservation>(payload).ReservedAtUtc) throw new ArgumentException("Completion precedes reservation.");
            using var command = Command(connection, transaction,
                "UPDATE interactions SET completed_at=COALESCE(completed_at,$completed) WHERE id=$id;", ("$completed", Utc(completedAtUtc)), ("$id", id));
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<OperationResult> SavePlaybackCheckpointAsync(PlaybackCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(OperationResult.Cancelled());
        CheckpointData data;
        try
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            Defined(checkpoint.Mode);
            if (checkpoint.PlanRevision.Value < 0 || checkpoint.Cursor.SourceSampleOffset < 0 || checkpoint.Cycle < 0 ||
                checkpoint.Cursor.SampleRate is <= 0 || checkpoint.ConsumedMarkerIds is null || checkpoint.ConsumedMarkerIds.Count > 10000)
                throw new ArgumentException("Invalid playback checkpoint.");
            foreach (var id in new[] { checkpoint.ProductId, checkpoint.GroupId, checkpoint.ClipId })
                if (id is not null) Id(id);
            var markers = checkpoint.ConsumedMarkerIds.Take(10001).ToArray();
            if (markers.Length > 10000) throw new ArgumentException("Invalid marker count.");
            foreach (var marker in markers) Id(marker);
            Array.Sort(markers, StringComparer.Ordinal);
            data = new CheckpointData(checkpoint.Mode, checkpoint.PlanRevision, checkpoint.ProductId, checkpoint.GroupId, checkpoint.ClipId,
                checkpoint.Cursor, checkpoint.Cycle, checkpoint.EffectSeed, markers);
        }
        catch (Exception error) when (IsStorageError(error)) { return Task.FromResult(OperationResult.Failed(SafeError(error))); }
        return WriteAsync((connection, transaction, _) =>
            SavePayload(connection, transaction, "checkpoints", data.Mode.ToString(), Encode(data)), cancellationToken);
    }

    public Task<PlaybackCheckpoint?> LoadPlaybackCheckpointAsync(BasePlaybackMode mode, CancellationToken cancellationToken = default) =>
        ReadAsync(connection =>
        {
            Defined(mode);
            var data = LoadPayload<CheckpointData>(connection, "checkpoints", mode.ToString());
            return data is null ? null : new PlaybackCheckpoint(data.Mode, data.PlanRevision, data.ProductId, data.GroupId, data.ClipId,
                data.Cursor, data.Cycle, data.EffectSeed, new HashSet<string>(data.ConsumedMarkerIds, StringComparer.Ordinal));
        }, cancellationToken);

    public Task<OperationResult> SaveAudioCacheMetadataAsync(AudioCacheMetadata metadata, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, _) =>
        {
            ArgumentNullException.ThrowIfNull(metadata);
            ValidateCache(metadata);
            SavePayload(connection, transaction, "cache_metadata", metadata.CacheKey, Encode(metadata with { CreatedAtUtc = metadata.CreatedAtUtc.ToUniversalTime() }));
        }, cancellationToken);

    public Task<AudioCacheMetadata?> LoadAudioCacheMetadataAsync(string cacheKey, CancellationToken cancellationToken = default) =>
        ReadAsync(connection =>
        {
            var metadata = LoadPayload<AudioCacheMetadata>(connection, "cache_metadata", cacheKey);
            if (metadata is not null) ValidateCache(metadata);
            return metadata;
        }, cancellationToken);

    private void ValidateCache(AudioCacheMetadata metadata)
    {
        Id(metadata.CacheKey);
        Id(metadata.EngineId);
        if (metadata.EngineRevision.Value < 0) throw new ArgumentException("Invalid engine revision.");
        var path = _paths.ValidatePath(metadata.Path);
        var relative = Path.GetRelativePath(_paths.CacheDirectory, path);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException("Cache metadata must reference a file inside the selected cache directory.");
    }

    public Task<OperationResult> AppendActionLedgerAsync(ActionLedgerEntry entry, CancellationToken cancellationToken = default) =>
        AppendActionLedgerBatchAsync([entry], cancellationToken);

    public Task<OperationResult> AppendActionLedgerBatchAsync(IReadOnlyList<ActionLedgerEntry> entries, CancellationToken cancellationToken = default)
    {
        // Snapshot the bounded collection before handing it to a background worker.
        if (entries is null || entries.Count > 1024) return Task.FromResult(OperationResult.Failed("Invalid ledger batch size."));
        var copy = entries.ToArray();
        return WriteAsync((connection, transaction, token) =>
        {
            foreach (var entry in copy)
            {
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(entry);
                Key(entry.SessionId);
                Id(entry.ActionType);
                Defined(entry.Status);
                if (entry.Detail?.Length > 4096) throw new ArgumentException("Ledger detail exceeds its bound.");
                SavePayload(connection, transaction, "action_ledger", Key(entry.ActionId),
                    Encode(entry with { RecordedAtUtc = entry.RecordedAtUtc.ToUniversalTime() }), immutable: true);
            }
        }, cancellationToken);
    }

    public Task<ActionLedgerEntry?> LoadActionLedgerAsync(Guid actionId, CancellationToken cancellationToken = default) =>
        ReadAsync(connection => LoadPayload<ActionLedgerEntry>(connection, "action_ledger", Key(actionId)), cancellationToken);

    public Task<OperationResult> SaveDocumentAsync(LocalStateDocument document, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, _) =>
        {
            ArgumentNullException.ThrowIfNull(document);
            Defined(document.Kind);
            Id(document.DocumentId);
            if (document.Version < 1) throw new ArgumentException("Invalid local document version.");
            CheckPayload(document.Json);
            using var command = Command(connection, transaction, """
                INSERT INTO documents(kind,id,version,payload,updated_at) VALUES($kind,$id,$version,$payload,$time)
                ON CONFLICT(kind,id) DO UPDATE SET version=excluded.version,payload=excluded.payload,updated_at=excluded.updated_at;
                """, ("$kind", (int)document.Kind), ("$id", document.DocumentId), ("$version", document.Version),
                ("$payload", document.Json), ("$time", Utc(document.UpdatedAtUtc)));
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<LocalStateDocument?> LoadDocumentAsync(LocalDocumentKind kind, string documentId, CancellationToken cancellationToken = default) =>
        ReadAsync(connection =>
        {
            Defined(kind);
            Id(documentId);
            using var command = Command(connection, null, $"SELECT version,{BoundedPayload},updated_at FROM documents WHERE kind=$kind AND id=$id;",
                ("$kind", (int)kind), ("$id", documentId), ("$max", _options.MaxPayloadBytes));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var json = ReadPayload(reader, 1);
            CheckPayload(json);
            return new LocalStateDocument(kind, documentId, reader.GetInt32(0), json,
                DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture));
        }, cancellationToken);

    private sealed record CheckpointData(BasePlaybackMode Mode, PlanRevision PlanRevision, string? ProductId,
        string? GroupId, string? ClipId, AudioCursor Cursor, long Cycle, long EffectSeed, string[] ConsumedMarkerIds);
}
