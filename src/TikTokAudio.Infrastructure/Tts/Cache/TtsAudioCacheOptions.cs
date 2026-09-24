namespace TikTokAudio.Infrastructure.Tts.Cache;

public sealed class TtsAudioCacheOptions
{
    public required string CacheDirectory { get; init; }
    public required string StagingDirectory { get; init; }
    public required int MaxEntries { get; init; }
    public required long MaxTotalBytes { get; init; }
    public required long MaxAssetBytes { get; init; }
}
