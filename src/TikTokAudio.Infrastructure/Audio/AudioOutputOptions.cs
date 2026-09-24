namespace TikTokAudio.Infrastructure.Audio;

public sealed record AudioOutputOptions
{
    public required string MaterialDirectory { get; init; }
    public required string CacheDirectory { get; init; }
    public required string DeviceId { get; init; }
    public required long MaxInputBytes { get; init; }
    public required long MaxDecodedBytes { get; init; }
    public int MaxPreparedSessions { get; init; } = 2;
    public int LatencyMilliseconds { get; init; } = 100;
}

public sealed record AudioOutputDevice(string Id, string Name);
