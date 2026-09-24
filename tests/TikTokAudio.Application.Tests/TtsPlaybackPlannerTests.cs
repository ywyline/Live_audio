using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class TtsPlaybackPlannerTests
{
    private static readonly PlanRevision Revision = new(7);
    private static readonly TtsGenerationSettings Settings = new(new("local", "v1", "model", "v1"), "voice", "vi", 1, 0, 1, "none");
    private static PlaybackPlannerContext Context => new(BasePlaybackMode.TtsScript, Revision, null, null, 0);

    [Fact]
    public async Task InitialReadinessRequiresActualFirstAndSecondSegments()
    {
        var document = Document(3);
        var planner = new TtsPlaybackPlanner(document, Revision);
        Assert.True(planner.ApplyPreparation(Revision, Snapshot(document, 1, 2)).IsSuccess);
        Assert.Equal(BasePlanAvailability.WaitingForSynthesis, planner.Availability);
        Assert.Null(await Next(planner));
        Assert.True(planner.ApplyPreparation(Revision, Snapshot(document, 0)).IsSuccess);
        Assert.Equal(BasePlanAvailability.Ready, planner.Availability);
        Assert.EndsWith(":0", (await Next(planner))!.ClipId);
    }

    [Fact]
    public async Task OneSegmentMayStartWithOneReadyAsset()
    {
        var planner = Ready(Document(1));
        Assert.Equal(BasePlanAvailability.Ready, planner.Availability);
        Assert.NotNull(await Next(planner));
    }

    [Fact]
    public async Task MissingNextWaitsWithoutSkippingAndBatchesAccumulate()
    {
        var document = Document(4);
        var planner = new TtsPlaybackPlanner(document, Revision);
        planner.ApplyPreparation(Revision, Snapshot(document, 0, 1));
        Assert.EndsWith(":0", (await Next(planner))!.ClipId);
        Assert.Null(await Next(planner));
        Assert.Null(planner.CurrentItem);
        Assert.Null(await Next(planner));
        Assert.Equal(BasePlanAvailability.WaitingForSynthesis, planner.Availability);
        planner.ApplyPreparation(Revision, Snapshot(document, 2));
        Assert.EndsWith(":1", (await Next(planner))!.ClipId);
        Assert.Null(await Next(planner));
        planner.ApplyPreparation(Revision, Snapshot(document, 3));
        Assert.EndsWith(":2", (await Next(planner))!.ClipId);
        Assert.EndsWith(":3", (await Next(planner))!.ClipId);
        var looped = await Next(planner);
        Assert.EndsWith(":0", looped!.ClipId);
        Assert.Equal(1, looped.Cycle);
    }

    [Fact]
    public async Task OnceCompletesWithoutErrorAndDoesNotRestart()
    {
        var planner = Ready(Document(2), false);
        Assert.NotNull(await Next(planner));
        Assert.NotNull(await Next(planner));
        Assert.Null(await Next(planner));
        Assert.Null(planner.CurrentItem);
        Assert.Equal(BasePlanAvailability.Completed, planner.Availability);
        Assert.Null(await Next(planner));
        Assert.Null(planner.Detail);
    }

    [Fact]
    public async Task MarkersAreAcknowledgedOncePerStartedItemAndCycle()
    {
        var planner = Ready(Document(1, true));
        var first = (await Next(planner))!;
        Assert.Single(planner.CurrentItem!.Boundaries);
        Assert.Equal(OperationStatus.Failed, planner.AcknowledgePlaybackStarted(first with { Cycle = 99 }).Status);
        Assert.Single(planner.CurrentItem.Boundaries);
        var boundary = Assert.Single(planner.AcknowledgePlaybackStarted(first).Value!);
        Assert.Empty(planner.AcknowledgePlaybackStarted(first).Value!);
        Assert.Empty(planner.CurrentItem.Boundaries);
        var second = (await Next(planner))!;
        var nextBoundary = Assert.Single(planner.AcknowledgePlaybackStarted(second).Value!);
        Assert.Equal(boundary.ProductId, nextBoundary.ProductId);
        Assert.NotEqual(boundary.BoundaryId, nextBoundary.BoundaryId);
    }

    [Fact]
    public async Task CheckpointRestoresExactSourceAndCannotReviveConsumedMarker()
    {
        var planner = Ready(Document(2, true));
        var first = (await Next(planner))!;
        var checkpoint = Checkpoint(first, new(12000, 48000));
        planner.AcknowledgePlaybackStarted(first);
        Assert.True((await planner.RestoreCheckpointAsync(checkpoint, default)).IsSuccess);
        var resumed = (await Next(planner))!;
        Assert.Equal(first.ClipId, resumed.ClipId);
        Assert.Equal(new AudioCursor(12000, 48000), planner.CurrentCursor);
        Assert.Empty(planner.AcknowledgePlaybackStarted(resumed).Value!);
        Assert.EndsWith(":1", (await Next(planner))!.ClipId);
        Assert.False((await planner.RestoreCheckpointAsync(checkpoint, default)).IsSuccess);
    }

    [Theory]
    [InlineData(-1, 48000)]
    [InlineData(48001, 48000)]
    [InlineData(100, 44100)]
    [InlineData(1, null)]
    public async Task InvalidCursorCannotChangePosition(long offset, int? rate)
    {
        var planner = Ready(Document(1));
        await Next(planner);
        Assert.False(planner.SaveCursor(new(offset, rate)).IsSuccess);
        Assert.Equal(new AudioCursor(0, 48000), planner.CurrentCursor);
    }

    [Fact]
    public async Task SavedCursorDoesNotScheduleDuplicateSelection()
    {
        var planner = Ready(Document(2));
        await Next(planner);
        Assert.True(planner.SaveCursor(new(14000, 48000)).IsSuccess);
        Assert.EndsWith(":1", (await Next(planner))!.ClipId);
    }

    [Fact]
    public async Task SnapshotValidationIsAtomicAndRejectsMarkerInjectionAndOtherRevision()
    {
        var document = Document(2, true);
        var planner = new TtsPlaybackPlanner(document, Revision);
        var valid = Snapshot(document, 0, 1);
        var forged = valid with { Segments = [valid.Segments[0], valid.Segments[1] with
            { Segment = document.Segments[1] with { Boundary = new("injected", "foreign") } }] };
        Assert.False(planner.ApplyPreparation(Revision, forged).IsSuccess);
        Assert.Equal(BasePlanAvailability.WaitingForSynthesis, planner.Availability);
        Assert.Equal(OperationStatus.Cancelled, planner.ApplyPreparation(new(8), valid).Status);
        Assert.Null(await Next(planner));
        Assert.True(planner.ApplyPreparation(Revision, valid).IsSuccess);
        Assert.NotNull(await Next(planner));
    }

    [Theory]
    [InlineData("index")]
    [InlineData("text")]
    [InlineData("duplicate")]
    [InlineData("engine")]
    [InlineData("duration")]
    [InlineData("rate")]
    [InlineData("key")]
    public void InvalidPreparationDoesNotPartiallyImport(string issue)
    {
        var document = Document(2);
        var planner = new TtsPlaybackPlanner(document, Revision);
        var entries = Snapshot(document, 0, 1).Segments.ToArray();
        entries[1] = issue switch
        {
            "index" => entries[1] with { Segment = entries[1].Segment with { Index = 99 } },
            "text" => entries[1] with { Segment = entries[1].Segment with { Text = "other" } },
            "duplicate" => entries[0],
            "engine" => entries[1] with { Asset = entries[1].Asset with { EngineId = "cloud" } },
            "duration" => entries[1] with { Asset = entries[1].Asset with { Duration = TimeSpan.Zero } },
            "rate" => entries[1] with { Asset = entries[1].Asset with { SampleRate = null } },
            "key" => entries[1] with { CacheKey = "" },
            _ => throw new InvalidOperationException()
        };
        Assert.False(planner.ApplyPreparation(Revision, new(TtsPreparationState.Ready, entries, 2, null)).IsSuccess);
        Assert.Equal(BasePlanAvailability.WaitingForSynthesis, planner.Availability);
        Assert.True(planner.ApplyPreparation(Revision, Snapshot(document, 1)).IsSuccess);
        Assert.Equal(BasePlanAvailability.WaitingForSynthesis, planner.Availability);
    }

    [Fact]
    public async Task PreparedAssetCannotBeReplacedByOlderSnapshot()
    {
        var document = Document(1);
        var planner = Ready(document);
        var first = (await Next(planner))!;
        var snapshot = Snapshot(document, 0);
        var stale = snapshot with { Segments = [snapshot.Segments[0] with { Asset = Asset("other") }] };
        Assert.False(planner.ApplyPreparation(Revision, stale).IsSuccess);
        Assert.Equal(first.Asset, planner.CurrentItem!.Asset);
        Assert.True(planner.ApplyPreparation(Revision, snapshot).IsSuccess);
    }

    [Fact]
    public async Task DocumentIsDefensivelyFrozen()
    {
        var segments = new[] { new TtsScriptSegment(0, "original", null) };
        var document = new TtsScriptDocument("original", "v1", segments);
        var planner = new TtsPlaybackPlanner(document, Revision);
        var valid = Snapshot(document, 0);
        segments[0] = new(0, "changed", new("foreign", "foreign"));
        Assert.True(planner.ApplyPreparation(Revision, valid).IsSuccess);
        Assert.Empty((await Next(planner))!.Boundaries);
    }

    [Fact]
    public async Task WrongModeAndRevisionDoNotAdvance()
    {
        var planner = Ready(Document(2));
        Assert.Null(await planner.SelectNextAsync(Context with { Mode = BasePlaybackMode.PreRecorded }, default));
        Assert.Null(await planner.SelectNextAsync(Context with { PlanRevision = new(8) }, default));
        Assert.EndsWith(":0", (await Next(planner))!.ClipId);
    }

    [Fact]
    public async Task GeneratorProgressMakesFirstTwoPlayableWhileLaterGenerationIsPending()
    {
        var document = Document(3);
        var blocked = Signal();
        var release = Signal();
        var provider = new Provider { Handler = async (request, _) =>
        {
            if (request.Text == "segment-2") { blocked.SetResult(); await release.Task; }
            return Outcome(request.Text);
        }};
        var planner = new TtsPlaybackPlanner(document, Revision);
        var generation = planner.PrepareAsync(new(provider, new Cache(), 3), Settings, 0, 3);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(generation.IsCompleted);
        Assert.Equal(BasePlanAvailability.Ready, planner.Availability);
        Assert.NotNull(await Next(planner));
        Assert.Null(await Next(planner));
        release.SetResult();
        Assert.Equal(OperationStatus.Succeeded, (await generation).Status);
        Assert.EndsWith(":1", (await Next(planner))!.ClipId);
        Assert.EndsWith(":2", (await Next(planner))!.ClipId);
    }

    [Fact]
    public async Task CancelledUncooperativeGeneratorCannotPublishLateAudioOrAllowAnotherBatch()
    {
        var blocked = Signal();
        var release = Signal();
        var provider = new Provider { Handler = async (request, _) =>
        { blocked.SetResult(); await release.Task; return Outcome(request.Text); }};
        var generator = new TtsPreGenerator(provider, new Cache(), 1);
        var planner = new TtsPlaybackPlanner(Document(1), Revision);
        var generation = planner.PrepareAsync(generator, Settings, 0, 1);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        planner.CancelPendingPreparation();
        Assert.Equal(OperationStatus.Failed, (await planner.PrepareAsync(generator, Settings, 0, 1)).Status);
        release.SetResult();
        Assert.Equal(OperationStatus.Cancelled, (await generation).Status);
        Assert.Null(await Next(planner));
        Assert.Equal(BasePlanAvailability.WaitingForSynthesis, planner.Availability);
    }

    [Fact]
    public async Task CancellationCallbacksDoNotBlockStop()
    {
        var blocked = Signal();
        var callbackEntered = Signal();
        var releaseCallback = Signal();
        var provider = new Provider { Handler = async (request, token) =>
        {
            // An intentionally uncooperative synchronous callback; isolated to the callback pool thread.
            using var registration = token.Register(() =>
            { callbackEntered.SetResult(); releaseCallback.Task.GetAwaiter().GetResult(); });
            blocked.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Outcome(request.Text);
        }};
        var planner = new TtsPlaybackPlanner(Document(1), Revision);
        var generation = planner.PrepareAsync(new(provider, new Cache(), 1), Settings, 0, 1);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        planner.CancelPendingPreparation();
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(generation.IsCompleted);
        releaseCallback.SetResult();
        Assert.Equal(OperationStatus.Cancelled, (await generation.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    public async Task CallerCancellationKeepsBatchOccupiedUntilCallbacksFinishEvenAfterStop()
    {
        using var caller = new CancellationTokenSource();
        var entered = Signal();
        var callbackEntered = Signal();
        var releaseCallback = Signal();
        var releaseProvider = Signal();
        var discarded = Signal();
        var calls = 0;
        var provider = new Provider { Handler = async (request, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // Source disposal owns this callback; provider completion deliberately does not await it.
                token.Register(() =>
                { callbackEntered.SetResult(); releaseCallback.Task.GetAwaiter().GetResult(); });
                entered.SetResult();
                await releaseProvider.Task;
            }
            return Outcome(request.Text);
        }};
        var cache = new Cache { AfterDiscard = () => discarded.TrySetResult() };
        var generator = new TtsPreGenerator(provider, cache, 1);
        var planner = new TtsPlaybackPlanner(Document(1), Revision);
        var generation = planner.PrepareAsync(generator, Settings, 0, 1, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        planner.CancelPendingPreparation();
        planner.CancelPendingPreparation();
        releaseProvider.SetResult();
        try
        {
            await discarded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(generation.IsCompleted);
            Assert.Equal(OperationStatus.Failed, (await planner.PrepareAsync(generator, Settings, 0, 1)).Status);
        }
        finally { releaseCallback.TrySetResult(); }
        Assert.Equal(OperationStatus.Cancelled, (await generation.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(OperationStatus.Succeeded, (await planner.PrepareAsync(generator, Settings, 0, 1)).Status);
    }
    [Fact]
    public async Task DifferentGeneratorCannotReplacePlanSource()
    {
        var planner = new TtsPlaybackPlanner(Document(2), Revision);
        var generator = new TtsPreGenerator(new Provider(), new Cache(), 1);
        Assert.Equal(OperationStatus.Succeeded, (await planner.PrepareAsync(generator, Settings, 0, 1)).Status);
        Assert.Equal(OperationStatus.Failed, (await planner.PrepareAsync(new(new Provider(), new Cache(), 1), Settings, 1, 1)).Status);
        Assert.Equal(OperationStatus.Succeeded, (await planner.PrepareAsync(generator, Settings, 1, 1)).Status);
        Assert.NotNull(await Next(planner));
    }

    [Fact]
    public async Task UnmarkedSegmentsRetainLastStartedProductWithoutNewBoundary()
    {
        var planner = Ready(Document(2, true));
        var first = (await Next(planner))!;
        planner.AcknowledgePlaybackStarted(first);
        var second = (await Next(planner))!;
        Assert.Equal(first.ProductId, second.ProductId);
        Assert.Empty(second.Boundaries);
        Assert.Empty(planner.AcknowledgePlaybackStarted(second).Value!);
    }
    private static TtsScriptDocument Document(int count, bool marker = false) => new("synthetic-script", "test-v1",
        Enumerable.Range(0, count).Select(index => new TtsScriptSegment(index, $"segment-{index}",
            marker && index == 0 ? new("product-1", "marker-1") : null)).ToArray());
    private static LocalAudioAsset Asset(string id) => new($"{id}.wav", "wav", "local", TimeSpan.FromSeconds(1), 48000);
    private static TtsPreparationSnapshot Snapshot(TtsScriptDocument document, params int[] indices) => new(
        TtsPreparationState.Ready, indices.Select(index => new PreparedTtsSegment(document.Segments[index], Asset($"segment-{index}"), $"key-{index}")).ToArray(),
        Math.Min(2, document.Segments.Count), null);
    private static TtsPlaybackPlanner Ready(TtsScriptDocument document, bool loop = true)
    {
        var planner = new TtsPlaybackPlanner(document, Revision, loop);
        Assert.True(planner.ApplyPreparation(Revision, Snapshot(document, Enumerable.Range(0, document.Segments.Count).ToArray())).IsSuccess);
        return planner;
    }
    private static Task<PlaybackPlanItem?> Next(TtsPlaybackPlanner planner) => planner.SelectNextAsync(Context, default);
    private static PlaybackCheckpoint Checkpoint(PlaybackPlanItem item, AudioCursor cursor) => new(item.Mode, item.PlanRevision,
        item.ProductId, item.GroupId, item.ClipId, cursor, item.Cycle, item.EffectSeed, new HashSet<string>());
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TtsSynthesisOutcome Outcome(string text) => new(OperationStatus.Succeeded, new(Asset(text), "local", new(1)), null);

    private sealed class Provider : ITtsProvider
    {
        public string EngineId => "local";
        public EngineRevision Revision => new(1);
        public Func<TtsSynthesisRequest, CancellationToken, Task<TtsSynthesisOutcome>> Handler { get; init; } =
            (request, _) => Task.FromResult(Outcome(request.Text));
        public Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken token) => Handler(request, token);
        public Task<TtsHealth> GetHealthAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Cache : ITtsAudioCache
    {
        public Action? AfterDiscard { get; init; }
        public Task<OperationResult<LocalAudioAsset>> FindAsync(string key, CancellationToken token) =>
            Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, null));
        public Task<OperationResult<LocalAudioAsset>> StoreAsync(string key, LocalAudioAsset asset, CancellationToken token) =>
            Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, asset));
        public Task<OperationResult> DiscardAsync(LocalAudioAsset asset)
        { AfterDiscard?.Invoke(); return Task.FromResult(OperationResult.Succeeded()); }
    }
}