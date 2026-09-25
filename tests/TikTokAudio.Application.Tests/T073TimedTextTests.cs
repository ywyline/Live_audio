using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Control;
using TikTokAudio.Application.Tests.Fixtures;

namespace TikTokAudio.Application.Tests;

public sealed class T073TimedTextTests
{
    [Fact]
    public void EmptyPlannerHasNoWork()
    {
        var planner = Create(new FakeClock());

        Assert.Empty(planner.TakeDue());
        Assert.False(planner.IsEnabled("missing"));
        planner.Reset();
        Assert.Empty(planner.TakeDue());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1025)]
    public void CapacityMustBeBoundedAndPositive(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new FakeClock(), capacity));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    public void CapacityLimitsAreAccepted(int capacity)
    {
        var clock = new FakeClock();
        var planner = Create(clock, capacity);
        planner.Configure(Enumerable.Range(0, capacity).Select(index => Schedule($"schedule-{index}")));

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(capacity, planner.TakeDue().Count);
    }

    [Fact]
    public void DependenciesCannotBeNull()
    {
        Assert.Throws<ArgumentNullException>(() => new TimedTextPlanner(null!, new SeededRandomSource(1), 1));
        Assert.Throws<ArgumentNullException>(() => new TimedTextPlanner(new FakeClock(), null!, 1));
    }

    [Fact]
    public void FirstMessageWaitsForExactIntervalBoundary()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule()]);

        Assert.Empty(planner.TakeDue());
        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.Empty(planner.TakeDue());
        clock.Advance(TimeSpan.FromTicks(1));

        var message = Assert.Single(planner.TakeDue());
        Assert.Equal("first", message.Text);
        Assert.Equal("schedule", message.ScheduleId);
        Assert.Equal(TimeSpan.FromSeconds(25), message.TimeToLive);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(1), message.EndsAtUtc);
        Assert.Empty(planner.TakeDue());
        Assert.Equal(0, clock.PendingDelayCount);
    }

    [Fact]
    public void LatePumpEmitsOneMessageThenSchedulesFromNow()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(messages: ["first", "second", "third"])]);

        clock.Advance(TimeSpan.FromSeconds(137));
        Assert.Equal("first", Assert.Single(planner.TakeDue()).Text);
        Assert.Empty(planner.TakeDue());
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(planner.TakeDue());
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("second", Assert.Single(planner.TakeDue()).Text);
    }

    [Fact]
    public void ResetDiscardsMissedIntervalsButRetainsSequentialCursor()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(messages: ["first", "second"])]);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("first", Assert.Single(planner.TakeDue()).Text);

        clock.Advance(TimeSpan.FromSeconds(100));
        planner.Reset();
        Assert.Empty(planner.TakeDue());
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(planner.TakeDue());
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("second", Assert.Single(planner.TakeDue()).Text);
    }

    [Fact]
    public void RepeatedResetAlwaysSchedulesInTheFuture()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule()]);
        clock.Advance(TimeSpan.FromSeconds(9));
        planner.Reset();
        clock.Advance(TimeSpan.FromSeconds(9));
        planner.Reset();
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(planner.TakeDue());
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(planner.TakeDue());
    }

    [Fact]
    public void LaterWindowStartDelaysTheFirstMessageUntilItsInclusiveBoundary()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(startSeconds: 50)]);
        Assert.False(planner.IsEnabled("schedule"));

        clock.Advance(TimeSpan.FromSeconds(50) - TimeSpan.FromTicks(1));
        Assert.Empty(planner.TakeDue());
        Assert.False(planner.IsEnabled("schedule"));
        clock.Advance(TimeSpan.FromTicks(1));

        Assert.True(planner.IsEnabled("schedule"));
        Assert.Single(planner.TakeDue());
    }

    [Fact]
    public void EarlierWindowStartDoesNotShortenTheFirstInterval()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(startSeconds: 5)]);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(planner.IsEnabled("schedule"));
        Assert.Empty(planner.TakeDue());
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Single(planner.TakeDue());
    }

    [Fact]
    public void EndBoundaryIsExclusiveEvenWhenADeadlineCoincides()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(endSeconds: 10)]);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(planner.TakeDue());
        Assert.False(planner.IsEnabled("schedule"));
        planner.Reset();
        Assert.Empty(planner.TakeDue());
    }

    [Fact]
    public void AlreadyExpiredSchedulesAreNotScheduled()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch.AddHours(2));
        var planner = Create(clock);
        planner.Configure([Schedule()]);

        Assert.False(planner.IsEnabled("schedule"));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Empty(planner.TakeDue());
    }

    [Fact]
    public void DisabledScheduleCannotProduceMessagesUntilEnabledByReplacement()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(enabled: false)]);
        clock.Advance(TimeSpan.FromSeconds(100));
        Assert.False(planner.IsEnabled("schedule"));
        Assert.Empty(planner.TakeDue());
        planner.Reset();
        Assert.Empty(planner.TakeDue());

        planner.Configure([Schedule(enabled: true)]);
        Assert.True(planner.IsEnabled("schedule"));
        Assert.Empty(planner.TakeDue());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(planner.TakeDue());
    }

    [Fact]
    public void DisablingOrRemovingAnExistingScheduleInvalidatesSubmissionCheck()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule()]);
        clock.Advance(TimeSpan.FromSeconds(10));
        var pending = Assert.Single(planner.TakeDue());

        planner.Configure([Schedule(enabled: false)]);
        Assert.False(planner.IsEnabled(pending.ScheduleId));
        planner.Configure([]);
        Assert.False(planner.IsEnabled(pending.ScheduleId));
    }

    [Fact]
    public void SubmissionCheckRejectsMessagesHeldBeyondTheirWindow()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(endSeconds: 12)]);
        clock.Advance(TimeSpan.FromSeconds(10));
        var pending = Assert.Single(planner.TakeDue());

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(clock.UtcNow, pending.EndsAtUtc);
        Assert.False(planner.IsEnabled(pending.ScheduleId));
    }

    [Fact]
    public void SequentialTemplatesWrapAndDifferentSchedulesHaveIndependentCursors()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([
            Schedule("one", ["a", "b"]),
            Schedule("two", ["x", "y"], intervalSeconds: 20)
        ]);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("a", Assert.Single(planner.TakeDue()).Text);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "b", "x" }, planner.TakeDue().Select(message => message.Text));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("a", Assert.Single(planner.TakeDue()).Text);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "b", "y" }, planner.TakeDue().Select(message => message.Text));
    }

    [Fact]
    public void RandomOrderUsesTheInjectedSeedAndIsRepeatable()
    {
        var clock = new FakeClock();
        var random = new SeededRandomSource(712);
        var expectedRandom = new SeededRandomSource(712);
        var planner = new TimedTextPlanner(clock, random, 1);
        string[] messages = ["a", "b", "c", "d"];
        planner.Configure([Schedule(messages: messages, order: TextTemplateOrder.Random)]);

        for (var index = 0; index < 20; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(messages[expectedRandom.NextInt32(0, messages.Length)], Assert.Single(planner.TakeDue()).Text);
        }
    }

    [Fact]
    public void ResetDoesNotRecreateOrConsumeRandomSource()
    {
        var clock = new FakeClock();
        var random = new CountingRandomSource();
        var planner = new TimedTextPlanner(clock, random, 1);
        planner.Configure([Schedule(messages: ["a", "b"], order: TextTemplateOrder.Random)]);
        planner.Reset();
        Assert.Empty(planner.TakeDue());
        Assert.Equal(0, random.Calls);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("a", Assert.Single(planner.TakeDue()).Text);
        planner.Reset();
        Assert.Equal(1, random.Calls);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("b", Assert.Single(planner.TakeDue()).Text);
        Assert.Equal(2, random.Calls);
    }

    [Fact]
    public void ConfigurationCopiesMessagesAndScheduleCollection()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        var messages = new List<string> { "original" };
        var schedules = new List<TimedTextSchedule> { Schedule() with { Messages = messages } };
        planner.Configure(schedules);

        messages[0] = "mutated";
        messages.Clear();
        schedules.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("original", Assert.Single(planner.TakeDue()).Text);
    }

    [Fact]
    public void FailedCapacityReplacementPreservesOriginalDeadlineAndCursor()
    {
        var clock = new FakeClock();
        var planner = Create(clock, 1);
        planner.Configure([Schedule(messages: ["first", "second"])]);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("first", Assert.Single(planner.TakeDue()).Text);
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Throws<ArgumentException>(() => planner.Configure([Schedule("one"), Schedule("two")]));
        clock.Advance(TimeSpan.FromSeconds(7));

        Assert.Equal("second", Assert.Single(planner.TakeDue()).Text);
        Assert.False(planner.IsEnabled("one"));
    }

    [Fact]
    public void FailedEnumerationDoesNotPartiallyReplaceConfiguration()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule()]);

        Assert.Throws<InvalidOperationException>(() => planner.Configure(BrokenSchedules()));
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("schedule", Assert.Single(planner.TakeDue()).ScheduleId);

        static IEnumerable<TimedTextSchedule> BrokenSchedules()
        {
            yield return Schedule("replacement");
            throw new InvalidOperationException("Synthetic enumeration failure.");
        }
    }

    [Fact]
    public void NullAndDuplicateSchedulesAreRejectedAtomically()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule()]);

        Assert.Throws<ArgumentNullException>(() => planner.Configure(null!));
        Assert.Throws<ArgumentException>(() => planner.Configure([null!]));
        Assert.Throws<ArgumentException>(() => planner.Configure([Schedule("same"), Schedule("same")]));
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("schedule", Assert.Single(planner.TakeDue()).ScheduleId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void ScheduleIdentifierMustBeNonempty(string? identifier)
    {
        Assert.Throws<ArgumentException>(() => Create(new FakeClock()).Configure([Schedule(identifier!)]));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void IntervalsAndLifetimesMustBePositive(int interval, int lifetime)
    {
        var schedule = Schedule() with { Interval = TimeSpan.FromSeconds(interval), TimeToLive = TimeSpan.FromSeconds(lifetime) };

        Assert.Throws<ArgumentException>(() => Create(new FakeClock()).Configure([schedule]));
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    public void WindowStartMustPrecedeEnd(int start, int end)
    {
        Assert.Throws<ArgumentException>(() => Create(new FakeClock()).Configure([Schedule(startSeconds: start, endSeconds: end)]));
    }

    [Fact]
    public void UndefinedOrderAndNullMessageListAreRejected()
    {
        var planner = Create(new FakeClock());

        Assert.Throws<ArgumentException>(() => planner.Configure([Schedule() with { Order = (TextTemplateOrder)99 }]));
        Assert.Throws<ArgumentException>(() => planner.Configure([Schedule() with { Messages = null! }]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void MessageCountIsBounded(int count)
    {
        var messages = Enumerable.Repeat("message", count).ToArray();

        Assert.Throws<ArgumentException>(() => Create(new FakeClock()).Configure([Schedule(messages: messages)]));
    }

    [Fact]
    public void MaximumMessageCountAndLengthAreAccepted()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        var text = new string('a', 4096);
        planner.Configure([Schedule(messages: Enumerable.Repeat(text, 1000).ToArray())]);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(text, Assert.Single(planner.TakeDue()).Text);
    }

    [Fact]
    public void OversizeMessagesAreRejectedWithoutSilentTruncation()
    {
        Assert.Throws<ArgumentException>(() => Create(new FakeClock()).Configure([Schedule(messages: [new string('a', 4097)])]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void MessageTextMustBeNonempty(string? message)
    {
        Assert.ThrowsAny<ArgumentException>(() => Create(new FakeClock()).Configure([Schedule(messages: [message!])]));
    }

    [Fact]
    public void TextUsesNfcWithoutDroppingVietnameseAccents()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(messages: ["  ca\u0300 phe\u0302 "])]);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("cà phê", Assert.Single(planner.TakeDue()).Text);
    }

    [Theory]
    [InlineData("Giá 100₫")]
    [InlineData("Giá 5$")]
    [InlineData("5 + 5 = 10")]
    [InlineData("@[2]")]
    [InlineData("<b>literal text</b>")]
    public void OperatorTextPreservesPricesSymbolsAndLiteralMarkers(string message)
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(messages: [message])]);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(message, Assert.Single(planner.TakeDue()).Text);
    }

    [Fact]
    public void MalformedUtf16AndNonWhitespaceControlsAreRejectedAtomically()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule()]);

        Assert.Throws<ArgumentException>(() => planner.Configure([Schedule(messages: ["bad\ud800text"])]));
        Assert.Throws<ArgumentException>(() => planner.Configure([Schedule(messages: ["bad\0text"])]));
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("first", Assert.Single(planner.TakeDue()).Text);
    }

    [Fact]
    public void WhitespaceInsideOperatorTextIsPreserved()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule(messages: ["  first\nsecond\tthird  "])]);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("first\nsecond\tthird", Assert.Single(planner.TakeDue()).Text);
    }

    [Fact]
    public void StoredWindowEndIsUtcEvenWhenConfigurationUsesAnotherOffset()
    {
        var clock = new FakeClock();
        var planner = Create(clock);
        planner.Configure([Schedule() with
        {
            StartsAtUtc = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(8)),
            EndsAtUtc = DateTimeOffset.UnixEpoch.AddHours(1).ToOffset(TimeSpan.FromHours(8))
        }]);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, Assert.Single(planner.TakeDue()).EndsAtUtc.Offset);
    }

    [Fact]
    public void WallClockJumpsWithinWindowCannotAccelerateOrDelayIntervals()
    {
        var clock = new AdjustableClock();
        var planner = Create(clock);
        planner.Configure([Schedule()]);
        clock.Utc = DateTimeOffset.UnixEpoch.AddMinutes(20);
        Assert.Empty(planner.TakeDue());
        clock.Ticks = TimeSpan.FromSeconds(10).Ticks;
        Assert.Single(planner.TakeDue());

        clock.Utc = DateTimeOffset.UnixEpoch.AddSeconds(1);
        Assert.Empty(planner.TakeDue());
        clock.Ticks = TimeSpan.FromSeconds(20).Ticks;

        Assert.Single(planner.TakeDue());
    }

    [Fact]
    public void ClockRollbackBeforeWindowStartSuppressesAnOtherwiseDueMessage()
    {
        var clock = new AdjustableClock();
        var planner = Create(clock);
        planner.Configure([Schedule(startSeconds: 20)]);
        clock.Ticks = TimeSpan.FromSeconds(20).Ticks;
        clock.Utc = DateTimeOffset.UnixEpoch.AddSeconds(19);
        Assert.Empty(planner.TakeDue());
        Assert.False(planner.IsEnabled("schedule"));

        clock.Utc = DateTimeOffset.UnixEpoch.AddSeconds(20);

        Assert.Single(planner.TakeDue());
        Assert.Empty(planner.TakeDue());
    }

    [Fact]
    public void ObservedExpiryIsNotResurrectedByWallClockRollback()
    {
        var clock = new AdjustableClock();
        var planner = Create(clock);
        planner.Configure([Schedule(endSeconds: 12)]);
        clock.Utc = DateTimeOffset.UnixEpoch.AddSeconds(12);
        Assert.False(planner.IsEnabled("schedule"));
        clock.Utc = DateTimeOffset.UnixEpoch.AddSeconds(10);
        clock.Ticks = TimeSpan.FromSeconds(10).Ticks;

        planner.Reset();
        Assert.False(planner.IsEnabled("schedule"));
        Assert.Empty(planner.TakeDue());
    }

    [Fact]
    public void UnrepresentableFutureDeadlineCannotWrapIntoImmediateWork()
    {
        var clock = new AdjustableClock { Ticks = long.MaxValue - 1 };
        var planner = Create(clock);
        planner.Configure([Schedule()]);

        Assert.Empty(planner.TakeDue());
        clock.Ticks = long.MaxValue;
        planner.Reset();
        Assert.Empty(planner.TakeDue());
    }

    private static TimedTextPlanner Create(IClock clock, int capacity = 4) => new(clock, new SeededRandomSource(14), capacity);

    private static TimedTextSchedule Schedule(
        string id = "schedule",
        string[]? messages = null,
        double intervalSeconds = 10,
        double startSeconds = 0,
        double endSeconds = 3600,
        bool enabled = true,
        TextTemplateOrder order = TextTemplateOrder.Sequential) =>
        new(id, messages ?? ["first"], TimeSpan.FromSeconds(intervalSeconds), TimeSpan.FromSeconds(25),
            DateTimeOffset.UnixEpoch.AddSeconds(startSeconds), DateTimeOffset.UnixEpoch.AddSeconds(endSeconds), order, enabled);

    private sealed class CountingRandomSource : IRandomSource
    {
        public int Calls { get; private set; }
        public int NextInt32(int minInclusive, int maxExclusive) => minInclusive + Calls++ % (maxExclusive - minInclusive);
        public double NextDouble() => throw new InvalidOperationException("Only integer template selection is expected.");
    }

    private sealed class AdjustableClock : IClock
    {
        public long Ticks { get; set; }
        public DateTimeOffset Utc { get; set; } = DateTimeOffset.UnixEpoch;
        public MonotonicTimestamp Now => new(Ticks);
        public DateTimeOffset UtcNow => Utc;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The planner must be driven by an explicit pump, not a timer.");
    }
}
