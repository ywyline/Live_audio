using System.Text.Json;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Playback;
using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Persistence;

/// <summary>Persists only local playback recovery data; loading never starts playback or platform actions.</summary>
public sealed class SqlitePlaybackRecoveryStore(SqliteStateStore stateStore) : IPlaybackRecoveryStore
{
    public const string DocumentId = "playback-recovery";
    private const int DocumentVersion = PlaybackRecoveryState.CurrentVersion;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };
    private readonly SqliteStateStore stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));

    public async Task<OperationResult> SaveAsync(
        PlaybackRecoveryState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsValid)
            return OperationResult.Failed("恢复状态版本或内容无效。");

        try
        {
            var document = new RecoveryDocument(
                state.Version,
                state.CapturedAtUtc.ToUniversalTime(),
                state.Mode,
                state.PlanRevision.Value,
                ToDocument(state.Checkpoint),
                state.PreRecordedSnapshot is null ? null : ToDocument(state.PreRecordedSnapshot),
                state.EngineId,
                state.EngineRevision?.Value,
                state.DefaultVoiceId);
            var json = JsonSerializer.Serialize(document, JsonOptions);
            return await stateStore.SaveDocumentAsync(
                new LocalStateDocument(LocalDocumentKind.ShuffleBag, DocumentId, DocumentVersion, json,
                    DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            return OperationResult.Failed($"恢复状态无法编码：{error.Message}");
        }
    }

    public async Task<PlaybackRecoveryState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await stateStore.LoadDocumentAsync(
            LocalDocumentKind.ShuffleBag, DocumentId, cancellationToken).ConfigureAwait(false);
        if (stored is null) return null;
        if (stored.Version != DocumentVersion)
            throw new InvalidDataException("本地恢复状态文档版本不受支持。");

        RecoveryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<RecoveryDocument>(stored.Json, JsonOptions);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("本地恢复状态文档损坏。", error);
        }

        if (document is null || document.Version != PlaybackRecoveryState.CurrentVersion)
            throw new InvalidDataException("本地恢复状态内容无效。");

        PlaybackRecoveryState state;
        try
        {
            state = FromDocument(document);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("本地恢复状态内容无效。", error);
        }

        if (!state.IsValid)
            throw new InvalidDataException("本地恢复状态内容无效。");
        return state;
    }

    private static CheckpointDocument ToDocument(PlaybackCheckpoint checkpoint) => new(
        checkpoint.Mode, checkpoint.PlanRevision.Value, checkpoint.ProductId, checkpoint.GroupId, checkpoint.ClipId,
        new CursorDocument(checkpoint.Cursor.SourceSampleOffset, checkpoint.Cursor.SampleRate), checkpoint.Cycle,
        checkpoint.EffectSeed, checkpoint.ConsumedMarkerIds?.ToArray() ?? []);

    private static SnapshotDocument ToDocument(PreRecordedPlannerSnapshot snapshot) => new(
        snapshot.CatalogFingerprint, ToDocument(snapshot.Checkpoint), snapshot.Bags.Select(bag =>
            new BagDocument(bag.GroupId, bag.RemainingClipIds.ToArray(), bag.LastClipId, bag.RandomState)).ToArray(),
        snapshot.EffectRandomState);

    private static PlaybackRecoveryState FromDocument(RecoveryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var checkpoint = FromDocument(document.Checkpoint);
        var snapshot = document.PreRecordedSnapshot is null ? null : FromDocument(document.PreRecordedSnapshot);
        return new PlaybackRecoveryState(document.Version, document.CapturedAtUtc, document.Mode,
            new PlanRevision(document.PlanRevision), checkpoint, snapshot, document.EngineId,
            document.EngineRevision is { } revision ? new EngineRevision(revision) : null, document.DefaultVoiceId);
    }

    private static PlaybackCheckpoint FromDocument(CheckpointDocument? document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.ConsumedMarkerIds is null) throw new InvalidDataException("缺少已消费标记。");
        return new PlaybackCheckpoint(document.Mode, new PlanRevision(document.PlanRevision), document.ProductId,
            document.GroupId, document.ClipId, new AudioCursor(document.Cursor.SourceSampleOffset, document.Cursor.SampleRate),
            document.Cycle, document.EffectSeed,
            new HashSet<string>(document.ConsumedMarkerIds, StringComparer.Ordinal));
    }

    private static PreRecordedPlannerSnapshot FromDocument(SnapshotDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Bags is null) throw new InvalidDataException("缺少洗牌袋。");
        return new PreRecordedPlannerSnapshot(document.CatalogFingerprint, FromDocument(document.Checkpoint),
            document.Bags.Select(bag =>
            {
                if (bag is null || bag.RemainingClipIds is null)
                    throw new InvalidDataException("洗牌袋内容无效。");
                return new PlannerBagSnapshot(bag.GroupId, Array.AsReadOnly(bag.RemainingClipIds), bag.LastClipId,
                    bag.RandomState);
            }).ToArray(), document.EffectRandomState);
    }

    private sealed record RecoveryDocument(
        int Version,
        DateTimeOffset CapturedAtUtc,
        BasePlaybackMode Mode,
        long PlanRevision,
        CheckpointDocument Checkpoint,
        SnapshotDocument? PreRecordedSnapshot,
        string? EngineId,
        long? EngineRevision,
        string? DefaultVoiceId);

    private sealed record CheckpointDocument(
        BasePlaybackMode Mode,
        long PlanRevision,
        string? ProductId,
        string? GroupId,
        string? ClipId,
        CursorDocument Cursor,
        long Cycle,
        long EffectSeed,
        string[]? ConsumedMarkerIds);

    private sealed record CursorDocument(long SourceSampleOffset, int? SampleRate);

    private sealed record SnapshotDocument(
        string CatalogFingerprint,
        CheckpointDocument Checkpoint,
        BagDocument[]? Bags,
        ulong EffectRandomState);

    private sealed record BagDocument(
        string GroupId,
        string[]? RemainingClipIds,
        string? LastClipId,
        ulong RandomState);
}