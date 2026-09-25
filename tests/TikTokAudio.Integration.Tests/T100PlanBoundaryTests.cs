using System.Globalization;
using System.Text;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Control;
using TikTokAudio.Application.Media;
using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class T100PlanBoundaryTests
{
    [Fact]
    public async Task NumericGroupsDriveOnlyProductEntryBoundariesAcrossUnequalCycles()
    {
        var plan = PreRecorded(Product(10, Group(10, 2, 1), Group(10, 1, 1)),
            Product(2, Group(2, 1, 1)), Product(1, Group(1, 10, 1), Group(1, 2, 1), Group(1, 1, 1)));
        await using var harness = new BoundaryHarness(plan);
        var expected = new[] { "1/1", "1/2", "1/10", "2/1", "10/1", "10/2" };

        await harness.StartAsync();
        for (var index = 0; index < expected.Length * 2; index++)
        {
            if (index > 0) await harness.CompleteAndPumpAsync();
            var current = Assert.IsType<PlaybackPlanItem>(harness.Scheduler.CurrentBaseItem);
            Assert.Equal(expected[index % expected.Length], current.GroupId);
            Assert.Equal(index / expected.Length, current.Cycle);
            Assert.Equal(current.Asset, harness.Output.Current!.Asset);
        }

        Assert.Equal(new[] { "platform-1", "platform-2", "platform-10", "platform-1", "platform-2", "platform-10" },
            harness.Room.Actions.Select(action => action.Product!.PlatformProductId));
        Assert.Equal(6, harness.Boundaries.Select(boundary => boundary.BoundaryId).Distinct().Count());
        Assert.All(harness.Room.Actions, action => Assert.Equal(BoundaryHarness.SessionId, action.Context.SessionId));
    }

    [Fact]
    public async Task IndependentShuffleBagsRemainIntactThroughSchedulerAndRepeatedProductEntry()
    {
        await using var harness = new BoundaryHarness(PreRecorded(Product(1, Group(1, 1, 3), Group(1, 2, 2))));
        var first = new List<string>();
        var second = new List<string>();
        await harness.StartAsync();

        for (var cycle = 0; cycle < 12; cycle++)
        {
            if (cycle > 0) await harness.CompleteAndPumpAsync();
            Assert.Equal("1/1", harness.Scheduler.CurrentBaseItem!.GroupId);
            first.Add(harness.Scheduler.CurrentBaseItem.ClipId!);
            await harness.CompleteAndPumpAsync();
            Assert.Equal("1/2", harness.Scheduler.CurrentBaseItem!.GroupId);
            second.Add(harness.Scheduler.CurrentBaseItem.ClipId!);
        }

        AssertBags(first, 3);
        AssertBags(second, 2);
        Assert.Equal(12, harness.Room.Actions.Count);
        Assert.Equal(12, harness.Boundaries.Select(boundary => boundary.BoundaryId).Distinct().Count());
        Assert.All(harness.Room.Actions, action => Assert.Equal("platform-1", action.Product!.PlatformProductId));
    }

    [Fact]
    public async Task InterruptionRestoresSourceCursorAndSelectionWithoutRepeatingProductAction()
    {
        var planner = PreRecorded(Product(1, Group(1, 1, 3), Group(1, 2, 2)));
        await using var harness = new BoundaryHarness(planner);
        await harness.StartAsync();
        var initial = harness.Scheduler.CurrentBaseItem!;
        var target = harness.Control.Snapshot.Target;
        var epoch = harness.Control.Snapshot.Context!.ProductEpoch;
        var cursor = new AudioCursor(17291, 24000);
        harness.Output.Cursor = cursor;

        Assert.Equal(OperationStatus.Succeeded, (await harness.Scheduler.QueueInteractionAsync(
            harness.Interaction("synthetic-follow"))).Result.Status);
        await harness.PumpUntilAsync(() => harness.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Equal("synthetic-follow", harness.Output.Current!.Asset.Path);
        Assert.Single(harness.Room.Actions);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await harness.PumpAsync();
        Assert.Equal(PlaybackState.InterruptPlaying, harness.Scheduler.State);
        Assert.Equal(target, harness.Control.Snapshot.Target);
        Assert.Equal(epoch, harness.Control.Snapshot.Context!.ProductEpoch);
        Assert.Equal(2, harness.Room.Actions.Count);
        Assert.All(harness.Room.Actions, action => Assert.Equal(target, action.Product));
        Assert.Single(harness.Boundaries);
        var completed = await harness.CompleteAndPumpAsync();

        Assert.Equal(OperationStatus.Succeeded, Assert.Single(completed.Interactions).Status);
        Assert.Equal(initial, harness.Scheduler.CurrentBaseItem);
        Assert.Equal(initial.Asset, harness.Output.Current!.Asset);
        Assert.Equal(cursor, harness.Output.Current.StartAt);
        Assert.Equal(initial.EffectSeed, harness.Scheduler.CurrentBaseItem!.EffectSeed);
        Assert.Equal(epoch, harness.Control.Snapshot.Context!.ProductEpoch);
        Assert.Equal(2, harness.Room.Actions.Count);
        Assert.Single(harness.Boundaries);
        Assert.Single(planner.CaptureSnapshot(cursor).Checkpoint.ConsumedMarkerIds);
    }

    [Fact]
    public async Task OperatorTextFlowsThroughMemoryTtsAndEmitsMarkerOnlyAfterItsAudioStarts()
    {
        var prepared = await PrepareScriptAsync("Xin chào. @[7] Sản phẩm tốt. Cảm ơn.");
        await using var harness = new BoundaryHarness(prepared.Planner);

        Assert.Equal(new[] { "Xin chào.", "Sản phẩm tốt.", "Cảm ơn." }, prepared.Provider.Texts);
        Assert.All(prepared.Provider.Texts, text => Assert.DoesNotContain("@[", text));
        await harness.StartAsync();
        Assert.Empty(harness.Room.Actions);
        Assert.Empty(harness.Boundaries);

        var marked = await harness.CompleteAndPumpAsync();
        Assert.Equal("platform-7", Assert.Single(marked.Boundaries).ProductId);
        Assert.Equal("memory-tts-2", harness.Output.Current!.Asset.Path);
        Assert.Equal("platform-7", Assert.Single(harness.Room.Actions).Product!.PlatformProductId);
        harness.Output.Cursor = new AudioCursor(30457, 24000);
        await harness.Scheduler.QueueInteractionAsync(harness.Interaction("synthetic-welcome"));
        await harness.PumpUntilAsync(() => harness.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Empty((await harness.CompleteAndPumpAsync()).Boundaries);
        Assert.Equal(new AudioCursor(30457, 24000), harness.Output.Current!.StartAt);
        Assert.Single(harness.Room.Actions);

        Assert.Empty((await harness.CompleteAndPumpAsync()).Boundaries);
        Assert.Empty((await harness.CompleteAndPumpAsync()).Boundaries);
        Assert.Equal(PlaybackState.Idle, harness.Scheduler.State);
        Assert.Single(harness.Boundaries);
        Assert.Single(harness.Room.Actions);
    }

    [Fact]
    public async Task ConsecutiveAndTrailingMarkersCannotCreateExtraProductActions()
    {
        var prepared = await PrepareScriptAsync("@[1] @[2] Xin chào. @[10]");
        Assert.Single(prepared.Import.Warnings);
        Assert.Equal(new[] { "Xin chào." }, prepared.Provider.Texts);
        await using var harness = new BoundaryHarness(prepared.Planner);

        await harness.StartAsync();
        Assert.Equal("platform-2", Assert.Single(harness.Room.Actions).Product!.PlatformProductId);
        await harness.CompleteAndPumpAsync();
        await harness.PumpAsync();

        Assert.Equal(PlaybackState.Idle, harness.Scheduler.State);
        Assert.Single(harness.Room.Actions);
        Assert.Single(harness.Output.Plays);
    }

    [Fact]
    public async Task FailedAudioStartCannotConsumeOrSubmitTheProductBoundary()
    {
        var planner = PreRecorded(Product(1, Group(1, 1, 1)));
        await using var harness = new BoundaryHarness(planner);
        harness.Output.PlayResults.Enqueue(OperationResult.Failed("Synthetic output failure."));

        var failed = await harness.StartAsync();

        Assert.Equal(OperationStatus.Failed, failed.Result.Status);
        Assert.Empty(failed.Boundaries);
        Assert.Empty(harness.Room.Actions);
        Assert.Empty(planner.CaptureSnapshot(AudioCursor.Start).Checkpoint.ConsumedMarkerIds);
        await harness.StopAsync();
        var retried = await harness.StartAsync();

        Assert.Equal(OperationStatus.Succeeded, retried.Result.Status);
        Assert.Single(retried.Boundaries);
        Assert.Single(harness.Room.Actions);
        Assert.Single(harness.Output.Plays);
        Assert.Equal(2, harness.Output.Attempts);
    }

    [Fact]
    public async Task SwitchingToWaitingTtsStopsPreRecordedModeUntilItsPreparedAudioIsReady()
    {
        await using var harness = new BoundaryHarness(PreRecorded(Product(1, Group(1, 1, 1))));
        await harness.StartAsync();
        var imported = Import("@[2] Xin chào. Cảm ơn.");
        var document = Assert.IsType<TtsScriptDocument>(imported.Document);
        var next = new TtsPlaybackPlanner(document, new PlanRevision(1), loop: false);

        var switched = await harness.ForwardAsync(await harness.Scheduler.SwitchBasePlanAsync(next));

        Assert.Equal(OperationStatus.Succeeded, switched.Result.Status);
        Assert.Equal(PlaybackState.Preparing, harness.Scheduler.State);
        Assert.Equal(PlaybackState.Stopped, harness.Output.State);
        Assert.Null(harness.Output.Current);
        Assert.Single(harness.Output.Plays);
        Assert.Single(harness.Room.Actions);
        Assert.Empty((await harness.PumpAsync()).Boundaries);
        var provider = new MemoryTtsProvider();
        var generation = await next.PrepareAsync(new TtsPreGenerator(provider, new MemoryTtsCache(), 8),
            Settings(), 0, document.Segments.Count);
        Assert.Equal(OperationStatus.Succeeded, generation.Status);
        var ready = await harness.PumpAsync();

        Assert.Equal(BasePlaybackMode.TtsScript, harness.Scheduler.CurrentBaseItem!.Mode);
        Assert.Equal("platform-2", Assert.Single(ready.Boundaries).ProductId);
        Assert.Equal(new[] { "platform-1", "platform-2" },
            harness.Room.Actions.Select(action => action.Product!.PlatformProductId));
        Assert.Equal(2, harness.Output.Plays.Count);
        Assert.Equal(1, harness.Output.StopCount);
        await harness.CompleteAndPumpAsync();
        await harness.CompleteAndPumpAsync();
        Assert.Equal(PlaybackState.Idle, harness.Scheduler.State);
        Assert.Equal(3, harness.Output.Plays.Count);
    }

    [Fact]
    public async Task StopInvalidatesLatePreparationAndAllFutureProductRenewal()
    {
        await using var harness = new BoundaryHarness(PreRecorded(Product(1, Group(1, 1, 1))));
        await harness.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<OperationResult<LocalAudioAsset>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = harness.Interaction("late-interaction") with
        {
            PrepareAsync = async _ =>
            {
                entered.TrySetResult();
                try { return await release.Task; }
                finally { returned.TrySetResult(); }
            }
        };
        await harness.Scheduler.QueueInteractionAsync(request);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stopped = await harness.StopAsync();
            Assert.Equal(OperationStatus.Cancelled, Assert.Single(stopped.Interactions).Status);
            release.TrySetResult(new(OperationStatus.Succeeded, Asset("late-interaction")));
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            harness.Clock.Advance(TimeSpan.FromHours(1));
            for (var index = 0; index < 4; index++)
            {
                Assert.Empty((await harness.PumpAsync()).Boundaries);
                await Task.Yield();
            }

            Assert.Equal(PlaybackState.Stopped, harness.Scheduler.State);
            Assert.Equal(PlaybackState.Stopped, harness.Output.State);
            Assert.Null(harness.Control.Snapshot.Target);
            Assert.Single(harness.Room.Actions);
            Assert.Single(harness.Output.Plays);
            Assert.DoesNotContain(harness.Output.Plays, play => play.Asset.Path == "late-interaction");
        }
        finally { release.TrySetResult(new(OperationStatus.Cancelled, null)); }
    }

    private static void AssertBags(IReadOnlyList<string> selected, int count)
    {
        for (var offset = 0; offset < selected.Count; offset += count)
        {
            Assert.Equal(count, selected.Skip(offset).Take(count).Distinct().Count());
            if (offset > 0) Assert.NotEqual(selected[offset - 1], selected[offset]);
        }
    }

    private static PreRecordedPlaybackPlanner PreRecorded(params ImportedAudioProduct[] products) =>
        new(new ProductDirectoryImport("memory-catalog", products, []), PlanRevision.Initial, new SeededRandomSource(907));

    private static ImportedAudioProduct Product(int product, params ImportedAudioGroup[] groups) =>
        new(product, product.ToString(CultureInfo.InvariantCulture), $"platform-{product}", groups, true);

    private static ImportedAudioGroup Group(int product, int group, int count) => new(group, $"{product}/{group}",
        Enumerable.Range(0, count).Select(index =>
            new ImportedAudioClip($"{product}/{group}/clip-{index}", Asset($"memory-{product}-{group}-{index}"), null)).ToArray());

    private static LocalAudioAsset Asset(string path) => new(path, "wav", "memory-engine", TimeSpan.FromSeconds(10), 24000);

    private static TtsScriptImportResult Import(string script)
    {
        var imported = new OperatorScriptImporter(new TtsScriptImportLimits(4096, 8, 200)).Import(Encoding.UTF8.GetBytes(script),
            new Dictionary<int, string> { [1] = "platform-1", [2] = "platform-2", [7] = "platform-7", [10] = "platform-10" });
        Assert.Equal(OperationStatus.Succeeded, imported.Status);
        return imported;
    }

    private static TtsGenerationSettings Settings() => new(new("memory-engine", "1", "memory-model", "1"),
        "synthetic-voice", "vi-VN", 1, 0, 1, "none");

    private static async Task<(TtsPlaybackPlanner Planner, MemoryTtsProvider Provider, TtsScriptImportResult Import)> PrepareScriptAsync(string text)
    {
        var imported = Import(text);
        var document = Assert.IsType<TtsScriptDocument>(imported.Document);
        var planner = new TtsPlaybackPlanner(document, PlanRevision.Initial, loop: false);
        var provider = new MemoryTtsProvider();
        var prepared = await planner.PrepareAsync(new TtsPreGenerator(provider, new MemoryTtsCache(), 8),
            Settings(), 0, document.Segments.Count);
        Assert.Equal(OperationStatus.Succeeded, prepared.Status);
        return (planner, provider, imported);
    }

    // Test-only wiring forwards emitted boundaries; it does not derive or reimplement planner rules.
    private sealed class BoundaryHarness : IAsyncDisposable
    {
        public static readonly Guid SessionId = Guid.Parse("0f4cadb4-7a31-402a-a30d-a61d7b5f00af");
        private const string RoomId = "synthetic-plan-room";
        public BoundaryHarness(IBasePlaybackPlan plan)
        {
            Scheduler = new AudioPlaybackScheduler(plan, Output, Clock);
            Control = new ProductControlCoordinator(Room, Clock);
        }

        public FakeClock Clock { get; } = new();
        public MemoryOutput Output { get; } = new();
        public SimulatedLiveRoomController Room { get; } = new();
        public ProductControlCoordinator Control { get; }
        public AudioPlaybackScheduler Scheduler { get; }
        public List<ProductBoundary> Boundaries { get; } = [];

        public async Task<SchedulerUpdate> StartAsync()
        {
            Assert.Equal(OperationStatus.Succeeded, Control.Start(SessionId, RoomId).Status);
            return await ForwardAsync(await Scheduler.StartAsync(SessionId.ToString("N")));
        }

        public async Task<SchedulerUpdate> ForwardAsync(SchedulerUpdate update)
        {
            foreach (var boundary in update.Boundaries)
            {
                Boundaries.Add(boundary);
                var origin = Scheduler.CurrentBaseItem!.Mode == BasePlaybackMode.TtsScript
                    ? ProductTargetOrigin.ScriptMarker : ProductTargetOrigin.ProductEnter;
                Assert.Equal(OperationStatus.Succeeded,
                    Control.SetTarget(new ProductTarget("local-" + boundary.ProductId, boundary.ProductId), origin).Status);
            }
            await Control.PumpAsync();
            return update;
        }

        public async Task<SchedulerUpdate> PumpAsync() => await ForwardAsync(await Scheduler.PumpAsync());

        public async Task<SchedulerUpdate> CompleteAndPumpAsync()
        {
            Output.Complete();
            return await PumpAsync();
        }

        public InteractionRequest Interaction(string id) => new(id, SessionId.ToString("N"), InteractionPriority.Follow,
            Clock.Now, TimeSpan.FromSeconds(60), _ => Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, Asset(id))));

        public async Task PumpUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                while (!condition())
                {
                    await PumpAsync().WaitAsync(timeout.Token);
                    if (!condition()) await Task.Delay(1, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            Assert.True(condition(), "Expected in-memory playback transition did not occur.");
        }

        public async Task<SchedulerUpdate> StopAsync()
        {
            Assert.Equal(OperationStatus.Succeeded, Control.Stop().Status);
            return await Scheduler.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Scheduler.StopAsync();
            Control.Dispose();
        }
    }

    private sealed class MemoryOutput : IAudioOutput
    {
        public PlaybackState State { get; private set; } = PlaybackState.Idle;
        public AudioCursor? Cursor { get; set; }
        public AudioCursor? CurrentCursor => Cursor;
        public AudioPlaybackRequest? Current { get; private set; }
        public List<AudioPlaybackRequest> Plays { get; } = [];
        public Queue<OperationResult> PlayResults { get; } = new();
        public int Attempts { get; private set; }
        public int StopCount { get; private set; }

        public Task<OperationResult> PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            var result = PlayResults.TryDequeue(out var configured) ? configured : OperationResult.Succeeded();
            if (result.IsSuccess)
            {
                Assert.NotEqual(PlaybackState.BasePlaying, State);
                Plays.Add(request);
                Current = request;
                Cursor = request.StartAt;
                State = PlaybackState.BasePlaying;
            }
            else State = PlaybackState.Error;
            return Task.FromResult(result);
        }

        public Task<OperationResult> PauseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = PlaybackState.Paused;
            return Task.FromResult(OperationResult.Succeeded());
        }

        public Task<OperationResult> ResumeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = PlaybackState.BasePlaying;
            return Task.FromResult(OperationResult.Succeeded());
        }

        public Task<OperationResult> StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            State = PlaybackState.Stopped;
            Current = null;
            return Task.FromResult(OperationResult.Succeeded());
        }

        public Task<OperationResult> SetEnvironmentMixAsync(EnvironmentMixSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Succeeded());

        public void Complete() => State = PlaybackState.Idle;
    }

    private sealed class MemoryTtsProvider : ITtsProvider
    {
        public string EngineId => "memory-engine";
        public EngineRevision Revision => EngineRevision.Initial;
        public List<string> Texts { get; } = [];
        public Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Texts.Add(request.Text);
            return Task.FromResult(new TtsSynthesisOutcome(OperationStatus.Succeeded,
                new TtsSynthesisResult(Asset($"memory-tts-{Texts.Count}"), EngineId, Revision), null));
        }
        public Task<TtsHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TtsHealth(TtsHealthStatus.Healthy, EngineId, null));
        public Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult<IReadOnlyList<TtsVoice>>(OperationStatus.Succeeded, []));
    }

    private sealed class MemoryTtsCache : ITtsAudioCache
    {
        private readonly Dictionary<string, LocalAudioAsset> _assets = new(StringComparer.Ordinal);
        public Task<OperationResult<LocalAudioAsset>> FindAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, _assets.GetValueOrDefault(key)));
        public Task<OperationResult<LocalAudioAsset>> StoreAsync(string key, LocalAudioAsset generatedAsset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _assets[key] = generatedAsset;
            return Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, generatedAsset));
        }
        public Task<OperationResult> DiscardAsync(LocalAudioAsset generatedAsset) => Task.FromResult(OperationResult.Succeeded());
    }
}
