using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Media;
using TikTokAudio.Application.Playback;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Audio;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class AudioSchedulerIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopInvalidatesActualOutputPreparationAndLateSessionCannotReplaceRestart(bool restart)
    {
        var factory = new Factory();
        using var output = new SingleVoiceAudioOutput(factory);
        var scheduler = new AudioPlaybackScheduler(Planner(), output, new Clock());
        Assert.True((await scheduler.StartAsync("room")).Result.IsSuccess);
        Assert.True((await scheduler.QueueInteractionAsync(new("interaction", "room", InteractionPriority.Follow,
            new(0), TimeSpan.FromSeconds(60), _ => Task.FromResult(new OperationResult<LocalAudioAsset>(
                OperationStatus.Succeeded, Asset("interaction")))))).Result.IsSuccess);
        Task<SchedulerUpdate>? pumping = null;
        for (var attempt = 0; attempt < 10000 && !factory.Entered.Task.IsCompleted; attempt++)
        {
            pumping = scheduler.PumpAsync();
            if (!pumping.IsCompleted) break;
            await pumping;
            await Task.Yield();
        }
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await scheduler.StopAsync()).Result.IsSuccess);
        Assert.Equal(PlaybackState.Stopped, output.State);
        if (pumping is not null) await pumping.WaitAsync(TimeSpan.FromSeconds(10));
        if (restart) Assert.True((await scheduler.StartAsync("new-room")).Result.IsSuccess);
        var late = new Session();
        factory.Late.SetResult(late);
        await late.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, late.Plays);
        Assert.Equal(restart ? PlaybackState.BasePlaying : PlaybackState.Stopped, output.State);
        Assert.Equal(restart ? PlaybackState.BasePlaying : PlaybackState.Stopped, scheduler.State);
        if (restart) Assert.Equal(1, factory.Restart.Plays);
        await scheduler.StopAsync();
    }

    private static LocalAudioAsset Asset(string path) => new(path, "wav", "test", TimeSpan.FromSeconds(1), 24000);
    private static PreRecordedPlaybackPlanner Planner() => new(new("memory",
        [new ImportedAudioProduct(1, "1", "product", [new ImportedAudioGroup(1, "1/1",
            [new ImportedAudioClip("1/1/a", Asset("base"), null)])], true)], []), PlanRevision.Initial, new RandomSource());

    private sealed class RandomSource : IRandomSource
    {
        public int NextInt32(int minInclusive, int maxExclusive) => minInclusive;
        public double NextDouble() => 0.5;
    }
    private sealed class Clock : IClock
    {
        public MonotonicTimestamp Now => new(0);
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class Factory : IAudioSessionFactory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IAudioPlaybackSession> Late { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Session Restart { get; } = new();
        private int bases;
        public Task<IAudioPlaybackSession> PrepareAsync(AudioPlaybackRequest request, CancellationToken token)
        {
            if (request.Asset.Path == "interaction") { Entered.TrySetResult(); return Late.Task; }
            return Task.FromResult<IAudioPlaybackSession>(++bases == 1 ? new Session() : Restart);
        }
    }
    private sealed class Session : IAudioPlaybackSession
    {
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Plays { get; private set; }
        public int SampleRate => 24000;
        public long LengthSamples => 24000;
        public long PositionSamples => 0;
        public event EventHandler<AudioSessionStoppedEventArgs>? Stopped { add { } remove { } }
        public void Play() => Plays++;
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public void Dispose() => Released.TrySetResult();
    }
}
