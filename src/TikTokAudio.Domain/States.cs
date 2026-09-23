namespace TikTokAudio.Domain;

public enum LiveSourceState
{
    Disconnected,
    Connecting,
    Connected,
    NeedsLogin,
    Unsupported,
    Error
}

public enum RoomControlState
{
    Disabled,
    Ready,
    Pending,
    NeedsConfirmation,
    Suspended,
    Unsupported,
    Error
}

public enum PlaybackState
{
    Idle,
    Preparing,
    BasePlaying,
    InterruptPlaying,
    Paused,
    Stopped,
    Error
}

public enum BasePlaybackMode
{
    PreRecorded,
    TtsScript
}

public enum LiveEventType
{
    Enter,
    Follow,
    Like,
    Comment,
    RoomStatus
}

public enum EventIdentityQuality
{
    Complete,
    MissingEventId,
    MissingUserId,
    Incomplete
}

public enum OperationStatus
{
    Succeeded,
    Failed,
    Unknown,
    Cancelled,
    Unsupported
}

public enum ControlActionStatus
{
    Confirmed,
    Rejected,
    Unknown,
    Unsupported,
    Cancelled
}
