using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Persistence;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class T080SqliteStateStoreTests
{
    private static readonly Guid SessionId = Guid.Parse("c81a773f-53e7-45d4-810c-24c8fa9f0d80");
    private static readonly Guid ReservationId = Guid.Parse("5b36e424-61e4-4780-a22b-9264bc57c680");
    private static readonly Guid ActionId = Guid.Parse("63446e30-3fb4-4fa1-9e75-39601bcdf080");
    private static readonly DateTimeOffset OffsetTime =
        new DateTimeOffset(2026, 9, 25, 9, 15, 30, TimeSpan.FromHours(7)).AddTicks(1234);

    [Fact]
    public void ConstructionDoesNotCreateDirectoriesOrDatabase()
    {
        using var scope = new StoreScope();

        _ = scope.Create();

        Assert.False(Directory.Exists(scope.ProjectRoot));
        Assert.False(File.Exists(scope.Paths.DatabasePath));
    }

    [Fact]
    public async Task NewDatabaseReturnsNullForEveryMissingRecord()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();

        Assert.True(File.Exists(scope.Paths.DatabasePath));
        await AssertEmptyAsync(store);
    }

    [Fact]
    public async Task RepeatedInitializationPreservesPreviouslySavedState()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var expected = Rules();
        Succeeded(await store.SaveRulesAsync(expected));

        Succeeded(await store.InitializeAsync());
        var reopened = await scope.OpenAsync();

        Assert.Equal(expected, await reopened.LoadRulesAsync(expected.RuleSetId));
    }

    [Fact]
    public async Task RuleMetadataUpsertsByIdentifierAndRoundTripsVietnameseAndUtc()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var first = new RuleSetSnapshot("bộ-quy-tắc-'đỏ'", 1, OffsetTime);
        var unrelated = new RuleSetSnapshot("bộ-quy-tắc-xanh", 5, OffsetTime);
        Succeeded(await store.SaveRulesAsync(first));
        Succeeded(await store.SaveRulesAsync(unrelated));
        var replacement = first with { Version = 2, UpdatedAtUtc = OffsetTime.AddMinutes(1) };
        Succeeded(await store.SaveRulesAsync(replacement));

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<RuleSetSnapshot>(await reopened.LoadRulesAsync(first.RuleSetId));

        Assert.Equal(replacement, loaded);
        Assert.Equal(TimeSpan.Zero, loaded.UpdatedAtUtc.Offset);
        Assert.Equal(unrelated, await reopened.LoadRulesAsync(unrelated.RuleSetId));
    }

    [Fact]
    public async Task PlanMetadataUpsertPreservesOtherPlanAndReopensWithModeAndRevision()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var plan = new PlaybackPlanSnapshot("kế-hoạch", BasePlaybackMode.PreRecorded, new PlanRevision(3), OffsetTime);
        var other = plan with { PlanId = "other-plan", Revision = new PlanRevision(99) };
        Succeeded(await store.SavePlanAsync(plan));
        Succeeded(await store.SavePlanAsync(other));
        var updated = plan with { Mode = BasePlaybackMode.TtsScript, Revision = new PlanRevision(4) };
        Succeeded(await store.SavePlanAsync(updated));

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<PlaybackPlanSnapshot>(await reopened.LoadPlanAsync(plan.PlanId));

        Assert.Equal(updated, loaded);
        Assert.Equal(TimeSpan.Zero, loaded.UpdatedAtUtc.Offset);
        Assert.Equal(other, await reopened.LoadPlanAsync(other.PlanId));
    }

    [Fact]
    public async Task SessionMetadataUpsertsAndRemainsIsolatedBySessionId()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var session = new SessionState(SessionId, "phòng-một", OffsetTime);
        var other = new SessionState(Guid.NewGuid(), "phòng-hai", OffsetTime.AddMinutes(1));
        Succeeded(await store.SaveSessionAsync(session));
        Succeeded(await store.SaveSessionAsync(other));
        var updated = session with { RoomId = "phòng-mới", StartedAtUtc = OffsetTime.AddMinutes(2) };
        Succeeded(await store.SaveSessionAsync(updated));

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<SessionState>(await reopened.LoadSessionAsync(SessionId));

        Assert.Equal(updated, loaded);
        Assert.Equal(TimeSpan.Zero, loaded.StartedAtUtc.Offset);
        Assert.Equal(other, await reopened.LoadSessionAsync(other.SessionId));
    }

    [Fact]
    public async Task EventFingerprintConflictPreservesFirstObservationAcrossReopen()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var first = new EventDeduplicationRecord("synthetic-fingerprint", OffsetTime, EventIdentityQuality.MissingEventId);
        Succeeded(await store.RecordEventAsync(first));
        Succeeded(await store.RecordEventAsync(first with
        {
            ReceivedAtUtc = OffsetTime.AddHours(1),
            Quality = EventIdentityQuality.Complete
        }));

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<EventDeduplicationRecord>(await reopened.FindEventAsync(first.Fingerprint));

        Assert.Equal(first, loaded);
        Assert.Equal(TimeSpan.Zero, loaded.ReceivedAtUtc.Offset);
    }

    [Fact]
    public async Task ReservationAndFirstCompletionAreDurableAndRepeatedCallsAreIdempotent()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var reservation = Reservation();
        Succeeded(await store.ReserveInteractionAsync(reservation));
        Succeeded(await store.ReserveInteractionAsync(reservation));
        var pending = Assert.IsType<StoredInteraction>(await store.LoadInteractionAsync(reservation.ReservationId));
        Assert.Equal(reservation, pending.Reservation);
        Assert.Null(pending.CompletedAtUtc);
        var completedAt = OffsetTime.AddMinutes(5);
        Succeeded(await store.CompleteInteractionAsync(reservation.ReservationId, completedAt));
        Succeeded(await store.CompleteInteractionAsync(reservation.ReservationId, completedAt.AddMinutes(10)));
        Succeeded(await store.ReserveInteractionAsync(reservation));

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<StoredInteraction>(await reopened.LoadInteractionAsync(reservation.ReservationId));

        Assert.Equal(reservation, loaded.Reservation);
        Assert.Equal(completedAt, loaded.CompletedAtUtc);
        Assert.Equal(TimeSpan.Zero, loaded.Reservation.ReservedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, loaded.CompletedAtUtc!.Value.Offset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReservationIdCannotBeReusedWithDifferentPayload(int changedField)
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var reservation = Reservation();
        Succeeded(await store.ReserveInteractionAsync(reservation));
        var conflict = changedField switch
        {
            0 => reservation with { EventFingerprint = "other-fingerprint" },
            1 => reservation with { ActionType = "Follow" },
            2 => reservation with { SessionId = Guid.NewGuid() },
            _ => reservation with { ReservedAtUtc = OffsetTime.AddSeconds(1) }
        };

        Assert.Equal(OperationStatus.Failed, (await store.ReserveInteractionAsync(conflict)).Status);

        var reopened = await scope.OpenAsync();
        Assert.Equal(reservation, (await reopened.LoadInteractionAsync(reservation.ReservationId))?.Reservation);
    }

    [Fact]
    public async Task ReservationBusinessKeyIsUniquePerSessionFingerprintAndActionType()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var original = Reservation();
        Succeeded(await store.ReserveInteractionAsync(original));
        var duplicate = original with { ReservationId = Guid.NewGuid() };

        Assert.Equal(OperationStatus.Failed, (await store.ReserveInteractionAsync(duplicate)).Status);
        Assert.Null(await store.LoadInteractionAsync(duplicate.ReservationId));
        var anotherAction = duplicate with { ActionType = "Follow" };
        Succeeded(await store.ReserveInteractionAsync(anotherAction));
        var anotherSession = duplicate with { ReservationId = Guid.NewGuid(), SessionId = Guid.NewGuid() };
        Succeeded(await store.ReserveInteractionAsync(anotherSession));

        var reopened = await scope.OpenAsync();
        Assert.Equal(original, (await reopened.LoadInteractionAsync(original.ReservationId))?.Reservation);
        Assert.Equal(anotherAction, (await reopened.LoadInteractionAsync(anotherAction.ReservationId))?.Reservation);
        Assert.Equal(anotherSession, (await reopened.LoadInteractionAsync(anotherSession.ReservationId))?.Reservation);
    }

    [Fact]
    public async Task CompletingUnknownReservationDoesNotCreateSyntheticCompletion()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();

        Assert.Equal(OperationStatus.Failed, (await store.CompleteInteractionAsync(ReservationId, OffsetTime)).Status);
        Assert.Null(await store.LoadInteractionAsync(ReservationId));
    }

    [Fact]
    public async Task CompletionBeforeReservationIsRejectedWithoutMarkingInteractionCompleted()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var reservation = Reservation();
        Succeeded(await store.ReserveInteractionAsync(reservation));

        Assert.Equal(OperationStatus.Failed,
            (await store.CompleteInteractionAsync(reservation.ReservationId, reservation.ReservedAtUtc.AddTicks(-1))).Status);

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<StoredInteraction>(await reopened.LoadInteractionAsync(reservation.ReservationId));
        Assert.Equal(reservation, loaded.Reservation);
        Assert.Null(loaded.CompletedAtUtc);
    }

    [Theory]
    [InlineData(BasePlaybackMode.PreRecorded)]
    [InlineData(BasePlaybackMode.TtsScript)]
    public async Task PlaybackCheckpointReopensWithExactSourceCursorAndConsumedMarkers(BasePlaybackMode mode)
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var checkpoint = Checkpoint(mode);
        Succeeded(await store.SavePlaybackCheckpointAsync(checkpoint));

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<PlaybackCheckpoint>(await reopened.LoadPlaybackCheckpointAsync(mode));

        EqualCheckpoint(checkpoint, loaded);
    }

    [Fact]
    public async Task CheckpointUpsertIsScopedByModeAndSupportsEmptyOptionalFields()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var original = Checkpoint(BasePlaybackMode.PreRecorded);
        var other = Checkpoint(BasePlaybackMode.TtsScript);
        Succeeded(await store.SavePlaybackCheckpointAsync(original));
        Succeeded(await store.SavePlaybackCheckpointAsync(other));
        var updated = original with
        {
            ProductId = null,
            GroupId = null,
            ClipId = null,
            Cursor = AudioCursor.Start,
            Cycle = 0,
            ConsumedMarkerIds = new HashSet<string>()
        };
        Succeeded(await store.SavePlaybackCheckpointAsync(updated));

        var reopened = await scope.OpenAsync();
        EqualCheckpoint(updated, Assert.IsType<PlaybackCheckpoint>(await reopened.LoadPlaybackCheckpointAsync(updated.Mode)));
        EqualCheckpoint(other, Assert.IsType<PlaybackCheckpoint>(await reopened.LoadPlaybackCheckpointAsync(other.Mode)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task InvalidCheckpointIdentifierCannotOverwritePreviousCheckpoint(int field, bool containsControl)
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var original = Checkpoint(BasePlaybackMode.PreRecorded);
        Succeeded(await store.SavePlaybackCheckpointAsync(original));
        var invalid = containsControl ? "synthetic\u0000identifier" : new string('x', 257);
        var modified = field switch
        {
            0 => original with { ProductId = invalid },
            1 => original with { GroupId = invalid },
            _ => original with { ClipId = invalid }
        };

        Assert.Equal(OperationStatus.Failed, (await store.SavePlaybackCheckpointAsync(modified)).Status);

        var reopened = await scope.OpenAsync();
        EqualCheckpoint(original, Assert.IsType<PlaybackCheckpoint>(await reopened.LoadPlaybackCheckpointAsync(original.Mode)));
    }

    [Fact]
    public async Task CheckpointIdentifiersAtExactMaximumLengthRoundTrip()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var checkpoint = Checkpoint(BasePlaybackMode.PreRecorded) with
        {
            ProductId = new string('p', 256),
            GroupId = new string('g', 256),
            ClipId = new string('c', 256)
        };

        Succeeded(await store.SavePlaybackCheckpointAsync(checkpoint));

        var reopened = await scope.OpenAsync();
        EqualCheckpoint(checkpoint, Assert.IsType<PlaybackCheckpoint>(await reopened.LoadPlaybackCheckpointAsync(checkpoint.Mode)));
    }

    [Fact]
    public async Task CacheMetadataRoundTripDoesNotRequireOrCreateAudioFiles()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var metadata = Cache(scope.Paths);
        Assert.False(File.Exists(metadata.Path));
        Succeeded(await store.SaveAudioCacheMetadataAsync(metadata));

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<AudioCacheMetadata>(await reopened.LoadAudioCacheMetadataAsync(metadata.CacheKey));

        Assert.Equal(metadata, loaded);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAtUtc.Offset);
        Assert.False(File.Exists(metadata.Path));
        Assert.Empty(Directory.EnumerateFiles(scope.Paths.CacheDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CacheMetadataUpsertReplacesPathAndEngineRevisionOnlyForMatchingKey()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var original = Cache(scope.Paths);
        var other = original with { CacheKey = "other-cache-key" };
        Succeeded(await store.SaveAudioCacheMetadataAsync(original));
        Succeeded(await store.SaveAudioCacheMetadataAsync(other));
        var updated = original with
        {
            Path = Path.Combine(scope.Paths.CacheDirectory, "updated.wav"),
            EngineId = "synthetic-engine-two",
            EngineRevision = new EngineRevision(8),
            CreatedAtUtc = OffsetTime.AddMinutes(1)
        };
        Succeeded(await store.SaveAudioCacheMetadataAsync(updated));

        var reopened = await scope.OpenAsync();
        Assert.Equal(updated, await reopened.LoadAudioCacheMetadataAsync(original.CacheKey));
        Assert.Equal(other, await reopened.LoadAudioCacheMetadataAsync(other.CacheKey));
        Assert.Empty(Directory.EnumerateFiles(scope.Paths.CacheDirectory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CacheMetadataRejectsRelativeTraversalAndOutsideCachePaths(int invalidPathKind)
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var invalidPath = invalidPathKind switch
        {
            0 => "relative.wav",
            1 => Path.Combine(scope.Paths.RootDirectory, "outside-cache.wav"),
            2 => Path.Combine(scope.Paths.CacheDirectory, "..", "escape.wav"),
            _ => Path.Combine(scope.Paths.RootDirectory, "cache-sibling", "outside.wav")
        };
        var metadata = Cache(scope.Paths) with { Path = invalidPath };

        Assert.Equal(OperationStatus.Failed, (await store.SaveAudioCacheMetadataAsync(metadata)).Status);

        Assert.Null(await store.LoadAudioCacheMetadataAsync(metadata.CacheKey));
        Assert.Empty(Directory.EnumerateFiles(scope.Paths.CacheDirectory, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(ControlActionStatus.Confirmed)]
    [InlineData(ControlActionStatus.Rejected)]
    [InlineData(ControlActionStatus.Unknown)]
    [InlineData(ControlActionStatus.Unsupported)]
    [InlineData(ControlActionStatus.Cancelled)]
    public async Task ActionLedgerRetainsOutcomeWithoutPromotingUnknownAfterReopen(ControlActionStatus status)
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var entry = Ledger() with { Status = status };
        Succeeded(await store.AppendActionLedgerAsync(entry));
        Succeeded(await store.AppendActionLedgerAsync(entry));

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<ActionLedgerEntry>(await reopened.LoadActionLedgerAsync(entry.ActionId));

        Assert.Equal(entry, loaded);
        Assert.Equal(status, loaded.Status);
        Assert.Equal(TimeSpan.Zero, loaded.RecordedAtUtc.Offset);
    }

    [Fact]
    public async Task ActionIdConflictCannotOverwritePreviouslyRecordedOutcome()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var original = Ledger();
        Succeeded(await store.AppendActionLedgerAsync(original));

        Assert.Equal(OperationStatus.Failed,
            (await store.AppendActionLedgerAsync(original with { Status = ControlActionStatus.Confirmed })).Status);

        var reopened = await scope.OpenAsync();
        Assert.Equal(original, await reopened.LoadActionLedgerAsync(original.ActionId));
    }

    [Fact]
    public async Task BatchConflictRollsBackEarlierNewEntriesAndPreservesOriginalRecord()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var original = Ledger();
        Succeeded(await store.AppendActionLedgerAsync(original));
        var beforeConflict = original with { ActionId = Guid.NewGuid(), Status = ControlActionStatus.Confirmed };
        var afterConflict = original with { ActionId = Guid.NewGuid(), Status = ControlActionStatus.Rejected };
        var conflicting = original with { Detail = "different synthetic detail" };

        Assert.Equal(OperationStatus.Failed,
            (await store.AppendActionLedgerBatchAsync([beforeConflict, conflicting, afterConflict])).Status);

        var reopened = await scope.OpenAsync();
        Assert.Null(await reopened.LoadActionLedgerAsync(beforeConflict.ActionId));
        Assert.Null(await reopened.LoadActionLedgerAsync(afterConflict.ActionId));
        Assert.Equal(original, await reopened.LoadActionLedgerAsync(original.ActionId));
    }

    [Fact]
    public async Task RepeatedBatchAndIdenticalDuplicateWithinBatchAreIdempotent()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var first = Ledger();
        var second = first with { ActionId = Guid.NewGuid(), Status = ControlActionStatus.Confirmed };
        ActionLedgerEntry[] batch = [first, first, second];

        Succeeded(await store.AppendActionLedgerBatchAsync(batch));
        Succeeded(await store.AppendActionLedgerBatchAsync(batch));

        var reopened = await scope.OpenAsync();
        Assert.Equal(first, await reopened.LoadActionLedgerAsync(first.ActionId));
        Assert.Equal(second, await reopened.LoadActionLedgerAsync(second.ActionId));
    }

    [Fact]
    public async Task ConflictingNewActionIdWithinOneBatchRollsBackTheEntireNewRecord()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var first = Ledger();
        var conflicting = first with { Status = ControlActionStatus.Confirmed };

        Assert.Equal(OperationStatus.Failed, (await store.AppendActionLedgerBatchAsync([first, conflicting])).Status);

        var reopened = await scope.OpenAsync();
        Assert.Null(await reopened.LoadActionLedgerAsync(first.ActionId));
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public async Task ActionLedgerBatchHasAnExplicitMaximumAndOversizeWritesNothing(int count, bool accepted)
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var batch = Enumerable.Range(0, count).Select(_ => Ledger() with { ActionId = Guid.NewGuid() }).ToArray();

        var result = await store.AppendActionLedgerBatchAsync(batch);

        Assert.Equal(accepted, result.IsSuccess);
        if (accepted)
        {
            Assert.Equal(batch[0], await store.LoadActionLedgerAsync(batch[0].ActionId));
            Assert.Equal(batch[^1], await store.LoadActionLedgerAsync(batch[^1].ActionId));
        }
        else
        {
            Assert.Null(await store.LoadActionLedgerAsync(batch[0].ActionId));
            Assert.Null(await store.LoadActionLedgerAsync(batch[^1].ActionId));
        }
    }

    [Theory]
    [InlineData(LocalDocumentKind.Settings)]
    [InlineData(LocalDocumentKind.Rules)]
    [InlineData(LocalDocumentKind.MediaIndex)]
    [InlineData(LocalDocumentKind.ShuffleBag)]
    [InlineData(LocalDocumentKind.ProductState)]
    public async Task VersionedJsonDocumentsUpsertAndRoundTripWithUnicode(LocalDocumentKind kind)
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var original = Document() with { Kind = kind };
        Succeeded(await store.SaveDocumentAsync(original));
        var updated = original with
        {
            Version = 2,
            Json = "{\"title\":\"Chào mừng Việt Nam\",\"items\":[\"đỏ\",\"xanh\"]}",
            UpdatedAtUtc = OffsetTime.AddSeconds(1)
        };
        Succeeded(await store.SaveDocumentAsync(updated));

        var reopened = await scope.OpenAsync();
        var loaded = Assert.IsType<LocalStateDocument>(await reopened.LoadDocumentAsync(kind, original.DocumentId));

        Assert.Equal(updated, loaded);
        Assert.Equal(TimeSpan.Zero, loaded.UpdatedAtUtc.Offset);
    }

    [Fact]
    public async Task DocumentIdentifierIsScopedByKind()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        var documents = Enum.GetValues<LocalDocumentKind>()
            .Select(kind => Document() with { Kind = kind, Json = $"{{\"kind\":\"{kind}\"}}" }).ToArray();
        foreach (var document in documents) Succeeded(await store.SaveDocumentAsync(document));

        var reopened = await scope.OpenAsync();

        foreach (var document in documents)
            Assert.Equal(document, await reopened.LoadDocumentAsync(document.Kind, document.DocumentId));
    }

    [Fact]
    public async Task CancelledInitializationCreatesNoFilesAndAllowsLaterNormalInitialization()
    {
        using var scope = new StoreScope();
        var store = scope.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(OperationStatus.Cancelled, (await store.InitializeAsync(cancellation.Token)).Status);
        Assert.False(Directory.Exists(scope.ProjectRoot));
        Succeeded(await store.InitializeAsync());
        await AssertEmptyAsync(store);
    }

    [Fact]
    public async Task PreCancelledWritesReturnCancelledAcrossEveryPortAndLeaveDatabaseEmpty()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        foreach (var write in WriteOperations(store, scope.Paths))
            Assert.Equal(OperationStatus.Cancelled, (await write(cancellation.Token)).Status);

        var reopened = await scope.OpenAsync();
        await AssertEmptyAsync(reopened);
    }

    [Fact]
    public async Task PreCancelledReadsThrowCancellationAcrossEveryLoadPort()
    {
        using var scope = new StoreScope();
        var store = await scope.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        foreach (var read in ReadOperations())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read(store, cancellation.Token));
    }

    [Fact]
    public async Task WritesBeforeInitializationFailWithoutCreatingDatabase()
    {
        using var scope = new StoreScope();
        var store = scope.Create();

        foreach (var write in WriteOperations(store, scope.Paths))
            Assert.Equal(OperationStatus.Failed, (await write(CancellationToken.None)).Status);

        Assert.False(Directory.Exists(scope.ProjectRoot));
        Assert.False(File.Exists(scope.Paths.DatabasePath));
    }

    [Fact]
    public async Task ReadsBeforeInitializationThrowInsteadOfPretendingRecordsAreMissing()
    {
        using var scope = new StoreScope();
        var store = scope.Create();

        foreach (var read in ReadOperations())
            await Assert.ThrowsAsync<InvalidOperationException>(() => read(store, CancellationToken.None));

        Assert.False(Directory.Exists(scope.ProjectRoot));
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(31, 1024)]
    [InlineData(1, 0)]
    [InlineData(1, 16777217)]
    public void InvalidStoreLimitsAreRejectedWithoutFilesystemSideEffects(int timeout, int payloadBytes)
    {
        using var scope = new StoreScope();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SqliteStateStore(scope.Paths, new SqliteStateStoreOptions(timeout, payloadBytes)));

        Assert.False(Directory.Exists(scope.ProjectRoot));
    }

    private static RuleSetSnapshot Rules() => new("synthetic-rules", 1, OffsetTime);
    private static PlaybackPlanSnapshot Plan() => new("synthetic-plan", BasePlaybackMode.PreRecorded, new PlanRevision(7), OffsetTime);
    private static InteractionReservation Reservation() => new(ReservationId, "synthetic-event", "Welcome", SessionId, OffsetTime);
    private static AudioCacheMetadata Cache(LocalDataPaths paths) => new("synthetic-cache", Path.Combine(paths.CacheDirectory, "synthetic.wav"),
        "synthetic-engine", new EngineRevision(7), OffsetTime);
    private static ActionLedgerEntry Ledger() => new(ActionId, SessionId, "Text:Manual", ControlActionStatus.Unknown, OffsetTime, "Synthetic outcome unknown.");
    private static LocalStateDocument Document() => new(LocalDocumentKind.Settings, "synthetic-document", 1,
        "{\"title\":\"Tiếng Việt\",\"enabled\":true}", OffsetTime);

    private static PlaybackCheckpoint Checkpoint(BasePlaybackMode mode) => new(mode, new PlanRevision(7), "sản-phẩm", "nhóm", "đoạn",
        new AudioCursor(12_345_678_901, 48000), 4, 1234567, new HashSet<string> { "marker-a", "đã-dùng" });

    private static void EqualCheckpoint(PlaybackCheckpoint expected, PlaybackCheckpoint actual)
    {
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.PlanRevision, actual.PlanRevision);
        Assert.Equal(expected.ProductId, actual.ProductId);
        Assert.Equal(expected.GroupId, actual.GroupId);
        Assert.Equal(expected.ClipId, actual.ClipId);
        Assert.Equal(expected.Cursor, actual.Cursor);
        Assert.Equal(expected.Cycle, actual.Cycle);
        Assert.Equal(expected.EffectSeed, actual.EffectSeed);
        Assert.True(expected.ConsumedMarkerIds.SetEquals(actual.ConsumedMarkerIds));
    }

    private static void Succeeded(OperationResult result) => Assert.True(result.IsSuccess, result.Detail);

    private static async Task AssertEmptyAsync(SqliteStateStore store)
    {
        Assert.Null(await store.LoadRulesAsync(Rules().RuleSetId));
        Assert.Null(await store.LoadPlanAsync(Plan().PlanId));
        Assert.Null(await store.LoadSessionAsync(SessionId));
        Assert.Null(await store.FindEventAsync("synthetic-event"));
        Assert.Null(await store.LoadInteractionAsync(ReservationId));
        Assert.Null(await store.LoadPlaybackCheckpointAsync(BasePlaybackMode.PreRecorded));
        Assert.Null(await store.LoadAudioCacheMetadataAsync("synthetic-cache"));
        Assert.Null(await store.LoadActionLedgerAsync(ActionId));
        Assert.Null(await store.LoadDocumentAsync(LocalDocumentKind.Settings, "synthetic-document"));
    }

    private static IEnumerable<Func<CancellationToken, Task<OperationResult>>> WriteOperations(SqliteStateStore store, LocalDataPaths paths) =>
    [
        token => store.SaveRulesAsync(Rules(), token),
        token => store.SavePlanAsync(Plan(), token),
        token => store.SaveSessionAsync(new SessionState(SessionId, "synthetic-room", OffsetTime), token),
        token => store.RecordEventAsync(new EventDeduplicationRecord("synthetic-event", OffsetTime, EventIdentityQuality.Complete), token),
        token => store.ReserveInteractionAsync(Reservation(), token),
        token => store.CompleteInteractionAsync(ReservationId, OffsetTime.AddMinutes(1), token),
        token => store.SavePlaybackCheckpointAsync(Checkpoint(BasePlaybackMode.PreRecorded), token),
        token => store.SaveAudioCacheMetadataAsync(Cache(paths), token),
        token => store.AppendActionLedgerAsync(Ledger(), token),
        token => store.AppendActionLedgerBatchAsync([Ledger()], token),
        token => store.SaveDocumentAsync(Document(), token)
    ];

    private static IEnumerable<Func<SqliteStateStore, CancellationToken, Task>> ReadOperations() =>
    [
        (store, token) => store.LoadRulesAsync(Rules().RuleSetId, token),
        (store, token) => store.LoadPlanAsync(Plan().PlanId, token),
        (store, token) => store.LoadSessionAsync(SessionId, token),
        (store, token) => store.FindEventAsync("synthetic-event", token),
        (store, token) => store.LoadInteractionAsync(ReservationId, token),
        (store, token) => store.LoadPlaybackCheckpointAsync(BasePlaybackMode.PreRecorded, token),
        (store, token) => store.LoadAudioCacheMetadataAsync("synthetic-cache", token),
        (store, token) => store.LoadActionLedgerAsync(ActionId, token),
        (store, token) => store.LoadDocumentAsync(LocalDocumentKind.Settings, "synthetic-document", token)
    ];

    private sealed class StoreScope : IDisposable
    {
        private readonly string _directoryName = "t080-" + Guid.NewGuid().ToString("N");
        private readonly string _temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string ProjectRoot { get; }
        public LocalDataPaths Paths { get; }

        public StoreScope()
        {
            ProjectRoot = Path.GetFullPath(Path.Combine(_temporaryRoot, _directoryName));
            Paths = LocalDataPaths.ForDevelopment(ProjectRoot);
        }

        public SqliteStateStore Create() => new(Paths, new SqliteStateStoreOptions(1, 1024 * 1024));

        public async Task<SqliteStateStore> OpenAsync()
        {
            var store = Create();
            Succeeded(await store.InitializeAsync());
            return store;
        }

        public void Dispose()
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var target = Path.GetFullPath(ProjectRoot);
            if (!string.Equals(Path.GetDirectoryName(target), _temporaryRoot, comparison) ||
                !string.Equals(Path.GetFileName(target), _directoryName, StringComparison.Ordinal) ||
                !Guid.TryParseExact(_directoryName[5..], "N", out _))
                throw new InvalidOperationException("Cleanup target is not this test's temporary GUID directory.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
