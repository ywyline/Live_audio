using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Audio;

// A frame contains one sample per source channel. Position is rendered source frames,
// never the decoder's read-ahead position. Prepare must not start device playback.
public interface IAudioSessionFactory
{
    Task<IAudioPlaybackSession> PrepareAsync(AudioPlaybackRequest request, CancellationToken cancellationToken);
}

public sealed class AudioSessionStoppedEventArgs(Exception? error = null) : EventArgs
{
    public Exception? Error { get; } = error;
}

public interface IAudioPlaybackSession : IDisposable
{
    int SampleRate { get; }
    long LengthSamples { get; }
    long PositionSamples { get; }
    event EventHandler<AudioSessionStoppedEventArgs>? Stopped;
    void Play();
    void Pause();
    void Resume();
    void Stop();
}
