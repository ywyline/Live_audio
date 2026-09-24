using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Events;

public interface ILiveEventProcessor
{
    Task<EventProcessResult> ProcessAsync(LiveEvent liveEvent, CancellationToken cancellationToken = default);
    Task<OperationResult> CompleteAsync(Guid reservationId, CancellationToken cancellationToken = default);
    Task<OperationResult> CancelAsync(Guid reservationId, CancellationToken cancellationToken = default);
    Task<OperationResult> ReconnectAsync(LiveConnectionRequest request, CancellationToken cancellationToken = default);
}

public enum EventProcessStatus
{
    Accepted,
    Duplicate,
    Reserved,
    SkippedMissingUserId,
    Completed,
    Cancelled,
    Failed,
    Unknown
}

public sealed record EventProcessResult(
    EventProcessStatus Status,
    string Fingerprint,
    EventIdentityQuality IdentityQuality,
    Guid? ReservationId = null,
    string? Detail = null)
{
    public bool ShouldProcess => Status is EventProcessStatus.Accepted or EventProcessStatus.Reserved;
}
