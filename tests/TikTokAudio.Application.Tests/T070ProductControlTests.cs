using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Control;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

[Collection("T070 product control")]
public sealed class T070ProductControlTests
{
    private static readonly Guid SessionId = Guid.Parse("b6b32f28-3e1b-4cb7-8c4c-ef0c99954540");
    private const string RoomId = "synthetic-room";

    [Fact]
    public async Task StartAndTargetSelectionDoNotSubmitBeforePump()
    {
        var controller = new SimulatedLiveRoomController();
        var coordinator = Create(controller, new FakeClock());

        Assert.True(coordinator.Start(SessionId, RoomId).IsSuccess);
        Assert.Empty(controller.Actions);
        Assert.True(coordinator.SetTarget(Product(1)).IsSuccess);
        Assert.Empty(controller.Actions);

        var snapshot = await coordinator.PumpAsync();

        Assert.Equal(Product(1), Assert.Single(controller.Actions).Product);
        Assert.Equal(RoomControlState.Ready, snapshot.State);
        Assert.Equal(ControlActionStatus.Confirmed, snapshot.LastResult?.Status);
        Assert.False(snapshot.HasInFlightOperation);
    }

    [Fact]
    public async Task TargetCannotActivateAnUnstartedSession()
    {
        var controller = new SimulatedLiveRoomController();
        var coordinator = Create(controller, new FakeClock());

        Assert.False(coordinator.SetTarget(Product(1)).IsSuccess);
        await coordinator.PumpAsync();

        Assert.Empty(controller.Actions);
        Assert.Null(coordinator.Snapshot.LastShownAt);
        Assert.Null(coordinator.Snapshot.RenewalDue);
    }

    [Fact]
    public async Task RenewAtZeroThirtySixtyAndSwitchAtEightyWithoutOldNinetyRenewal()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await coordinator.PumpAsync();
        Assert.Equal(3, controller.Actions.Count);

        clock.Advance(TimeSpan.FromSeconds(20));
        coordinator.SetTarget(Product(2));
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await coordinator.PumpAsync();

