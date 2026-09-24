using System.Security.Cryptography;
using System.Text;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Events;

/// <summary>Outcome of recording an incoming event for event-level deduplication.</summary>
public enum EventDeduplicationStatus
{
    Accepted,
    Duplicate,
    DegradedAccepted,
    Unknown,
    Failed,
    Cancelled,
    Invalid
}

/// <summary>Outcome of reserving a user-facing interaction.</summary>
public enum InteractionReservationStatus
{
    Reserved,
    AlreadyReserved,
    SkippedMissingUserId,
    Completed,
    Unknown,
    Failed,
    Cancelled,
    Invalid
}

public sealed record EventDeduplicationResult(
    EventDeduplicationStatus Status,
    string Fingerprint,
    EventIdentityQuality Quality,
    string? Detail = null)
{
    public bool ShouldProcess => Status is EventDeduplicationStatus.Accepted or EventDeduplicationStatus.DegradedAccepted;
    public bool IsDegraded => Quality != EventIdentityQuality.Complete;
}

public sealed record InteractionReservationResult(
    InteractionReservationStatus Status,
    Guid ReservationId,
    string Fingerprint,
    string? Detail = null)
{
    public bool IsReserved => Status == InteractionReservationStatus.Reserved;
    public bool IsSkipped => Status == InteractionReservationStatus.SkippedMissingUserId;
}

/// <summary>
/// Coordinates strict event deduplication and per-session user/action reservations.
/// The class deliberately does not execute audio or platform side effects.
/// </summary>
public class EventDeduplicationCoordinator : ILiveEventProcessor
{
    private static readonly TimeSpan MissingEventIdBucket = TimeSpan.FromSeconds(2);

    private readonly IStateStore stateStore;
    private readonly object gate = new();
    private readonly Dictionary<string, InteractionReservationStatus> reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<EventDeduplicationResult>> eventOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<InteractionReservationResult>> reservationOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> reservationIds = new();
    private readonly Dictionary<Guid, InteractionReservationStatus> reservationStates = new();
    private Guid? currentSessionId;
    private string? currentRoomId;

    public EventDeduplicationCoordinator(IStateStore stateStore)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task<EventProcessResult> ProcessAsync(
        LiveEvent liveEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        var eventResult = await RegisterEventAsync(liveEvent, cancellationToken).ConfigureAwait(false);
        if (!eventResult.ShouldProcess)
        {
            var processStatus = eventResult.Status switch
            {
                EventDeduplicationStatus.Duplicate => EventProcessStatus.Duplicate,
                EventDeduplicationStatus.Cancelled => EventProcessStatus.Cancelled,
                EventDeduplicationStatus.Unknown => EventProcessStatus.Unknown,
                _ => EventProcessStatus.Failed
            };
            return new(processStatus, eventResult.Fingerprint, eventResult.Quality, null, eventResult.Detail);
        }

        if (liveEvent.EventType is not (LiveEventType.Enter or LiveEventType.Follow))
            return new(EventProcessStatus.Accepted, eventResult.Fingerprint, eventResult.Quality, null, eventResult.Detail);

        var reservation = await ReserveInteractionAsync(liveEvent, liveEvent.EventType.ToString(), cancellationToken).ConfigureAwait(false);
        var status = reservation.Status switch
        {
            InteractionReservationStatus.Reserved => EventProcessStatus.Reserved,
            InteractionReservationStatus.AlreadyReserved => EventProcessStatus.Duplicate,
            InteractionReservationStatus.Completed => EventProcessStatus.Completed,
            InteractionReservationStatus.SkippedMissingUserId => EventProcessStatus.SkippedMissingUserId,
            InteractionReservationStatus.Cancelled => EventProcessStatus.Cancelled,
            InteractionReservationStatus.Unknown => EventProcessStatus.Unknown,
            _ => EventProcessStatus.Failed
        };
        if (reservation.ReservationId != Guid.Empty)
        {
            lock (gate)
            {
                reservationIds[reservation.ReservationId] = reservation.Fingerprint;
                reservationStates[reservation.ReservationId] = reservation.Status;
            }
        }
        return new(status, eventResult.Fingerprint, eventResult.Quality,
            reservation.ReservationId == Guid.Empty ? null : reservation.ReservationId,
            reservation.Detail ?? eventResult.Detail);
    }

    public async Task<OperationResult> CompleteAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        string? fingerprint;
        InteractionReservationStatus state;
        lock (gate)
        {
            if (!reservationIds.TryGetValue(reservationId, out fingerprint))
                return OperationResult.Failed("Unknown interaction reservation.");
            state = reservationStates[reservationId];
        }
        if (state != InteractionReservationStatus.Reserved)
            return state == InteractionReservationStatus.Completed
                ? OperationResult.Succeeded("Interaction already completed.")
                : OperationResult.Failed("Interaction reservation is not in Reserved state.");

