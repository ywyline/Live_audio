using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Control;
using TikTokAudio.Application.Events;
using TikTokAudio.Application.Interactions;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class T073TextDispatchTests
{
    private static readonly Guid SessionId = Guid.Parse("cae49611-adb4-44fb-aa8a-4109e74aa517");
    private const string RoomId = "synthetic-text-room";
    private static readonly TimeSpan LongTtl = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task StartAndEnqueueHaveNoExternalSideEffectUntilPump()
    {
        using var h = new Harness();

        Assert.Empty(h.Controller.Actions);
        Assert.True(h.Dispatch.QueueManual("manual first", LongTtl).IsSuccess);
        Assert.Empty(h.Controller.Actions);
        Assert.Empty(h.Dispatch.DrainLedger());

        var snapshot = await h.Dispatch.PumpAsync();

        Assert.Equal("manual first", Assert.Single(h.Controller.Actions).Text);
        Assert.Equal(ControlActionStatus.Confirmed, snapshot.LastResult?.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch, snapshot.LastSentAt);
        Assert.False(snapshot.HasInFlightOperation);
        Assert.Equal(0, snapshot.QueuedCount);
        Assert.Equal(SessionId, Assert.Single(h.Dispatch.DrainLedger()).SessionId);
    }

    [Fact]
    public async Task ManualMessagesUseFifoAndExactThirtySecondBoundary()
    {
        using var h = new Harness();
        h.Dispatch.QueueManual("first", LongTtl);
        h.Dispatch.QueueManual("second", LongTtl);
        h.Dispatch.QueueManual("third", LongTtl);
        await h.Dispatch.PumpAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        await h.Dispatch.PumpAsync();
        Assert.Equal(new[] { "first" }, h.Sent);
        h.Clock.Advance(TimeSpan.FromTicks(1));
        await h.Dispatch.PumpAsync();
        Assert.Equal(new[] { "first", "second" }, h.Sent);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "first", "second", "third" }, h.Sent);
        Assert.Equal(TimeSpan.FromSeconds(90).Ticks, h.Dispatch.Snapshot.NextSendAt?.Ticks);
    }

    [Theory]
    [InlineData(30, 45, 45)]
    [InlineData(60, 45, 60)]
    [InlineData(10, 0, 10)]
    public async Task EffectiveIntervalRespectsBothOperatorValueAndVerifiedPlatformMinimum(
        int configuredSeconds, int platformSeconds, int effectiveSeconds)
    {
        var options = Options() with
        {
            SendInterval = TimeSpan.FromSeconds(configuredSeconds),
            VerifiedMinimumInterval = TimeSpan.FromSeconds(platformSeconds)
        };
        using var h = new Harness(dispatchOptions: options);
        h.Dispatch.QueueManual("first", LongTtl);
        h.Dispatch.QueueManual("second", LongTtl);
        await h.Dispatch.PumpAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(effectiveSeconds) - TimeSpan.FromTicks(1));
        await h.Dispatch.PumpAsync();
        Assert.Single(h.Sent);
        h.Clock.Advance(TimeSpan.FromTicks(1));
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "first", "second" }, h.Sent);
    }

    [Theory]
    [InlineData(ControlActionStatus.Unknown, ControlActionStatus.Unknown)]
    [InlineData(ControlActionStatus.Rejected, ControlActionStatus.Rejected)]
    [InlineData(ControlActionStatus.Cancelled, ControlActionStatus.Unknown)]
    public async Task UnconfirmedSubmissionStillConsumesIntervalButNeverRetriesOriginal(
        ControlActionStatus response, ControlActionStatus ledgerStatus)
    {
        using var h = new Harness(textStatus: response);
        h.Dispatch.QueueManual("uncertain first", LongTtl);
        h.Dispatch.QueueManual("independent second", LongTtl);
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        await h.Dispatch.PumpAsync();
        Assert.Equal(new[] { "uncertain first" }, h.Sent);

        h.Clock.Advance(TimeSpan.FromTicks(1));
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "uncertain first", "independent second" }, h.Sent);
        var ledger = h.Dispatch.DrainLedger();
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, entry => Assert.Equal(ledgerStatus, entry.Status));
    }

    [Fact]
    public async Task ExpiredQueuedMessageIsDroppedAtExactTtlWithoutLedgerEntry()
    {
        using var h = new Harness();
        h.Dispatch.QueueManual("already submitted", LongTtl);
        await h.Dispatch.PumpAsync();
        h.Dispatch.QueueManual("must expire", TimeSpan.FromSeconds(30));
        h.Dispatch.QueueManual("still fresh", LongTtl);

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "already submitted", "still fresh" }, h.Sent);
        Assert.Equal(1, h.Dispatch.Snapshot.DroppedCount);
        Assert.Equal(2, h.Dispatch.DrainLedger().Count);
    }

    [Fact]
    public async Task FullQueueRejectsNewMessageWithoutEvictingOlderAcceptedMessages()
    {
        using var h = new Harness(dispatchOptions: Options() with { QueueCapacity = 2 });
        Assert.True(h.Dispatch.QueueManual("first", LongTtl).IsSuccess);
        Assert.True(h.Dispatch.QueueManual("second", LongTtl).IsSuccess);
        Assert.False(h.Dispatch.QueueManual("rejected", LongTtl).IsSuccess);
        Assert.Equal(2, h.Dispatch.Snapshot.QueuedCount);
        await h.Dispatch.PumpAsync();
        Assert.True(h.Dispatch.QueueManual("third", LongTtl).IsSuccess);

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "first", "second", "third" }, h.Sent);
    }

    [Fact]
    public async Task FullLedgerAppliesBackpressureUntilRecordsAreDrained()
    {
        using var h = new Harness(dispatchOptions: Options() with { LedgerCapacity = 1 });
        h.Dispatch.QueueManual("first", LongTtl);
        h.Dispatch.QueueManual("second", LongTtl);
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();

        Assert.Single(h.Sent);
        Assert.Equal(1, h.Dispatch.Snapshot.QueuedCount);
        Assert.Equal(1, h.Dispatch.Snapshot.PendingLedgerCount);
        var first = Assert.Single(h.Dispatch.DrainLedger());
        Assert.Empty(h.Dispatch.DrainLedger());
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "first", "second" }, h.Sent);
        var second = Assert.Single(h.Dispatch.DrainLedger());
        Assert.NotEqual(first.ActionId, second.ActionId);
        Assert.Equal(DateTimeOffset.UnixEpoch, first.RecordedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), second.RecordedAtUtc);
    }

    [Fact]
    public async Task LedgerNeverCopiesMessageBodyOrEchoedPlatformDetail()
    {
        const string body = "synthetic-private-message-7f4e";
        var controller = new TextOnlyController { EchoDetail = true };
        using var dispatch = Create(controller, new FakeClock());
        dispatch.Start(SessionId, RoomId);
        dispatch.QueueManual(body, LongTtl);

        await dispatch.PumpAsync();

        Assert.Equal(body, Assert.Single(controller.Sent));
        var entry = Assert.Single(dispatch.DrainLedger());
        Assert.DoesNotContain(body, entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTextDoesNotReadProductVisibilityOrResend()
    {
        var clock = new FakeClock();
        var controller = new TextOnlyController { ResultStatus = ControlActionStatus.Unknown };
        using var dispatch = Create(controller, clock);
        dispatch.Start(SessionId, RoomId);
        dispatch.QueueManual("uncertain", LongTtl);

        await dispatch.PumpAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await dispatch.PumpAsync();

        Assert.Equal("uncertain", Assert.Single(controller.Sent));
        Assert.Equal(ControlActionStatus.Unknown, Assert.Single(dispatch.DrainLedger()).Status);
        Assert.Equal(0, controller.ShowCalls);
        Assert.Equal(0, controller.ReadCalls);
    }

    [Fact]
    public async Task ManualTextPreservesCurrencyAndLiteralMarkersWithoutExecutingProductActions()
    {
        const string text = "Giá 50.000 ₫, giảm 10% @[3]";
        var controller = new TextOnlyController();
        using var dispatch = Create(controller, new FakeClock());
        dispatch.Start(SessionId, RoomId);
        Assert.True(dispatch.QueueManual(text, LongTtl).IsSuccess);

        await dispatch.PumpAsync();

        Assert.Equal(text, Assert.Single(controller.Sent));
        Assert.Equal(0, controller.ShowCalls);
        Assert.Equal(0, controller.ReadCalls);
    }

    [Fact]
    public async Task MissingSendCapabilityDoesNotCallPlatformOrCreateLedgerEntry()
    {
        var controller = new TextOnlyController { Capabilities = new(true, true, false) };
        using var dispatch = Create(controller, new FakeClock());
        dispatch.Start(SessionId, RoomId);
        dispatch.QueueManual("not supported", LongTtl);

        await dispatch.PumpAsync();

        Assert.Empty(controller.Sent);
        Assert.Empty(dispatch.DrainLedger());
        Assert.Equal(RoomControlState.Unsupported, dispatch.Snapshot.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MissingOrDisabledTextRulesNeverSendAutomaticReply(int disabledMode)
    {
        var rule = Rule();
        var configuredRules = disabledMode switch
        {
            0 => Array.Empty<KeywordRule>(),
            1 => new[] { rule with { Enabled = false } },
            2 => new[] { rule with { TextEnabled = false } },
            _ => new[] { rule }
        };
        var interactionOptions = Policy() with { TextEnabled = disabledMode != 3 };
        using var h = new Harness(configuredRules, interactionOptions);
        h.Enqueue(LiveEventType.Comment, "disabled", "hello");

        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Dispatch.PumpAsync();

        Assert.Empty(h.Sent);
        Assert.Empty(h.Dispatch.DrainLedger());
    }

    [Fact]
    public async Task EachKeywordChannelFinishesOnceAndTextDoesNotConsumeVoiceLease()
    {
        using var h = new Harness([Rule()]);
        var candidate = h.AddComment("first");
        var voice = h.Rules.TryPrepareNext();
        Assert.NotNull(voice);
        Assert.True(h.Rules.BeginPlayback(voice.Candidate));

        await h.Dispatch.PumpAsync();

        Assert.Equal("Text hello", Assert.Single(h.Sent));
        Assert.Null(h.Rules.TryPrepareNext());
        Assert.Null(h.Rules.TrySelectTextNext());
        h.Rules.MarkVoiceCompleted(candidate);
        h.Rules.MarkVoiceCompleted(candidate);
        Assert.Null(h.Rules.TryPrepareNext());
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();
        Assert.Single(h.Sent);
        Assert.Single(h.Dispatch.DrainLedger());
    }

    [Fact]
    public async Task TextSuccessNeitherStartsNorResetsKeywordVoiceCooldown()
    {
        using var h = new Harness([Rule()], dispatchOptions: Options() with { SendInterval = TimeSpan.FromSeconds(1) });
        var first = h.AddComment("first");
        await h.Dispatch.PumpAsync();
        var firstVoice = h.Rules.TryPrepareNext();
        Assert.NotNull(firstVoice);
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.MarkVoiceCompleted(first);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = h.AddComment("second");

        await h.Dispatch.PumpAsync();
        Assert.Equal(2, h.Sent.Length);
        Assert.Null(h.Rules.TryPrepareNext());
        h.Clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(second, h.Rules.TryPrepareNext()?.Candidate);
    }

    [Theory]
    [InlineData(LiveEventType.Enter, 15)]
    [InlineData(LiveEventType.Follow, 10)]
    [InlineData(LiveEventType.Comment, 10)]
    public async Task ManualTextCannotResetWelcomeFollowOrKeywordCooldown(LiveEventType kind, int seconds)
    {
        using var h = new Harness([Rule()]);
        var first = h.Add(kind, "first");
        Assert.NotNull(h.Rules.TryPrepareNext());
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.MarkVoiceCompleted(first);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = h.Add(kind, "second");
        h.Dispatch.QueueManual("manual independent", LongTtl);

        await h.Dispatch.PumpAsync();
        Assert.Equal("manual independent", Assert.Single(h.Sent));
        Assert.Null(h.Rules.TryPrepareNext());
        h.Clock.Advance(TimeSpan.FromSeconds(seconds - 5));

        Assert.Equal(second, h.Rules.TryPrepareNext()?.Candidate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspensionClearsCoolingTextButPreservesWaitingVoice(bool disconnect)
    {
        using var h = new Harness([Rule()], Policy() with { TextCooldown = TimeSpan.FromSeconds(60) });
        var first = h.AddComment("first");
        Assert.NotNull(h.Rules.TryPrepareNext());
        Assert.True(h.Rules.BeginPlayback(first));
        h.Rules.MarkVoiceCompleted(first);
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var waiting = h.AddComment("waiting");
        Assert.Null(h.Rules.TrySelectTextNext());

        Assert.True((disconnect ? h.Dispatch.Disconnect() : h.Dispatch.Pause()).IsSuccess);
        h.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(h.Dispatch.Resume(SessionId, RoomId).IsSuccess);
        await h.Dispatch.PumpAsync();

        Assert.Single(h.Sent);
        Assert.Null(h.Rules.TrySelectTextNext());
        Assert.Equal(waiting, h.Rules.TryPrepareNext()?.Candidate);
    }

    [Fact]
    public async Task ResumeDropsCommentsReceivedWhileDisconnectedWithoutClearingPlayingVoice()
    {
        using var h = new Harness([Rule()], Policy() with { KeywordCooldown = TimeSpan.Zero });
        var playing = h.AddComment("playing");
        Assert.NotNull(h.Rules.TryPrepareNext());
        Assert.True(h.Rules.BeginPlayback(playing));
        h.Dispatch.Disconnect();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var arrivedOffline = h.AddComment("offline");
        h.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(h.Dispatch.Resume(SessionId, RoomId).IsSuccess);
        await h.Dispatch.PumpAsync();

        Assert.Empty(h.Sent);
        Assert.Null(h.Rules.TrySelectTextNext());
        Assert.Null(h.Rules.TryPrepareNext());
        h.Rules.MarkVoiceCompleted(playing);
        Assert.Equal(arrivedOffline, h.Rules.TryPrepareNext()?.Candidate);
    }

    [Fact]
    public async Task SuspensionDoesNotResetCompletedTextCooldown()
    {
        using var h = new Harness([Rule()], Policy() with { TextCooldown = TimeSpan.FromSeconds(60) });
        h.AddComment("first");
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Dispatch.Pause();
        h.Dispatch.Resume(SessionId, RoomId);
        h.AddComment("fresh after resume");

        h.Clock.Advance(TimeSpan.FromSeconds(29));
        await h.Dispatch.PumpAsync();
        Assert.Single(h.Sent);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();

        Assert.Equal(2, h.Sent.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResumeRequiresSameSessionAndRoom(bool changedSession)
    {
        using var h = new Harness();
        h.Dispatch.QueueManual("discard on disconnect", LongTtl);
        h.Dispatch.Disconnect();

        var result = h.Dispatch.Resume(changedSession ? Guid.NewGuid() : SessionId,
            changedSession ? RoomId : "another-room");
        Assert.False(result.IsSuccess);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();

        Assert.Empty(h.Sent);
        Assert.Empty(h.Dispatch.DrainLedger());
    }

    [Fact]
    public async Task StopCannotResumeAndRestartPreservesPhysicalSendInterval()
    {
        using var h = new Harness();
        h.Dispatch.QueueManual("already submitted", LongTtl);
        await h.Dispatch.PumpAsync();
        h.Dispatch.QueueManual("old pending", LongTtl);
        h.Dispatch.Stop();
        Assert.False(h.Dispatch.Resume(SessionId, RoomId).IsSuccess);
        Assert.False(h.Dispatch.QueueManual("while stopped", LongTtl).IsSuccess);
        Assert.Equal(0, h.Dispatch.Snapshot.QueuedCount);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(h.Dispatch.Start(SessionId, RoomId).IsSuccess);
        h.Dispatch.QueueManual("new pending", LongTtl);

        await h.Dispatch.PumpAsync();
        Assert.Single(h.Sent);
        h.Clock.Advance(TimeSpan.FromSeconds(29));
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "already submitted", "new pending" }, h.Sent);
    }

    [Fact]
    public async Task ResumePreservesPhysicalIntervalAndDiscardsQueuedManualText()
    {
        using var h = new Harness();
        h.Dispatch.QueueManual("already submitted", LongTtl);
        await h.Dispatch.PumpAsync();
        h.Dispatch.QueueManual("old pending", LongTtl);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Dispatch.Pause();
        Assert.False(h.Dispatch.QueueManual("while paused", LongTtl).IsSuccess);
        h.Dispatch.Resume(SessionId, RoomId);
        h.Dispatch.QueueManual("new pending", LongTtl);

        await h.Dispatch.PumpAsync();
        Assert.Single(h.Sent);
        h.Clock.Advance(TimeSpan.FromSeconds(29));
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "already submitted", "new pending" }, h.Sent);
    }

    [Fact]
    public async Task TimedAndManualMessagesShareIntervalAndQueueOrder()
    {
        using var h = new Harness();
        h.Dispatch.ConfigureSchedules([Schedule(TimeSpan.FromSeconds(10))]);
        h.Dispatch.QueueManual("manual first", LongTtl);
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.Dispatch.PumpAsync();
        Assert.Single(h.Sent);
        h.Dispatch.QueueManual("manual after timed", LongTtl);

        h.Clock.Advance(TimeSpan.FromSeconds(20));
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "manual first", "timed message", "manual after timed" }, h.Sent);
    }

    [Fact]
    public async Task KeywordTimedAndManualCandidatesUseOneFifoChannel()
    {
        using var h = new Harness([Rule()]);
        h.Dispatch.QueueManual("manual first", LongTtl);
        h.AddComment("keyword");
        await h.Dispatch.PumpAsync();
        h.Dispatch.ConfigureSchedules([Schedule(TimeSpan.FromSeconds(10))]);
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.Dispatch.PumpAsync();
        h.Dispatch.QueueManual("manual last", LongTtl);
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Dispatch.PumpAsync();

        Assert.Equal(new[] { "manual first", "Text hello", "timed message", "manual last" }, h.Sent);
    }

    [Fact]
    public async Task TimedDispatchAfterReconnectStartsInTheFutureWithoutBacklog()
    {
        using var h = new Harness();
        h.Dispatch.ConfigureSchedules([Schedule(TimeSpan.FromSeconds(30))]);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Dispatch.Disconnect();
        h.Clock.Advance(TimeSpan.FromSeconds(90));
        h.Dispatch.Resume(SessionId, RoomId);
        await h.Dispatch.PumpAsync();
        Assert.Empty(h.Sent);

        h.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        await h.Dispatch.PumpAsync();
        Assert.Empty(h.Sent);
        h.Clock.Advance(TimeSpan.FromTicks(1));
        await h.Dispatch.PumpAsync();

        Assert.Equal("timed message", Assert.Single(h.Sent));
    }

    [Theory]
    [InlineData(-86400)]
    [InlineData(86400)]
    public async Task WallClockJumpCannotChangeTotalIntervalOrExtendManualTtl(int wallJumpSeconds)
    {
        var clock = new AdjustableClock();
        var controller = new TextOnlyController();
        using var dispatch = Create(controller, clock);
        dispatch.Start(SessionId, RoomId);
        dispatch.QueueManual("first", LongTtl);
        await dispatch.PumpAsync();
        dispatch.QueueManual("expires before slot", TimeSpan.FromSeconds(20));
        dispatch.QueueManual("fresh at next slot", LongTtl);
        clock.WallOffset = TimeSpan.FromSeconds(wallJumpSeconds);
        clock.Inner.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        await dispatch.PumpAsync();
        Assert.Single(controller.Sent);

        clock.Inner.Advance(TimeSpan.FromTicks(1));
        await dispatch.PumpAsync();

        Assert.Equal(new[] { "first", "fresh at next slot" }, controller.Sent);
    }

    [Theory]
    [InlineData("", 30)]
    [InlineData("   ", 30)]
    [InlineData("valid", 0)]
    [InlineData("valid", -1)]
    public void InvalidManualInputReturnsFailureWithoutEnqueueing(string text, int ttlSeconds)
    {
        using var h = new Harness();

        Assert.False(h.Dispatch.QueueManual(text, TimeSpan.FromSeconds(ttlSeconds)).IsSuccess);
        Assert.Equal(0, h.Dispatch.Snapshot.QueuedCount);
        Assert.Empty(h.Dispatch.DrainLedger());
        Assert.Empty(h.Sent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1025)]
    [InlineData(1, 0)]
    [InlineData(1, 4097)]
    [InlineData(2, 0)]
    [InlineData(2, 1025)]
    [InlineData(3, 0)]
    [InlineData(3, -1)]
    [InlineData(4, 0)]
    [InlineData(4, -1)]
    [InlineData(5, -1)]
    public void InvalidCapacitiesAndTimingsAreRejected(int field, int value)
    {
        var options = field switch
        {
            0 => Options() with { QueueCapacity = value },
            1 => Options() with { LedgerCapacity = value },
            2 => Options() with { ScheduleCapacity = value },
            3 => Options() with { RequestTimeout = TimeSpan.FromTicks(value) },
            4 => Options() with { SendInterval = TimeSpan.FromTicks(value) },
            _ => Options() with { VerifiedMinimumInterval = TimeSpan.FromTicks(value) }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TextDispatchCoordinator(new SimulatedLiveRoomController(), new FakeClock(),
                new SeededRandomSource(73), options));
    }

    private static TextDispatchOptions Options() => new(8, 32, 8, TimeSpan.FromSeconds(5), TimeSpan.Zero);

    private static InteractionPolicyOptions Policy() => new() { KeywordTtl = LongTtl };

    private static KeywordRule Rule() => new("keyword", (IReadOnlyList<string>)new[] { "hello" })
    {
        VoiceTemplates = new[] { "Voice {comment}" },
        TextTemplates = new[] { "Text {comment}" }
    };

    private static TimedTextSchedule Schedule(TimeSpan interval) => new("synthetic-schedule",
        new[] { "timed message" }, interval, LongTtl, DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddMinutes(10));

    private static TextDispatchCoordinator Create(ILiveRoomController controller, IClock clock) =>
        new(controller, clock, new SeededRandomSource(73), Options());

    private sealed class Harness : IDisposable
    {
        public FakeClock Clock { get; } = new();
        public SimulatedLiveRoomController Controller { get; }
        public InteractionRuleCoordinator Rules { get; }
        public TextDispatchCoordinator Dispatch { get; }
        public string?[] Sent => Controller.Actions.Select(action => action.Text).ToArray();

        public Harness(IEnumerable<KeywordRule>? rules = null, InteractionPolicyOptions? policy = null,
            TextDispatchOptions? dispatchOptions = null, ControlActionStatus textStatus = ControlActionStatus.Confirmed)
        {
            Controller = new SimulatedLiveRoomController(textStatus: textStatus);
            Rules = new InteractionRuleCoordinator(Clock, rules, policy ?? Policy(), new SeededRandomSource(61));
            Rules.SetContext(SessionId, RoomId);
            Dispatch = new TextDispatchCoordinator(Controller, Clock, new SeededRandomSource(73), dispatchOptions ?? Options(), Rules);
            Assert.True(Dispatch.Start(SessionId, RoomId).IsSuccess);
        }

        public InteractionAdmissionResult Enqueue(LiveEventType type, string eventId, string? content = null)
        {
            var liveEvent = new LiveEvent("synthetic-source", SessionId, RoomId, type, eventId,
                "synthetic-user-" + eventId, "Synthetic Name", Clock.UtcNow, Clock.UtcNow,
                content, null, null, EventIdentityQuality.Complete);
            var reserved = type is LiveEventType.Enter or LiveEventType.Follow;
            return Rules.Enqueue(liveEvent, new EventProcessResult(
                reserved ? EventProcessStatus.Reserved : EventProcessStatus.Accepted,
                "synthetic-fingerprint-" + eventId, liveEvent.IdentityQuality, reserved ? Guid.NewGuid() : null));
        }

        public InteractionCandidate Add(LiveEventType type, string eventId)
        {
            var result = Enqueue(type, eventId, type == LiveEventType.Comment ? "hello" : null);
            Assert.True(result.Accepted, result.Detail);
            return Assert.IsType<InteractionCandidate>(result.Candidate);
        }

        public InteractionCandidate AddComment(string eventId) => Add(LiveEventType.Comment, eventId);
        public void Dispose() => Dispatch.Dispose();
    }

    private sealed class TextOnlyController : ILiveRoomController
    {
        public RoomControlState State => RoomControlState.Ready;
        public RoomControlCapabilities Capabilities { get; init; } = new(true, true, true);
        public ControlActionStatus ResultStatus { get; init; } = ControlActionStatus.Confirmed;
        public bool EchoDetail { get; init; }
        public List<string> Sent { get; } = [];
        public int ShowCalls { get; private set; }
        public int ReadCalls { get; private set; }

        public Task<ControlActionResult> SendTextAsync(string text, RoomOperationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(text);
            return Task.FromResult(new ControlActionResult(ResultStatus, EchoDetail ? text : "synthetic result"));
        }

        public Task<ControlActionResult> ShowProductAsync(ProductTarget target, RoomOperationContext context,
            CancellationToken cancellationToken)
        {
            ShowCalls++;
            throw new InvalidOperationException("Text dispatch cannot show products.");
        }

        public Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)> ReadProductVisibilityAsync(
            RoomOperationContext context, CancellationToken cancellationToken)
        {
            ReadCalls++;
            throw new InvalidOperationException("Text dispatch cannot read product visibility.");
        }
    }

    private sealed class AdjustableClock : IClock
    {
        public FakeClock Inner { get; } = new();
        public TimeSpan WallOffset { get; set; }
        public MonotonicTimestamp Now => Inner.Now;
        public DateTimeOffset UtcNow => Inner.UtcNow + WallOffset;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Inner.DelayAsync(delay, cancellationToken);
    }
}
