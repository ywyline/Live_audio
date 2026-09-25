using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Control;

public enum TextDispatchSource { Manual, Timed, Keyword }

public sealed record TextDispatchOptions(int QueueCapacity, int LedgerCapacity, int ScheduleCapacity,
    TimeSpan RequestTimeout, TimeSpan VerifiedMinimumInterval)
{
    public TimeSpan SendInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan EffectiveInterval => SendInterval >= VerifiedMinimumInterval ? SendInterval : VerifiedMinimumInterval;

    internal void Validate()
    {
        if (QueueCapacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        if (LedgerCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(LedgerCapacity));
        if (ScheduleCapacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(ScheduleCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RequestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SendInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(VerifiedMinimumInterval, TimeSpan.Zero);
    }
}

public sealed record TextDispatchLateResult(Guid ActionId, ControlActionStatus Status, DateTimeOffset ReceivedAtUtc);

public sealed record TextDispatchSnapshot(RoomControlState State, RoomOperationContext? Context,
    int QueuedCount, bool HasInFlightOperation, DateTimeOffset? LastSentAt,
    MonotonicTimestamp? NextSendAt, int PendingLedgerCount, long DroppedCount,
    ControlActionResult? LastResult, string? Detail, TextDispatchLateResult? LastIgnoredResponse);
