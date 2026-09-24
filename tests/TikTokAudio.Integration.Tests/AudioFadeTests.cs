using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Audio;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class AudioFadeTests
{
    [Fact]
    public async Task FadeUsesBoundedThirtyMillisecondStreamGainRamp()
    {
        var gains = new List<float>();
        var duration = TimeSpan.Zero;
        await AudioFade.FadeOutAsync(gains.Add, (delay, _) =>
        {
            duration += delay;
            return Task.CompletedTask;
        }, default);
        Assert.Equal(TimeSpan.FromMilliseconds(30), duration);
        Assert.Equal(5, gains.Count);
        Assert.Equal(0, gains[^1]);
        Assert.All(gains, gain => Assert.InRange(gain, 0, 1));
        Assert.True(gains.Zip(gains.Skip(1)).All(pair => pair.First > pair.Second));
    }

    [Fact]
    public async Task CancellationDuringDelayNeverAppliesLateGain()
    {
        using var cancellation = new CancellationTokenSource();
        var gains = new List<float>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AudioFade.FadeOutAsync(gains.Add,
            (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Empty(gains);
    }

    [Fact]
    public async Task FadeFailureIsObservable()
    {
        await Assert.ThrowsAsync<IOException>(() => AudioFade.FadeOutAsync(_ => throw new IOException(),
            (_, _) => Task.CompletedTask, default));
    }

    [Fact]
    public async Task PauseCapturesCursorOnlyAfterFadeCompletes()
    {
        var session = new FadingSession();
        using var output = new SingleVoiceAudioOutput(new Factory(session));
        await output.PlayAsync(Request(), default);
        var pause = output.PauseAsync(default);
        Assert.False(pause.IsCompleted);
        Assert.Equal(0, session.Pauses);
        Assert.Equal(PlaybackState.BasePlaying, output.State);
        session.Position = 720;
        session.Fade.SetResult();
        Assert.True((await pause).IsSuccess);
        Assert.Equal(1, session.Pauses);
        Assert.Equal(new AudioCursor(720, 24000), output.CurrentCursor);
        Assert.Equal(PlaybackState.Paused, output.State);
    }

    [Fact]
    public async Task EmergencyStopDoesNotWaitForFadeAndLateFadeCannotPause()
    {
        var session = new FadingSession();
        using var output = new SingleVoiceAudioOutput(new Factory(session));
        await output.PlayAsync(Request(), default);
        var pause = output.PauseAsync(default);
        Assert.True((await output.StopAsync(default)).IsSuccess);
        Assert.False(pause.IsCompleted);
        Assert.True(session.Disposed);
        session.Fade.SetResult();
        Assert.Equal(OperationStatus.Cancelled, (await pause).Status);
        Assert.Equal(0, session.Pauses);
        Assert.Equal(PlaybackState.Stopped, output.State);
    }

    [Fact]
    public async Task ReplacementCannotBePausedByOldFade()
    {
        var first = new FadingSession();
        var second = new FadingSession();
        using var output = new SingleVoiceAudioOutput(new Factory(first, second));
        await output.PlayAsync(Request(), default);
        var pause = output.PauseAsync(default);
        Assert.True((await output.PlayAsync(Request(), default)).IsSuccess);
        first.Fade.SetResult();
        Assert.Equal(OperationStatus.Cancelled, (await pause).Status);
        Assert.Equal(0, first.Pauses);
        Assert.Equal(0, second.Pauses);
        Assert.Equal(PlaybackState.BasePlaying, output.State);
    }

    [Fact]
    public async Task FailedFadeDoesNotClaimPausedSuccess()
    {
        var session = new FadingSession();
        using var output = new SingleVoiceAudioOutput(new Factory(session));
        await output.PlayAsync(Request(), default);
        var pause = output.PauseAsync(default);
        session.Fade.SetException(new IOException());
        Assert.Equal(OperationStatus.Failed, (await pause).Status);
        Assert.Equal(0, session.Pauses);
        Assert.Equal(PlaybackState.Error, output.State);
        Assert.True((await output.StopAsync(default)).IsSuccess);
    }

    [Fact]
    public async Task CancelledFadeLeavesOutputPlayingAndCanBeStopped()
    {
        var session = new FadingSession();
        using var output = new SingleVoiceAudioOutput(new Factory(session));
        await output.PlayAsync(Request(), default);
        var pause = output.PauseAsync(default);
        session.Fade.SetCanceled();
        Assert.Equal(OperationStatus.Cancelled, (await pause).Status);
        Assert.Equal(0, session.Pauses);
        Assert.Equal(PlaybackState.BasePlaying, output.State);
        Assert.True((await output.StopAsync(default)).IsSuccess);
    }

    [Fact]
    public async Task NewPreparationInvalidatesFadeWithoutLeavingOldVoiceMuted()
    {
        var session = new FadingSession();
        var next = new TaskCompletionSource<IAudioPlaybackSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new SingleVoiceAudioOutput(new DelayedFactory(session, next.Task));
        await output.PlayAsync(Request(), default);
        var pause = output.PauseAsync(default);
        var replacement = output.PlayAsync(Request(), default);
        session.Fade.SetResult();
        Assert.Equal(OperationStatus.Cancelled, (await pause).Status);
        Assert.True(session.VolumeRestored);
        Assert.Equal(0, session.Pauses);
        next.SetException(new IOException());
        Assert.Equal(OperationStatus.Failed, (await replacement).Status);
        Assert.Equal(PlaybackState.BasePlaying, output.State);
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task NonCooperatingFadeCannotCommitPauseAfterCallerCancellation()
    {
        var session = new FadingSession();
        using var output = new SingleVoiceAudioOutput(new Factory(session));
        using var cancellation = new CancellationTokenSource();
        await output.PlayAsync(Request(), default);
        var pause = output.PauseAsync(cancellation.Token);
        cancellation.Cancel();
        session.Fade.SetResult();
        Assert.Equal(OperationStatus.Cancelled, (await pause).Status);
        Assert.True(session.VolumeRestored);
        Assert.Equal(0, session.Pauses);
        Assert.Equal(PlaybackState.BasePlaying, output.State);
    }

    private sealed class DelayedFactory(IAudioPlaybackSession first, Task<IAudioPlaybackSession> second) : IAudioSessionFactory
    {
        private int calls;
        public Task<IAudioPlaybackSession> PrepareAsync(AudioPlaybackRequest request, CancellationToken token) =>
            ++calls == 1 ? Task.FromResult(first) : second;
    }
    private static AudioPlaybackRequest Request() => new(
        new LocalAudioAsset("synthetic.wav", "wav", "test", TimeSpan.FromSeconds(1), 24000), AudioCursor.Start);

    private sealed class Factory(params FadingSession[] sessions) : IAudioSessionFactory
    {
        private int next;
        public Task<IAudioPlaybackSession> PrepareAsync(AudioPlaybackRequest request, CancellationToken token) =>
            Task.FromResult<IAudioPlaybackSession>(sessions[next++]);
    }

    private sealed class FadingSession : IAudioPlaybackSession, IFadingAudioPlaybackSession
    {
        public TaskCompletionSource Fade { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Position { get; set; }
        public int Pauses { get; private set; }
        public bool VolumeRestored { get; private set; }
        public bool Disposed { get; private set; }
        public int SampleRate => 24000;
        public long LengthSamples => 24000;
        public long PositionSamples => Position;
        public event EventHandler<AudioSessionStoppedEventArgs>? Stopped { add { } remove { } }
        public Task FadeOutAsync(CancellationToken token) => Fade.Task;
        public void RestoreVolume() => VolumeRestored = true;
        public void Play() { }
        public void Pause() => Pauses++;
        public void Resume() { }
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
}
