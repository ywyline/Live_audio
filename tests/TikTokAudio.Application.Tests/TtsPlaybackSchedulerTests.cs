using System.Text;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Media;
using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class TtsPlaybackSchedulerTests
{
    [Fact]
    public async Task InitialPlaybackRequiresCurrentAndNextSegment()
    {
        var h = new Harness();
        h.Supply(0);
        await h.Scheduler.StartAsync("session");
        Assert.Equal(PlaybackState.Preparing, h.Scheduler.State);
        Assert.False(string.IsNullOrWhiteSpace(h.Scheduler.Detail));
        Assert.Empty(h.Output.Plays);
        await h.Pump();
        await h.Pump();
        Assert.Empty(h.Output.Plays);
        h.Supply(1);
        var update = await h.Pump();
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal("tts-0", h.Output.Current!.Asset.Path);
        Assert.Single(update.Boundaries);
    }

    [Fact]
    public async Task ExhaustedSupplyWaitsThenContinuesAtNextSegment()
    {
        var h = new Harness();
        h.Supply(0, 1);
        await h.Scheduler.StartAsync("session");
        h.Output.Complete();
        await h.Pump();
        Assert.Equal(PlaybackState.Preparing, h.Scheduler.State);
        Assert.Single(h.Output.Plays);
        await h.Pump();
        await h.Pump();
        Assert.Single(h.Output.Plays);
        h.Supply(2);
        await h.Pump();
        Assert.Equal("tts-1", h.Output.Current!.Asset.Path);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
    }

    [Fact]
    public async Task OneShotNaturallyCompletesAndDoesNotReplayOnPump()
    {
        var h = new Harness(loop: false);
        h.Supply(0, 1, 2);
        await h.Scheduler.StartAsync("session");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal($"tts-{i}", h.Output.Current!.Asset.Path);
            h.Output.Complete();
            await h.Pump();
        }
        Assert.Equal(PlaybackState.Idle, h.Scheduler.State);
        await h.Pump();
        await h.Pump();
        Assert.Equal(3, h.Output.Plays.Count);
        Assert.Empty(h.Completions);
    }

    [Fact]
    public async Task LoopStartsNewCycleAndConsumesMarkerOncePerCycle()
    {
        var h = new Harness();
        h.Supply(0, 1, 2);
        var first = await h.Scheduler.StartAsync("session");
        var initialMarker = Assert.Single(first.Boundaries);
        for (var i = 0; i < 3; i++)
        {
            h.Output.Complete();
            await h.Pump();
        }
        Assert.Equal("tts-0", h.Output.Current!.Asset.Path);
        Assert.Equal(1, h.Scheduler.CurrentBaseItem!.Cycle);
        var nextMarker = Assert.Single(h.Boundaries);
        Assert.Equal(initialMarker.ProductId, nextMarker.ProductId);
        Assert.NotEqual(initialMarker.BoundaryId, nextMarker.BoundaryId);
        Assert.Empty((await h.Pump()).Boundaries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InteractionCanPlayDuringInitialOrBetweenSegmentWait(bool betweenSegments)
    {
        var h = new Harness();
        if (betweenSegments) h.Supply(0, 1);
        await h.Scheduler.StartAsync("session");
        if (betweenSegments)
        {
            h.Output.Complete();
            await h.Pump();
        }
        Assert.Equal(PlaybackState.Preparing, h.Scheduler.State);
        await h.Scheduler.QueueInteractionAsync(h.Request("interaction"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Equal("interaction", h.Output.Current!.Asset.Path);
        Assert.Equal(0, h.Output.PauseCount);
        h.Output.Complete();
        await h.Pump();
        Assert.Equal(PlaybackState.Preparing, h.Scheduler.State);
        Assert.Equal(OperationStatus.Succeeded, Assert.Single(h.Completions).Status);
        h.Supply(0, 1, 2);
        await h.Pump();
        Assert.Equal(betweenSegments ? "tts-1" : "tts-0", h.Output.Current!.Asset.Path);
    }

    [Fact]
    public async Task PausedSynthesisWaitDoesNotStartSuppliedAudioUntilResume()
    {
        var h = new Harness();
        await h.Scheduler.StartAsync("session");
        Assert.Equal(OperationStatus.Succeeded, (await h.Scheduler.PauseAsync()).Result.Status);
        Assert.Equal(PlaybackState.Paused, h.Scheduler.State);
        h.Supply(0, 1);
        await h.Pump();
        Assert.Empty(h.Output.Plays);
        await h.Scheduler.ResumeAsync();
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal("tts-0", h.Output.Current!.Asset.Path);
    }

    [Fact]
    public async Task InteractionResumesSameTtsAssetAtExactCursorWithoutRepeatingMarker()
    {
        var h = new Harness();
        h.Supply(0, 1, 2);
        Assert.Single((await h.Scheduler.StartAsync("session")).Boundaries);
        var original = h.Scheduler.CurrentBaseItem!;
        h.Output.Cursor = new AudioCursor(57291, 24000);
        await h.Scheduler.QueueInteractionAsync(h.Request("interaction"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        h.Output.Complete();
        await h.Pump();
        Assert.Equal(original, h.Scheduler.CurrentBaseItem);
        Assert.Equal(original.Asset, h.Output.Current!.Asset);
        Assert.Equal(new AudioCursor(57291, 24000), h.Output.Current.StartAt);
        Assert.Empty(h.Boundaries);
        Assert.Equal(OperationStatus.Succeeded, Assert.Single(h.Completions).Status);
    }

    [Fact]
    public async Task FailedOutputStartKeepsMarkerForSuccessfulRetry()
    {
        var h = new Harness();
        h.Supply(0, 1, 2);
        h.Output.PlayResults.Enqueue(OperationResult.Failed("synthetic open failure"));
        var failed = await h.Scheduler.StartAsync("session");
        Assert.Equal(OperationStatus.Failed, failed.Result.Status);
        Assert.Empty(failed.Boundaries);
        Assert.Equal(PlaybackState.Error, h.Scheduler.State);
        await h.Scheduler.StopAsync();
        var retried = await h.Scheduler.StartAsync("retry-session");
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Single(retried.Boundaries);
        Assert.Equal("tts-0", h.Output.Current!.Asset.Path);
    }

    [Fact]
    public async Task SwitchingToPreRecordedCancelsActiveInteractionAndPreservesSession()
    {
        var h = new Harness();
        h.Supply(0, 1, 2);
        await h.Scheduler.StartAsync("session");
        await h.Scheduler.QueueInteractionAsync(h.Request("old-interaction"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        var update = await h.Scheduler.SwitchBasePlanAsync(PreRecorded(1));
        Assert.Equal(OperationStatus.Succeeded, update.Result.Status);
        Assert.Equal(OperationStatus.Cancelled, Assert.Single(update.Interactions).Status);
        Assert.Equal(BasePlaybackMode.PreRecorded, h.Scheduler.CurrentBaseItem!.Mode);
        Assert.Equal("prerecorded", h.Output.Current!.Asset.Path);
        Assert.Equal(OperationStatus.Succeeded,
            (await h.Scheduler.QueueInteractionAsync(h.Request("new-interaction"))).Result.Status);
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Equal("new-interaction", h.Output.Current!.Asset.Path);
    }

    [Fact]
    public async Task SwitchingToWaitingTtsStopsOldBaseUntilCurrentAndNextReady()
    {
        var clock = new FakeClock();
        var output = new FakeOutput();
        var scheduler = new AudioPlaybackScheduler(PreRecorded(0), output, clock);
        await scheduler.StartAsync("session");
        var document = Document();
        var next = new TtsPlaybackPlanner(document, new PlanRevision(1));
        var switched = await scheduler.SwitchBasePlanAsync(next);
        Assert.Equal(OperationStatus.Succeeded, switched.Result.Status);
        Assert.Equal(PlaybackState.Preparing, scheduler.State);
        Assert.Equal(PlaybackState.Stopped, output.State);
        Supply(next, document, 0, 1);
        await scheduler.PumpAsync();
        Assert.Equal(PlaybackState.BasePlaying, scheduler.State);
        Assert.Equal(BasePlaybackMode.TtsScript, scheduler.CurrentBaseItem!.Mode);
        Assert.Equal("tts-0", output.Current!.Asset.Path);
    }

    [Fact]
    public async Task SwitchRejectsSameOrOlderRevisionWithoutStoppingCurrentAudio()
    {
        var h = new Harness();
        h.Supply(0, 1);
        await h.Scheduler.StartAsync("session");
        var current = h.Output.Current;
        var update = await h.Scheduler.SwitchBasePlanAsync(PreRecorded(0));
        Assert.Equal(OperationStatus.Failed, update.Result.Status);
        Assert.Same(current, h.Output.Current);
        Assert.Equal(0, h.Output.StopCount);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
    }

    [Fact]
    public async Task ModeSwitchInvalidatesLateOldOutputPreparation()
    {
        var h = new Harness();
        h.Supply(0, 1);
        var gate = h.Output.GateNextPlay();
        var starting = h.Scheduler.StartAsync("session");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var switched = await h.Scheduler.SwitchBasePlanAsync(PreRecorded(1)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationStatus.Succeeded, switched.Result.Status);
        gate.Release.TrySetResult(OperationResult.Succeeded());
        await starting.WaitAsync(TimeSpan.FromSeconds(10));
        await gate.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("prerecorded", h.Output.Current!.Asset.Path);
        Assert.DoesNotContain(h.Output.Installed, asset => asset.Path.StartsWith("tts-", StringComparison.Ordinal));
        Assert.Equal(BasePlaybackMode.PreRecorded, h.Scheduler.CurrentBaseItem!.Mode);
    }

    [Fact]
    public async Task StopDuringModeSwitchRejectsLateNewOutputAndDoesNotRevivePlayback()
    {
        var h = new Harness();
        h.Supply(0, 1);
        await h.Scheduler.StartAsync("session");
        var gate = h.Output.GateNextPlay();
        var switching = h.Scheduler.SwitchBasePlanAsync(PreRecorded(1));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        gate.Release.TrySetResult(OperationResult.Succeeded());
        var switched = await switching.WaitAsync(TimeSpan.FromSeconds(10));
        await gate.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationStatus.Cancelled, switched.Result.Status);
        Assert.Empty(switched.Boundaries);
        Assert.Equal(PlaybackState.Stopped, h.Scheduler.State);
        Assert.Equal(PlaybackState.Stopped, h.Output.State);
        Assert.DoesNotContain(h.Output.Installed, asset => asset.Path == "prerecorded");
    }

    [Fact]
    public async Task SwitchingCancelsPendingInteractionAndIgnoresItsLateResult()
    {
        var h = new Harness();
        h.Supply(0, 1);
        await h.Scheduler.StartAsync("session");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<OperationResult<LocalAudioAsset>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;
        await h.Scheduler.QueueInteractionAsync(h.Request("late", token =>
        {
            receivedToken = token;
            entered.TrySetResult();
            return release.Task;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var switched = await h.Scheduler.SwitchBasePlanAsync(PreRecorded(1));
        Assert.Equal(OperationStatus.Cancelled, Assert.Single(switched.Interactions).Status);
        Assert.True(receivedToken.IsCancellationRequested);
        release.TrySetResult(new(OperationStatus.Succeeded, Asset("late")));
        await h.Pump();
        Assert.Equal("prerecorded", h.Output.Current!.Asset.Path);
        Assert.DoesNotContain(h.Output.Plays, request => request.Asset.Path == "late");
    }

    [Fact]
    public async Task FailedStopDuringModeSwitchDoesNotStartNewBase()
    {
        var h = new Harness();
        h.Supply(0, 1);
        await h.Scheduler.StartAsync("session");
        h.Output.StopResults.Enqueue(OperationResult.Failed("synthetic stop failure"));
        var switched = await h.Scheduler.SwitchBasePlanAsync(PreRecorded(1));
        Assert.Equal(OperationStatus.Failed, switched.Result.Status);
        Assert.Equal(PlaybackState.Error, h.Scheduler.State);
        Assert.Empty(switched.Boundaries);
        Assert.DoesNotContain(h.Output.Plays, request => request.Asset.Path == "prerecorded");
    }

    [Fact]
    public async Task SwitchingWhilePausedDefersNewBaseAndMarkerUntilResume()
    {
        var h = new Harness();
        h.Supply(0, 1);
        await h.Scheduler.StartAsync("session");
        await h.Scheduler.PauseAsync();
        var switched = await h.Scheduler.SwitchBasePlanAsync(PreRecorded(1));
        Assert.Equal(OperationStatus.Succeeded, switched.Result.Status);
        Assert.Equal(PlaybackState.Paused, h.Scheduler.State);
        Assert.Empty(switched.Boundaries);
        Assert.DoesNotContain(h.Output.Plays, request => request.Asset.Path == "prerecorded");
        var resumed = await h.Scheduler.ResumeAsync();
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal("prerecorded", h.Output.Current!.Asset.Path);
        Assert.Single(resumed.Boundaries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrModeSwitchCancelsOwnedTtsPreparationAndRejectsLateAsset(bool switchMode)
    {
        var h = new Harness();
        var provider = new GatedProvider();
        var cache = new MemoryCache();
        var generator = new TtsPreGenerator(provider, cache, 3);
        var settings = new TtsGenerationSettings(new("memory-engine", "1", "memory-model", "1"),
            "voice", "vi-VN", 1, 0, 1, "none");
        var preparing = h.Planner.PrepareAsync(generator, settings, 0, 3);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Scheduler.StartAsync("session");
        if (switchMode) await h.Scheduler.SwitchBasePlanAsync(PreRecorded(1));
        else await h.Scheduler.StopAsync();
        Assert.True(provider.Token.IsCancellationRequested);
        provider.Release.TrySetResult();
        var result = await preparing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        await h.Pump();
        Assert.Equal(switchMode ? PlaybackState.BasePlaying : PlaybackState.Stopped, h.Scheduler.State);
        Assert.DoesNotContain(h.Output.Plays, request => request.Asset.Path == "late-generated");
        Assert.Equal(0, cache.StoreCount);
        Assert.Equal(1, cache.DiscardCount);
    }
    [Fact]
    public async Task ModeSwitchStartsNewPlanEvenWhenOrdinaryCommandCapacityIsFull()
    {
        var h = new Harness(capacity: 1);
        h.Supply(0, 1);
        var gate = h.Output.GateNextPlay();
        var starting = h.Scheduler.StartAsync("session");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queuedPump = h.Scheduler.PumpAsync();
        var switched = await h.Scheduler.SwitchBasePlanAsync(PreRecorded(1)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationStatus.Succeeded, switched.Result.Status);
        Assert.Equal("prerecorded", h.Output.Current!.Asset.Path);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        gate.Release.TrySetResult(OperationResult.Succeeded());
        await Task.WhenAll(starting, queuedPump).WaitAsync(TimeSpan.FromSeconds(10));
        await gate.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(h.Output.Installed, asset => asset.Path.StartsWith("tts-", StringComparison.Ordinal));
        Assert.Equal(OperationStatus.Succeeded,
            (await h.Scheduler.QueueInteractionAsync(h.Request("current-session"))).Result.Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleSegmentUsesItsOwnReadyAssetForOnceOrLoop(bool loop)
    {
        var script = new TtsScriptDocument("single synthetic segment", "test-v1",
            [new TtsScriptSegment(0, "Xin chào.", new ProductBoundary("product-one", "marker-one"))]);
        var planner = new TtsPlaybackPlanner(script, PlanRevision.Initial, loop);
        Supply(planner, script, 0);
        var output = new FakeOutput();
        var scheduler = new AudioPlaybackScheduler(planner, output, new FakeClock());
        var started = await scheduler.StartAsync("session");
        Assert.Equal(OperationStatus.Succeeded, started.Result.Status);
        Assert.Single(started.Boundaries);
        Assert.Equal(PlaybackState.BasePlaying, scheduler.State);
        output.Complete();
        var finished = await scheduler.PumpAsync();
        Assert.Equal(loop ? PlaybackState.BasePlaying : PlaybackState.Idle, scheduler.State);
        Assert.Equal(loop ? 2 : 1, output.Plays.Count);
        if (loop)
        {
            Assert.Equal(1, scheduler.CurrentBaseItem!.Cycle);
            Assert.Single(finished.Boundaries);
        }
        else Assert.Empty(finished.Boundaries);
    }
    [Fact]
    public async Task ImportedVietnameseScriptFlowsThroughPreparationAndOneShotPlaybackWithOneMarker()
    {
        var importer = new OperatorScriptImporter(new TtsScriptImportLimits(4096, 10, 200));
        var imported = importer.Import(Encoding.UTF8.GetBytes("@[7] Xin chào. Sản phẩm tốt."),
            new Dictionary<int, string> { [7] = "mapped-product-seven" });
        Assert.Equal(OperationStatus.Succeeded, imported.Status);
        var script = Assert.IsType<TtsScriptDocument>(imported.Document);
        Assert.Equal(2, script.Segments.Count);
        var planner = new TtsPlaybackPlanner(script, PlanRevision.Initial, loop: false);
        var provider = new ImmediateProvider();
        var cache = new MemoryCache();
        var generator = new TtsPreGenerator(provider, cache, 10);
        var settings = new TtsGenerationSettings(new("memory-engine", "1", "memory-model", "1"),
            "voice", "vi-VN", 1, 0, 1, "none");
        var prepared = await planner.PrepareAsync(generator, settings, 0, script.Segments.Count);
        Assert.Equal(OperationStatus.Succeeded, prepared.Status);
        Assert.Equal(new[] { "Xin chào.", "Sản phẩm tốt." }, provider.Texts);
        Assert.All(provider.Texts, text => Assert.DoesNotContain("@[", text));
        Assert.Equal(2, cache.StoreCount);
        var output = new FakeOutput();
        var scheduler = new AudioPlaybackScheduler(planner, output, new FakeClock());
        var started = await scheduler.StartAsync("session");
        Assert.Equal(OperationStatus.Succeeded, started.Result.Status);
        Assert.Equal("mapped-product-seven", Assert.Single(started.Boundaries).ProductId);
        Assert.Equal("generated-1", output.Current!.Asset.Path);
        Assert.Empty((await scheduler.PumpAsync()).Boundaries);
        output.Complete();
        var next = await scheduler.PumpAsync();
        Assert.Empty(next.Boundaries);
        Assert.Equal("generated-2", output.Current!.Asset.Path);
        output.Complete();
        var completed = await scheduler.PumpAsync();
        Assert.Empty(completed.Boundaries);
        Assert.Equal(PlaybackState.Idle, scheduler.State);
        Assert.Equal(2, output.Plays.Count);
    }
    private static LocalAudioAsset Asset(string path) => new(path, "wav", "memory-engine", TimeSpan.FromSeconds(10), 24000);

    private static TtsScriptDocument Document() => new("synthetic script", "test-v1",
    [new(0, "Xin chào.", new ProductBoundary("product-one", "marker-one")),
        new(1, "Sản phẩm tốt.", null), new(2, "Cảm ơn.", null)]);

    private static void Supply(TtsPlaybackPlanner planner, TtsScriptDocument document, params int[] indexes)
    {
        var snapshot = new TtsPreparationSnapshot(TtsPreparationState.Ready,
            indexes.Select(index => new PreparedTtsSegment(document.Segments[index], Asset($"tts-{index}"), $"cache-{index}")).ToArray(),
            Math.Min(2, indexes.Length), null);
        Assert.Equal(OperationStatus.Succeeded, planner.ApplyPreparation(planner.Revision, snapshot).Status);
    }

    private static PreRecordedPlaybackPlanner PreRecorded(long revision)
    {
        var catalog = new ProductDirectoryImport("memory-root",
            [new ImportedAudioProduct(1, "1", "product-one",
                [new ImportedAudioGroup(1, "1/1", [new ImportedAudioClip("1/1/a.wav", Asset("prerecorded"), null)])], true)], []);
        return new PreRecordedPlaybackPlanner(catalog, new PlanRevision(revision), new SeededRandomSource(17));
    }

    private sealed class Harness(bool loop = true, int capacity = 22)
    {
        public FakeClock Clock { get; } = new();
        public FakeOutput Output { get; } = new();
        public TtsScriptDocument Script { get; } = Document();
        private TtsPlaybackPlanner? planner;
        private AudioPlaybackScheduler? scheduler;
        public TtsPlaybackPlanner Planner => planner ??= new TtsPlaybackPlanner(Script, PlanRevision.Initial, loop);
        public AudioPlaybackScheduler Scheduler => scheduler ??= new AudioPlaybackScheduler(Planner, Output, Clock, capacity);
        public List<InteractionCompletion> Completions { get; } = [];
        public List<ProductBoundary> Boundaries { get; } = [];
        public void Supply(params int[] indexes) => TtsPlaybackSchedulerTests.Supply(Planner, Script, indexes);
        public InteractionRequest Request(string id, Func<CancellationToken, Task<OperationResult<LocalAudioAsset>>>? prepare = null) =>
            new(id, "session", InteractionPriority.Follow, Clock.Now, TimeSpan.FromSeconds(60),
                prepare ?? (_ => Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, Asset(id)))));
        public async Task<SchedulerUpdate> Pump()
        {
            var update = await Scheduler.PumpAsync();
            Completions.AddRange(update.Interactions);
            Boundaries.AddRange(update.Boundaries);
            return update;
        }
        public async Task Until(Func<bool> predicate)
        {
            for (var attempt = 0; attempt < 10000 && !predicate(); attempt++)
            {
                await Pump();
                await Task.Yield();
            }
            Assert.True(predicate(), "Expected in-memory scheduler transition did not occur.");
        }
    }

    private sealed class ImmediateProvider : ITtsProvider
    {
        public string EngineId => "memory-engine";
        public EngineRevision Revision => EngineRevision.Initial;
        public List<string> Texts { get; } = [];
        public Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Texts.Add(request.Text);
            return Task.FromResult(new TtsSynthesisOutcome(OperationStatus.Succeeded,
                new(Asset($"generated-{Texts.Count}"), EngineId, Revision), null));
        }
        public Task<TtsHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TtsHealth(TtsHealthStatus.Healthy, EngineId, null));
        public Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult<IReadOnlyList<TtsVoice>>(OperationStatus.Succeeded, []));
    }
    private sealed class GatedProvider : ITtsProvider
    {
        public string EngineId => "memory-engine";
        public EngineRevision Revision => EngineRevision.Initial;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public async Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Entered.TrySetResult();
            await Release.Task;
            return new(OperationStatus.Succeeded, new(Asset("late-generated"), EngineId, Revision), null);
        }
        public Task<TtsHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TtsHealth(TtsHealthStatus.Healthy, EngineId, null));
        public Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult<IReadOnlyList<TtsVoice>>(OperationStatus.Succeeded, []));
    }

    private sealed class MemoryCache : ITtsAudioCache
    {
        public int StoreCount { get; private set; }
        public int DiscardCount { get; private set; }
        public Task<OperationResult<LocalAudioAsset>> FindAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, null));
        public Task<OperationResult<LocalAudioAsset>> StoreAsync(string key, LocalAudioAsset generatedAsset, CancellationToken cancellationToken)
        {
            StoreCount++;
            return Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, generatedAsset));
        }
        public Task<OperationResult> DiscardAsync(LocalAudioAsset generatedAsset)
        {
            DiscardCount++;
            return Task.FromResult(OperationResult.Succeeded());
        }
    }
    private sealed class PlayGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OperationResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeOutput : IAudioOutput
    {
        private readonly object sync = new();
        private long generation;
        private PlayGate? nextGate;
        public PlaybackState State { get; private set; } = PlaybackState.Idle;
        public AudioCursor? Cursor { get; set; }
        public AudioCursor? CurrentCursor => Cursor;
        public AudioPlaybackRequest? Current { get; private set; }
        public List<AudioPlaybackRequest> Plays { get; } = [];
        public List<LocalAudioAsset> Installed { get; } = [];
        public Queue<OperationResult> PlayResults { get; } = new();
        public Queue<OperationResult> StopResults { get; } = new();
        public int PauseCount { get; private set; }
        public int StopCount { get; private set; }
        public PlayGate GateNextPlay() => nextGate = new PlayGate();
        public async Task<OperationResult> PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken)
        {
            long invocation;
            PlayGate? gate;
            lock (sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                invocation = ++generation;
                Plays.Add(request);
                gate = nextGate;
                nextGate = null;
            }
            try
            {
                OperationResult result;
                if (gate is not null)
                {
                    gate.Entered.TrySetResult();
                    result = await gate.Release.Task;
                }
                else result = PlayResults.TryDequeue(out var queued) ? queued : OperationResult.Succeeded();
                lock (sync)
                {
                    if (invocation != generation || cancellationToken.IsCancellationRequested) return OperationResult.Cancelled();
                    if (result.IsSuccess)
                    {
                        Current = request;
                        Installed.Add(request.Asset);
                        Cursor = request.StartAt;
                        State = PlaybackState.BasePlaying;
                    }
                    else State = PlaybackState.Error;
                    return result;
                }
            }
            finally { gate?.Finished.TrySetResult(); }
        }
        public Task<OperationResult> PauseAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested) return Task.FromResult(OperationResult.Cancelled());
                if (State != PlaybackState.BasePlaying) return Task.FromResult(OperationResult.Failed("no playing stream"));
                PauseCount++;
                State = PlaybackState.Paused;
                return Task.FromResult(OperationResult.Succeeded());
            }
        }
        public Task<OperationResult> ResumeAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested) return Task.FromResult(OperationResult.Cancelled());
                if (State != PlaybackState.Paused) return Task.FromResult(OperationResult.Failed("no paused stream"));
                State = PlaybackState.BasePlaying;
                return Task.FromResult(OperationResult.Succeeded());
            }
        }
        public Task<OperationResult> StopAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested) return Task.FromResult(OperationResult.Cancelled());
                generation++;
                StopCount++;
                var result = StopResults.TryDequeue(out var queued) ? queued : OperationResult.Succeeded();
                State = result.IsSuccess ? PlaybackState.Stopped : PlaybackState.Error;
                return Task.FromResult(result);
            }
        }
        public Task<OperationResult> SetEnvironmentMixAsync(EnvironmentMixSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Succeeded());
        public void Complete()
        {
            lock (sync)
            {
                Cursor = new AudioCursor(240000, 24000);
                State = PlaybackState.Idle;
            }
        }
    }
}
