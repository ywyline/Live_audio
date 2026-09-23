namespace TikTokAudio.Domain;

public sealed record SessionState(Guid SessionId, string RoomId, DateTimeOffset StartedAtUtc);

public sealed record EventDeduplicationRecord(
    string Fingerprint,
    DateTimeOffset ReceivedAtUtc,
    EventIdentityQuality Quality);

public sealed record InteractionReservation(
    Guid ReservationId,
    string EventFingerprint,
    string ActionType,
    Guid SessionId,
    DateTimeOffset ReservedAtUtc);

public sealed record AudioCacheMetadata(
    string CacheKey,
    string Path,
    string EngineId,
    EngineRevision EngineRevision,
    DateTimeOffset CreatedAtUtc);

public sealed record ActionLedgerEntry(
    Guid ActionId,
    Guid SessionId,
    string ActionType,
    ControlActionStatus Status,
    DateTimeOffset RecordedAtUtc,
    string? Detail);
