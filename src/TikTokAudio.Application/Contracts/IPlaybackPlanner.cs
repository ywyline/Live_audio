using TikTokAudio.Domain;

namespace TikTokAudio.Application.Contracts;

public interface IPlaybackPlanner
{
    Task<PlaybackPlanItem?> SelectNextAsync(PlaybackPlannerContext context, CancellationToken cancellationToken);
    Task<OperationResult> RestoreCheckpointAsync(PlaybackCheckpoint checkpoint, CancellationToken cancellationToken);
}
