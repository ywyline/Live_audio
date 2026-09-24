namespace TikTokAudio.Infrastructure.Audio;

public static class AudioFade
{
    // This gain applies only to the owned stream, never to the device master volume.
    public static async Task FadeOutAsync(Action<float> applyGain,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyGain);
        ArgumentNullException.ThrowIfNull(delay);
        for (var step = 1; step <= 5; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await delay(TimeSpan.FromMilliseconds(6), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            applyGain(1f - step / 5f);
        }
    }
}
