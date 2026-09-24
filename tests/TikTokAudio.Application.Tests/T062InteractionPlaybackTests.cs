using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Events;
using TikTokAudio.Application.Interactions;
using TikTokAudio.Application.Media;
using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class T062InteractionPlaybackTests
{
    [Fact]
    public async Task SlowSynthesisDoesNotPauseBaseAndCompletesReservationOnlyAfterPlayback()
    {
        var provider = new TestProvider();
        var gate = new TaskCompletionSource<TtsSynthesisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Handler = (_, token) => gate.Task.WaitAsync(token);
        var h = Harness.Create(provider);
        await h.Scheduler.StartAsync(h.SessionText);
        var candidate = h.Enqueue(InteractionKind.Follow, "slow-user");

        Assert.Equal(OperationStatus.Succeeded, (await h.Coordinator.QueueNextAsync(h.SessionText)).Status);
        await provider.Entered.Task;
        await h.Coordinator.PumpAsync();
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Equal(0, h.Events.CompleteCount);

        gate.SetResult(provider.Success("slow"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Equal(0, h.Events.CompleteCount);
        Assert.Equal(0, h.Rules.Options.FollowCooldown.Ticks == 0 ? 0 : h.Events.CompleteCount);

        h.Output.Complete();
        var update = await h.Until(() => h.Events.CompleteCount == 1);
        Assert.Contains(update.Settlements, item => item.RequestId == candidate.CandidateId &&
            item.PlaybackStatus == OperationStatus.Succeeded && item.ReservationStatus == OperationStatus.Succeeded);
        Assert.Equal(1, h.Events.CompleteCount);
        Assert.Equal(0, h.Events.CancelCount);
    }

    [Fact]
    public async Task FailedSynthesisReleasesReservationAndNextFollowCanPlay()
    {
        var provider = new TestProvider();
        var calls = 0;
        provider.Handler = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return Task.FromResult(calls == 1
                ? new TtsSynthesisOutcome(OperationStatus.Failed, null, "synthetic failure")
                : provider.Success("second"));
        };
        var h = Harness.Create(provider);
        await h.Scheduler.StartAsync(h.SessionText);
        var first = h.Enqueue(InteractionKind.Follow, "first");
        Assert.Equal(OperationStatus.Succeeded, (await h.Coordinator.QueueNextAsync(h.SessionText)).Status);
        var failed = await h.Until(() => h.LastSettlement is { Settlements: var items } && items.Any(item => item.RequestId == first.CandidateId));
        Assert.Equal(OperationStatus.Failed, failed.Settlements.Single().PlaybackStatus);
        Assert.Equal(0, h.Events.CompleteCount);
        Assert.Equal(1, h.Events.CancelCount);

        h.Enqueue(InteractionKind.Follow, "second");
        Assert.Equal(OperationStatus.Succeeded, (await h.Coordinator.QueueNextAsync(h.SessionText)).Status);
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        h.Output.Complete();
        await h.Until(() => h.Events.CompleteCount == 1);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MismatchedEngineRevisionIsCancelledWithoutPlaybackOrCompletion()
    {
        var provider = new TestProvider
        {
            Handler = (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                var result = new TtsSynthesisResult(new LocalAudioAsset("stale.wav", "wav", "memory-engine", null, 24000), "memory-engine", new EngineRevision(99));
                return Task.FromResult(new TtsSynthesisOutcome(OperationStatus.Succeeded, result, null));
            }
        };
        var h = Harness.Create(provider);
        await h.Scheduler.StartAsync(h.SessionText);
        var candidate = h.Enqueue(InteractionKind.Keyword, "stale-user", "关键词");
        Assert.Equal(OperationStatus.Succeeded, (await h.Coordinator.QueueNextAsync(h.SessionText)).Status);
        await h.Until(() => h.Events.CancelCount == 1);
        Assert.DoesNotContain(h.Output.Plays, item => item.Asset.Path == "stale.wav");
        Assert.Equal(0, h.Events.CompleteCount);
        Assert.Equal(1, h.Events.CancelCount);
    }

    [Fact]
    public async Task NewWelcomeReplacesSlowOlderCandidateAndOnlyLatestIsPlayed()
    {
        var provider = new TestProvider();
        var firstGate = new TaskCompletionSource<TtsSynthesisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Handler = (_, token) => firstGate.Task.WaitAsync(token);
        var h = Harness.Create(provider);
        await h.Scheduler.StartAsync(h.SessionText);
        var first = h.Enqueue(InteractionKind.Welcome, "old-user", occurredAt: h.Clock.UtcNow);
        Assert.Equal(OperationStatus.Succeeded, (await h.Coordinator.QueueNextAsync(h.SessionText)).Status);
        await provider.Entered.Task;
        h.Enqueue(InteractionKind.Welcome, "new-user", occurredAt: h.Clock.UtcNow.AddSeconds(1));

        await h.Coordinator.PumpAsync();
        firstGate.TrySetResult(provider.Success("old"));
        await h.Until(() => h.Events.CancelCount == 1);
        Assert.Equal(1, h.Events.CancelCount);

        provider.Handler = (_, token) => Task.FromResult(provider.Success("new"));
        Assert.Equal(OperationStatus.Succeeded, (await h.Coordinator.QueueNextAsync(h.SessionText)).Status);
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Contains(h.Output.Plays, item => item.Asset.Path == "new.wav");
    }

    [Fact]
    public async Task DuplicateQueueDoesNotReleaseTheAlreadyQueuedCandidate()
    {
        var provider = new TestProvider();
        var gate = new TaskCompletionSource<TtsSynthesisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Handler = (_, token) => gate.Task.WaitAsync(token);
        var h = Harness.Create(provider);
        await h.Scheduler.StartAsync(h.SessionText);
        var candidate = h.Enqueue(InteractionKind.Follow, "duplicate-user");

        Assert.Equal(OperationStatus.Succeeded, (await h.Coordinator.QueueNextAsync(h.SessionText)).Status);
        await provider.Entered.Task;
        var duplicate = await h.Coordinator.QueueCandidateAsync(candidate, h.SessionText);

        Assert.Equal(OperationStatus.Failed, duplicate.Status);
        Assert.Equal(0, h.Events.CancelCount);
        gate.SetResult(provider.Success("duplicate"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        h.Output.Complete();
        await h.Until(() => h.Events.CompleteCount == 1);
    }

    [Fact]
    public async Task StopCancelsReservationOnceEvenWhenSchedulerReportsTerminalOutcome()
    {
        var provider = new TestProvider();
        var gate = new TaskCompletionSource<TtsSynthesisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Handler = (_, token) => gate.Task.WaitAsync(token);
        var h = Harness.Create(provider);
        await h.Scheduler.StartAsync(h.SessionText);
        h.Enqueue(InteractionKind.Follow, "stop-user");
        Assert.Equal(OperationStatus.Succeeded, (await h.Coordinator.QueueNextAsync(h.SessionText)).Status);
        await provider.Entered.Task;

        var stopped = await h.Coordinator.StopAsync();
        Assert.Equal(1, h.Events.CancelCount);
        Assert.DoesNotContain(stopped.Settlements, item => item.ReservationStatus == OperationStatus.Succeeded);

        await h.Coordinator.PumpAsync();
        Assert.Equal(1, h.Events.CancelCount);
    }

    [Fact]
    public async Task PlaybackFailureCancelsReservationWithoutMarkingVoiceCompleted()
    {
        var provider = new TestProvider();
        provider.Handler = (_, token) => Task.FromResult(provider.Success("failure"));
        var h = Harness.Create(provider);
        h.Output.FailPlayback = true;
        await h.Scheduler.StartAsync(h.SessionText);
        var candidate = h.Enqueue(InteractionKind.Follow, "failed-playback");
        Assert.Equal(OperationStatus.Succeeded, (await h.Coordinator.QueueNextAsync(h.SessionText)).Status);

        var settled = await h.Until(() => h.LastSettlement is { Settlements: var items } &&
            items.Any(item => item.RequestId == candidate.CandidateId));
        var item = settled.Settlements.Single(item => item.RequestId == candidate.CandidateId);
        Assert.Equal(OperationStatus.Failed, item.PlaybackStatus);
        Assert.Equal(OperationStatus.Cancelled, item.ReservationStatus);
        Assert.Equal(0, h.Events.CompleteCount);
        Assert.Equal(1, h.Events.CancelCount);
    }

    [Fact]
    public async Task CallerCancellationStillReleasesReservation()
    {
        var provider = new TestProvider();
        var h = Harness.Create(provider);
        await h.Scheduler.StartAsync(h.SessionText);
        h.Enqueue(InteractionKind.Follow, "cancelled-queue");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await h.Coordinator.QueueNextAsync(h.SessionText, cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(0, h.Events.CompleteCount);
        Assert.Equal(1, h.Events.CancelCount);
    }

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public TestOutput Output { get; } = new();
        public TestEventProcessor Events { get; } = new();
        public TestCache Cache { get; } = new();
        public InteractionRuleCoordinator Rules { get; }
        public AudioPlaybackScheduler Scheduler { get; }
        public InteractionPlaybackCoordinator Coordinator { get; }
        public string SessionText { get; } = Guid.NewGuid().ToString("D");
        public InteractionPlaybackUpdate? LastSettlement { get; private set; }
        private readonly Guid session;

        private Harness(TestProvider provider)
        {
            session = Guid.Parse(SessionText);
            Rules = new InteractionRuleCoordinator(Clock, [new KeywordRule("rule", ["关键词"], textEnabled: false)]);
            var registry = new TtsEngineRegistry([new TtsEngineConfiguration("memory-engine", provider, "voice")], "memory-engine");
            Scheduler = new AudioPlaybackScheduler(new TestPlan(), Output, Clock);
            Coordinator = new InteractionPlaybackCoordinator(Rules, Scheduler, registry, Cache, Clock,
                snapshot => new TtsGenerationSettings(new(snapshot.EngineId, "1", "model", "1"), snapshot.DefaultVoiceId ?? "voice", "vi", 1, 0, 1, "none"), Events);
        }

        public static Harness Create(TestProvider provider) => new(provider);

        public InteractionCandidate Enqueue(InteractionKind kind, string userId, string? content = null, DateTimeOffset? occurredAt = null)
        {
            var type = kind switch { InteractionKind.Welcome => LiveEventType.Enter, InteractionKind.Follow => LiveEventType.Follow, _ => LiveEventType.Comment };
            var reservation = Guid.NewGuid();
            var admission = new EventProcessResult(EventProcessStatus.Reserved, $"fp-{reservation:N}", EventIdentityQuality.Complete, reservation);
            var result = Rules.Enqueue(new LiveEvent("test", session, "room", type, Guid.NewGuid().ToString("N"), userId, userId,
                occurredAt, Clock.UtcNow, content ?? string.Empty, null, null, EventIdentityQuality.Complete), admission);
            return Assert.IsType<InteractionCandidate>(result.Candidate);
        }

        public async Task<InteractionPlaybackUpdate> Until(Func<bool> predicate)
        {
            InteractionPlaybackUpdate update = new(new(OperationResult.Succeeded(), [], []), []);
            for (var i = 0; i < 1000 && !predicate(); i++)
            {
                update = await Coordinator.PumpAsync();
                LastSettlement = update;
                await Task.Yield();
            }
            Assert.True(predicate(), "Expected interaction transition did not occur.");
            return update;
        }
    }

    private sealed class TestProvider : ITtsProvider
    {
        public string EngineId => "memory-engine";
        public EngineRevision Revision => EngineRevision.Initial;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<TtsSynthesisRequest, CancellationToken, Task<TtsSynthesisOutcome>> Handler { get; set; } = null!;
        public Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return InvokeAsync(request, cancellationToken);
        }
        private async Task<TtsSynthesisOutcome> InvokeAsync(TtsSynthesisRequest request, CancellationToken token)
        {
            try { return await Handler(request, token); }
            catch (OperationCanceledException) { CancellationObserved.TrySetResult(); throw; }
        }
        public TtsSynthesisOutcome Success(string name) => new(OperationStatus.Succeeded,
            new(new LocalAudioAsset(name + ".wav", "wav", EngineId, TimeSpan.FromSeconds(1), 24000), EngineId, Revision), null);
        public Task<TtsHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new TtsHealth(TtsHealthStatus.Healthy, EngineId, null));
        public Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken cancellationToken) => Task.FromResult(new OperationResult<IReadOnlyList<TtsVoice>>(OperationStatus.Succeeded, []));
    }

    private sealed class TestCache : ITtsAudioCache
    {
        private readonly Dictionary<string, LocalAudioAsset> values = new(StringComparer.Ordinal);
        public Task<OperationResult<LocalAudioAsset>> FindAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, values.GetValueOrDefault(key)));
        public Task<OperationResult<LocalAudioAsset>> StoreAsync(string key, LocalAudioAsset generatedAsset, CancellationToken cancellationToken)
        {
            values[key] = generatedAsset;
            return Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, generatedAsset));
        }
        public int DiscardCount { get; private set; }
        public Task<OperationResult> DiscardAsync(LocalAudioAsset generatedAsset)
        {
            DiscardCount++;
            return Task.FromResult(OperationResult.Succeeded());
        }
    }

    private sealed class TestEventProcessor : ILiveEventProcessor
    {
        public int CompleteCount { get; private set; }
        public int CancelCount { get; private set; }
        public Task<OperationResult> CompleteAsync(Guid reservationId, CancellationToken cancellationToken = default) { CompleteCount++; return Task.FromResult(OperationResult.Succeeded()); }
        public Task<OperationResult> CancelAsync(Guid reservationId, CancellationToken cancellationToken = default) { CancelCount++; return Task.FromResult(OperationResult.Cancelled()); }
        public Task<EventProcessResult> ProcessAsync(LiveEvent liveEvent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> ReconnectAsync(LiveConnectionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestPlan : IBasePlaybackPlan
    {
        private readonly LocalAudioAsset baseAsset = new("base.wav", "wav", "pre", TimeSpan.FromSeconds(10), 24000);
        public BasePlaybackMode Mode => BasePlaybackMode.PreRecorded;
        public PlanRevision Revision => PlanRevision.Initial;
        public PlaybackPlanItem? CurrentItem { get; private set; }
        public AudioCursor CurrentCursor { get; private set; } = AudioCursor.Start;
        public BasePlanAvailability Availability => BasePlanAvailability.Ready;
        public string? Detail => null;
        public OperationResult<IReadOnlyList<ProductBoundary>> AcknowledgePlaybackStarted(PlaybackPlanItem item) { CurrentItem = item; return new(OperationStatus.Succeeded, []); }
        public OperationResult SaveCursor(AudioCursor sourceCursor) { CurrentCursor = sourceCursor; return OperationResult.Succeeded(); }
        public void CancelPendingPreparation() { }
        public Task<PlaybackPlanItem?> SelectNextAsync(PlaybackPlannerContext context, CancellationToken cancellationToken)
        {
            CurrentItem ??= new(baseAsset, Mode, Revision, null, "group", "clip", 0, 1, []);
            CurrentCursor = AudioCursor.Start;
            return Task.FromResult<PlaybackPlanItem?>(CurrentItem);
        }
        public Task<OperationResult> RestoreCheckpointAsync(PlaybackCheckpoint checkpoint, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Succeeded());
    }

    private sealed class TestOutput : IAudioOutput
    {
        public PlaybackState State { get; private set; } = PlaybackState.Idle;
        public AudioCursor? CurrentCursor { get; private set; } = AudioCursor.Start;
        public AudioPlaybackRequest? Current { get; private set; }
        public List<AudioPlaybackRequest> Plays { get; } = [];
        public bool FailPlayback { get; set; }
        public int PauseCount { get; private set; }
        public Task<OperationResult> PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailPlayback) return Task.FromResult(OperationResult.Failed("simulated playback failure"));
            Current = request; Plays.Add(request); State = PlaybackState.BasePlaying; CurrentCursor = request.StartAt;
            return Task.FromResult(OperationResult.Succeeded());
        }
        public Task<OperationResult> PauseAsync(CancellationToken cancellationToken) { PauseCount++; State = PlaybackState.Paused; return Task.FromResult(OperationResult.Succeeded()); }
        public Task<OperationResult> ResumeAsync(CancellationToken cancellationToken) { State = PlaybackState.BasePlaying; return Task.FromResult(OperationResult.Succeeded()); }
        public Task<OperationResult> StopAsync(CancellationToken cancellationToken) { State = PlaybackState.Stopped; return Task.FromResult(OperationResult.Succeeded()); }
        public Task<OperationResult> SetEnvironmentMixAsync(EnvironmentMixSettings settings, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Succeeded());
        public void Complete() { State = PlaybackState.Idle; CurrentCursor = new AudioCursor(100, 24000); }
    }
}







