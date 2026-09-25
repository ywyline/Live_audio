using TikTokAudio.Application.Playback;

namespace TikTokAudio.Infrastructure.Audio;

// The caller owns this short-lived cache asset and must dispose it after audition.
public sealed class AudioEffectPreview : IDisposable
{
    private readonly PreparedAudioFile file;
    private readonly EffectFrameMap frameMap;

    private AudioEffectPreview(PreparedAudioFile file, EffectFrameMap frameMap)
    {
        this.file = file;
        this.frameMap = frameMap;
    }

    public string Path => file.Path;
    public int SampleRate => file.SampleRate;
    public long SourceFrames => frameMap.SourceFrames;
    public long RenderedFrames => frameMap.RenderedFrames;

    public static async Task<AudioEffectPreview> CreateAsync(AudioOutputOptions options, string sourcePath,
        PlaybackEffectSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!EffectWaveProcessor.Supports(selection))
            throw new NotSupportedException("The selected effects cannot be rendered for preview.");
        var file = await AudioFilePreparation.PrepareAsync(options, sourcePath, cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await EffectWaveProcessor.ProcessAsync(file.Path, selection,
                options.MaxDecodedBytes, cancellationToken).ConfigureAwait(false);
            return new AudioEffectPreview(file, map);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void Dispose() => file.Dispose();
}
