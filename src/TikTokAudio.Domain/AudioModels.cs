namespace TikTokAudio.Domain;

public sealed record LocalAudioAsset(
    string Path,
    string Format,
    string EngineId,
    TimeSpan? Duration,
    int? SampleRate);

public readonly record struct AudioCursor(long SourceSampleOffset, int? SampleRate)
{
    public static AudioCursor Start => new(0, null);
}

public sealed record AudioPlaybackRequest(LocalAudioAsset Asset, AudioCursor StartAt);

public sealed record EnvironmentMixSettings(bool Enabled, double RelativeDecibels);

public sealed record PlaybackCheckpoint(
    BasePlaybackMode Mode,
    PlanRevision PlanRevision,
    string? ProductId,
    string? GroupId,
    string? ClipId,
    AudioCursor Cursor,
    long Cycle,
    long EffectSeed,
    IReadOnlySet<string> ConsumedMarkerIds);

public sealed record ProductBoundary(string ProductId, string BoundaryId);

public sealed record PlaybackPlanItem(
    LocalAudioAsset Asset,
    BasePlaybackMode Mode,
    PlanRevision PlanRevision,
    string? ProductId,
    string? GroupId,
    string? ClipId,
    long Cycle,
    long EffectSeed,
    IReadOnlyList<ProductBoundary> Boundaries);

public sealed record PlaybackPlannerContext(
    BasePlaybackMode Mode,
    PlanRevision PlanRevision,
    string? ProductId,
    string? GroupId,
    long Cycle);
