using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Audio;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class AudioOutputTests
{
    private static AudioPlaybackRequest Request(long frame = 0) =>
        new(new("synthetic.wav", "wav", "test", null, 24000), new(frame, 24000));
    private static TaskCompletionSource<IAudioPlaybackSession> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task PauseResumePreservesRenderedSourceFrameAndStopKeepsCheckpoint()
    {
        var session = new Session { Position = 734 };
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => Task.FromResult<IAudioPlaybackSession>(session)));
        Assert.Equal(OperationStatus.Succeeded, (await output.PlayAsync(Request(734), default)).Status);
        session.Position = 2143;
        Assert.Equal(OperationStatus.Succeeded, (await output.PauseAsync(default)).Status);
        Assert.Equal(PlaybackState.Paused, output.State);
        Assert.Equal(new AudioCursor(2143, 24000), output.CurrentCursor);
        Assert.Equal(OperationStatus.Succeeded, (await output.ResumeAsync(default)).Status);
        Assert.Equal(PlaybackState.BasePlaying, output.State);
        Assert.Equal(2143, output.CurrentCursor!.Value.SourceSampleOffset);
        await output.StopAsync(default);
        Assert.Equal(PlaybackState.Stopped, output.State);
        Assert.Equal(2143, output.CurrentCursor!.Value.SourceSampleOffset);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task PreparationFailureDoesNotInterruptCurrentAudio()
    {
        var session = new Session();
        var calls = 0;
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => ++calls == 1
            ? Task.FromResult<IAudioPlaybackSession>(session) : throw new InvalidDataException()));
        await output.PlayAsync(Request(), default);
        Assert.Equal(OperationStatus.Failed, (await output.PlayAsync(Request(), default)).Status);
        Assert.True(session.Playing);
        Assert.Equal(PlaybackState.BasePlaying, output.State);
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task PreviousVoiceIsStoppedBeforeReplacementStarts()
    {
        var first = new Session();
        var second = new Session { OnPlay = () => Assert.False(first.Playing) };
        var count = 0;
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => Task.FromResult<IAudioPlaybackSession>(++count == 1 ? first : second)));
        await output.PlayAsync(Request(), default);
        var staleCallback = first.CaptureCallback();
        await output.PlayAsync(Request(), default);
        staleCallback?.Invoke(first, new(new IOException()));
        Assert.Equal(PlaybackState.BasePlaying, output.State);
        Assert.True(first.Disposed);
        Assert.True(second.Playing);
    }

    [Fact]
    public async Task StopInvalidatesLatePreparationEvenWhenFactoryIgnoresCancellation()
    {
        var pending = Pending();
        CancellationToken observed = default;
        using var output = new SingleVoiceAudioOutput(new Factory((_, token) => { observed = token; return pending.Task; }));
        var play = output.PlayAsync(Request(), default);
        Assert.Equal(OperationStatus.Succeeded, (await output.StopAsync(default)).Status);
        Assert.True(observed.IsCancellationRequested);
        var late = new Session();
        pending.SetResult(late);
        Assert.Equal(OperationStatus.Cancelled, (await play).Status);
        Assert.True(late.Disposed);
        Assert.Equal(0, late.PlayCount);
        Assert.Equal(PlaybackState.Stopped, output.State);
    }

    [Fact]
    public async Task NewestPreparationWinsRegardlessOfCompletionOrder()
    {
        var first = Pending();
        var count = 0;
        var current = new Session();
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => ++count == 1 ? first.Task : Task.FromResult<IAudioPlaybackSession>(current)));
        var old = output.PlayAsync(Request(), default);
        await output.PlayAsync(Request(), default);
        var late = new Session();
        first.SetResult(late);
        Assert.Equal(OperationStatus.Cancelled, (await old).Status);
        Assert.True(late.Disposed);
        Assert.True(current.Playing);
    }

    [Fact]
    public async Task CancelAfterStartStopsOnlyThatSession()
    {
        using var token = new CancellationTokenSource();
        var first = new Session();
        var second = new Session();
        var count = 0;
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => Task.FromResult<IAudioPlaybackSession>(++count == 1 ? first : second)));
        await output.PlayAsync(Request(), token.Token);
        await output.PlayAsync(Request(), default);
        token.Cancel();
        Assert.True(second.Playing);
        Assert.Equal(PlaybackState.BasePlaying, output.State);
    }

    [Fact]
    public async Task CancelCurrentPlaybackStopsIt()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session();
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => Task.FromResult<IAudioPlaybackSession>(session)));
        await output.PlayAsync(Request(), cancellation.Token);
        cancellation.Cancel();
        Assert.False(session.Playing);
        Assert.True(session.Disposed);
        Assert.Equal(PlaybackState.Stopped, output.State);
    }

    [Theory]
    [InlineData(false, PlaybackState.Idle)]
    [InlineData(true, PlaybackState.Error)]
    public async Task CompletionAndDeviceFailureAreExplicit(bool failure, PlaybackState expected)
    {
        var session = new Session();
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => Task.FromResult<IAudioPlaybackSession>(session)));
        await output.PlayAsync(Request(), default);
        session.Complete(failure ? new IOException() : null);
        Assert.Equal(expected, output.State);
        Assert.Equal(failure ? 0 : session.LengthSamples, output.CurrentCursor!.Value.SourceSampleOffset);
    }

    [Fact]
    public async Task FailedDeviceStartReleasesSessionAndReportsError()
    {
        var session = new Session { OnPlay = () => throw new InvalidOperationException() };
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => Task.FromResult<IAudioPlaybackSession>(session)));
        Assert.Equal(OperationStatus.Failed, (await output.PlayAsync(Request(), default)).Status);
        Assert.True(session.Disposed);
        Assert.Equal(PlaybackState.Error, output.State);
    }

    [Fact]
    public async Task CancellationDuringPreparationPreservesPreviousVoice()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new Session();
        var late = new Session();
        var pending = Pending();
        var count = 0;
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => ++count == 1 ? Task.FromResult<IAudioPlaybackSession>(first) : pending.Task));
        await output.PlayAsync(Request(), default);
        var result = output.PlayAsync(Request(), cancellation.Token);
        cancellation.Cancel();
        pending.SetResult(late);
        Assert.Equal(OperationStatus.Cancelled, (await result).Status);
        Assert.True(first.Playing);
        Assert.True(late.Disposed);
    }

    [Fact]
    public async Task PreCancelledOperationsHaveNoSideEffects()
    {
        var calls = 0;
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => { calls++; return Task.FromResult<IAudioPlaybackSession>(new Session()); }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(OperationStatus.Cancelled, (await output.PlayAsync(Request(), cancelled.Token)).Status);
        Assert.Equal(OperationStatus.Cancelled, (await output.StopAsync(cancelled.Token)).Status);
        Assert.Equal(OperationStatus.Cancelled, (await output.PauseAsync(cancelled.Token)).Status);
        Assert.Equal(0, calls);
        Assert.Equal(PlaybackState.Idle, output.State);
    }

    [Fact]
    public async Task DisposeInvalidatesPreparationAndCannotRestart()
    {
        var pending = Pending();
        var output = new SingleVoiceAudioOutput(new Factory((_, _) => pending.Task));
        var play = output.PlayAsync(Request(), default);
        output.Dispose();
        var late = new Session();
        pending.SetResult(late);
        Assert.Equal(OperationStatus.Cancelled, (await play).Status);
        Assert.True(late.Disposed);
        Assert.Equal(OperationStatus.Failed, (await output.PlayAsync(Request(), default)).Status);
        output.Dispose();
    }

    [Fact]
    public async Task PauseResumeWithoutAudioFailsAndEnvironmentMixIsExplicitlyUnsupported()
    {
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => throw new InvalidOperationException()));
        Assert.Equal(OperationStatus.Failed, (await output.PauseAsync(default)).Status);
        Assert.Equal(OperationStatus.Failed, (await output.ResumeAsync(default)).Status);
        Assert.Equal(OperationStatus.Unsupported, (await output.SetEnvironmentMixAsync(new(true, -20), default)).Status);
        Assert.Equal(OperationStatus.Succeeded, (await output.SetEnvironmentMixAsync(new(false, -20), default)).Status);
    }

    [Fact]
    public async Task UnavailableDevicePositionDoesNotPreventStopOrEscapeCompletionCallback()
    {
        var session = new Session();
        using var output = new SingleVoiceAudioOutput(new Factory((_, _) => Task.FromResult<IAudioPlaybackSession>(session)));
        await output.PlayAsync(Request(), default);
        session.PositionError = true;
        session.Complete(new IOException());
        Assert.Equal(PlaybackState.Error, output.State);
        var stopped = await output.StopAsync(default);
        Assert.Equal(OperationStatus.Succeeded, stopped.Status);
        Assert.NotNull(stopped.Detail);
        Assert.True(session.Disposed);
        Assert.Equal(PlaybackState.Stopped, output.State);
    }

    [Fact]
    public async Task StopFailureDoesNotStartASecondVoiceAndDisposeStillCancelsPreparation()
    {
        var current = new Session();
        var replacement = new Session();
        var pending = Pending();
        CancellationToken preparing = default;
        var count = 0;
        var output = new SingleVoiceAudioOutput(new Factory((_, token) =>
        {
            if (++count == 1) return Task.FromResult<IAudioPlaybackSession>(current);
            if (count == 2) return Task.FromResult<IAudioPlaybackSession>(replacement);
            preparing = token;
            return pending.Task;
        }));
        await output.PlayAsync(Request(), default);
        current.StopError = true;
        Assert.Equal(OperationStatus.Failed, (await output.PlayAsync(Request(), default)).Status);
        Assert.Equal(0, replacement.PlayCount);
        var waiting = output.PlayAsync(Request(), default);
        Assert.Throws<IOException>(() => output.Dispose());
        Assert.True(current.Disposed);
        Assert.True(preparing.IsCancellationRequested);
        var late = new Session();
        pending.SetResult(late);
        Assert.Equal(OperationStatus.Cancelled, (await waiting).Status);
        Assert.True(late.Disposed);
        output.Dispose();
    }

    private sealed class Factory(Func<AudioPlaybackRequest, CancellationToken, Task<IAudioPlaybackSession>> prepare) : IAudioSessionFactory
    {
        public Task<IAudioPlaybackSession> PrepareAsync(AudioPlaybackRequest request, CancellationToken cancellationToken) => prepare(request, cancellationToken);
    }

    private sealed class Session : IAudioPlaybackSession
    {
        public int SampleRate => 24000;
        public long LengthSamples => 24000;
        public long PositionSamples => PositionError ? throw new IOException() : Position;
        public bool PositionError { get; set; }
        public bool StopError { get; set; }
        public long Position { get; set; }
        public bool Playing { get; private set; }
        public bool Disposed { get; private set; }
        public int PlayCount { get; private set; }
        public Action? OnPlay { get; init; }
        public event EventHandler<AudioSessionStoppedEventArgs>? Stopped;
        public void Play() { PlayCount++; OnPlay?.Invoke(); Playing = true; }
        public void Pause() => Playing = false;
        public void Resume() => Playing = true;
        public void Stop() { if (StopError) throw new IOException(); Playing = false; }
        public void Dispose() { Disposed = true; Playing = false; }
        public EventHandler<AudioSessionStoppedEventArgs>? CaptureCallback() => Stopped;
        public void Complete(Exception? error) { Playing = false; if (error is null) Position = LengthSamples; Stopped?.Invoke(this, new(error)); }
    }
}
