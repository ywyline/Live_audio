using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Playback;

public enum InteractionPriority { Welcome, Keyword, Follow }

public sealed record InteractionRequest(
    string RequestId,
    string SessionId,
    InteractionPriority Priority,
    MonotonicTimestamp ReceivedAt,
    TimeSpan TimeToLive,
    Func<CancellationToken, Task<OperationResult<LocalAudioAsset>>> PrepareAsync);

public sealed record InteractionCompletion(string RequestId, OperationStatus Status, string? Detail = null, string? SessionId = null);

public sealed record SchedulerUpdate(
    OperationResult Result,
    IReadOnlyList<ProductBoundary> Boundaries,
    IReadOnlyList<InteractionCompletion> Interactions);
