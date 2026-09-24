namespace TikTokAudio.Infrastructure.Audio;

// Optional capability; the frozen application audio contract remains unchanged.
public interface IFadingAudioPlaybackSession
{
    Task FadeOutAsync(CancellationToken cancellationToken);
    void RestoreVolume();
}
