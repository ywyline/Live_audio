using Xunit;
﻿using System.Text.Json;
using TikTokAudio.Application.Playback;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Persistence;

namespace TikTokAudio.Integration.Tests;

public sealed class T081RecoveryTests
{
    [Fact]
    public async Task SqliteRecoveryStateRoundTripsCheckpointShuffleBagAndEngineConfiguration()
    {
        using var scope = new StoreScope();
        var firstStore = await scope.OpenAsync();
        var recovery = new SqlitePlaybackRecoveryStore(firstStore);
        var state = State();

        Assert.Equal(OperationStatus.Succeeded, (await recovery.SaveAsync(state)).Status);
        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<PlaybackRecoveryState>(await new SqlitePlaybackRecoveryStore(reopened).LoadAsync());

        Assert.Equal(state.Version, loaded.Version);
        Assert.Equal(state.Mode, loaded.Mode);
        Assert.Equal(state.CapturedAtUtc, loaded.CapturedAtUtc);
        Assert.Equal(state.PlanRevision, loaded.PlanRevision);
        Assert.Equal(state.Checkpoint.Mode, loaded.Checkpoint.Mode);
        Assert.Equal(state.Checkpoint.PlanRevision, loaded.Checkpoint.PlanRevision);
        Assert.Equal(state.Checkpoint.ProductId, loaded.Checkpoint.ProductId);
        Assert.Equal(state.Checkpoint.GroupId, loaded.Checkpoint.GroupId);
        Assert.Equal(state.Checkpoint.ClipId, loaded.Checkpoint.ClipId);
        Assert.Equal(state.Checkpoint.Cursor, loaded.Checkpoint.Cursor);
        Assert.Equal(state.Checkpoint.Cycle, loaded.Checkpoint.Cycle);
        Assert.Equal(state.Checkpoint.EffectSeed, loaded.Checkpoint.EffectSeed);
        Assert.True(loaded.Checkpoint.ConsumedMarkerIds.SetEquals(state.Checkpoint.ConsumedMarkerIds));
        Assert.Equal(state.EngineId, loaded.EngineId);
        Assert.Equal(state.EngineRevision, loaded.EngineRevision);
        Assert.Equal(state.DefaultVoiceId, loaded.DefaultVoiceId);
        var bag = Assert.Single(loaded.PreRecordedSnapshot!.Bags);
        Assert.Equal("1/1", bag.GroupId);
        Assert.Equal(["clip-b"], bag.RemainingClipIds);
        Assert.Equal("clip-a", bag.LastClipId);
        Assert.Equal((ulong)77, bag.RandomState);
    }

    [Fact]
    public async Task CorruptOrFutureRecoveryDocumentIsRejectedWithoutPlannerSideEffects()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var recovery = new SqlitePlaybackRecoveryStore(store);
        var json = JsonSerializer.Serialize(new { Version = 999 });
        Assert.Equal(OperationStatus.Succeeded,
            (await store.SaveDocumentAsync(new LocalStateDocument(LocalDocumentKind.ShuffleBag,
                SqlitePlaybackRecoveryStore.DocumentId, 999, json, DateTimeOffset.UtcNow))).Status);

        await Assert.ThrowsAsync<InvalidDataException>(() => recovery.LoadAsync());

        Assert.Equal(OperationStatus.Succeeded,
            (await store.SaveDocumentAsync(new LocalStateDocument(LocalDocumentKind.ShuffleBag,
                SqlitePlaybackRecoveryStore.DocumentId, 1, "{}", DateTimeOffset.UtcNow))).Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => recovery.LoadAsync());
    }

    [Fact]
    public async Task InvalidStateIsRejectedBeforeItReachesSqlite()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var recovery = new SqlitePlaybackRecoveryStore(store);
        var invalid = State() with { Version = 999 };

        Assert.Equal(OperationStatus.Failed, (await recovery.SaveAsync(invalid)).Status);
        Assert.Null(await store.LoadDocumentAsync(LocalDocumentKind.ShuffleBag,
            SqlitePlaybackRecoveryStore.DocumentId));
    }

    private static PlaybackRecoveryState State()
    {
        var checkpoint = new PlaybackCheckpoint(BasePlaybackMode.PreRecorded, new PlanRevision(4),
            "product-1", "1/1", "clip-a", new AudioCursor(1234, 24000), 2, 91,
            new HashSet<string>(["marker-1"], StringComparer.Ordinal));
        var snapshot = new PreRecordedPlannerSnapshot("catalog-fingerprint", checkpoint,
            [new PlannerBagSnapshot("1/1", ["clip-b"], "clip-a", 77)], 55);
        return new PlaybackRecoveryState(PlaybackRecoveryState.CurrentVersion,
            DateTimeOffset.Parse("2026-09-25T08:00:00Z"), BasePlaybackMode.PreRecorded, checkpoint.PlanRevision,
            checkpoint, snapshot, "local", new EngineRevision(12), "voice-vn");
    }

    private sealed class StoreScope : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "TikTokAudio-T081-" + Guid.NewGuid().ToString("N"));
        public LocalDataPaths Paths => LocalDataPaths.ForDevelopment(root);

        public async Task<SqliteStateStore> OpenAsync()
        {
            var store = new SqliteStateStore(Paths, new SqliteStateStoreOptions(1, 1024 * 1024));
            Assert.Equal(OperationStatus.Succeeded, (await store.InitializeAsync()).Status);
            return store;
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
