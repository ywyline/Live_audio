using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Control;

public enum ProductTargetOrigin { ProductEnter, ScriptMarker, Manual }

public sealed record ProductControlOptions
{
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public bool AllowRejectedRetry { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RenewalInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RequestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RetryDelay, TimeSpan.Zero);
    }
}

public sealed record ProductControlIgnoredResponse(
    RoomOperationContext Context, ProductTarget Target, ControlActionResult Result, DateTimeOffset ReceivedAt);

public sealed record ProductControlSnapshot(
    RoomControlState State,
    RoomOperationContext? Context,
    ProductTarget? Target,
    DateTimeOffset? LastShownAt,
    MonotonicTimestamp? RenewalDue,
    ControlActionResult? LastResult,
    bool HasInFlightOperation,
    string? Detail,
    ProductControlIgnoredResponse? LastIgnoredResponse);
