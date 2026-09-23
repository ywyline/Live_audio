using TikTokAudio.Domain;

namespace TikTokAudio.Application.Contracts;

public interface IStateStore
{
    Task<RuleSetSnapshot?> LoadRulesAsync(string ruleSetId, CancellationToken cancellationToken);
    Task<OperationResult> SaveRulesAsync(RuleSetSnapshot rules, CancellationToken cancellationToken);
    Task<PlaybackPlanSnapshot?> LoadPlanAsync(string planId, CancellationToken cancellationToken);
    Task<OperationResult> SavePlanAsync(PlaybackPlanSnapshot plan, CancellationToken cancellationToken);

    Task<SessionState?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<OperationResult> SaveSessionAsync(SessionState session, CancellationToken cancellationToken);

    Task<EventDeduplicationRecord?> FindEventAsync(string fingerprint, CancellationToken cancellationToken);
    Task<OperationResult> RecordEventAsync(EventDeduplicationRecord record, CancellationToken cancellationToken);

    Task<OperationResult> ReserveInteractionAsync(InteractionReservation reservation, CancellationToken cancellationToken);
    Task<OperationResult> CompleteInteractionAsync(Guid reservationId, DateTimeOffset completedAtUtc, CancellationToken cancellationToken);

    Task<OperationResult> SavePlaybackCheckpointAsync(PlaybackCheckpoint checkpoint, CancellationToken cancellationToken);
    Task<OperationResult> SaveAudioCacheMetadataAsync(AudioCacheMetadata metadata, CancellationToken cancellationToken);
    Task<OperationResult> AppendActionLedgerAsync(ActionLedgerEntry entry, CancellationToken cancellationToken);
}
