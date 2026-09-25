using TikTokAudio.Application.Playback;
using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Audio;

public interface IEffectAudioSessionFactory : IAudioSessionFactory
{
    bool Supports(PlaybackEffectSelection selection);
    Task<IAudioPlaybackSession> PrepareWithEffectsAsync(AudioPlaybackRequest request,
        PlaybackEffectSelection selection, CancellationToken cancellationToken);
}
