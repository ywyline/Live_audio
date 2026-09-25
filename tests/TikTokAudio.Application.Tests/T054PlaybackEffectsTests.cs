using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Media;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Domain;
using Xunit;

namespace TikTokAudio.Application.Tests;

public sealed class T054PlaybackEffectsTests
{
    private static PlaybackPlanItem Item(long seed = 42, BasePlaybackMode mode = BasePlaybackMode.PreRecorded) =>
        new(new LocalAudioAsset("test.wav", "wav", "prerecorded", TimeSpan.FromSeconds(1), 24000),
            mode, PlanRevision.Initial, "1", "1/1", "clip", 0, seed, []);

    [Fact]
    public void EffectsAreOffByDefaultAndNeverApplyToTts()
    {
        Assert.Equal(PlaybackEffectSelection.Bypass, PlaybackEffects.Select(Item(), new PlaybackEffectOptions()));
        Assert.Equal(PlaybackEffectSelection.Bypass,
            PlaybackEffects.Select(Item(mode: BasePlaybackMode.TtsScript), new PlaybackEffectOptions { Enabled = true }));
    }

    [Fact]
    public void EnabledButNeutralConfigurationStillUsesTheBypassPath()
    {
        var options = new PlaybackEffectOptions
        {
            Enabled = true,
            Speed = new EffectRange(1, 1),
            PitchSemitones = new EffectRange(0, 0),
            StereoBalance = new EffectRange(0, 0),
            EqDecibels = new EffectRange(0, 0)
        };

        Assert.Equal(PlaybackEffectSelection.Bypass, PlaybackEffects.Select(Item(), options));
    }

    [Fact]
    public void CheckpointedSeedSelectsSameBoundedEffectOnEveryResume()
    {
        var settings = new PlaybackEffectOptions { Enabled = true, EnvironmentEnabled = true };
        var first = PlaybackEffects.Select(Item(), settings);
        Assert.Equal(first, PlaybackEffects.Select(Item(), settings));
        Assert.NotEqual(first, PlaybackEffects.Select(Item(43), settings));
        Assert.InRange(first.Speed, 0.97, 1.03);
        Assert.InRange(first.PitchSemitones, -0.3, 0.3);
        Assert.InRange(first.StereoBalance, -0.1, 0.1);
        Assert.InRange(first.EqDecibels, -1, 1);
        Assert.True(first.EnvironmentEnabled);
        Assert.Equal(42, first.EffectSeed);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(1.01, double.PositiveInfinity)]
    [InlineData(1.1, 1)]
    [InlineData(0.1, 1)]
    public void InvalidSpeedIsRejectedEvenWhenBypassed(double min, double max)
    {
        Assert.ThrowsAny<ArgumentException>(() => PlaybackEffects.Select(Item(),
            new PlaybackEffectOptions { Speed = new EffectRange(min, max) }));
    }

    [Fact]
    public void UnboundedOrTooLoudParametersAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackEffectOptions
        { EnvironmentRelativeDecibels = -20 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackEffectOptions
        { StereoBalance = new EffectRange(-2, 0) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackEffectOptions
        { EqDecibels = new EffectRange(0, 14) }.Validate());
    }

    [Fact]
    public async Task EnabledEffectsWithoutOutputCapabilityFailWithoutPlayingOrConsumingBoundary()
    {
        var output = new BasicOutput();
        var scheduler = new AudioPlaybackScheduler(Planner(), output, new FakeClock(), effectOptions: Enabled());
        var started = await scheduler.StartAsync("session");
        Assert.Equal(OperationStatus.Unsupported, started.Result.Status);
        Assert.Equal(PlaybackState.Error, scheduler.State);
        Assert.Equal(0, output.Plays);
        Assert.Empty(started.Boundaries);
    }

    [Fact]
    public async Task DisabledEffectsContinueThroughOriginalOutput()
    {
        var output = new BasicOutput();
        var scheduler = new AudioPlaybackScheduler(Planner(), output, new FakeClock());
        var started = await scheduler.StartAsync("session");
        Assert.Equal(OperationStatus.Succeeded, started.Result.Status);
        Assert.Equal(1, output.Plays);
        Assert.Single(started.Boundaries);
    }

