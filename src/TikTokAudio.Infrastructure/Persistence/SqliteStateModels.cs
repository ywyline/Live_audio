using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Persistence;

public sealed record SqliteStateStoreOptions(int BusyTimeoutSeconds, int MaxPayloadBytes)
{
    internal void Validate()
    {
        if (BusyTimeoutSeconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(BusyTimeoutSeconds));
        if (MaxPayloadBytes is < 1 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes));
    }
}

public enum LocalDocumentKind { Settings, Rules, MediaIndex, ShuffleBag, ProductState }

public sealed record LocalStateDocument(LocalDocumentKind Kind, string DocumentId, int Version,
    string Json, DateTimeOffset UpdatedAtUtc);

public sealed record StoredInteraction(InteractionReservation Reservation, DateTimeOffset? CompletedAtUtc);
