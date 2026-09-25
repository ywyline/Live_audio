using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Events;
using TikTokAudio.Application.Interactions;
using TikTokAudio.Domain;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class T100EventPlaybackTests
{
    [Fact]
    public async Task SlowVoicePersistsReservationBeforeInterruptAndCompletionOnlyAfterEnd()
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        var prepared = new TaskCompletionSource<TtsSynthesisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Provider.Handler = (_, token) => prepared.Task.WaitAsync(token);
        var item = h.Event(LiveEventType.Follow);
        var admission = await h.DeliverAsync(item);
        var reservation = admission.Admission.ReservationId!.Value;
        var original = h.Scheduler.CurrentBaseItem!;
        h.Output.CurrentCursor = new AudioCursor(45678, 24000);

        await h.QueueAsync();
        await h.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.PumpAsync();

        Assert.NotNull(await h.Store.FindEventAsync(admission.Admission.Fingerprint));
        Assert.Null((await h.Store.LoadInteractionAsync(reservation))!.CompletedAtUtc);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Single(h.Output.Plays);

        prepared.SetResult(h.Provider.Success());
        await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Null((await h.Store.LoadInteractionAsync(reservation))!.CompletedAtUtc);
        Assert.Equal(original.ClipId, h.Scheduler.CurrentBaseItem!.ClipId);
        h.Output.Complete();
        await h.UntilAsync(() => h.Settlements.Count == 1);

        Assert.Equal(OperationStatus.Succeeded, h.Settlements[0].PlaybackStatus);
        Assert.Equal(OperationStatus.Succeeded, h.Settlements[0].ReservationStatus);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(new AudioCursor(45678, 24000), h.Output.Plays[^1].StartAt);
        Assert.Equal(original.ClipId, h.Scheduler.CurrentBaseItem!.ClipId);
        Assert.Equal(original.EffectSeed, h.Scheduler.CurrentBaseItem.EffectSeed);
        Assert.Equal(original.Cycle, h.Scheduler.CurrentBaseItem.Cycle);
        Assert.Single(h.Boundaries);

        var checkpoint = h.Plan.CaptureSnapshot(h.Scheduler.BaseCursor).Checkpoint;
        Assert.True((await h.Store.SavePlaybackCheckpointAsync(checkpoint)).IsSuccess);
        var reopened = await h.ReopenAsync();
        Assert.NotNull((await reopened.LoadInteractionAsync(reservation))!.CompletedAtUtc);
        Assert.Equal(checkpoint.Cursor, (await reopened.LoadPlaybackCheckpointAsync(BasePlaybackMode.PreRecorded))!.Cursor);
        Assert.Equal(checkpoint.ConsumedMarkerIds.Order(), (await reopened.LoadPlaybackCheckpointAsync(BasePlaybackMode.PreRecorded))!.ConsumedMarkerIds.Order());
    }

    [Fact]
    public async Task EventReplayAfterReconnectAndStoreReopenCannotCreateAnotherVoice()
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        var item = h.Event(LiveEventType.Enter);
        await h.DeliverAsync(item);
        await h.QueueAsync();
        await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        h.Output.Complete();
        await h.UntilAsync(() => h.Settlements.Count == 1);
        var plays = h.Output.Plays.Count;
        await h.ReconnectAsync();

        Assert.Equal(EventProcessStatus.Duplicate, (await h.DeliverAsync(item)).Admission.Status);
        var reopened = await h.ReopenAsync();
        var freshProcessor = new EventDeduplicationCoordinator(reopened);
        Assert.True((await freshProcessor.ReconnectAsync(new(h.SessionId, T100PlaybackHarness.RoomId))).IsSuccess);
        Assert.Equal(EventProcessStatus.Duplicate, (await freshProcessor.ProcessAsync(item)).Status);
        await h.QueueAsync();
        await h.PumpAsync();

        Assert.Equal(plays, h.Output.Plays.Count);
        Assert.Single(h.Provider.Requests);
        Assert.Equal(h.SessionId, (await reopened.LoadSessionAsync(h.SessionId))!.SessionId);
    }

    [Fact]
    public async Task WelcomeAndFollowHaveSeparateOncePerUserSlotsAndCompleteThroughPlayback()
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        foreach (var type in new[] { LiveEventType.Enter, LiveEventType.Follow })
        {
            var admitted = await h.DeliverAsync(h.Event(type));
            Assert.Equal(EventProcessStatus.Reserved, admitted.Admission.Status);
            await h.QueueAsync();
            await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
            h.Output.Complete();
            var prior = h.Settlements.Count;
            await h.UntilAsync(() => h.Settlements.Count > prior);
            Assert.NotNull((await h.Store.LoadInteractionAsync(admitted.Admission.ReservationId!.Value))!.CompletedAtUtc);
            Assert.Equal(EventProcessStatus.Completed, (await h.DeliverAsync(h.Event(type))).Admission.Status);
        }
        Assert.Equal(2, h.Provider.Requests.Count);
        Assert.Equal(2, h.Settlements.Count);
    }

    [Theory]
    [InlineData(LiveEventType.Enter)]
    [InlineData(LiveEventType.Follow)]
    public async Task MissingUserIdentityIsRecordedAsDegradedButNeverCreatesVoice(LiveEventType type)
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        var admitted = await h.DeliverAsync(h.Event(type, user: null, displayName: "synthetic-shared-name"));

        Assert.Equal(EventProcessStatus.SkippedMissingUserId, admitted.Admission.Status);
        Assert.Null(admitted.Candidate);
        Assert.Equal(EventIdentityQuality.MissingUserId, (await h.Store.FindEventAsync(admitted.Admission.Fingerprint))!.Quality);
        await h.QueueAsync();
        await h.PumpAsync();
        Assert.Empty(h.Provider.Requests);
        Assert.Single(h.Output.Plays);
        Assert.Empty(h.Controller.Actions);
    }

    [Theory]
    [InlineData(LiveEventType.Like)]
    [InlineData(LiveEventType.Comment)]
    public async Task LikesAndUnmatchedCommentsArePersistedWithoutSynthesisOrReplies(LiveEventType type)
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        var admitted = await h.DeliverAsync(h.Event(type, content: "unmatched synthetic content"));
        Assert.Equal(EventProcessStatus.Accepted, admitted.Admission.Status);
        Assert.Null(admitted.Candidate);
        Assert.NotNull(await h.Store.FindEventAsync(admitted.Admission.Fingerprint));
        await h.QueueAsync();
        await h.PumpAsync();
        Assert.Empty(h.Provider.Requests);
        Assert.Empty(h.Controller.Actions);
        Assert.Equal(0, h.Output.PauseCount);
    }

    [Fact]
    public async Task FailedSynthesisDoesNotPauseBaseOrMarkReservationCompleted()
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        h.Provider.Handler = (_, _) => Task.FromResult(new TtsSynthesisOutcome(OperationStatus.Failed, null, "synthetic failure"));
        var item = h.Event(LiveEventType.Follow);
        var admitted = await h.DeliverAsync(item);
        await h.QueueAsync();
        await h.UntilAsync(() => h.Settlements.Count == 1);

        Assert.Equal(OperationStatus.Failed, h.Settlements[0].PlaybackStatus);
        Assert.Null((await h.Store.LoadInteractionAsync(admitted.Admission.ReservationId!.Value))!.CompletedAtUtc);
        Assert.Equal(EventProcessStatus.Duplicate, (await h.DeliverAsync(item)).Admission.Status);
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Single(h.Output.Plays);
        h.Provider.Handler = null;
        await h.DeliverAsync(h.Event(LiveEventType.Follow, "different-user"));
        await h.QueueAsync();
        await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Equal(2, h.Provider.Requests.Count);
    }

    [Fact]
    public async Task OutputFailureRestoresSourceCursorWithoutSuccessfulCompletion()
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        h.Output.CurrentCursor = new AudioCursor(12345, 24000);
        h.Output.FailInteraction = true;
        var admitted = await h.DeliverAsync(h.Event(LiveEventType.Follow));
        await h.QueueAsync();
        await h.UntilAsync(() => h.Settlements.Count == 1);

        Assert.Equal(OperationStatus.Failed, h.Settlements[0].PlaybackStatus);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(new AudioCursor(12345, 24000), h.Output.Plays[^1].StartAt);
        Assert.Null((await h.Store.LoadInteractionAsync(admitted.Admission.ReservationId!.Value))!.CompletedAtUtc);
        Assert.Single(h.Boundaries);
    }

    [Theory]
    [InlineData(LiveEventType.Enter, 15)]
    [InlineData(LiveEventType.Follow, 10)]
    [InlineData(LiveEventType.Comment, 10)]
    public async Task CategoryCooldownStartsAfterFullCompletionNotAtAdmissionOrPreparation(LiveEventType type, int cooldownSeconds)
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        await h.DeliverAsync(h.Event(type, "first", "giá"));
        await h.QueueAsync();
        await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        await h.DeliverAsync(h.Event(type, "second", "giá"));
        await h.PumpAsync();
        Assert.Single(h.Provider.Requests);
        Assert.Equal(2, h.Output.Plays.Count);
        h.Output.Complete();
        await h.UntilAsync(() => h.Settlements.Count == 1);
        await h.QueueAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(cooldownSeconds) - TimeSpan.FromTicks(1));
        await h.QueueAsync();
        Assert.Single(h.Provider.Requests);
        h.Clock.Advance(TimeSpan.FromTicks(1));
        await h.QueueAsync();
        await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Equal(2, h.Provider.Requests.Count);
    }

    [Fact]
    public async Task WrongEngineRevisionIsDiscardedWithoutPlaybackOrSuccessfulReservation()
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        h.Provider.Handler = (_, _) => Task.FromResult(new TtsSynthesisOutcome(OperationStatus.Succeeded,
            new(new LocalAudioAsset("stale-result.wav", "wav", h.Provider.EngineId, TimeSpan.FromSeconds(1), 24000),
                h.Provider.EngineId, new EngineRevision(99)), null));
        var admitted = await h.DeliverAsync(h.Event(LiveEventType.Follow));
        await h.QueueAsync();
        await h.UntilAsync(() => h.Settlements.Count == 1);

        Assert.True(h.AssetDiscarded.Task.IsCompletedSuccessfully);
        Assert.Equal(OperationStatus.Cancelled, h.Settlements[0].PlaybackStatus);
        Assert.Null((await h.Store.LoadInteractionAsync(admitted.Admission.ReservationId!.Value))!.CompletedAtUtc);
        Assert.Single(h.Output.Plays);
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
    }

    [Fact]
    public async Task PauseHoldsActiveVoiceAndSuppressesQueuedTextUntilExplicitResume()
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        var admitted = await h.DeliverAsync(h.Event(LiveEventType.Follow));
        h.Output.CurrentCursor = new AudioCursor(54321, 24000);
        await h.QueueAsync();
        await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.True(h.Text.QueueManual("synthetic queued text", TimeSpan.FromMinutes(5)).IsSuccess);
        Assert.True((await h.Scheduler.PauseAsync()).Result.IsSuccess);
        Assert.True(h.Text.Pause().IsSuccess);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await h.PumpAsync();

        Assert.Equal(PlaybackState.Paused, h.Scheduler.State);
        Assert.Empty(h.Controller.Actions);
        Assert.Empty(h.Settlements);
        Assert.Null((await h.Store.LoadInteractionAsync(admitted.Admission.ReservationId!.Value))!.CompletedAtUtc);
        Assert.True((await h.Scheduler.ResumeAsync()).Result.IsSuccess);
        Assert.True(h.Text.Resume(h.SessionId, T100PlaybackHarness.RoomId).IsSuccess);
        await h.PumpAsync();
        Assert.Equal(PlaybackState.InterruptPlaying, h.Scheduler.State);
        Assert.Empty(h.Controller.Actions);
        h.Output.Complete();
        await h.UntilAsync(() => h.Settlements.Count == 1);
        Assert.NotNull((await h.Store.LoadInteractionAsync(admitted.Admission.ReservationId!.Value))!.CompletedAtUtc);
        Assert.Equal(new AudioCursor(54321, 24000), h.Output.Plays[^1].StartAt);
        Assert.Single(h.Boundaries);
    }

    [Fact]
    public async Task NewWelcomeReplacesSlowCandidateWithoutCompletingOrPlayingOldOne()
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        var late = new TaskCompletionSource<TtsSynthesisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Provider.Handler = (_, _) => late.Task; // Deliberately non-cooperative engine completion.
        var old = await h.DeliverAsync(h.Event(LiveEventType.Enter, "old-user"));
        await h.QueueAsync();
        await h.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var latest = await h.DeliverAsync(h.Event(LiveEventType.Enter, "latest-user"));
        await h.PumpAsync();
        late.SetResult(h.Provider.Success());
        await h.AssetDiscarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.UntilAsync(() => h.Settlements.Any(s => s.RequestId == old.Candidate!.CandidateId));
        Assert.Equal(0, h.Output.PauseCount);
        h.Provider.Handler = null;
        await h.QueueAsync();
        await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        h.Output.Complete();
        await h.UntilAsync(() => h.Settlements.Any(s => s.RequestId == latest.Candidate!.CandidateId));

        Assert.Null((await h.Store.LoadInteractionAsync(old.Admission.ReservationId!.Value))!.CompletedAtUtc);
        Assert.NotNull((await h.Store.LoadInteractionAsync(latest.Admission.ReservationId!.Value))!.CompletedAtUtc);
        Assert.Single(h.Output.Plays, p => p.Asset.EngineId == "memory-engine");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpirationOrStopInvalidatesLateSynthesisWithoutCompletingReservation(bool stop)
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        var late = new TaskCompletionSource<TtsSynthesisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Provider.Handler = (_, _) => late.Task;
        var admitted = await h.DeliverAsync(h.Event(LiveEventType.Follow));
        await h.QueueAsync();
        await h.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (stop)
        {
            h.Rules.Stop();
            var stopped = await h.Voice.StopAsync();
            h.Settlements.AddRange(stopped.Settlements);
        }
        else
        {
            h.Clock.Advance(h.Rules.Options.FollowTtl);
            await h.PumpAsync();
        }
        late.SetResult(h.Provider.Success());
        await h.AssetDiscarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.UntilAsync(() => h.Settlements.Count == 1);
        await h.PumpAsync();

        Assert.Single(h.Output.Plays);
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Null((await h.Store.LoadInteractionAsync(admitted.Admission.ReservationId!.Value))!.CompletedAtUtc);
        Assert.Equal(OperationStatus.Cancelled, h.Settlements[0].PlaybackStatus);
        Assert.Equal(stop ? PlaybackState.Stopped : PlaybackState.BasePlaying, h.Scheduler.State);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task KeywordChannelsAreIndependentAndRemoteMarkersNeverBecomeProductBoundaries(bool voice, bool text)
    {
        await using var h = await T100PlaybackHarness.CreateAsync(new InteractionPolicyOptions { VoiceEnabled = voice, TextEnabled = text });
        var item = h.Event(LiveEventType.Comment, content: "giá @[999]", displayName: "Tiếng Việt @[888]");
        var admitted = await h.DeliverAsync(item);
        Assert.NotNull(admitted.Candidate);
        await h.QueueAsync();
        await h.PumpAsync();
        if (voice)
        {
            await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
            h.Output.Complete();
            await h.UntilAsync(() => h.Settlements.Count == 1);
            var request = Assert.Single(h.Provider.Requests);
            Assert.Contains("Tiếng Việt", request.Text);
            Assert.DoesNotContain("@[", request.Text);
        }
        else Assert.Empty(h.Provider.Requests);
        Assert.Equal(text ? 1 : 0, h.Controller.Actions.Count);
        Assert.Single(h.Boundaries); // Only the trusted base plan's original product entry.
        Assert.Equal(EventProcessStatus.Duplicate, (await h.DeliverAsync(item)).Admission.Status);
        await h.QueueAsync();
        await h.PumpAsync();
        Assert.Equal(voice ? 1 : 0, h.Provider.Requests.Count);
        Assert.Equal(text ? 1 : 0, h.TextLedger.Count);
        if (text)
        {
            var entry = Assert.Single(h.TextLedger);
            Assert.Equal(ControlActionStatus.Confirmed, entry.Status);
            Assert.Equal(entry, await (await h.ReopenAsync()).LoadActionLedgerAsync(entry.ActionId));
        }
    }

    [Fact]
    public async Task FollowOverflowPersistsReceiptsButOnlyKeepsTwentyPendingVoices()
    {
        await using var h = await T100PlaybackHarness.CreateAsync();
        var reservations = new List<Guid>();
        for (var index = 0; index < 22; index++)
        {
            var admitted = await h.DeliverAsync(h.Event(LiveEventType.Follow, $"synthetic-{index}"));
            reservations.Add(admitted.Admission.ReservationId!.Value);
            Assert.NotNull(await h.Store.FindEventAsync(admitted.Admission.Fingerprint));
        }
        Assert.Equal(20, h.Rules.Snapshot.FollowCount);
        await h.PumpAsync();
        foreach (var id in reservations.Take(2)) Assert.Null((await h.Store.LoadInteractionAsync(id))!.CompletedAtUtc);
        await h.QueueAsync();
        await h.UntilAsync(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Contains("synthetic-2", Assert.Single(h.Provider.Requests).Text);
        Assert.Equal(2, h.Output.Plays.Count);
    }
}