    [Fact]
    public async Task InteractionRestoresSelectedEffectsAndSourceCursor()
    {
        var output = new EffectOutput();
        var scheduler = new AudioPlaybackScheduler(Planner(), output, new FakeClock(), effectOptions: Enabled());
        var started = await scheduler.StartAsync("session");
        Assert.Equal(OperationStatus.Succeeded, started.Result.Status);
        Assert.Single(started.Boundaries);
        var first = Assert.Single(output.Effects);
        output.Cursor = new AudioCursor(1700, 24000);
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new InteractionRequest("test", "session", InteractionPriority.Welcome,
            new MonotonicTimestamp(0), TimeSpan.FromMinutes(1), _ =>
            {
                preparationEntered.TrySetResult();
                return Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded,
                    new LocalAudioAsset("interaction.wav", "wav", "test", TimeSpan.FromSeconds(1), 24000)));
            });
        await scheduler.QueueInteractionAsync(request);
        // A yield count cannot prove that the scheduler's background preparation has run.
        // This timeout only guards a stalled test; no business-clock time is advanced.
        using var readinessTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await preparationEntered.Task.WaitAsync(readinessTimeout.Token);
        while (scheduler.State != PlaybackState.InterruptPlaying)
        {
            readinessTimeout.Token.ThrowIfCancellationRequested();
            await scheduler.PumpAsync(readinessTimeout.Token);
            if (scheduler.State != PlaybackState.InterruptPlaying)
                await Task.Delay(1, readinessTimeout.Token);
        }
        Assert.Equal(PlaybackState.InterruptPlaying, scheduler.State);
        Assert.Equal(new AudioCursor(1700, 24000), scheduler.BaseCursor);
        output.FinishInteraction();
        var resumed = await scheduler.PumpAsync();
        Assert.Equal(OperationStatus.Succeeded, resumed.Result.Status);
        Assert.Empty(resumed.Boundaries);
        Assert.Equal(2, output.Effects.Count);
        Assert.Equal(first.Selection, output.Effects[1].Selection);
        Assert.Equal(new AudioCursor(1700, 24000), output.Effects[1].Request.StartAt);
    }

    private static PlaybackEffectOptions Enabled() => new()
    {
        Enabled = true, Speed = new EffectRange(1, 1), PitchSemitones = new EffectRange(0, 0),
        StereoBalance = new EffectRange(0.2, 0.2), EqDecibels = new EffectRange(0, 0)
    };

    private static PreRecordedPlaybackPlanner Planner() => new(new ProductDirectoryImport("memory-root",
        [new ImportedAudioProduct(1, "1", "product-1",
            [new ImportedAudioGroup(1, "1/1", [new ImportedAudioClip("clip",
                new LocalAudioAsset("memory-root/clip.wav", "wav", "pre", TimeSpan.FromSeconds(1), 24000), null)])], true)], []),
        PlanRevision.Initial, new SeededRandomSource(3));

    private sealed class EffectOutput : IEffectAudioOutput
    {
        public List<(AudioPlaybackRequest Request, PlaybackEffectSelection Selection)> Effects { get; } = [];
        public PlaybackState State { get; private set; } = PlaybackState.Idle;
        public AudioCursor Cursor { get; set; } = new(0, 24000);
        public AudioCursor? CurrentCursor => Cursor;
        public Task<OperationResult> PlayWithEffectsAsync(AudioPlaybackRequest request,
            PlaybackEffectSelection selection, CancellationToken token)
        {
            Effects.Add((request, selection));
            return PlayAsync(request, token);
        }
        public Task<OperationResult> PlayAsync(AudioPlaybackRequest request, CancellationToken token)
        {
            Cursor = request.StartAt;
            State = PlaybackState.BasePlaying;
            return Task.FromResult(OperationResult.Succeeded());
        }
        public Task<OperationResult> PauseAsync(CancellationToken token)
        {
            State = PlaybackState.Paused;
            return Task.FromResult(OperationResult.Succeeded());
        }
        public Task<OperationResult> ResumeAsync(CancellationToken token) =>
            Task.FromResult(OperationResult.Succeeded());
        public Task<OperationResult> StopAsync(CancellationToken token)
        {
            State = PlaybackState.Stopped;
            return Task.FromResult(OperationResult.Succeeded());
        }
        public void FinishInteraction() => State = PlaybackState.Idle;
        public Task<OperationResult> SetEnvironmentMixAsync(EnvironmentMixSettings settings, CancellationToken token) =>
            Task.FromResult(OperationResult.Unsupported());
    }

    private sealed class BasicOutput : IAudioOutput
    {
        public int Plays { get; private set; }
        public PlaybackState State { get; private set; } = PlaybackState.Idle;
        public AudioCursor? CurrentCursor { get; private set; }
        public Task<OperationResult> PlayAsync(AudioPlaybackRequest request, CancellationToken token)
        {
            Plays++;
            State = PlaybackState.BasePlaying;
            CurrentCursor = request.StartAt;
            return Task.FromResult(OperationResult.Succeeded());
        }
        public Task<OperationResult> PauseAsync(CancellationToken token) => Task.FromResult(OperationResult.Unsupported());
        public Task<OperationResult> ResumeAsync(CancellationToken token) => Task.FromResult(OperationResult.Unsupported());
        public Task<OperationResult> StopAsync(CancellationToken token)
        {
            State = PlaybackState.Stopped;
            return Task.FromResult(OperationResult.Succeeded());
        }
        public Task<OperationResult> SetEnvironmentMixAsync(EnvironmentMixSettings settings, CancellationToken token) =>
            Task.FromResult(OperationResult.Unsupported());
    }
}
