using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Events;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class T060EventDeduplicationTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SameStrictEventIdIsAcceptedOnceAndDuplicateAfterReplay()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var liveEvent = Event(LiveEventType.Comment, "event-1", content: "Xin chào");

        var first = await coordinator.ProcessAsync(liveEvent);
        var replay = await coordinator.ProcessAsync(liveEvent);

        Assert.Equal(EventProcessStatus.Accepted, first.Status);
        Assert.Equal(EventIdentityQuality.Complete, first.IdentityQuality);
        Assert.Equal(EventProcessStatus.Duplicate, replay.Status);
        Assert.Equal(first.Fingerprint, replay.Fingerprint);
        Assert.Single(store.Events);
        Assert.Equal(1, store.RecordEventCalls);
    }

    [Fact]
    public async Task StrictFingerprintSeparatesRoomAndEventType()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var sameId = Event(LiveEventType.Comment, "event-1");

        var same = await coordinator.RegisterEventAsync(sameId);
        var otherRoom = await coordinator.RegisterEventAsync(sameId with { RoomId = "room-2" });
        var otherType = await coordinator.RegisterEventAsync(sameId with { EventType = LiveEventType.Like });

        Assert.Equal(EventDeduplicationStatus.Accepted, same.Status);
        Assert.Equal(EventDeduplicationStatus.Accepted, otherRoom.Status);
        Assert.Equal(EventDeduplicationStatus.Accepted, otherType.Status);
        Assert.Equal(3, store.Events.Count);
    }

    [Fact]
    public async Task MissingEventIdUsesDegradedShortTermFingerprint()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var withoutId = Event(LiveEventType.Comment, eventId: null, content: "  Xin   chào  ");

        var first = await coordinator.ProcessAsync(withoutId);
        var replay = await coordinator.ProcessAsync(withoutId with { ReceivedAt = ReceivedAt.AddMilliseconds(500) });
        var changedContent = await coordinator.ProcessAsync(withoutId with { Content = "Câu hỏi khác" });

        Assert.Equal(EventProcessStatus.Accepted, first.Status);
        Assert.Equal(EventIdentityQuality.MissingEventId, first.IdentityQuality);
        Assert.Equal(EventProcessStatus.Duplicate, replay.Status);
        Assert.Equal(EventProcessStatus.Accepted, changedContent.Status);
        Assert.Equal(2, store.Events.Count);
        Assert.NotEqual(first.Fingerprint, changedContent.Fingerprint);
    }

    [Fact]
    public async Task MissingEventIdAndUserIdAreExplicitlyDegraded()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var withoutIdentity = Event(LiveEventType.Comment, eventId: null, userId: null, content: "hello");

        var result = await coordinator.ProcessAsync(withoutIdentity);

        Assert.Equal(EventProcessStatus.Accepted, result.Status);
        Assert.Equal(EventIdentityQuality.Incomplete, result.IdentityQuality);
        Assert.True(result.Fingerprint.Length > 0);
    }

    [Fact]
    public async Task MissingUserIdSkipsTargetedInteractionWithoutReservation()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var follow = Event(LiveEventType.Follow, "follow-1", userId: null, displayName: "same-looking-name");

        var result = await coordinator.ProcessAsync(follow);

        Assert.Equal(EventProcessStatus.SkippedMissingUserId, result.Status);
        Assert.Null(result.ReservationId);
        Assert.Equal(0, store.ReserveInteractionCalls);
        Assert.Empty(store.Reservations);
    }

    [Fact]
    public async Task ReservedBecomesCompletedOnlyAfterSuccessfulCompletion()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var follow = Event(LiveEventType.Follow, "follow-1");

        var reserved = await coordinator.ProcessAsync(follow);
        Assert.Equal(EventProcessStatus.Reserved, reserved.Status);
        Assert.NotNull(reserved.ReservationId);
        Assert.Equal(InteractionReservationStatus.Reserved,
            coordinator.GetInteractionStatus(EventDeduplicationCoordinator.BuildBusinessFingerprint(follow, "Follow")));

        var completion = await coordinator.CompleteAsync(reserved.ReservationId!.Value);

        Assert.Equal(OperationStatus.Succeeded, completion.Status);
        Assert.Equal(InteractionReservationStatus.Completed,
            coordinator.GetInteractionStatus(EventDeduplicationCoordinator.BuildBusinessFingerprint(follow, "Follow")));
        Assert.Single(store.CompletedReservationIds);
    }

    [Fact]
    public async Task FailedCompletionDoesNotMarkReservationCompleted()
    {
        var store = new InMemoryStateStore { CompleteStatus = OperationStatus.Failed };
        var coordinator = new EventDeduplicationCoordinator(store);
        var follow = Event(LiveEventType.Follow, "follow-1");

        var reserved = await coordinator.ProcessAsync(follow);
        var completion = await coordinator.CompleteAsync(reserved.ReservationId!.Value);

        Assert.Equal(OperationStatus.Failed, completion.Status);
        Assert.Equal(InteractionReservationStatus.Reserved,
            coordinator.GetInteractionStatus(EventDeduplicationCoordinator.BuildBusinessFingerprint(follow, "Follow")));
        Assert.Empty(store.CompletedReservationIds);
    }

    [Fact]
    public async Task CancelledReservationIsNotCompletedAndDoesNotRetryAutomatically()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var follow = Event(LiveEventType.Follow, "follow-1");

        var reserved = await coordinator.ProcessAsync(follow);
        var cancelled = await coordinator.CancelAsync(reserved.ReservationId!.Value);
        var retryWithNewEventId = await coordinator.ProcessAsync(follow with { EventId = "follow-2" });

        Assert.Equal(OperationStatus.Cancelled, cancelled.Status);
        Assert.Equal(EventProcessStatus.Duplicate, retryWithNewEventId.Status);
        Assert.Equal(InteractionReservationStatus.Reserved,
            coordinator.GetInteractionStatus(EventDeduplicationCoordinator.BuildBusinessFingerprint(follow, "Follow")));
        Assert.Empty(store.CompletedReservationIds);
    }

    [Fact]
    public async Task WelcomeAndFollowHaveIndependentPerSessionUserReservations()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var enter = Event(LiveEventType.Enter, "enter-1");
        var follow = Event(LiveEventType.Follow, "follow-1");

        var welcome = await coordinator.ProcessAsync(enter);
        var followResult = await coordinator.ProcessAsync(follow);
        var repeatedWelcome = await coordinator.ProcessAsync(enter with { EventId = "enter-2" });
        var repeatedFollow = await coordinator.ProcessAsync(follow with { EventId = "follow-2" });

        Assert.Equal(EventProcessStatus.Reserved, welcome.Status);
        Assert.Equal(EventProcessStatus.Reserved, followResult.Status);
        Assert.Equal(EventProcessStatus.Duplicate, repeatedWelcome.Status);
        Assert.Equal(EventProcessStatus.Duplicate, repeatedFollow.Status);
        Assert.Equal(2, store.Reservations.Count);
    }

    [Fact]
    public async Task ReconnectPreservesSessionAndEventDeduplication()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var session = Guid.NewGuid();
        var request = new LiveConnectionRequest(session, "room-1");
        var liveEvent = Event(LiveEventType.Comment, "comment-1", sessionId: session);

        Assert.Equal(OperationStatus.Succeeded, (await coordinator.ReconnectAsync(request)).Status);
        Assert.Equal(EventProcessStatus.Accepted, (await coordinator.ProcessAsync(liveEvent)).Status);
        Assert.Equal(OperationStatus.Succeeded, (await coordinator.ReconnectAsync(request)).Status);
        var replay = await coordinator.ProcessAsync(liveEvent);
        var differentSession = await coordinator.ReconnectAsync(new LiveConnectionRequest(Guid.NewGuid(), "room-1"));

        Assert.Equal(EventProcessStatus.Duplicate, replay.Status);
        Assert.Equal(OperationStatus.Failed, differentSession.Status);
        Assert.Equal(session, Assert.Single(store.Sessions).Key);
        Assert.Equal(1, store.RecordEventCalls);
    }

    [Fact]
    public async Task ConcurrentRegistrationOfOneEventDoesNotRecordItTwice()
    {
        var store = new InMemoryStateStore();
        var coordinator = new EventDeduplicationCoordinator(store);
        var liveEvent = Event(LiveEventType.Comment, "comment-1");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => coordinator.RegisterEventAsync(liveEvent)));

        Assert.Single(results, result => result.Status == EventDeduplicationStatus.Accepted);
        Assert.Equal(7, results.Count(result => result.Status == EventDeduplicationStatus.Duplicate));
        Assert.Equal(1, store.RecordEventCalls);
    }

    private static LiveEvent Event(
        LiveEventType eventType,
        string? eventId,
        string? userId = "user-1",
        string? displayName = "Nguyễn",
        string? content = "hello",
        Guid? sessionId = null,
        string roomId = "room-1")
    {
        return new LiveEvent(
            SourceId: "source-1",
            SessionId: sessionId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RoomId: roomId,
            EventType: eventType,
            EventId: eventId,
            UserId: userId,
            DisplayName: displayName,
            OccurredAt: ReceivedAt,
            ReceivedAt: ReceivedAt,
            Content: content,
            LikeCountDelta: null,
            LikeCountTotal: null,
            IdentityQuality: EventIdentityQuality.Complete);
    }

    private sealed class InMemoryStateStore : IStateStore
    {
        public Dictionary<string, EventDeduplicationRecord> Events { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, SessionState> Sessions { get; } = [];
        public Dictionary<Guid, InteractionReservation> Reservations { get; } = [];
        public HashSet<Guid> CompletedReservationIds { get; } = [];
        public int RecordEventCalls { get; private set; }
        public int ReserveInteractionCalls { get; private set; }
        public OperationStatus ReserveStatus { get; init; } = OperationStatus.Succeeded;
        public OperationStatus CompleteStatus { get; init; } = OperationStatus.Succeeded;

        public Task<RuleSetSnapshot?> LoadRulesAsync(string ruleSetId, CancellationToken cancellationToken) =>
            Task.FromResult<RuleSetSnapshot?>(null);

        public Task<OperationResult> SaveRulesAsync(RuleSetSnapshot rules, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Succeeded());

        public Task<PlaybackPlanSnapshot?> LoadPlanAsync(string planId, CancellationToken cancellationToken) =>
            Task.FromResult<PlaybackPlanSnapshot?>(null);

        public Task<OperationResult> SavePlanAsync(PlaybackPlanSnapshot plan, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Succeeded());

        public Task<SessionState?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.TryGetValue(sessionId, out var session) ? session : null);

        public Task<OperationResult> SaveSessionAsync(SessionState session, CancellationToken cancellationToken)
        {
            Sessions[session.SessionId] = session;
            return Task.FromResult(OperationResult.Succeeded());
        }

        public Task<EventDeduplicationRecord?> FindEventAsync(string fingerprint, CancellationToken cancellationToken) =>
            Task.FromResult(Events.TryGetValue(fingerprint, out var record) ? record : null);

        public Task<OperationResult> RecordEventAsync(EventDeduplicationRecord record, CancellationToken cancellationToken)
        {
            RecordEventCalls++;
            if (Events.ContainsKey(record.Fingerprint))
                return Task.FromResult(OperationResult.Failed("duplicate"));
            Events[record.Fingerprint] = record;
            return Task.FromResult(new OperationResult(ReserveStatus == OperationStatus.Succeeded
                ? OperationStatus.Succeeded
                : ReserveStatus, "recorded"));
        }

        public Task<OperationResult> ReserveInteractionAsync(InteractionReservation reservation, CancellationToken cancellationToken)
        {
            ReserveInteractionCalls++;
            if (ReserveStatus != OperationStatus.Succeeded)
                return Task.FromResult(new OperationResult(ReserveStatus, "reservation test result"));
            Reservations[reservation.ReservationId] = reservation;
            return Task.FromResult(OperationResult.Succeeded());
        }

        public Task<OperationResult> CompleteInteractionAsync(Guid reservationId, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
        {
            if (CompleteStatus == OperationStatus.Succeeded)
                CompletedReservationIds.Add(reservationId);
            return Task.FromResult(new OperationResult(CompleteStatus, "completion test result"));
        }

        public Task<OperationResult> SavePlaybackCheckpointAsync(PlaybackCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Succeeded());

        public Task<OperationResult> SaveAudioCacheMetadataAsync(AudioCacheMetadata metadata, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Succeeded());

        public Task<OperationResult> AppendActionLedgerAsync(ActionLedgerEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Succeeded());
    }
}