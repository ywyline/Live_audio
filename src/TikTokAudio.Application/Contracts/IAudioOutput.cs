using TikTokAudio.Domain;

namespace TikTokAudio.Application.Contracts;

public interface IAudioOutput
{
    PlaybackState State { get; }
    AudioCursor? CurrentCursor { get; }

    Task<OperationResult> PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken);
    Task<OperationResult> PauseAsync(CancellationToken cancellationToken);
    Task<OperationResult> ResumeAsync(CancellationToken cancellationToken);
    Task<OperationResult> StopAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetEnvironmentMixAsync(EnvironmentMixSettings settings, CancellationToken cancellationToken);
}
