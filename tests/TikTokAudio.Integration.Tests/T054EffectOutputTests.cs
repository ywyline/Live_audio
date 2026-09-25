using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Playback;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Audio;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class T054EffectOutputTests
{
    private static readonly PlaybackEffectSelection Supported = new(1, 0, 0.3, -2, false, -35);
    private static AudioPlaybackRequest Request(long cursor = 0) => new(
        new LocalAudioAsset("synthetic.wav", "wav", "pre", null, 24000), new AudioCursor(cursor, 24000));

    [Fact]
    public async Task EffectPathAndResumePassSourceCursorToFactory()
    {
        var factory = new Factory();
        using var output = new SingleVoiceAudioOutput(factory);
        Assert.Equal(OperationStatus.Succeeded,
            (await output.PlayWithEffectsAsync(Request(1700), Supported, default)).Status);
        Assert.Equal(1700, factory.Requests[0].StartAt.SourceSampleOffset);
        Assert.Equal(Supported, factory.Selections[0]);
        factory.Sessions[0].Position = 2500;
        Assert.Equal(OperationStatus.Succeeded, (await output.PauseAsync(default)).Status);
        Assert.Equal(new AudioCursor(2500, 24000), output.CurrentCursor);
        Assert.Equal(OperationStatus.Succeeded,
            (await output.PlayWithEffectsAsync(Request(2500), Supported, default)).Status);
        Assert.Equal(2500, factory.Requests[1].StartAt.SourceSampleOffset);
        Assert.Equal(Supported, factory.Selections[1]);
        Assert.Equal(1, factory.Sessions[1].PlayCount);
    }

    [Fact]
    public async Task UnsupportedEffectDoesNotReplaceAudibleBase()
    {
        var factory = new Factory();
        using var output = new SingleVoiceAudioOutput(factory);
        Assert.Equal(OperationStatus.Succeeded, (await output.PlayAsync(Request(), default)).Status);
        Assert.Equal(OperationStatus.Unsupported, (await output.PlayWithEffectsAsync(Request(),
            Supported with { Speed = 1.01 }, default)).Status);
        Assert.Single(factory.Sessions);
        Assert.False(factory.Sessions[0].Disposed);
        Assert.Equal(PlaybackState.BasePlaying, output.State);
    }

    private sealed class Factory : IEffectAudioSessionFactory
    {
        public List<AudioPlaybackRequest> Requests { get; } = [];
        public List<PlaybackEffectSelection> Selections { get; } = [];
        public List<Session> Sessions { get; } = [];
        public bool Supports(PlaybackEffectSelection selection) => selection.Speed == 1;
        public Task<IAudioPlaybackSession> PrepareAsync(AudioPlaybackRequest request, CancellationToken token) =>
            Make(request, null);
        public Task<IAudioPlaybackSession> PrepareWithEffectsAsync(AudioPlaybackRequest request,
            PlaybackEffectSelection selection, CancellationToken token) => Make(request, selection);
        private Task<IAudioPlaybackSession> Make(AudioPlaybackRequest request, PlaybackEffectSelection? selection)
        {
            Requests.Add(request);
            if (selection is not null) Selections.Add(selection);
            var session = new Session { Position = request.StartAt.SourceSampleOffset };
            Sessions.Add(session);
            return Task.FromResult<IAudioPlaybackSession>(session);
        }
    }

    private sealed class Session : IAudioPlaybackSession
    {
        public int SampleRate => 24000;
        public long LengthSamples => 240000;
        public long Position { get; set; }
        public long PositionSamples => Position;
        public bool Disposed { get; private set; }
        public int PlayCount { get; private set; }
        public event EventHandler<AudioSessionStoppedEventArgs>? Stopped
        {
            add { }
            remove { }
        }
        public void Play() => PlayCount++;
        public void Pause() { }
        public void Resume() => Play();
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
}
