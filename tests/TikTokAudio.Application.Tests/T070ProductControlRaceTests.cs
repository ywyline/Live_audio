using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Control;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class T070ProductControlRaceTests
{
    private static readonly Guid Session = Guid.Parse("c3bfa315-9d1b-480b-9a45-285de08e15be");
    private static ProductTarget Product(int number) => new($"local-{number}", $"platform-{number}");

    [Fact]
    public async Task NonCooperativeOldShowHoldsLaneThenCorrectsOnlyLatestTarget()
    {
        var (coordinator, controller, _) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        var firstEpoch = coordinator.Snapshot.Context!.ProductEpoch;
        coordinator.SetTarget(Product(2));
        coordinator.SetTarget(Product(3));
        Assert.True(controller.Calls[0].Token.IsCancellationRequested);
        await coordinator.PumpAsync();
        Assert.Single(controller.Calls);
        Assert.Null(coordinator.Snapshot.LastShownAt);

        controller.Complete(0);
        var snapshot = await coordinator.PumpAsync();
        Assert.Equal(2, controller.Calls.Count);
        Assert.Equal(Product(3), controller.Calls[1].Target);
        Assert.NotEqual(firstEpoch, controller.Calls[1].Context.ProductEpoch);
        Assert.Null(snapshot.LastShownAt);
        Assert.Equal(Product(1), snapshot.LastIgnoredResponse?.Target);
        controller.Complete(1);
        Assert.Equal(RoomControlState.Ready, (await coordinator.PumpAsync()).State);
        Assert.Equal(1, controller.MaximumActive);
    }

    [Fact]
    public async Task PriorityBatchEndsEvenWhileOldRequestOwnsLane()
    {
        var (coordinator, controller, _) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        coordinator.SetTarget(Product(2), ProductTargetOrigin.Manual);
        await coordinator.PumpAsync();
        Assert.True(coordinator.SetTarget(Product(3), ProductTargetOrigin.ProductEnter).IsSuccess);
        controller.Complete(0);
        await coordinator.PumpAsync();
        Assert.Equal(Product(3), controller.Calls[1].Target);
        Assert.Equal(2, controller.Calls.Count);
    }

    [Fact]
    public async Task TimeoutNeverReleasesLaneAndLateConfirmationRequiresReadback()
    {
        var (coordinator, controller, clock) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var timeout = await coordinator.PumpAsync();
        Assert.Equal(RoomControlState.NeedsConfirmation, timeout.State);
        Assert.Equal(ControlActionStatus.Unknown, timeout.LastResult?.Status);
        Assert.True(timeout.HasInFlightOperation);
        Assert.True(controller.Calls[0].Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        await coordinator.PumpAsync();
        Assert.Single(controller.Calls);

        controller.Complete(0);
        var late = await coordinator.PumpAsync();
        Assert.Null(late.LastShownAt);
        Assert.Null(late.RenewalDue);
        Assert.True(controller.Calls[1].IsRead);
        controller.Complete(1, visibility: new(Product(1), true, DateTimeOffset.MaxValue));
        var verified = await coordinator.PumpAsync();
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(6), verified.LastShownAt);
        Assert.Equal(TimeSpan.FromSeconds(36).Ticks, verified.RenewalDue?.Ticks);
        Assert.Equal(1, controller.MaximumActive);
    }

    [Fact]
    public async Task ResponseAfterDeadlineWithoutTimeoutPumpIsStillUnknown()
    {
        var (coordinator, controller, clock) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(6));
        controller.Complete(0);
        var snapshot = await coordinator.PumpAsync();
        Assert.Null(snapshot.LastShownAt);
        Assert.Null(snapshot.RenewalDue);
        Assert.True(controller.Calls[1].IsRead);
        controller.Complete(1, ControlActionStatus.Unknown);
        Assert.Equal(RoomControlState.NeedsConfirmation, (await coordinator.PumpAsync()).State);
    }

    [Fact]
    public async Task TimelyConfirmationRemainsValidWhenPumpOccursAfterDeadline()
    {
        var (coordinator, controller, clock) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        controller.Complete(0);
        clock.Advance(TimeSpan.FromSeconds(10));
        var snapshot = await coordinator.PumpAsync();
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(4), snapshot.LastShownAt);
        Assert.Equal(TimeSpan.FromSeconds(34).Ticks, snapshot.RenewalDue?.Ticks);
        Assert.Single(controller.Calls);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("disconnect")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task LifecycleBarrierPreventsLateResponseFromRevivingRenewal(string action)
    {
        var (coordinator, controller, clock) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        switch (action)
        {
            case "pause": coordinator.Pause(); break;
            case "disconnect": coordinator.Disconnect(); break;
            case "stop": coordinator.Stop(); break;
            default: coordinator.Dispose(); break;
        }
        Assert.True(controller.Calls[0].Token.IsCancellationRequested);
        Assert.False(coordinator.SetTarget(Product(2)).IsSuccess);
        controller.Complete(0);
        clock.Advance(TimeSpan.FromMinutes(1));
        var snapshot = await coordinator.PumpAsync();
        Assert.Single(controller.Calls);
        Assert.Null(snapshot.LastShownAt);
        Assert.Null(snapshot.RenewalDue);
        Assert.False(snapshot.HasInFlightOperation);
        Assert.NotNull(snapshot.LastIgnoredResponse);
        if (action == "dispose") Assert.False(coordinator.Start(Session, "room").IsSuccess);
    }

    [Fact]
    public async Task NewRoomWaitsForOldLaneButNeverAcceptsOldConfirmation()
    {
        var (coordinator, controller, _) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        var newSession = Guid.NewGuid();
        coordinator.Start(newSession, "new-room");
        coordinator.SetTarget(Product(2));
        await coordinator.PumpAsync();
        Assert.Single(controller.Calls);
        controller.Complete(0);
        var snapshot = await coordinator.PumpAsync();
        Assert.Equal(newSession, controller.Calls[1].Context.SessionId);
        Assert.Equal("new-room", controller.Calls[1].Context.RoomId);
        Assert.Null(snapshot.LastShownAt);
        Assert.Equal(1, controller.MaximumActive);
    }

    [Fact]
    public async Task ResumeWaitsForCancelledOldShowThenReadsBeforeAnyWrite()
    {
        var (coordinator, controller, clock) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        coordinator.Pause();
        coordinator.Resume(Session, "room");
        await coordinator.PumpAsync();
        Assert.Single(controller.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Complete(0);
        await coordinator.PumpAsync();
        Assert.True(controller.Calls[1].IsRead);
        controller.Complete(1, visibility: new(Product(1), true, null));
        var snapshot = await coordinator.PumpAsync();
        Assert.Equal(TimeSpan.FromSeconds(31).Ticks, snapshot.RenewalDue?.Ticks);
        Assert.Equal(2, controller.Calls.Count);
    }

    [Fact]
    public async Task SupersededReadCannotConfirmNewTargetOrScheduleOldRenewal()
    {
        var (coordinator, controller, _) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        controller.Complete(0, ControlActionStatus.Unknown);
        await coordinator.PumpAsync();
        coordinator.SetTarget(Product(2));
        await coordinator.PumpAsync();
        Assert.Equal(2, controller.Calls.Count);
        Assert.True(controller.Calls[1].Token.IsCancellationRequested);
        controller.Complete(1, visibility: new(Product(1), true, null));
        var snapshot = await coordinator.PumpAsync();
        Assert.Null(snapshot.LastShownAt);
        Assert.Equal(Product(2), controller.Calls[2].Target);
        Assert.Equal(1, controller.MaximumActive);
    }

    [Fact]
    public async Task ReadTimeoutDoesNotLoopReadOrWrite()
    {
        var (coordinator, controller, clock) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        controller.Complete(0, ControlActionStatus.Unknown);
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await coordinator.PumpAsync();
        controller.Complete(1, visibility: new(Product(1), true, null));
        var snapshot = await coordinator.PumpAsync();
        Assert.Equal(RoomControlState.NeedsConfirmation, snapshot.State);
        Assert.Null(snapshot.RenewalDue);
        clock.Advance(TimeSpan.FromHours(1));
        await coordinator.PumpAsync();
        Assert.Equal(2, controller.Calls.Count);
    }

    [Fact]
    public async Task RejectedRetryIsInvalidatedByTargetChange()
    {
        var (coordinator, controller, clock) = Create(new ProductControlOptions { AllowRejectedRetry = true });
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        controller.Complete(0, ControlActionStatus.Rejected);
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        coordinator.SetTarget(Product(2));
        await coordinator.PumpAsync();
        controller.Complete(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await coordinator.PumpAsync();
        Assert.Equal(new[] { Product(1), Product(2) }, controller.Calls.Select(call => call.Target));
    }

    [Theory]
    [InlineData(RoomControlState.Disabled)]
    [InlineData(RoomControlState.Suspended)]
    [InlineData(RoomControlState.Error)]
    [InlineData(RoomControlState.Pending)]
    [InlineData(RoomControlState.Unsupported)]
    [InlineData(RoomControlState.NeedsConfirmation)]
    public async Task UnreadyControllerDoesNotReceiveAWrite(RoomControlState state)
    {
        var (coordinator, controller, _) = Create();
        controller.State = state;
        coordinator.SetTarget(Product(1));
        var snapshot = await coordinator.PumpAsync();
        Assert.Empty(controller.Calls);
        Assert.NotEqual(RoomControlState.Ready, snapshot.State);
        Assert.Null(snapshot.RenewalDue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionOrCancellationIsObservedAndReconciledWithoutBlindWrite(bool cancelled)
    {
        var (coordinator, controller, _) = Create();
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        controller.Fail(0, cancelled ? new OperationCanceledException() : new InvalidOperationException("synthetic secret not to expose"));
        var snapshot = await coordinator.PumpAsync();
        Assert.True(controller.Calls[1].IsRead);
        Assert.DoesNotContain("synthetic secret", snapshot.LastResult?.Detail ?? "");
        controller.Complete(1, ControlActionStatus.Unknown);
        Assert.Equal(RoomControlState.NeedsConfirmation, (await coordinator.PumpAsync()).State);
        Assert.Equal(2, controller.Calls.Count);
    }

    [Fact]
    public async Task CancelledPumpDoesNotSubmitPendingTarget()
    {
        var (coordinator, controller, _) = Create();
        coordinator.SetTarget(Product(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.PumpAsync(new CancellationToken(true)));
        Assert.Empty(controller.Calls);
        await coordinator.PumpAsync();
        Assert.Single(controller.Calls);
    }

    [Fact]
    public async Task ConcurrentPumpsStillReserveOnlyOnePhysicalOperation()
    {
        var (coordinator, controller, _) = Create();
        coordinator.SetTarget(Product(1));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => coordinator.PumpAsync())));
        Assert.Single(controller.Calls);
        Assert.Equal(1, controller.MaximumActive);
        controller.Complete(0);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => coordinator.PumpAsync())));
        Assert.Single(controller.Calls);
    }

    private static (ProductControlCoordinator Coordinator, LaneController Controller, FakeClock Clock) Create(ProductControlOptions? options = null)
    {
        var clock = new FakeClock();
        var controller = new LaneController();
        var coordinator = new ProductControlCoordinator(controller, clock, options);
        Assert.True(coordinator.Start(Session, "room").IsSuccess);
        return (coordinator, controller, clock);
    }

    private sealed record Response(ControlActionResult Result, ProductVisibility? Visibility);
    private sealed record Call(bool IsRead, ProductTarget? Target, RoomOperationContext Context, CancellationToken Token)
    {
        public TaskCompletionSource<Response> Completion { get; } = new();
    }

    private sealed class LaneController : ILiveRoomController
    {
        private int _active;
        public int MaximumActive { get; private set; }
        public List<Call> Calls { get; } = [];
        public RoomControlState State { get; set; } = RoomControlState.Ready;
        public RoomControlCapabilities Capabilities { get; } = new(true, true, true);

        public async Task<ControlActionResult> ShowProductAsync(ProductTarget target, RoomOperationContext context, CancellationToken cancellationToken)
            => (await Add(false, target, context, cancellationToken).ConfigureAwait(false)).Result;

        public async Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)> ReadProductVisibilityAsync(
            RoomOperationContext context, CancellationToken cancellationToken)
        {
            var response = await Add(true, null, context, cancellationToken).ConfigureAwait(false);
            return (response.Result.Status, response.Visibility, response.Result.Detail);
        }

        public Task<ControlActionResult> SendTextAsync(string text, RoomOperationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Product control cannot send text.");

        private Task<Response> Add(bool read, ProductTarget? target, RoomOperationContext context, CancellationToken token)
        {
            var call = new Call(read, target, context, token);
            Calls.Add(call);
            MaximumActive = Math.Max(MaximumActive, ++_active);
            return call.Completion.Task;
        }

        public void Complete(int index, ControlActionStatus status = ControlActionStatus.Confirmed, ProductVisibility? visibility = null)
        {
            _active--;
            CompleteOnNeutralContext(() => Calls[index].Completion.SetResult(new Response(new ControlActionResult(status), visibility)));
        }

        public void Fail(int index, Exception exception)
        {
            _active--;
            CompleteOnNeutralContext(() => Calls[index].Completion.SetException(exception));
        }

        private static void CompleteOnNeutralContext(Action complete)
        {
            // These deterministic receipts must finish before the fake clock advances. xUnit's
            // synchronization context otherwise queues ConfigureAwait(false) continuations.
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                complete();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }
}
