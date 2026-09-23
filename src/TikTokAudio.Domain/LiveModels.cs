namespace TikTokAudio.Domain;

public sealed record LiveEvent(
    string SourceId,
    Guid SessionId,
    string RoomId,
    LiveEventType EventType,
    string? EventId,
    string? UserId,
    string? DisplayName,
    DateTimeOffset? OccurredAt,
    DateTimeOffset ReceivedAt,
    string? Content,
    long? LikeCountDelta,
    long? LikeCountTotal,
    EventIdentityQuality IdentityQuality);

public sealed record LiveConnectionRequest(Guid SessionId, string RoomId);

public sealed record LiveSourceCapabilities(
    bool ReadEnter,
    bool ReadFollow,
    bool ReadLike,
    bool ReadComment,
    bool ReadRoomStatus);

public sealed record RoomControlCapabilities(
    bool ShowProduct,
    bool ReadProductVisibility,
    bool SendText);

public sealed record ProductTarget(string LocalProductId, string PlatformProductId);

public sealed record RoomOperationContext(
    Guid SessionId,
    string RoomId,
    ProductEpoch ProductEpoch);

public sealed record ProductVisibility(
    ProductTarget Target,
    bool IsVisible,
    DateTimeOffset? ConfirmedAt);
