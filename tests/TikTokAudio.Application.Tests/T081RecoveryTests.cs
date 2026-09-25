using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Media;
using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class T081RecoveryTests
{
    private static readonly PlanRevision Revision = new(7);
    private static readonly TtsGenerationSettings Settings =
        new(new("local", "v1", "model", "v1"), "voice", "vi", 1, 0, 1, "none");

    [Fact]
    public async Task PreRecordedCaptureCreatesReadOnlyPreviewAndExplicitRestorePreservesState()
    {
        var store = new MemoryRecoveryStore();
        var coordinator = new PlaybackRecoveryCoordinator(store);
        var source = PreRecorded();
        var selected = Assert.IsType<PlaybackPlanItem>(await Next(source));
        source.AcknowledgePlaybackStarted(selected);
        var engine = new TtsEngineSnapshot("local", new EngineRevision(3), "voice", false);

        Assert.Equal(OperationStatus.Succeeded,
            (await coordinator.CapturePreRecordedAsync(source, new AudioCursor(1200, 24000), engine,
                DateTimeOffset.Parse("2026-09-25T00:00:00Z"))).Status);
        var preview = Assert.IsType<PlaybackRecoveryPreview>(await coordinator.CreatePreviewAsync());
        Assert.True(preview.RequiresManualConfirmation);
        Assert.False(preview.WillPlayAudio);
        Assert.False(preview.WillInvokePlatformActions);
        Assert.Equal(engine.EngineId, preview.State.EngineId);
        Assert.Equal(engine.Revision, preview.State.EngineRevision);

        var restored = PreRecorded();
        Assert.Null(restored.CurrentItem);
        Assert.Equal(OperationStatus.Succeeded,
            PlaybackRecoveryCoordinator.RestorePreRecordedAfterConfirmation(restored, preview).Status);
        var replay = Assert.IsType<PlaybackPlanItem>(await Next(restored));
        Assert.Equal(selected.ClipId, replay.ClipId);
        Assert.Equal(new AudioCursor(1200, 24000), restored.CurrentCursor);
        Assert.Empty(replay.Boundaries);
    }

    [Fact]
    public async Task PreviewDoesNotMutatePlannerAndCancelledTtsRestoreDoesNotMutatePlanner()
    {
        var store = new MemoryRecoveryStore();
        var coordinator = new PlaybackRecoveryCoordinator(store);
        var source = ReadyTts();
        var selected = Assert.IsType<PlaybackPlanItem>(await Next(source));
        Assert.Equal(OperationStatus.Succeeded,
            (await coordinator.CaptureTtsAsync(source, new AudioCursor(9000, 48000))).Status);
        var preview = Assert.IsType<PlaybackRecoveryPreview>(await coordinator.CreatePreviewAsync());
        Assert.Equal(selected.ClipId, source.CurrentItem!.ClipId);
        Assert.Equal(new AudioCursor(9000, 48000), source.CurrentCursor);

        var target = ReadyTts();
        var targetItem = Assert.IsType<PlaybackPlanItem>(await Next(target));
        var before = target.CurrentCursor;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await PlaybackRecoveryCoordinator.RestoreTtsAfterConfirmationAsync(target, preview, cancellation.Token);
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(targetItem.ClipId, target.CurrentItem!.ClipId);
        Assert.Equal(before, target.CurrentCursor);
    }

    [Fact]
    public async Task TtsCaptureAndExplicitRestorePreserveCheckpointAndConsumedMarkers()
    {
        var store = new MemoryRecoveryStore();
        var coordinator = new PlaybackRecoveryCoordinator(store);
        var source = ReadyTts(marker: true);
        var selected = Assert.IsType<PlaybackPlanItem>(await Next(source));
        Assert.Single(source.AcknowledgePlaybackStarted(selected).Value!);
        Assert.Equal(OperationStatus.Succeeded,
            (await coordinator.CaptureTtsAsync(source, new AudioCursor(4000, 48000),
                new TtsEngineSnapshot("local", new EngineRevision(8), "voice", false))).Status);
        var preview = Assert.IsType<PlaybackRecoveryPreview>(await coordinator.CreatePreviewAsync());

        var target = ReadyTts(marker: true);
        await Next(target);
        Assert.Equal(OperationStatus.Succeeded,
            (await PlaybackRecoveryCoordinator.RestoreTtsAfterConfirmationAsync(target, preview)).Status);
        var replay = Assert.IsType<PlaybackPlanItem>(await Next(target));
        Assert.Equal(selected.ClipId, replay.ClipId);
        Assert.Equal(new AudioCursor(4000, 48000), target.CurrentCursor);
        Assert.Empty(replay.Boundaries);
        Assert.Equal(new EngineRevision(8), preview.State.EngineRevision);
    }

    [Fact]
    public async Task InvalidPreviewCannotRestorePlanner()
    {
        var store = new MemoryRecoveryStore();
        var coordinator = new PlaybackRecoveryCoordinator(store);
        var source = PreRecorded();
        await Next(source);
        await coordinator.CapturePreRecordedAsync(source, AudioCursor.Start);
        var preview = Assert.IsType<PlaybackRecoveryPreview>(await coordinator.CreatePreviewAsync());
        var invalid = preview with { State = preview.State with { Version = 999 } };
        var target = PreRecorded();

        Assert.Equal(OperationStatus.Failed,
            PlaybackRecoveryCoordinator.RestorePreRecordedAfterConfirmation(target, invalid).Status);
        Assert.Null(target.CurrentItem);
    }

    private static PreRecordedPlaybackPlanner PreRecorded() => new(
        new ProductDirectoryImport("memory-root", [new ImportedAudioProduct(1, "1", "platform-1", [
            new ImportedAudioGroup(1, "1/1", [new ImportedAudioClip("1/1/clip.wav",
                new LocalAudioAsset("memory-root/1/1/clip.wav", "wav", "prerecorded", TimeSpan.FromSeconds(10), 24000), null)])
        ], true)], []), PlanRevision.Initial, new SeededRandomSource(1234));

    private static TtsPlaybackPlanner ReadyTts(bool marker = false)
    {
        var document = new TtsScriptDocument("synthetic", "v1", [
            new TtsScriptSegment(0, "hello", marker ? new("product-1", "marker-1") : null),
            new TtsScriptSegment(1, "world", null)
        ]);
        var planner = new TtsPlaybackPlanner(document, Revision);
        var assets = document.Segments.Select(segment => new PreparedTtsSegment(segment,
            new LocalAudioAsset($"{segment.Index}.wav", "wav", "local", TimeSpan.FromSeconds(1), 48000),
            $"key-{segment.Index}")).ToArray();
        Assert.True(planner.ApplyPreparation(Revision, new(TtsPreparationState.Ready, assets, 2, null)).IsSuccess);
        return planner;
    }

    private static Task<PlaybackPlanItem?> Next(PreRecordedPlaybackPlanner planner) =>
        planner.SelectNextAsync(new(BasePlaybackMode.PreRecorded, planner.Revision,
            planner.CurrentItem?.ProductId, planner.CurrentItem?.GroupId, planner.CurrentItem?.Cycle ?? 0), default);

    private static Task<PlaybackPlanItem?> Next(TtsPlaybackPlanner planner) =>
        planner.SelectNextAsync(new(BasePlaybackMode.TtsScript, planner.Revision, null, null, 0), default);

    private sealed class MemoryRecoveryStore : IPlaybackRecoveryStore
    {
        private PlaybackRecoveryState? state;
        public Task<PlaybackRecoveryState?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(state);
        public Task<OperationResult> SaveAsync(PlaybackRecoveryState value, CancellationToken cancellationToken = default)
        {
            state = value;
            return Task.FromResult(OperationResult.Succeeded());
        }
    }
}