        OperationResult result;
        try
        {
            result = await stateStore.CompleteInteractionAsync(
                reservationId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationResult.Cancelled("Completion was cancelled; the interaction remains unconfirmed.");
        }

        lock (gate)
        {
            reservationStates[reservationId] = result.Status switch
            {
                OperationStatus.Succeeded => InteractionReservationStatus.Completed,
                OperationStatus.Unknown => InteractionReservationStatus.Unknown,
                _ => InteractionReservationStatus.Reserved
            };
            if (fingerprint is not null && result.Status == OperationStatus.Succeeded)
                reservations[fingerprint] = InteractionReservationStatus.Completed;
        }
        return result;
    }

    public Task<OperationResult> CancelAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(OperationResult.Cancelled("Cancellation requested before interaction cancellation."));
        lock (gate)
        {
            if (!reservationIds.ContainsKey(reservationId))
                return Task.FromResult(OperationResult.Failed("Unknown interaction reservation."));
            reservationStates[reservationId] = InteractionReservationStatus.Cancelled;
        }
        return Task.FromResult(OperationResult.Cancelled("Interaction cancelled; it is not recorded as completed."));
    }

    public async Task<OperationResult> ReconnectAsync(
        LiveConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.RoomId))
            return OperationResult.Failed("Reconnect requires a non-empty SessionId and RoomId.");

        lock (gate)
        {
            if (currentSessionId is not null && currentSessionId != request.SessionId)
                return OperationResult.Failed("Reconnect SessionId does not match the current session.");
            if (currentRoomId is not null && !string.Equals(currentRoomId, request.RoomId, StringComparison.Ordinal))
                return OperationResult.Failed("Reconnect RoomId does not match the current session.");
        }

        var previous = await stateStore.LoadSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (previous is not null && !string.Equals(previous.RoomId, request.RoomId, StringComparison.Ordinal))
            return OperationResult.Failed("Persisted RoomId does not match the reconnect request.");

        var save = await stateStore.SaveSessionAsync(
            previous ?? new SessionState(request.SessionId, request.RoomId, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        if (save.Status == OperationStatus.Succeeded)
        {
            lock (gate)
            {
                currentSessionId = request.SessionId;
                currentRoomId = request.RoomId;
            }
        }
        return save;
    }

    /// <summary>
    /// Records an event before any downstream side effect. Calls for one fingerprint
    /// are serialized so concurrent delivery cannot pass the find-then-record window twice.
    /// </summary>
    public Task<EventDeduplicationResult> RegisterEventAsync(
        LiveEvent liveEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        var fingerprint = BuildEventFingerprint(liveEvent);
        lock (gate)
        {
            if (eventOperations.TryGetValue(fingerprint, out var existing))
            {
                return existing.WaitAsync(cancellationToken);
            }

            var operation = RegisterEventCoreAsync(liveEvent, fingerprint, cancellationToken);
            eventOperations[fingerprint] = operation;
            _ = RemoveCompletedEventOperationAsync(fingerprint, operation);
            return operation;
        }
    }

    /// <summary>Alias used by hosts that name the operation ProcessEvent.</summary>
    public Task<EventDeduplicationResult> ProcessEventAsync(
        LiveEvent liveEvent,
        CancellationToken cancellationToken = default) => RegisterEventAsync(liveEvent, cancellationToken);

    /// <summary>
    /// Reserves one per-session user/action slot. Missing stable user IDs are explicitly
    /// skipped; display names are never used as a substitute.
    /// </summary>
    public Task<InteractionReservationResult> ReserveInteractionAsync(
        LiveEvent liveEvent,
        string actionType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return Task.FromResult(new InteractionReservationResult(
                InteractionReservationStatus.Invalid,
                Guid.Empty,
                string.Empty,
                "ActionType 不能为空。"));
        }

        var normalizedAction = NormalizeIdentity(actionType);
        if (string.IsNullOrWhiteSpace(liveEvent.UserId))
        {
            return Task.FromResult(new InteractionReservationResult(
                InteractionReservationStatus.SkippedMissingUserId,
                Guid.Empty,
                BuildBusinessFingerprint(liveEvent, normalizedAction),
                "缺少稳定 UserId，跳过针对性互动；不能使用昵称代替用户标识。"));
        }

        var fingerprint = BuildBusinessFingerprint(liveEvent, normalizedAction);
        lock (gate)
        {
            if (reservationOperations.TryGetValue(fingerprint, out var existing))
            {
                return existing.WaitAsync(cancellationToken);
            }

            var operation = ReserveInteractionCoreAsync(liveEvent, normalizedAction, fingerprint, cancellationToken);
            reservationOperations[fingerprint] = operation;
            _ = RemoveCompletedReservationOperationAsync(fingerprint, operation);
            return operation;
        }
    }

    /// <summary>Marks a successfully completed interaction and persists the completion.</summary>
    public async Task<OperationResult> CompleteInteractionAsync(
        InteractionReservationResult reservation,
        CancellationToken cancellationToken = default)
    {
        if (reservation.Status != InteractionReservationStatus.Reserved || reservation.ReservationId == Guid.Empty)
        {
            return OperationResult.Failed("只有 Reserved 状态的预留可以完成。");
        }

        var result = await stateStore.CompleteInteractionAsync(
            reservation.ReservationId,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        lock (gate)
        {
            if (result.Status == OperationStatus.Succeeded)
            {
                reservations[reservation.Fingerprint] = InteractionReservationStatus.Completed;
            }
            else if (result.Status == OperationStatus.Unknown)
            {
                reservations[reservation.Fingerprint] = InteractionReservationStatus.Unknown;
            }
        }

        return result;
    }

    /// <summary>
    /// Retains a reservation as Unknown after an interrupted/uncertain side effect.
    /// It is intentionally not completed and therefore cannot be automatically replayed.
    /// </summary>
    public OperationResult MarkInteractionUnknown(InteractionReservationResult reservation, string? detail = null)
    {
        if (reservation.ReservationId == Guid.Empty || string.IsNullOrWhiteSpace(reservation.Fingerprint))
        {
            return OperationResult.Failed("无效的互动预留。");
        }

        lock (gate)
        {
            reservations[reservation.Fingerprint] = InteractionReservationStatus.Unknown;
        }

        return OperationResult.Unknown(detail ?? "互动结果未知，保留预留并等待人工确认。");
    }

    public InteractionReservationStatus GetInteractionStatus(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return InteractionReservationStatus.Invalid;
        lock (gate)
        {
            return reservations.TryGetValue(fingerprint, out var status)
                ? status
                : InteractionReservationStatus.Unknown;
        }
    }

    /// <summary>Strict identity: SourceId + RoomId + EventType + EventId.</summary>
    public static string BuildStrictFingerprint(LiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        if (string.IsNullOrWhiteSpace(liveEvent.EventId))
        {
            throw new ArgumentException("严格事件指纹需要 EventId。", nameof(liveEvent));
        }

        return HashFingerprint($"strict\u001f{NormalizeIdentity(liveEvent.SourceId)}\u001f{NormalizeIdentity(liveEvent.RoomId)}\u001f{liveEvent.EventType}\u001f{NormalizeIdentity(liveEvent.EventId!)}");
    }

    public static string BuildEventFingerprint(LiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        if (!string.IsNullOrWhiteSpace(liveEvent.EventId)) return BuildStrictFingerprint(liveEvent);

        var timestamp = liveEvent.OccurredAt ?? liveEvent.ReceivedAt;
        timestamp = Bucket(timestamp, MissingEventIdBucket);
        var quality = $"fallback\u001f{NormalizeIdentity(liveEvent.SourceId)}\u001f{NormalizeIdentity(liveEvent.RoomId)}\u001f{liveEvent.EventType}\u001f{NormalizeIdentity(liveEvent.UserId)}\u001f{NormalizeContent(liveEvent.Content)}\u001f{timestamp.UtcTicks}";
        return HashFingerprint(quality);
    }

    public static string BuildBusinessFingerprint(LiveEvent liveEvent, string actionType)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        if (string.IsNullOrWhiteSpace(liveEvent.UserId)) return string.Empty;
        return HashFingerprint($"business\u001f{liveEvent.SessionId:D}\u001f{NormalizeIdentity(liveEvent.RoomId)}\u001f{NormalizeIdentity(liveEvent.UserId)}\u001f{NormalizeIdentity(actionType)}");
    }

    private async Task<EventDeduplicationResult> RegisterEventCoreAsync(
        LiveEvent liveEvent,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await stateStore.FindEventAsync(fingerprint, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var replayAge = liveEvent.ReceivedAt.ToUniversalTime() - existing.ReceivedAtUtc;
                var isShortTermFallback = string.IsNullOrWhiteSpace(liveEvent.EventId);
                if (!isShortTermFallback || replayAge <= MissingEventIdBucket)
                    return new(EventDeduplicationStatus.Duplicate, fingerprint, existing.Quality,
                        "Event already recorded; duplicate side effects are skipped.");
            }

            var quality = DetermineQuality(liveEvent);
            var recordResult = await stateStore.RecordEventAsync(
                new EventDeduplicationRecord(fingerprint, liveEvent.ReceivedAt.ToUniversalTime(), quality),
                cancellationToken).ConfigureAwait(false);
            if (recordResult.Status == OperationStatus.Succeeded)
            {
                return new(
                    quality == EventIdentityQuality.Complete ? EventDeduplicationStatus.Accepted : EventDeduplicationStatus.DegradedAccepted,
                    fingerprint,
                    quality,
                    quality == EventIdentityQuality.Complete ? null : "缺少完整事件身份，已采用降级短期指纹。");
            }

            return new(
                recordResult.Status == OperationStatus.Cancelled ? EventDeduplicationStatus.Cancelled :
                recordResult.Status == OperationStatus.Unknown ? EventDeduplicationStatus.Unknown : EventDeduplicationStatus.Failed,
                fingerprint,
                quality,
                recordResult.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(EventDeduplicationStatus.Cancelled, fingerprint, DetermineQuality(liveEvent), "事件去重已取消。");
        }
    }

    private async Task<InteractionReservationResult> ReserveInteractionCoreAsync(
        LiveEvent liveEvent,
        string actionType,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (reservations.TryGetValue(fingerprint, out var current))
            {
                var knownStatus = current switch
                {
                    InteractionReservationStatus.Completed => InteractionReservationStatus.Completed,
                    InteractionReservationStatus.Unknown => InteractionReservationStatus.Unknown,
                    InteractionReservationStatus.Cancelled => InteractionReservationStatus.Cancelled,
                    _ => InteractionReservationStatus.AlreadyReserved
                };
                return new(knownStatus, CreateDeterministicGuid(fingerprint), fingerprint,
                    "The same session, user, and action is already reserved or completed.");
            }
        }

        var reservationId = CreateDeterministicGuid(fingerprint);
        try
        {
            var result = await stateStore.ReserveInteractionAsync(
                new InteractionReservation(reservationId, fingerprint, actionType, liveEvent.SessionId, liveEvent.ReceivedAt.ToUniversalTime()),
                cancellationToken).ConfigureAwait(false);
            if (result.Status == OperationStatus.Succeeded)
            {
                lock (gate)
                {
                    reservations[fingerprint] = InteractionReservationStatus.Reserved;
                    reservationIds[reservationId] = fingerprint;
                    reservationStates[reservationId] = InteractionReservationStatus.Reserved;
                }
                return new(InteractionReservationStatus.Reserved, reservationId, fingerprint);
            }

            return new(
                result.Status == OperationStatus.Cancelled ? InteractionReservationStatus.Cancelled :
                result.Status == OperationStatus.Unknown ? InteractionReservationStatus.Unknown : InteractionReservationStatus.Failed,
                reservationId,
                fingerprint,
                result.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(InteractionReservationStatus.Cancelled, reservationId, fingerprint, "互动预留已取消。");
        }
    }

    private async Task RemoveCompletedEventOperationAsync(string fingerprint, Task<EventDeduplicationResult> operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { }
        lock (gate)
        {
            if (eventOperations.TryGetValue(fingerprint, out var current) && ReferenceEquals(current, operation))
                eventOperations.Remove(fingerprint);
        }
    }

    private async Task RemoveCompletedReservationOperationAsync(string fingerprint, Task<InteractionReservationResult> operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { }
        lock (gate)
        {
            if (reservationOperations.TryGetValue(fingerprint, out var current) && ReferenceEquals(current, operation))
                reservationOperations.Remove(fingerprint);
        }
    }

    private static EventIdentityQuality DetermineQuality(LiveEvent liveEvent)
    {
        var missingEventId = string.IsNullOrWhiteSpace(liveEvent.EventId);
        var missingUserId = string.IsNullOrWhiteSpace(liveEvent.UserId);
        if (missingEventId && missingUserId) return EventIdentityQuality.Incomplete;
        if (missingEventId) return EventIdentityQuality.MissingEventId;
        if (missingUserId) return EventIdentityQuality.MissingUserId;
        return liveEvent.IdentityQuality == EventIdentityQuality.Complete ? EventIdentityQuality.Complete : liveEvent.IdentityQuality;
    }

    private static DateTimeOffset Bucket(DateTimeOffset value, TimeSpan size)
    {
        var utcTicks = value.UtcTicks - value.UtcTicks % size.Ticks;
        return new DateTimeOffset(utcTicks, TimeSpan.Zero);
    }

    private static string NormalizeIdentity(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeContent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        var builder = new StringBuilder(normalized.Length);
        var whitespace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespace = true;
                continue;
            }
            if (whitespace && builder.Length > 0) builder.Append(' ');
            whitespace = false;
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static string HashFingerprint(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }
}

/// <summary>Descriptive alias for hosts that include the source in the type name.</summary>
public sealed class LiveEventDeduplicationCoordinator : EventDeduplicationCoordinator
{
    public LiveEventDeduplicationCoordinator(IStateStore stateStore) : base(stateStore) { }
}
