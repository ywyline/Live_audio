using TikTokAudio.Domain;

namespace TikTokAudio.Application.Playback;

public sealed record PlannerBagSnapshot(
    string GroupId,
    IReadOnlyList<string> RemainingClipIds,
    string? LastClipId,
    ulong RandomState);

// Persist this together with the checkpoint; a cursor alone cannot reconstruct shuffle history.
public sealed record PreRecordedPlannerSnapshot(
    string CatalogFingerprint,
    PlaybackCheckpoint Checkpoint,
    IReadOnlyList<PlannerBagSnapshot> Bags,
    ulong EffectRandomState);