        Assert.Equal(new[] { Product(1), Product(1), Product(1), Product(2) },
            controller.Actions.Select(action => action.Product));
        Assert.Equal(controller.Actions[0].Context.ProductEpoch, controller.Actions[2].Context.ProductEpoch);
        Assert.NotEqual(controller.Actions[2].Context.ProductEpoch, controller.Actions[3].Context.ProductEpoch);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(80), coordinator.Snapshot.LastShownAt);
        Assert.Equal(TimeSpan.FromSeconds(110).Ticks, coordinator.Snapshot.RenewalDue?.Ticks);

        clock.Advance(TimeSpan.FromSeconds(20));
        await coordinator.PumpAsync();
        Assert.Equal(Product(2), controller.Actions[4].Product);
        Assert.Equal(5, controller.Actions.Count);
    }

    [Fact]
    public async Task RenewalDoesNotRunOneTickEarly()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();

        clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        await coordinator.PumpAsync();
        Assert.Single(controller.Actions);

        clock.Advance(TimeSpan.FromTicks(1));
        await coordinator.PumpAsync();
        Assert.Equal(2, controller.Actions.Count);
    }

    [Fact]
    public async Task LateConfirmationUsesCompletionTimeInsteadOfSubmissionOrNextPump()
    {
        var clock = new FakeClock();
        var completion = new TaskCompletionSource<ControlActionResult>();
        var controller = new ControlledController { Show = (_, _, _) => completion.Task };
        var coordinator = Started(controller, clock,
            new ProductControlOptions { RequestTimeout = TimeSpan.FromMinutes(1) });
        coordinator.SetTarget(Product(1));

        var pending = await coordinator.PumpAsync();
        Assert.True(pending.HasInFlightOperation);
        Assert.Null(pending.LastShownAt);
        Assert.Null(pending.RenewalDue);

        clock.Advance(TimeSpan.FromSeconds(7));
        completion.SetResult(new ControlActionResult(ControlActionStatus.Confirmed));
        await Task.Yield();
        clock.Advance(TimeSpan.FromSeconds(11));
        var confirmed = await coordinator.PumpAsync();

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(7), confirmed.LastShownAt);
        Assert.Equal(TimeSpan.FromSeconds(37).Ticks, confirmed.RenewalDue?.Ticks);
        clock.Advance(TimeSpan.FromSeconds(18));
        await coordinator.PumpAsync();
        Assert.Single(controller.Shows);
        clock.Advance(TimeSpan.FromSeconds(1));
        await coordinator.PumpAsync();
        Assert.Equal(2, controller.Shows.Count);
    }

    [Fact]
    public async Task ALongGapSubmitsOnlyOneRenewalAndDoesNotReplayMissedTicks()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();

        clock.Advance(TimeSpan.FromMinutes(5));
        await coordinator.PumpAsync();
        await coordinator.PumpAsync();

        Assert.Equal(2, controller.Actions.Count);
        Assert.Equal(TimeSpan.FromSeconds(330).Ticks, coordinator.Snapshot.RenewalDue?.Ticks);
    }

    [Theory]
    [InlineData(ProductTargetOrigin.Manual, ProductTargetOrigin.ScriptMarker, ProductTargetOrigin.ProductEnter)]
    [InlineData(ProductTargetOrigin.Manual, ProductTargetOrigin.ProductEnter, ProductTargetOrigin.ScriptMarker)]
    [InlineData(ProductTargetOrigin.ScriptMarker, ProductTargetOrigin.Manual, ProductTargetOrigin.ProductEnter)]
    [InlineData(ProductTargetOrigin.ScriptMarker, ProductTargetOrigin.ProductEnter, ProductTargetOrigin.Manual)]
    [InlineData(ProductTargetOrigin.ProductEnter, ProductTargetOrigin.Manual, ProductTargetOrigin.ScriptMarker)]
    [InlineData(ProductTargetOrigin.ProductEnter, ProductTargetOrigin.ScriptMarker, ProductTargetOrigin.Manual)]
    public async Task ManualWinsTheSameSubmissionBatchRegardlessOfArrivalOrder(
        ProductTargetOrigin first, ProductTargetOrigin second, ProductTargetOrigin third)
    {
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, new FakeClock());

        foreach (var origin in new[] { first, second, third })
        {
            coordinator.SetTarget(Product((int)origin + 1), origin);
        }

        await coordinator.PumpAsync();

        Assert.Equal(Product(3), Assert.Single(controller.Actions).Product);
        Assert.Equal(Product(3), coordinator.Snapshot.Target);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScriptMarkerWinsProductEnterAtTheSameBoundary(bool markerFirst)
    {
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, new FakeClock());
        var origins = markerFirst
            ? new[] { ProductTargetOrigin.ScriptMarker, ProductTargetOrigin.ProductEnter }
            : new[] { ProductTargetOrigin.ProductEnter, ProductTargetOrigin.ScriptMarker };

        foreach (var origin in origins)
        {
            coordinator.SetTarget(Product((int)origin + 1), origin);
        }

        await coordinator.PumpAsync();

        Assert.Equal(Product(2), Assert.Single(controller.Actions).Product);
    }

    [Fact]
    public async Task LaterProductEnterCanReplacePreviouslySubmittedManualTarget()
    {
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, new FakeClock());
        coordinator.SetTarget(Product(1), ProductTargetOrigin.Manual);
        await coordinator.PumpAsync();

        coordinator.SetTarget(Product(2), ProductTargetOrigin.ProductEnter);
        await coordinator.PumpAsync();

        Assert.Equal(new[] { Product(1), Product(2) }, controller.Actions.Select(action => action.Product));
    }

    [Fact]
    public async Task EqualPriorityPendingTargetsKeepOnlyTheLatest()
    {
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, new FakeClock());
        coordinator.SetTarget(Product(1));
        coordinator.SetTarget(Product(2));
        coordinator.SetTarget(Product(3));

        await coordinator.PumpAsync();

        Assert.Equal(Product(3), Assert.Single(controller.Actions).Product);
    }

    [Fact]
    public async Task ProductSwitchAtTheExactRenewalBoundarySuppressesOldRenewal()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(30));

        coordinator.SetTarget(Product(2));
        await coordinator.PumpAsync();

        Assert.Equal(new[] { Product(1), Product(2) }, controller.Actions.Select(action => action.Product));
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, coordinator.Snapshot.RenewalDue?.Ticks);
    }

    [Fact]
    public async Task UnknownShowReadsBackAndRenewsOnlyAfterCurrentTargetIsConfirmedVisible()
    {
        var clock = new FakeClock();
        var controller = new ControlledController
        {
            Show = (_, _, _) => Task.FromResult(new ControlActionResult(ControlActionStatus.Unknown)),
            Read = (_, _) => Visibility(Product(1), true)
        };
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));

        await coordinator.PumpAsync();
        await coordinator.PumpAsync();

        Assert.Single(controller.Shows);
        Assert.Single(controller.Reads);
        Assert.Equal(RoomControlState.Ready, coordinator.Snapshot.State);
        Assert.Equal(DateTimeOffset.UnixEpoch, coordinator.Snapshot.LastShownAt);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, coordinator.Snapshot.RenewalDue?.Ticks);
    }

    [Fact]
    public async Task ReadBackWithMatchingLocalIdButDifferentPlatformIdCannotConfirmCurrentTarget()
    {
        var clock = new FakeClock();
        var controller = new ControlledController
        {
            Show = (_, _, _) => Task.FromResult(new ControlActionResult(ControlActionStatus.Unknown)),
            Read = (_, _) => Visibility(new ProductTarget("1", "synthetic-platform-other"), true)
        };
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));

        await coordinator.PumpAsync();
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.PumpAsync();

        Assert.Single(controller.Shows);
        Assert.Single(controller.Reads);
        Assert.Equal(RoomControlState.NeedsConfirmation, coordinator.Snapshot.State);
        Assert.Null(coordinator.Snapshot.LastShownAt);
        Assert.Null(coordinator.Snapshot.RenewalDue);
    }

    [Theory]
    [InlineData(ControlActionStatus.Unknown, false, true)]
    [InlineData(ControlActionStatus.Confirmed, false, true)]
    [InlineData(ControlActionStatus.Confirmed, true, false)]
    [InlineData(ControlActionStatus.Unsupported, false, true)]
    public async Task UnverifiableUnknownShowNeedsConfirmationAndNeverBlindlyRetries(
        ControlActionStatus readStatus, bool visible, bool currentTarget)
    {
        var clock = new FakeClock();
        var controller = new ControlledController
        {
            Show = (_, _, _) => Task.FromResult(new ControlActionResult(ControlActionStatus.Unknown)),
            Read = (_, _) => Task.FromResult<(ControlActionStatus, ProductVisibility?, string?)>((
                readStatus, new ProductVisibility(Product(currentTarget ? 1 : 2), visible, null), "synthetic"))
        };
        var coordinator = Started(controller, clock, new ProductControlOptions { AllowRejectedRetry = true });
        coordinator.SetTarget(Product(1));

        await coordinator.PumpAsync();
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await coordinator.PumpAsync();

        Assert.Single(controller.Shows);
        Assert.Single(controller.Reads);
        Assert.Equal(RoomControlState.NeedsConfirmation, coordinator.Snapshot.State);
        Assert.Null(coordinator.Snapshot.LastShownAt);
        Assert.Null(coordinator.Snapshot.RenewalDue);
    }

    [Fact]
    public async Task UnsupportedShowCapabilityNeverInvokesController()
    {
        var controller = new ControlledController
        {
            Capabilities = new RoomControlCapabilities(false, true, true)
        };
        var coordinator = Create(controller, new FakeClock());
        coordinator.Start(SessionId, RoomId);
        coordinator.SetTarget(Product(1));

        await coordinator.PumpAsync();

        Assert.Empty(controller.Shows);
        Assert.Empty(controller.Reads);
        Assert.Equal(RoomControlState.Unsupported, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task UnknownShowWithoutReadCapabilityNeedsConfirmation()
    {
        var clock = new FakeClock();
        var controller = new ControlledController
        {
            Capabilities = new RoomControlCapabilities(true, false, true),
            Show = (_, _, _) => Task.FromResult(new ControlActionResult(ControlActionStatus.Unknown))
        };
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));

        await coordinator.PumpAsync();
        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.PumpAsync();

        Assert.Single(controller.Shows);
        Assert.Empty(controller.Reads);
        Assert.Equal(RoomControlState.NeedsConfirmation, coordinator.Snapshot.State);
        Assert.Null(coordinator.Snapshot.RenewalDue);
    }

    [Fact]
    public async Task RejectedShowIsNotRetriedByDefault()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController(ControlActionStatus.Rejected);
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));

        await coordinator.PumpAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await coordinator.PumpAsync();

        Assert.Single(controller.Actions);
        Assert.Equal(ControlActionStatus.Rejected, coordinator.Snapshot.LastResult?.Status);
        Assert.Null(coordinator.Snapshot.LastShownAt);
        Assert.Null(coordinator.Snapshot.RenewalDue);
    }

    [Fact]
    public async Task AllowedRejectedRetryRunsOnceAfterTwoSecondsAndThenStops()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController(ControlActionStatus.Rejected);
        var coordinator = Started(controller, clock, new ProductControlOptions { AllowRejectedRetry = true });
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();

        clock.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        await coordinator.PumpAsync();
        Assert.Single(controller.Actions);
        clock.Advance(TimeSpan.FromTicks(1));
        await coordinator.PumpAsync();
        Assert.Equal(2, controller.Actions.Count);

        clock.Advance(TimeSpan.FromHours(1));
        await coordinator.PumpAsync();
        Assert.Equal(2, controller.Actions.Count);
        Assert.Null(coordinator.Snapshot.RenewalDue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrDisconnectCancelsRenewalAndResumeChecksVisibility(bool disconnect)
    {
        var clock = new FakeClock();
        var controller = new ControlledController { Read = (_, _) => Visibility(Product(1), true) };
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        var oldEpoch = coordinator.Snapshot.Context!.ProductEpoch;

        Assert.True((disconnect ? coordinator.Disconnect() : coordinator.Pause()).IsSuccess);
        Assert.Equal(Product(1), coordinator.Snapshot.Target);
        Assert.Null(coordinator.Snapshot.LastShownAt);
        Assert.Null(coordinator.Snapshot.RenewalDue);
        Assert.NotEqual(oldEpoch, coordinator.Snapshot.Context!.ProductEpoch);
        clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.PumpAsync();
        Assert.Single(controller.Shows);

        Assert.True(coordinator.Resume(SessionId, RoomId).IsSuccess);
        Assert.Empty(controller.Reads);
        await coordinator.PumpAsync();
        await coordinator.PumpAsync();
        Assert.Single(controller.Reads);
        Assert.Single(controller.Shows);
        Assert.Equal(TimeSpan.FromSeconds(90).Ticks, coordinator.Snapshot.RenewalDue?.Ticks);

        clock.Advance(TimeSpan.FromSeconds(30));
        await coordinator.PumpAsync();
        Assert.Equal(2, controller.Shows.Count);
    }

    [Fact]
    public async Task ResumeCanShowAgainOnlyAfterReadConfirmsTargetIsNotVisible()
    {
        var controller = new ControlledController { Read = (_, _) => Visibility(Product(1), false) };
        var coordinator = Started(controller, new FakeClock());
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        coordinator.Pause();
        coordinator.Resume(SessionId, RoomId);

        await coordinator.PumpAsync();
        await coordinator.PumpAsync();

        Assert.Equal(new[] { "show", "read", "show" }, controller.Calls);
        Assert.Equal(RoomControlState.Ready, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task ResumeWithoutVisibilityEvidenceDoesNotShowAgain()
    {
        var controller = new ControlledController();
        var coordinator = Started(controller, new FakeClock());
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        coordinator.Pause();
        coordinator.Resume(SessionId, RoomId);

        await coordinator.PumpAsync();
        await coordinator.PumpAsync();

        Assert.Equal(new[] { "show", "read" }, controller.Calls);
        Assert.Equal(RoomControlState.NeedsConfirmation, coordinator.Snapshot.State);
        Assert.Null(coordinator.Snapshot.RenewalDue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResumeRejectsChangedSessionOrRoom(bool changedSession)
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        coordinator.Disconnect();

        var result = coordinator.Resume(changedSession ? Guid.NewGuid() : SessionId,
            changedSession ? RoomId : "another-synthetic-room");
        Assert.False(result.IsSuccess);
        clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.PumpAsync();

        Assert.Single(controller.Actions);
        Assert.Null(coordinator.Snapshot.RenewalDue);
    }

    [Fact]
    public async Task StopCannotBeResumedAndNewStartDoesNotRestoreOldTarget()
    {
        var clock = new FakeClock();
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, clock);
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();

        Assert.True(coordinator.Stop().IsSuccess);
        Assert.False(coordinator.Resume(SessionId, RoomId).IsSuccess);
        clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.PumpAsync();
        Assert.Single(controller.Actions);
        Assert.Null(coordinator.Snapshot.Target);
        Assert.Null(coordinator.Snapshot.RenewalDue);

        Assert.True(coordinator.Start(SessionId, RoomId).IsSuccess);
        await coordinator.PumpAsync();
        Assert.Single(controller.Actions);
        coordinator.SetTarget(Product(2));
        await coordinator.PumpAsync();
        Assert.Equal(Product(2), controller.Actions[1].Product);
    }

    [Fact]
    public async Task StartingAnotherRoomClearsOldTargetAndUsesNewContext()
    {
        var controller = new SimulatedLiveRoomController();
        var coordinator = Started(controller, new FakeClock());
        coordinator.SetTarget(Product(1));
        await coordinator.PumpAsync();
        var replacementSession = Guid.NewGuid();

        Assert.True(coordinator.Start(replacementSession, "replacement-room").IsSuccess);
        await coordinator.PumpAsync();
        Assert.Single(controller.Actions);
        Assert.Null(coordinator.Snapshot.Target);
        Assert.Null(coordinator.Snapshot.LastShownAt);

        coordinator.SetTarget(Product(2));
        await coordinator.PumpAsync();
        var replacement = controller.Actions[1];
        Assert.Equal(replacementSession, replacement.Context.SessionId);
        Assert.Equal("replacement-room", replacement.Context.RoomId);
        Assert.NotEqual(controller.Actions[0].Context.ProductEpoch, replacement.Context.ProductEpoch);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(2, 0)]
    [InlineData(2, -1)]
    public void NonpositiveTimingsAreRejected(int field, int ticks)
    {
        var duration = TimeSpan.FromTicks(ticks);
        var options = field switch
        {
            0 => new ProductControlOptions { RenewalInterval = duration },
            1 => new ProductControlOptions { RequestTimeout = duration },
            _ => new ProductControlOptions { RetryDelay = duration }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new SimulatedLiveRoomController(), new FakeClock(), options));
    }

    private static ProductTarget Product(int number) => new(number.ToString(), $"synthetic-platform-{number}");

    private static ProductControlCoordinator Create(ILiveRoomController controller, FakeClock clock,
        ProductControlOptions? options = null) => new(controller, clock, options);

    private static ProductControlCoordinator Started(ILiveRoomController controller, FakeClock clock,
        ProductControlOptions? options = null)
    {
        var coordinator = Create(controller, clock, options);
        Assert.True(coordinator.Start(SessionId, RoomId).IsSuccess);
        return coordinator;
    }

    private static Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)> Visibility(
        ProductTarget target, bool visible) =>
        Task.FromResult<(ControlActionStatus, ProductVisibility?, string?)>((ControlActionStatus.Confirmed,
            new ProductVisibility(target, visible, null), "synthetic visibility"));

    private sealed class ControlledController : ILiveRoomController
    {
        public RoomControlState State => RoomControlState.Ready;
        public RoomControlCapabilities Capabilities { get; init; } = new(true, true, true);
        public List<(ProductTarget Target, RoomOperationContext Context)> Shows { get; } = [];
        public List<RoomOperationContext> Reads { get; } = [];
        public List<string> Calls { get; } = [];
        public Func<ProductTarget, RoomOperationContext, CancellationToken, Task<ControlActionResult>> Show { get; init; }
            = (_, _, _) => Task.FromResult(new ControlActionResult(ControlActionStatus.Confirmed));
        public Func<RoomOperationContext, CancellationToken,
            Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)>> Read { get; init; }
            = (_, _) => Task.FromResult<(ControlActionStatus, ProductVisibility?, string?)>((ControlActionStatus.Unknown, null, "synthetic unknown"));

        public Task<ControlActionResult> ShowProductAsync(ProductTarget target, RoomOperationContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add("show");
            Shows.Add((target, context));
            return Show(target, context, cancellationToken);
        }

        public Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)> ReadProductVisibilityAsync(
            RoomOperationContext context, CancellationToken cancellationToken)
        {
            Calls.Add("read");
            Reads.Add(context);
            return Read(context, cancellationToken);
        }

        public Task<ControlActionResult> SendTextAsync(string text, RoomOperationContext context,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Product control must not send text.");
    }
}

[CollectionDefinition("T070 product control", DisableParallelization = true)]
public sealed class T070ProductControlCollection;