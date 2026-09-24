using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Playback;

public enum BasePlanAvailability { Ready, WaitingForSynthesis, Completed, Failed }

/// <summary>Application scheduling bridge; the frozen IPlaybackPlanner contract remains unchanged.</summary>
public interface IBasePlaybackPlan : IPlaybackPlanner
{
    BasePlaybackMode Mode { get; }
    PlanRevision Revision { get; }
    PlaybackPlanItem? CurrentItem { get; }
    AudioCursor CurrentCursor { get; }
    BasePlanAvailability Availability { get; }
    string? Detail { get; }
    OperationResult<IReadOnlyList<ProductBoundary>> AcknowledgePlaybackStarted(PlaybackPlanItem item);
    OperationResult SaveCursor(AudioCursor cursor);
    // Invalidate preparation synchronously, observe cancellation asynchronously; never block Stop.
    void CancelPendingPreparation();
}
