using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class T021FixturesTests
{
    [Fact]
    public async Task EventSourceInjectsEventsInDeterministicOrder()
    {
        var sessionId = Guid.NewGuid();
        var source = new SimulatedLiveEventSource();
        await source.ConnectAsync(new LiveConnectionRequest(sessionId, "room"), CancellationToken.None);
        var first = CreateEvent(sessionId, "1", LiveEventType.Enter);
        var second = CreateEvent(sessionId, "2", LiveEventType.Comment);

        source.Inject(first);
        source.Inject(second);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var received = new List<LiveEvent>();
        await foreach (var item in source.ReadEventsAsync(cancellation.Token))
        {
            received.Add(item);
            if (received.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(new[] { "1", "2" }, received.Select(item => item.EventId));
        Assert.Equal(new[] { first, second }, source.InjectedEvents);
    }

    [Fact]
    public async Task RoomControllerRecordsProductAndTextActions()
    {
        var controller = new SimulatedLiveRoomController();
        var context = new RoomOperationContext(Guid.NewGuid(), "room", ProductEpoch.Initial);
        var target = new ProductTarget("1", "platform-1");

        var product = await controller.ShowProductAsync(target, context, CancellationToken.None);
        var text = await controller.SendTextAsync("test message", context, CancellationToken.None);
        var visibility = await controller.ReadProductVisibilityAsync(context, CancellationToken.None);

        Assert.Equal(ControlActionStatus.Confirmed, product.Status);
        Assert.Equal(ControlActionStatus.Confirmed, text.Status);
        Assert.Equal(ControlActionStatus.Confirmed, visibility.Status);
        Assert.Equal(target, visibility.Visibility!.Target);
        Assert.Collection(
            controller.Actions,
            action => Assert.Equal(("ShowProduct", target, null), (action.Kind, action.Product, action.Text)),
            action => Assert.Equal(("SendText", null, "test message"), (action.Kind, action.Product, action.Text)));
    }

    [Fact]
    public async Task FakeClockAdvancesUtcAndCompletesDelaysWithoutSleeping()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var delay = clock.DelayAsync(TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        Assert.Equal(DateTimeOffset.UnixEpoch, clock.UtcNow);
        Assert.False(delay.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(delay.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await delay;

        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, clock.Now.Ticks);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(10), clock.UtcNow);
        Assert.Equal(0, clock.PendingDelayCount);
    }

    [Fact]
    public void SeededRandomSourcesRepeatAndDifferentSeedsDiverge()
    {
        var first = new SeededRandomSource(1234);
        var second = new SeededRandomSource(1234);
        var different = new SeededRandomSource(5678);
        var firstSequence = Enumerable.Range(0, 8).Select(_ => first.NextInt32(0, 1000)).ToArray();
        var secondSequence = Enumerable.Range(0, 8).Select(_ => second.NextInt32(0, 1000)).ToArray();
        var differentSequence = Enumerable.Range(0, 8).Select(_ => different.NextInt32(0, 1000)).ToArray();

        Assert.Equal(firstSequence, secondSequence);
        Assert.NotEqual(firstSequence, differentSequence);
    }

    [Fact]
    public async Task FixturesDoNotRequireExternalConnection()
    {
        var sessionId = Guid.NewGuid();
        var source = new SimulatedLiveEventSource();
        var result = await source.ConnectAsync(new LiveConnectionRequest(sessionId, "offline-room"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(LiveSourceState.Connected, source.State);
        Assert.DoesNotContain("http", source.Capabilities.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static LiveEvent CreateEvent(Guid sessionId, string eventId, LiveEventType type)
    {
        return new LiveEvent(
            "simulated",
            sessionId,
            "room",
            type,
            eventId,
            "user-" + eventId,
            "User " + eventId,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            type == LiveEventType.Comment ? "hello" : null,
            null,
            null,
            EventIdentityQuality.Complete);
    }
}
