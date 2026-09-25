using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Control;
using TikTokAudio.Application.Events;
using TikTokAudio.Application.Interactions;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class T073TextDispatchRaceTests
{
    private static readonly Guid Session = Guid.Parse("e1412036-1e97-441b-8f91-62e15a63ecce");
    private static readonly TimeSpan LongTtl = TimeSpan.FromMinutes(3);
    private static TextDispatchOptions Options() => new(8, 8, 8, TimeSpan.FromSeconds(5), TimeSpan.Zero);

    [Fact]
    public async Task TimeoutKeepsPhysicalLaneAndRecordsExactlyOneUnknownEvenAfterDrain()
    {
        var clock = new FakeClock();
        var controller = new DeferredController();
        using var dispatch = Create(controller, clock, Options() with { LedgerCapacity = 1 });
        dispatch.QueueManual("first", LongTtl);
        dispatch.QueueManual("second", LongTtl);
        await dispatch.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var timeout = await dispatch.PumpAsync();
        Assert.True(timeout.HasInFlightOperation);
        Assert.Equal(RoomControlState.NeedsConfirmation, timeout.State);
        Assert.True(controller.Calls[0].Token.IsCancellationRequested);
        var first = Assert.Single(dispatch.DrainLedger());
        Assert.Equal(ControlActionStatus.Unknown, first.Status);
        clock.Advance(TimeSpan.FromSeconds(30));
        await dispatch.PumpAsync();
        Assert.Single(controller.Calls);

        controller.Complete(0, ControlActionStatus.Confirmed);
        Assert.Empty(dispatch.DrainLedger());
        Assert.Equal(first.ActionId, dispatch.Snapshot.LastIgnoredResponse?.ActionId);
        Assert.Null(dispatch.Snapshot.LastSentAt);
        await dispatch.PumpAsync();
        Assert.Equal("second", controller.Calls[1].Text);
        controller.Complete(1, ControlActionStatus.Confirmed);
        Assert.Equal(ControlActionStatus.Confirmed, Assert.Single(dispatch.DrainLedger()).Status);
        Assert.Equal(1, controller.MaximumActive);
    }

    [Fact]
    public async Task CompletionAfterDeadlineWithoutPumpRemainsUnknown()
    {
        var clock = new FakeClock();
        var controller = new DeferredController();
        using var dispatch = Create(controller, clock);
        dispatch.QueueManual("first", LongTtl);
        await dispatch.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(6));
        controller.Complete(0, ControlActionStatus.Confirmed);
        Assert.Equal(ControlActionStatus.Unknown, Assert.Single(dispatch.DrainLedger()).Status);
        Assert.Null(dispatch.Snapshot.LastSentAt);
        clock.Advance(TimeSpan.FromMinutes(1));
        await dispatch.PumpAsync();
        Assert.Single(controller.Calls);
    }

    [Fact]
    public async Task ReceiptTimestampIsNotShiftedByALaterPump()
    {
        var clock = new FakeClock();
        var controller = new DeferredController();
        using var dispatch = Create(controller, clock);
        dispatch.QueueManual("first", LongTtl);
        await dispatch.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        controller.Complete(0, ControlActionStatus.Confirmed);
        clock.Advance(TimeSpan.FromSeconds(10));
        await dispatch.PumpAsync();
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(4), dispatch.Snapshot.LastSentAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(4), Assert.Single(dispatch.DrainLedger()).RecordedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, dispatch.Snapshot.NextSendAt?.Ticks);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("disconnect")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task LifecycleInvalidationCannotBeUndoneByLateConfirmation(string operation)
    {
        var clock = new FakeClock();
        var controller = new DeferredController();
        using var dispatch = Create(controller, clock);
        dispatch.QueueManual("first", LongTtl);
        dispatch.QueueManual("queued", LongTtl);
        await dispatch.PumpAsync();
        switch (operation)
        {
            case "pause": dispatch.Pause(); break;
            case "disconnect": dispatch.Disconnect(); break;
            case "stop": dispatch.Stop(); break;
            default: dispatch.Dispose(); break;
        }
        Assert.True(controller.Calls[0].Token.IsCancellationRequested);
        Assert.Equal(0, dispatch.Snapshot.QueuedCount);
        Assert.Equal(ControlActionStatus.Unknown, Assert.Single(dispatch.DrainLedger()).Status);
        controller.Complete(0, ControlActionStatus.Confirmed);
        clock.Advance(TimeSpan.FromMinutes(1));
        var snapshot = await dispatch.PumpAsync();
        Assert.Single(controller.Calls);
        Assert.Null(snapshot.LastSentAt);
        Assert.Empty(dispatch.DrainLedger());
        Assert.NotNull(snapshot.LastIgnoredResponse);
        Assert.False(dispatch.QueueManual("late", LongTtl).IsSuccess);
    }

    [Fact]
    public async Task NewSessionMustWaitForOldPhysicalCallAndCannotInheritItsSuccess()
    {
        var clock = new FakeClock();
        var controller = new DeferredController();
        using var dispatch = Create(controller, clock);
        dispatch.QueueManual("old", LongTtl);
        await dispatch.PumpAsync();
        var replacement = Guid.NewGuid();
        dispatch.Start(replacement, "new-room");
        dispatch.QueueManual("new", LongTtl);
        clock.Advance(TimeSpan.FromSeconds(30));
        await dispatch.PumpAsync();
        Assert.Single(controller.Calls);
        controller.Complete(0, ControlActionStatus.Confirmed);
        await dispatch.PumpAsync();
        Assert.Equal(replacement, controller.Calls[1].Context.SessionId);
        Assert.Equal("new-room", controller.Calls[1].Context.RoomId);
        Assert.Null(dispatch.Snapshot.LastSentAt);
        Assert.Equal(Session, Assert.Single(dispatch.DrainLedger()).SessionId);
        Assert.Equal(1, controller.MaximumActive);
    }

    [Fact]
    public async Task QueuedMessageExpiresWhileNonCooperativeRequestStillOwnsLane()
    {
        var clock = new FakeClock();
        var controller = new DeferredController();
        using var dispatch = Create(controller, clock);
        dispatch.QueueManual("first", LongTtl);
        dispatch.QueueManual("expires", TimeSpan.FromSeconds(10));
        await dispatch.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await dispatch.PumpAsync();
        Assert.Equal(0, dispatch.Snapshot.QueuedCount);
        Assert.Equal(1, dispatch.Snapshot.DroppedCount);
        controller.Complete(0, ControlActionStatus.Unknown);
        await dispatch.PumpAsync();
        Assert.Single(controller.Calls);
    }

    [Fact]
    public async Task ReplacingSameScheduleIdInvalidatesAlreadyQueuedOldText()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController();
        using var dispatch = Create(controller, clock);
        dispatch.ConfigureSchedules([Schedule("old", 1, 100)]);
        dispatch.QueueManual("manual", LongTtl);
        await dispatch.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        await dispatch.PumpAsync();
        Assert.Equal(1, dispatch.Snapshot.QueuedCount);
        dispatch.ConfigureSchedules([Schedule("new", 60, 100)]);
        Assert.Equal(0, dispatch.Snapshot.QueuedCount);
        clock.Advance(TimeSpan.FromSeconds(29));
        await dispatch.PumpAsync();
        Assert.Single(controller.Actions);
        clock.Advance(TimeSpan.FromSeconds(31));
        await dispatch.PumpAsync();
        Assert.Equal(new[] { "manual", "new" }, controller.Actions.Select(action => action.Text));
    }

    [Fact]
    public async Task WindowClosingPreventsQueuedTimedMessageFromBeingSentLater()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController();
        using var dispatch = Create(controller, clock);
        dispatch.ConfigureSchedules([Schedule("timed", 1, 20)]);
        dispatch.QueueManual("manual", LongTtl);
        await dispatch.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        await dispatch.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(29));
        await dispatch.PumpAsync();
        Assert.Single(controller.Actions);
        Assert.Equal(0, dispatch.Snapshot.QueuedCount);
    }

    [Fact]
    public async Task SubmittedKeywordSurvivesReplacementAndStartsRuleCooldownAtReceipt()
    {
        var clock = new FakeClock();
        var controller = new DeferredController();
        var rules = Rules(clock, LongTtl);
        using var dispatch = Create(controller, clock, Options() with { SendInterval = TimeSpan.FromSeconds(1) }, rules);
        var old = Comment(rules, clock, "old");
        await dispatch.PumpAsync();
        Assert.Null(rules.TrySelectTextNext());
        clock.Advance(TimeSpan.FromSeconds(1));
        var replacement = Comment(rules, clock, "new");
        Assert.Same(replacement, rules.TrySelectNext()?.Candidate);
        Assert.False(rules.CanPlay(old, InteractionChannel.Voice));
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Complete(0, ControlActionStatus.Confirmed);
        // Wait until t=11 to pump: cooldown must still be from receipt t=2, not this pump.
        clock.Advance(TimeSpan.FromSeconds(9));
        await dispatch.PumpAsync();
        Assert.Single(controller.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await dispatch.PumpAsync();
        Assert.Equal(2, controller.Calls.Count);
        Assert.Equal("reply new", controller.Calls[1].Text);
        Assert.Equal(ControlActionStatus.Confirmed, Assert.Single(dispatch.DrainLedger()).Status);
    }

    [Fact]
    public async Task SubmittedKeywordExpiryDoesNotLoseCompletionCooldown()
    {
        var clock = new FakeClock();
        var controller = new DeferredController();
        var rules = Rules(clock, TimeSpan.FromSeconds(2));
        using var dispatch = Create(controller, clock, Options() with { SendInterval = TimeSpan.FromSeconds(1) }, rules);
        Comment(rules, clock, "expired-before-receipt");
        await dispatch.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(3));
        var fresh = Comment(rules, clock, "fresh");
        controller.Complete(0, ControlActionStatus.Confirmed);
        await dispatch.PumpAsync();
        Assert.Single(controller.Calls);
        Assert.Null(rules.TrySelectTextNext());
        Assert.Same(fresh, rules.TrySelectNext()?.Candidate);
    }

    [Fact]
    public async Task TextOnlyRuleSendsOnceWithoutCreatingVoiceWork()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController();
        var rules = Rules(clock, LongTtl, voice: false);
        using var dispatch = Create(controller, clock, rules: rules);
        Comment(rules, clock, "text-only");
        await dispatch.PumpAsync();
        Assert.Null(rules.TrySelectNext());
        Assert.Null(rules.TrySelectTextNext());
        clock.Advance(TimeSpan.FromMinutes(1));
        await dispatch.PumpAsync();
        Assert.Equal("reply text-only", Assert.Single(controller.Actions).Text);
    }

    [Theory]
    [InlineData("synchronous-exception")]
    [InlineData("null-task")]
    [InlineData("undefined-status")]
    public async Task InvalidAdapterOutcomesAreUnknownWithoutLeakingDetails(string mode)
    {
        var controller = new InvalidController(mode);
        using var dispatch = Create(controller, new FakeClock());
        dispatch.QueueManual("synthetic secret content", LongTtl);
        await dispatch.PumpAsync();
        var entry = Assert.Single(dispatch.DrainLedger());
        Assert.Equal(ControlActionStatus.Unknown, entry.Status);
        Assert.DoesNotContain("synthetic secret", entry.Detail ?? "");
        Assert.Equal(1, controller.Calls);
    }

    [Fact]
    public async Task CancellationCallbackFailureDoesNotReleasePhysicalLane()
    {
        var clock = new FakeClock();
        var controller = new DeferredController { ThrowOnCancel = true };
        using var dispatch = Create(controller, clock);
        dispatch.QueueManual("first", LongTtl);
        await dispatch.PumpAsync();
        dispatch.Disconnect();
        Assert.Contains("callback failed", dispatch.Snapshot.Detail);
        dispatch.Resume(Session, "room");
        dispatch.QueueManual("second", LongTtl);
        clock.Advance(TimeSpan.FromSeconds(30));
        await dispatch.PumpAsync();
        Assert.Single(controller.Calls);
        controller.Complete(0, ControlActionStatus.Cancelled);
        await dispatch.PumpAsync();
        Assert.Equal(2, controller.Calls.Count);
        Assert.Equal(1, controller.MaximumActive);
    }

    [Fact]
    public async Task ConcurrentPumpsCannotSubmitTwoMessagesIntoOneLane()
    {
        var clock = new FakeClock();
        var controller = new DeferredController();
        using var dispatch = Create(controller, clock, Options() with { SendInterval = TimeSpan.FromTicks(1) });
        dispatch.QueueManual("first", LongTtl);
        dispatch.QueueManual("second", LongTtl);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => dispatch.PumpAsync())));
        clock.Advance(TimeSpan.FromTicks(1));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => dispatch.PumpAsync())));
        Assert.Single(controller.Calls);
        controller.Complete(0, ControlActionStatus.Confirmed);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => dispatch.PumpAsync())));
        Assert.Equal(2, controller.Calls.Count);
        Assert.Equal(1, controller.MaximumActive);
    }

    [Fact]
    public async Task CancelledPumpDoesNotSubmitAnAcceptedMessage()
    {
        var controller = new DeferredController();
        using var dispatch = Create(controller, new FakeClock());
        dispatch.QueueManual("first", LongTtl);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch.PumpAsync(new CancellationToken(true)));
        Assert.Empty(controller.Calls);
        await dispatch.PumpAsync();
        Assert.Single(controller.Calls);
    }

    private static TimedTextSchedule Schedule(string text, int interval, int ends) => new("schedule",
        new[] { text }, TimeSpan.FromSeconds(interval), LongTtl, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(ends));

    private static TextDispatchCoordinator Create(ILiveRoomController controller, FakeClock clock,
        TextDispatchOptions? options = null, InteractionRuleCoordinator? rules = null)
    {
        var coordinator = new TextDispatchCoordinator(controller, clock, new SeededRandomSource(73), options ?? Options(), rules);
        Assert.True(coordinator.Start(Session, "room").IsSuccess);
        return coordinator;
    }

    private static InteractionRuleCoordinator Rules(FakeClock clock, TimeSpan ttl, bool voice = true)
    {
        var rule = new KeywordRule("rule", (IReadOnlyList<string>)new[] { "reply" })
        {
            VoiceEnabled = voice, TextEnabled = true, TextCooldown = TimeSpan.FromSeconds(10),
            TextTemplates = new[] { "{comment}" }, VoiceTemplates = new[] { "{comment}" }
        };
        var rules = new InteractionRuleCoordinator(clock, [rule], new InteractionPolicyOptions { KeywordTtl = ttl });
        rules.SetContext(Session, "room");
        return rules;
    }

    private static InteractionCandidate Comment(InteractionRuleCoordinator rules, FakeClock clock, string id)
    {
        var liveEvent = new LiveEvent("synthetic-source", Session, "room", LiveEventType.Comment, id, "synthetic-user", "Synthetic",
            clock.UtcNow, clock.UtcNow, "reply " + id, null, null, EventIdentityQuality.Complete);
        var admitted = rules.Enqueue(liveEvent, new EventProcessResult(EventProcessStatus.Accepted, "synthetic:" + id, EventIdentityQuality.Complete));
        Assert.True(admitted.Accepted);
        return Assert.IsType<InteractionCandidate>(admitted.Candidate);
    }

    private sealed record Call(string Text, RoomOperationContext Context, CancellationToken Token)
    {
        public TaskCompletionSource<ControlActionResult> Completion { get; } = new();
    }

    private sealed class DeferredController : ILiveRoomController
    {
        private int _active;
        public List<Call> Calls { get; } = [];
        public int MaximumActive { get; private set; }
        public bool ThrowOnCancel { get; init; }
        public RoomControlState State => RoomControlState.Ready;
        public RoomControlCapabilities Capabilities { get; } = new(false, false, true);
        public Task<ControlActionResult> SendTextAsync(string text, RoomOperationContext context, CancellationToken cancellationToken)
        {
            var call = new Call(text, context, cancellationToken);
            Calls.Add(call);
            MaximumActive = Math.Max(MaximumActive, ++_active);
            if (ThrowOnCancel) cancellationToken.Register(() => throw new InvalidOperationException("synthetic callback failure"));
            return call.Completion.Task;
        }

        public void Complete(int index, ControlActionStatus status)
        {
            _active--;
            // Observe receipt before advancing FakeClock; restore xUnit's context even on failure.
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                Calls[index].Completion.SetResult(new(status));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        public Task<ControlActionResult> ShowProductAsync(ProductTarget target, RoomOperationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Text dispatch must not show products.");
        public Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)> ReadProductVisibilityAsync(RoomOperationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Text dispatch must not read product visibility.");
    }

    private sealed class InvalidController(string mode) : ILiveRoomController
    {
        public int Calls { get; private set; }
        public RoomControlState State => RoomControlState.Ready;
        public RoomControlCapabilities Capabilities { get; } = new(false, false, true);
        public Task<ControlActionResult> SendTextAsync(string text, RoomOperationContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return mode switch
            {
                "null-task" => null!,
                "undefined-status" => Task.FromResult(new ControlActionResult((ControlActionStatus)999, "synthetic secret")),
                _ => throw new InvalidOperationException("synthetic secret")
            };
        }
        public Task<ControlActionResult> ShowProductAsync(ProductTarget target, RoomOperationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException();
        public Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)> ReadProductVisibilityAsync(RoomOperationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException();
    }
}
