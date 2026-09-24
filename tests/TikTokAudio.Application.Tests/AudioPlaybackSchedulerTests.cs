using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Media;
using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class AudioPlaybackSchedulerTests
{
    [Fact]
    public async Task StartPlaysOnlyOneBaseAndEmitsItsBoundaryOnce()
    {
        var h = new Harness();
        var start = await h.Scheduler.StartAsync("session");
        Assert.Equal(OperationStatus.Succeeded, start.Result.Status);
        Assert.Single(start.Boundaries);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(BasePlaybackMode.PreRecorded, h.Scheduler.CurrentBaseItem!.Mode);
        Assert.Single(h.Output.Plays);
        Assert.Empty((await h.Scheduler.PumpAsync()).Boundaries);
        Assert.NotEqual(OperationStatus.Succeeded, (await h.Scheduler.StartAsync("other")).Result.Status);
        Assert.Single(h.Output.Plays);
    }

    [Fact]
    public async Task SlowPreparationLeavesBasePlayingUntilPreparedAndPumped()
    {
        var h = await Harness.Started();
        var probe = new Preparation();
        var admitted = await h.Scheduler.QueueInteractionAsync(h.Request("slow", prepare: probe.Run));
        Assert.Equal(OperationStatus.Succeeded, admitted.Result.Status);
        await probe.Started.Task;
        for (var i = 0; i < 4; i++) await h.Pump();
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Single(h.Output.Plays);
        probe.Complete("slow");
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Equal(1, h.Output.PauseCount);
        Assert.Equal("slow", h.Output.Current!.Asset.Path);
        Assert.Empty(h.Completions);
    }

    [Theory]
    [InlineData(OperationStatus.Failed)]
    [InlineData(OperationStatus.Unknown)]
    [InlineData(OperationStatus.Cancelled)]
    [InlineData(OperationStatus.Unsupported)]
    public async Task UnsuccessfulPreparationPreservesBaseAndReportsExactStatus(OperationStatus status)
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("failure", prepare: _ =>
            Task.FromResult(new OperationResult<LocalAudioAsset>(status, null, "synthetic preparation result"))));
        await h.Until(() => h.Completions.Count > 0);
        Assert.Equal(status, Assert.Single(h.Completions).Status);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Single(h.Output.Plays);
    }

    [Fact]
    public async Task ThrowingPreparationDoesNotPauseBaseOrLeakCapacity()
    {
        var h = await Harness.Started(1);
        await h.Scheduler.QueueInteractionAsync(h.Request("throw", prepare: _ =>
            throw new InvalidOperationException("synthetic failure")));
        await h.Until(() => h.Completions.Count > 0);
        Assert.Equal(OperationStatus.Failed, Assert.Single(h.Completions).Status);
        Assert.Equal(0, h.Scheduler.PendingCount);
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Equal(OperationStatus.Succeeded,
            (await h.Scheduler.QueueInteractionAsync(h.Request("next"))).Result.Status);
    }

    [Fact]
    public async Task SuccessfulPreparationWithoutAssetIsFailure()
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("missing", prepare: _ =>
            Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, null))));
        await h.Until(() => h.Completions.Count > 0);
        Assert.Equal(OperationStatus.Failed, Assert.Single(h.Completions).Status);
        Assert.Equal(0, h.Output.PauseCount);
    }

    [Fact]
    public async Task ReadyInteractionsUsePriorityAndStableOrderWithoutInterruptingEachOther()
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("active", InteractionPriority.Welcome));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        var requests = new[]
        {
            h.Request("welcome", InteractionPriority.Welcome),
            h.Request("keyword", InteractionPriority.Keyword),
            h.Request("follow-first", InteractionPriority.Follow),
            h.Request("follow-second", InteractionPriority.Follow)
        };
        foreach (var request in requests) await h.Scheduler.QueueInteractionAsync(request);
        for (var i = 0; i < 20; i++) { await h.Pump(); await Task.Yield(); }
        Assert.Equal("active", h.Output.Current!.Asset.Path);
        Assert.Equal(2, h.Output.Plays.Count);
        foreach (var expected in new[] { "follow-first", "follow-second", "keyword", "welcome" })
        {
            h.Output.Complete();
            await h.Until(() => h.Output.Current?.Asset.Path == expected);
            Assert.Equal(PlaybackState.InterruptPlaying, h.Scheduler.State);
        }
        h.Output.Complete();
        await h.Until(() => h.Scheduler.State == PlaybackState.BasePlaying);
        Assert.Equal(new[] { "active", "follow-first", "follow-second", "keyword", "welcome" },
            h.Completions.Select(x => x.RequestId));
        Assert.All(h.Completions, x => Assert.Equal(OperationStatus.Succeeded, x.Status));
    }

    [Fact]
    public async Task NaturalCompletionRestoresSameClipCursorSeedAndConsumedBoundaries()
    {
        var h = await Harness.Started();
        var before = h.Scheduler.CurrentBaseItem!;
        h.Output.Cursor = new AudioCursor(73123, 24000);
        await h.Scheduler.QueueInteractionAsync(h.Request("interaction"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        Assert.Equal(new AudioCursor(73123, 24000), h.Scheduler.BaseCursor);
        Assert.Empty(h.Completions);
        h.Output.Complete();
        await h.Until(() => h.Scheduler.State == PlaybackState.BasePlaying);
        var after = h.Scheduler.CurrentBaseItem!;
        Assert.Equal(before.ClipId, after.ClipId);
        Assert.Equal(before.GroupId, after.GroupId);
        Assert.Equal(before.ProductId, after.ProductId);
        Assert.Equal(before.Cycle, after.Cycle);
        Assert.Equal(before.EffectSeed, after.EffectSeed);
        Assert.Equal(before.PlanRevision, after.PlanRevision);
        Assert.Equal(before.Asset, h.Output.Current!.Asset);
        Assert.Equal(new AudioCursor(73123, 24000), h.Output.Current.StartAt);
        Assert.Empty(h.Boundaries);
        Assert.Equal(OperationStatus.Succeeded, Assert.Single(h.Completions).Status);
        await h.Pump();
        Assert.Single(h.Completions);
    }

    [Fact]
    public async Task OnlyNaturalBaseEndAdvancesPlanner()
    {
        var h = await Harness.Started();
        var first = h.Scheduler.CurrentBaseItem!;
        h.Output.Complete();
        await h.Pump();
        Assert.NotEqual(first.GroupId, h.Scheduler.CurrentBaseItem!.GroupId);
        Assert.Equal("1/2", h.Scheduler.CurrentBaseItem.GroupId);
        Assert.Empty(h.Boundaries);
        h.Output.Complete();
        await h.Pump();
        Assert.Equal(1, h.Scheduler.CurrentBaseItem.Cycle);
        Assert.Single(h.Boundaries);
    }

    [Theory]
    [InlineData(OperationStatus.Failed)]
    [InlineData(OperationStatus.Unknown)]
    [InlineData(OperationStatus.Cancelled)]
    [InlineData(OperationStatus.Unsupported)]
    public async Task InteractionPlayFailureAttemptsExactBaseRestoration(OperationStatus failure)
    {
        var h = await Harness.Started();
        var baseAsset = h.Output.Current!.Asset;
        h.Output.Cursor = new AudioCursor(48000, 24000);
        h.Output.PlayResults.Enqueue(new OperationResult(failure, "interaction output failed"));
        await h.Scheduler.QueueInteractionAsync(h.Request("bad-play"));
        await h.Until(() => h.Completions.Count > 0);
        Assert.Equal(failure, Assert.Single(h.Completions).Status);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(baseAsset, h.Output.Current!.Asset);
        Assert.Equal(new AudioCursor(48000, 24000), h.Output.Current.StartAt);
        Assert.Empty(h.Boundaries);
    }

    [Fact]
    public async Task InteractionOutputErrorIsNotSuccessfulCompletionAndRestoresBase()
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("error"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        h.Output.Fail();
        await h.Pump();
        Assert.Equal(OperationStatus.Failed, Assert.Single(h.Completions).Status);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
    }

    [Fact]
    public async Task FailedBaseRestorationReportsErrorInsteadOfRemainingInterruptPlaying()
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("interaction"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        h.Output.PlayResults.Enqueue(OperationResult.Failed("restoration failure"));
        h.Output.Complete();
        var update = await h.Pump();
        Assert.Equal(OperationStatus.Failed, update.Result.Status);
        Assert.Equal(PlaybackState.Error, h.Scheduler.State);
        Assert.Equal(OperationStatus.Succeeded, Assert.Single(h.Completions).Status);
    }

    [Fact]
    public async Task BaseOutputErrorDoesNotAdvanceOrPretendToFinish()
    {
        var h = await Harness.Started();
        var before = h.Scheduler.CurrentBaseItem!;
        h.Output.Fail();
        var update = await h.Pump();
        Assert.Equal(OperationStatus.Failed, update.Result.Status);
        Assert.Equal(PlaybackState.Error, h.Scheduler.State);
        Assert.Equal(before.ClipId, h.Scheduler.CurrentBaseItem!.ClipId);
        Assert.Single(h.Output.Plays);
    }

    [Fact]
    public async Task PauseCancelsPendingWorkAndResumeOnlyRestartsBase()
    {
        var h = await Harness.Started();
        var probe = new Preparation();
        await h.Scheduler.QueueInteractionAsync(h.Request("pending", prepare: probe.Run));
        await probe.Started.Task;
        h.Record(await h.Scheduler.PauseAsync());
        probe.Complete("pending");
        for (var i = 0; i < 10; i++) await h.Pump();
        Assert.Equal(PlaybackState.Paused, h.Scheduler.State);
        Assert.Single(h.Output.Plays);
        Assert.Equal(OperationStatus.Cancelled, Assert.Single(h.Completions).Status);
        Assert.True(probe.Token.IsCancellationRequested);
        Assert.NotEqual(OperationStatus.Succeeded, (await h.Scheduler.QueueInteractionAsync(h.Request("while-paused"))).Result.Status);
        await h.Scheduler.ResumeAsync();
        await h.Pump();
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Single(h.Output.Plays);
    }

    [Fact]
    public async Task PausedInteractionResumesInteractionWithoutCompletingOrSelectingBase()
    {
        var h = await Harness.Started();
        var original = h.Scheduler.CurrentBaseItem!;
        await h.Scheduler.QueueInteractionAsync(h.Request("voice"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        await h.Scheduler.PauseAsync();
        Assert.Equal(PlaybackState.Paused, h.Scheduler.State);
        await h.Pump();
        Assert.Empty(h.Completions);
        await h.Scheduler.ResumeAsync();
        Assert.Equal(PlaybackState.InterruptPlaying, h.Scheduler.State);
        Assert.Equal("voice", h.Output.Current!.Asset.Path);
        Assert.Equal(original.ClipId, h.Scheduler.CurrentBaseItem!.ClipId);
        Assert.Equal(2, h.Output.Plays.Count);
        Assert.Empty(h.Completions);
    }

    [Fact]
    public async Task StopCancelsActiveAndPendingAndLatePreparationCannotResurrect()
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("active"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        var late = new Preparation();
        await h.Scheduler.QueueInteractionAsync(h.Request("late", prepare: late.Run));
        await late.Started.Task;
        h.Record(await h.Scheduler.StopAsync());
        Assert.Equal(PlaybackState.Stopped, h.Scheduler.State);
        Assert.True(late.Token.IsCancellationRequested);
        var played = h.Output.Plays.Count;
        late.Complete("late");
        for (var i = 0; i < 10; i++) await h.Pump();
        Assert.Equal(PlaybackState.Stopped, h.Scheduler.State);
        Assert.Equal(played, h.Output.Plays.Count);
        Assert.Equal(new[] { "active", "late" }, h.Completions.Select(x => x.RequestId).OrderBy(x => x));
        Assert.All(h.Completions, x => Assert.Equal(OperationStatus.Cancelled, x.Status));
        Assert.NotEqual(OperationStatus.Succeeded, (await h.Scheduler.ResumeAsync()).Result.Status);
        Assert.Equal(PlaybackState.Stopped, h.Scheduler.State);
    }

    [Fact]
    public async Task StopWhileOutputPlayIsPendingStopsLateSuccessfulOutput()
    {
        var h = await Harness.Started();
        var latePlay = new TaskCompletionSource<OperationResult>();
        h.Output.PlayGate = latePlay;
        var playEntered = h.Output.PlayEntered;
        await h.Scheduler.QueueInteractionAsync(h.Request("late-play"));
        var pump = h.Scheduler.PumpAsync();
        while (!playEntered.Task.IsCompleted && !pump.IsCompleted) await Task.Yield();
        if (!playEntered.Task.IsCompleted)
        {
            await pump;
            pump = PumpUntilPlayEntered(h, playEntered.Task);
        }
        await playEntered.Task;
        var stop = h.Scheduler.StopAsync();
        latePlay.SetResult(OperationResult.Succeeded());
        await h.Output.LatePlayFinished.Task;
        h.Record(await pump);
        h.Record(await stop);
        await h.Pump();
        Assert.Equal(PlaybackState.Stopped, h.Scheduler.State);
        Assert.Equal(PlaybackState.Stopped, h.Output.State);
        Assert.DoesNotContain(h.Completions, x => x.Status == OperationStatus.Succeeded);
        Assert.True(h.Output.StopCount > 0);
    }

    [Fact]
    public async Task SessionChangeCancelsOldWorkAndRestoresBaseWithoutBoundaryReplay()
    {
        var h = await Harness.Started();
        var original = h.Scheduler.CurrentBaseItem!;
        h.Output.Cursor = new AudioCursor(24000, 24000);
        await h.Scheduler.QueueInteractionAsync(h.Request("old-active"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        var late = new Preparation();
        await h.Scheduler.QueueInteractionAsync(h.Request("old-pending", prepare: late.Run));
        await late.Started.Task;
        h.Record(await h.Scheduler.ChangeSessionAsync("new-session"));
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(original.Asset, h.Output.Current!.Asset);
        Assert.Equal(new AudioCursor(24000, 24000), h.Output.Current.StartAt);
        Assert.True(late.Token.IsCancellationRequested);
        late.Complete("old-pending");
        for (var i = 0; i < 8; i++) await h.Pump();
        Assert.DoesNotContain(h.Output.Plays, x => x.Asset.Path == "old-pending");
        Assert.Empty(h.Boundaries);
        Assert.All(h.Completions, x => Assert.Equal(OperationStatus.Cancelled, x.Status));
        Assert.NotEqual(OperationStatus.Succeeded,
            (await h.Scheduler.QueueInteractionAsync(h.Request("wrong-session"))).Result.Status);
        Assert.Equal(OperationStatus.Succeeded,
            (await h.Scheduler.QueueInteractionAsync(h.Request("new") with { SessionId = "new-session" })).Result.Status);
    }

    [Fact]
    public async Task ExpiryAtExactTtlRejectsBeforePreparation()
    {
        var h = await Harness.Started();
        var invoked = false;
        var request = h.Request("expired", prepare: _ =>
        {
            invoked = true;
            return Task.FromResult(Prepared("expired"));
        });
        h.Clock.Advance(request.TimeToLive);
        var update = await h.Scheduler.QueueInteractionAsync(request);
        Assert.Equal(OperationStatus.Cancelled, update.Result.Status);
        Assert.False(invoked);
        Assert.Single(h.Output.Plays);
    }

    [Fact]
    public async Task PreparationFinishingAtTtlIsCancelledBeforePausingBase()
    {
        var h = await Harness.Started();
        var probe = new Preparation();
        var request = h.Request("expired", prepare: probe.Run);
        await h.Scheduler.QueueInteractionAsync(request);
        await probe.Started.Task;
        h.Clock.Advance(request.TimeToLive);
        probe.Complete("expired");
        await h.Until(() => h.Completions.Count > 0);
        Assert.Equal(OperationStatus.Cancelled, Assert.Single(h.Completions).Status);
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Single(h.Output.Plays);
    }

    [Fact]
    public async Task CandidateExpiryWhileAnotherInteractionPlaysCannotStartLater()
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("active"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        await h.Scheduler.QueueInteractionAsync(h.Request("waiting") with { TimeToLive = TimeSpan.FromSeconds(1) });
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Output.Complete();
        await h.Until(() => h.Completions.Any(x => x.RequestId == "waiting"));
        Assert.Equal(OperationStatus.Cancelled, h.Completions.Single(x => x.RequestId == "waiting").Status);
        Assert.DoesNotContain(h.Output.Plays, x => x.Asset.Path == "waiting");
    }

    [Fact]
    public async Task HangingPreparationsOccupyBoundedCapacity()
    {
        var h = await Harness.Started(2);
        var one = new Preparation();
        var two = new Preparation();
        await h.Scheduler.QueueInteractionAsync(h.Request("one", prepare: one.Run));
        await h.Scheduler.QueueInteractionAsync(h.Request("two", prepare: two.Run));
        await Task.WhenAll(one.Started.Task, two.Started.Task);
        Assert.Equal(2, h.Scheduler.PendingCount);
        var invoked = false;
        var third = await h.Scheduler.QueueInteractionAsync(h.Request("three", prepare: _ =>
        {
            invoked = true;
            return Task.FromResult(Prepared("three"));
        }));
        Assert.NotEqual(OperationStatus.Succeeded, third.Result.Status);
        Assert.False(invoked);
        Assert.Equal(2, h.Scheduler.PendingCount);
        Assert.Equal(0, h.Output.PauseCount);
        one.Complete("one");
        two.Complete("two");
        await h.Scheduler.StopAsync();
    }

    [Fact]
    public async Task DuplicatePendingRequestIdIsRejectedWithoutSecondPreparation()
    {
        var h = await Harness.Started();
        var probe = new Preparation();
        await h.Scheduler.QueueInteractionAsync(h.Request("same", prepare: probe.Run));
        var duplicate = await h.Scheduler.QueueInteractionAsync(h.Request("same"));
        Assert.NotEqual(OperationStatus.Succeeded, duplicate.Result.Status);
        Assert.Equal(1, h.Scheduler.PendingCount);
        probe.Complete("same");
        await h.Scheduler.StopAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidCapacityIsRejected(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Harness(capacity));

    [Fact]
    public async Task QueueBeforeStartDoesNotPrepareOrPlay()
    {
        var h = new Harness();
        var invoked = false;
        var result = await h.Scheduler.QueueInteractionAsync(h.Request("not-started", prepare: _ =>
        {
            invoked = true;
            return Task.FromResult(Prepared("not-started"));
        }));
        Assert.NotEqual(OperationStatus.Succeeded, result.Result.Status);
        Assert.False(invoked);
        Assert.Empty(h.Output.Plays);
    }

    [Fact]
    public async Task CancelledQueueDoesNotPrepareOrPause()
    {
        var h = await Harness.Started();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var invoked = false;
        var update = await h.Scheduler.QueueInteractionAsync(h.Request("cancelled", prepare: _ =>
        {
            invoked = true;
            return Task.FromResult(Prepared("cancelled"));
        }), cancelled.Token);
        Assert.Equal(OperationStatus.Cancelled, update.Result.Status);
        Assert.False(invoked);
        Assert.Equal(0, h.Output.PauseCount);
    }

    [Fact]
    public async Task FailedInitialPlayHasNoProductBoundaryAndCanBeStopped()
    {
        var h = new Harness();
        h.Output.PlayResults.Enqueue(OperationResult.Failed("unavailable output"));
        var update = await h.Scheduler.StartAsync("session");
        Assert.Equal(OperationStatus.Failed, update.Result.Status);
        Assert.Empty(update.Boundaries);
        Assert.Equal(PlaybackState.Error, h.Scheduler.State);
        await h.Scheduler.StopAsync();
        Assert.Equal(PlaybackState.Stopped, h.Scheduler.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveTtlDoesNotPrepareOrPause(int ttlSeconds)
    {
        var h = await Harness.Started();
        var update = await h.Scheduler.QueueInteractionAsync(h.Request("invalid") with
            { TimeToLive = TimeSpan.FromSeconds(ttlSeconds) });
        Assert.Equal(OperationStatus.Failed, update.Result.Status);
        Assert.Equal(0, h.Scheduler.PendingCount);
        Assert.Equal(0, h.Output.PauseCount);
    }

    [Fact]
    public async Task UnknownPriorityIsRejected()
    {
        var h = await Harness.Started();
        var update = await h.Scheduler.QueueInteractionAsync(h.Request("invalid", (InteractionPriority)987));
        Assert.Equal(OperationStatus.Failed, update.Result.Status);
        Assert.Equal(0, h.Scheduler.PendingCount);
    }

    [Fact]
    public async Task FutureReceivedAtCannotBypassFreshness()
    {
        var h = await Harness.Started();
        var update = await h.Scheduler.QueueInteractionAsync(h.Request("future") with
            { ReceivedAt = new MonotonicTimestamp(h.Clock.Now.Ticks + 1) });
        Assert.Equal(OperationStatus.Cancelled, update.Result.Status);
        Assert.Equal(0, h.Output.PauseCount);
    }

    [Fact]
    public async Task CancelledPreparationIsTerminalAndCannotStartAfterLateSuccess()
    {
        var h = await Harness.Started();
        using var cancellation = new CancellationTokenSource();
        var probe = new Preparation();
        await h.Scheduler.QueueInteractionAsync(h.Request("cancelled", prepare: probe.Run), cancellation.Token);
        await probe.Started.Task;
        cancellation.Cancel();
        await h.Until(() => h.Completions.Count > 0);
        probe.Complete("cancelled");
        for (var i = 0; i < 8; i++) await h.Pump();
        Assert.Equal(OperationStatus.Cancelled, Assert.Single(h.Completions).Status);
        Assert.Equal(0, h.Output.PauseCount);
        Assert.Single(h.Output.Plays);
    }

    [Fact]
    public async Task IgnoringCancellationCannotEvadePreparationCapacityByRestarting()
    {
        var h = await Harness.Started(1);
        var hanging = new Preparation();
        await h.Scheduler.QueueInteractionAsync(h.Request("hanging", prepare: hanging.Run));
        await hanging.Started.Task;
        await h.Scheduler.StopAsync();
        await h.Scheduler.StartAsync("session");
        var rejected = await h.Scheduler.QueueInteractionAsync(h.Request("new"));
        Assert.NotEqual(OperationStatus.Succeeded, rejected.Result.Status);
        hanging.Complete("hanging");
        await h.Scheduler.StopAsync();
    }
    [Fact]
    public async Task StopThenNewStartKeepsNewStreamWhenOldPlayReturns()
    {
        var h = await Harness.Started();
        var gate = new TaskCompletionSource<OperationResult>();
        h.Output.PlayGate = gate;
        await h.Scheduler.QueueInteractionAsync(h.Request("old-play"));
        var pendingPump = PumpUntilPlayEntered(h, h.Output.PlayEntered.Task);
        await h.Output.PlayEntered.Task;
        h.Record(await h.Scheduler.StopAsync());
        await pendingPump;
        Assert.Equal(OperationStatus.Succeeded, (await h.Scheduler.StartAsync("fresh-session")).Result.Status);
        var fresh = h.Output.Current;
        var stops = h.Output.StopCount;
        gate.SetResult(OperationResult.Succeeded());
        await h.Output.LatePlayFinished.Task;
        await h.Pump();
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(PlaybackState.BasePlaying, h.Output.State);
        Assert.Same(fresh, h.Output.Current);
        Assert.Equal(stops, h.Output.StopCount);
        Assert.DoesNotContain(h.Completions, x => x.Status == OperationStatus.Succeeded);
    }

    [Fact]
    public async Task StopWhilePauseIsPendingPreventsLatePauseChangingStoppedState()
    {
        var h = await Harness.Started();
        var gate = new TaskCompletionSource<OperationResult>();
        h.Output.PauseGate = gate;
        var pause = h.Scheduler.PauseAsync();
        await h.Output.PauseEntered.Task;
        h.Record(await h.Scheduler.StopAsync());
        gate.SetResult(OperationResult.Succeeded());
        await h.Output.LatePauseFinished.Task;
        Assert.Equal(OperationStatus.Cancelled, (await pause).Result.Status);
        Assert.Equal(PlaybackState.Stopped, h.Scheduler.State);
        Assert.Equal(PlaybackState.Stopped, h.Output.State);
        Assert.Single(h.Output.Plays);
    }

    [Fact]
    public async Task SessionChangeDuringPendingPlayCancelsOldRequestBeforeItReturns()
    {
        var h = await Harness.Started();
        var original = h.Output.Current!.Asset;
        h.Output.Cursor = new AudioCursor(12345, 24000);
        var gate = new TaskCompletionSource<OperationResult>();
        h.Output.PlayGate = gate;
        await h.Scheduler.QueueInteractionAsync(h.Request("old-session-play"));
        var pendingPump = PumpUntilPlayEntered(h, h.Output.PlayEntered.Task);
        await h.Output.PlayEntered.Task;
        var change = h.Scheduler.ChangeSessionAsync("new-session");
        await DrainCommand(change);
        h.Record(await change);
        h.Record(await pendingPump);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(original, h.Output.Current!.Asset);
        Assert.Equal(new AudioCursor(12345, 24000), h.Output.Current.StartAt);
        gate.SetResult(OperationResult.Succeeded());
        await h.Output.LatePlayFinished.Task;
        await h.Pump();
        Assert.Equal(original, h.Output.Current.Asset);
        Assert.Equal(PlaybackState.BasePlaying, h.Output.State);
        Assert.Equal(OperationStatus.Cancelled, Assert.Single(h.Completions).Status);
        Assert.Empty(h.Boundaries);
    }

    [Fact]
    public async Task FailedOutputPauseReportsErrorRatherThanFalsePlayingState()
    {
        var h = await Harness.Started();
        h.Output.PauseResults.Enqueue(OperationResult.Failed("simulated fade failure"));
        var update = await h.Scheduler.PauseAsync();
        Assert.Equal(OperationStatus.Failed, update.Result.Status);
        Assert.Equal(PlaybackState.Error, h.Scheduler.State);
        Assert.Equal(PlaybackState.Error, h.Output.State);
        Assert.Single(h.Output.Plays);
    }

    [Fact]
    public async Task InteractionExpiringDuringDelayedPauseRestoresBaseWithoutStarting()
    {
        var h = await Harness.Started();
        var original = h.Output.Current!.Asset;
        h.Output.Cursor = new AudioCursor(9999, 24000);
        var gate = new TaskCompletionSource<OperationResult>();
        h.Output.PauseGate = gate;
        await h.Scheduler.QueueInteractionAsync(h.Request("expires-during-fade") with
            { TimeToLive = TimeSpan.FromSeconds(1) });
        var pump = PumpUntilOperationEntered(h, h.Output.PauseEntered.Task);
        await h.Output.PauseEntered.Task;
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        gate.SetResult(OperationResult.Succeeded());
        h.Record(await pump);
        Assert.Equal(OperationStatus.Cancelled, Assert.Single(h.Completions).Status);
        Assert.Equal(PlaybackState.BasePlaying, h.Scheduler.State);
        Assert.Equal(original, h.Output.Current!.Asset);
        Assert.Equal(new AudioCursor(9999, 24000), h.Output.Current.StartAt);
        Assert.DoesNotContain(h.Output.Plays, x => x.Asset.Path == "expires-during-fade");
        Assert.Empty(h.Boundaries);
    }

    [Fact]
    public async Task FailedPauseBeforeInteractionCannotStartVoiceOrClaimBasePlaying()
    {
        var h = await Harness.Started();
        h.Output.PauseResults.Enqueue(OperationResult.Failed("simulated preemption fade failure"));
        await h.Scheduler.QueueInteractionAsync(h.Request("must-not-start"));
        await h.Until(() => h.Output.PauseCount > 0);
        Assert.Equal(PlaybackState.Error, h.Scheduler.State);
        Assert.Equal(PlaybackState.Error, h.Output.State);
        Assert.Single(h.Output.Plays);
        Assert.DoesNotContain(h.Completions, x => x.Status == OperationStatus.Succeeded);
    }

    private static async Task<SchedulerUpdate> PumpUntilOperationEntered(Harness h, Task entered)
    {
        for (var attempt = 0; attempt < 10000; attempt++)
        {
            var task = h.Scheduler.PumpAsync();
            if (entered.IsCompleted) return await task;
            await task;
            if (entered.IsCompleted) return await task;
            await Task.Yield();
        }
        throw new InvalidOperationException("Prepared interaction did not reach its output operation.");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NaturalCompletionSurvivesStopOrSessionChangeDuringBaseRestoration(bool changeSession)
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("completed-before-invalidation"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        var restoreGate = new TaskCompletionSource<OperationResult>();
        h.Output.PlayGate = restoreGate;
        h.Output.Complete();
        var pump = h.Scheduler.PumpAsync();
        await h.Output.PlayEntered.Task;
        var control = changeSession
            ? h.Scheduler.ChangeSessionAsync("next-session")
            : h.Scheduler.StopAsync();
        restoreGate.SetResult(OperationResult.Succeeded());
        h.Record(await control);
        h.Record(await pump);
        await h.Output.LatePlayFinished.Task;
        await h.Pump();
        var completion = Assert.Single(h.Completions);
        Assert.Equal("completed-before-invalidation", completion.RequestId);
        Assert.Equal(OperationStatus.Succeeded, completion.Status);
        Assert.Equal("session", completion.SessionId);
        Assert.Empty(h.Boundaries);
        if (!changeSession)
        {
            Assert.Equal(PlaybackState.Stopped, h.Scheduler.State);
            Assert.Equal(PlaybackState.Stopped, h.Output.State);
        }
    }

    [Fact]
    public async Task PendingCancellationSurvivesStopDuringPauseFadeExactlyOnce()
    {
        var h = await Harness.Started();
        var preparation = new Preparation();
        await h.Scheduler.QueueInteractionAsync(h.Request("cancelled-before-stop", prepare: preparation.Run));
        await preparation.Started.Task;
        var fade = new TaskCompletionSource<OperationResult>();
        h.Output.PauseGate = fade;
        var pause = h.Scheduler.PauseAsync();
        await h.Output.PauseEntered.Task;
        h.Record(await h.Scheduler.StopAsync());
        fade.SetResult(OperationResult.Succeeded());
        h.Record(await pause);
        await h.Output.LatePauseFinished.Task;
        preparation.Complete("cancelled-before-stop");
        await h.Pump();
        var completion = Assert.Single(h.Completions);
        Assert.Equal("cancelled-before-stop", completion.RequestId);
        Assert.Equal(OperationStatus.Cancelled, completion.Status);
        Assert.Equal("session", completion.SessionId);
        Assert.Empty(h.Boundaries);
        Assert.Equal(PlaybackState.Stopped, h.Scheduler.State);
    }

    [Fact]
    public async Task SessionChangeWaitsForActualStopBeforeAnyConcurrentBaseRestoration()
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("old-voice"));
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        var stopGate = new TaskCompletionSource<OperationResult>();
        h.Output.StopGate = stopGate;
        var change = h.Scheduler.ChangeSessionAsync("new-session");
        await h.Output.StopEntered.Task;
        var playCount = h.Output.Plays.Count;
        var pump = h.Scheduler.PumpAsync();
        for (var i = 0; i < 4; i++) await Task.Yield();
        Assert.Equal(playCount, h.Output.Plays.Count);
        stopGate.SetResult(OperationResult.Succeeded());
        h.Record(await change);
        h.Record(await pump);
        await h.Until(() => h.Scheduler.State == PlaybackState.BasePlaying);
        var completion = Assert.Single(h.Completions);
        Assert.Equal(OperationStatus.Cancelled, completion.Status);
        Assert.Equal("session", completion.SessionId);
        Assert.Empty(h.Boundaries);
        Assert.Equal(PlaybackState.BasePlaying, h.Output.State);
    }
    [Fact]
    public async Task InteractionExpiringDuringOutputStartNeverInstallsAndRestoresExactBase()
    {
        var h = await Harness.Started();
        var original = h.Output.Current!.Asset;
        h.Output.Cursor = new AudioCursor(45678, 24000);
        var gate = new TaskCompletionSource<OperationResult>();
        h.Output.PlayGate = gate;
        await h.Scheduler.QueueInteractionAsync(h.Request("expires-in-output-start") with
            { TimeToLive = TimeSpan.FromSeconds(1) });
        var pump = PumpUntilPlayEntered(h, h.Output.PlayEntered.Task);
        await h.Output.PlayEntered.Task;
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        try
        {
            for (var attempt = 0; attempt < 10000 && !h.Output.GatedPlayToken.IsCancellationRequested; attempt++)
                await Task.Yield();
            Assert.True(h.Output.GatedPlayToken.IsCancellationRequested,
                "TTL must cancel the pending output-start request before a late successful preparation installs it.");
        }
        finally { gate.TrySetResult(OperationResult.Succeeded()); }
        h.Record(await pump);
        await h.Output.LatePlayFinished.Task;
        await h.Until(() => h.Scheduler.State == PlaybackState.BasePlaying);
        var completion = Assert.Single(h.Completions);
        Assert.Equal("expires-in-output-start", completion.RequestId);
        Assert.Equal(OperationStatus.Cancelled, completion.Status);
        Assert.Equal("session", completion.SessionId);
        Assert.DoesNotContain(h.Output.InstalledAssets, asset => asset.Path == "expires-in-output-start");
        Assert.Equal(original, h.Output.Current!.Asset);
        Assert.Equal(new AudioCursor(45678, 24000), h.Output.Current.StartAt);
        Assert.Empty(h.Boundaries);
    }

    [Fact]
    public async Task SuccessfullyStartedInteractionContinuesPastTtlUntilNaturalCompletion()
    {
        var h = await Harness.Started();
        await h.Scheduler.QueueInteractionAsync(h.Request("started-before-expiry") with
            { TimeToLive = TimeSpan.FromSeconds(1) });
        await h.Until(() => h.Scheduler.State == PlaybackState.InterruptPlaying);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.Pump();
        Assert.Equal(PlaybackState.InterruptPlaying, h.Scheduler.State);
        Assert.Equal("started-before-expiry", h.Output.Current!.Asset.Path);
        Assert.Empty(h.Completions);
        h.Output.Complete();
        await h.Until(() => h.Scheduler.State == PlaybackState.BasePlaying);
        var completion = Assert.Single(h.Completions);
        Assert.Equal(OperationStatus.Succeeded, completion.Status);
        Assert.Equal("session", completion.SessionId);
    }
    private static async Task DrainCommand(Task command)
    {
        for (var i = 0; i < 10000 && !command.IsCompleted; i++) await Task.Yield();
        Assert.True(command.IsCompleted, "Control command waited for stale output preparation instead of invalidating it.");
    }
    private static Task<SchedulerUpdate> PumpUntilPlayEntered(Harness h, Task entered) =>
        PumpUntilOperationEntered(h, entered);

    private static LocalAudioAsset Asset(string path) => new(path, "wav", "simulated", TimeSpan.FromSeconds(10), 24000);
    private static OperationResult<LocalAudioAsset> Prepared(string path) => new(OperationStatus.Succeeded, Asset(path));

    private sealed class Preparation
    {
        private readonly TaskCompletionSource<OperationResult<LocalAudioAsset>> completion = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public Task<OperationResult<LocalAudioAsset>> Run(CancellationToken token)
        {
            Token = token;
            Started.TrySetResult();
            return completion.Task;
        }
        public void Complete(string path) => completion.TrySetResult(Prepared(path));
    }

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeOutput Output { get; } = new();
        public AudioPlaybackScheduler Scheduler { get; }
        public List<InteractionCompletion> Completions { get; } = [];
        public List<ProductBoundary> Boundaries { get; } = [];

        public Harness(int capacity = 22)
        {
            var groups = new[]
            {
                new ImportedAudioGroup(1, "1/1", [new ImportedAudioClip("1/1/a.wav", Asset("base-a"), null),
                    new ImportedAudioClip("1/1/b.wav", Asset("base-b"), null)]),
                new ImportedAudioGroup(2, "1/2", [new ImportedAudioClip("1/2/c.wav", Asset("base-c"), null)])
            };
            var catalog = new ProductDirectoryImport("memory-root",
                [new ImportedAudioProduct(1, "1", "platform-one", groups, true)], []);
            var planner = new PreRecordedPlaybackPlanner(catalog, PlanRevision.Initial, new SeededRandomSource(17));
            Scheduler = new AudioPlaybackScheduler(planner, Output, Clock, capacity);
        }

        public static async Task<Harness> Started(int capacity = 22)
        {
            var h = new Harness(capacity);
            Assert.Equal(OperationStatus.Succeeded, (await h.Scheduler.StartAsync("session")).Result.Status);
            return h;
        }

        public InteractionRequest Request(string id, InteractionPriority priority = InteractionPriority.Welcome,
            Func<CancellationToken, Task<OperationResult<LocalAudioAsset>>>? prepare = null) =>
            new(id, "session", priority, Clock.Now, TimeSpan.FromSeconds(60), prepare ?? (_ => Task.FromResult(Prepared(id))));

        public void Record(SchedulerUpdate update)
        {
            Completions.AddRange(update.Interactions);
            Boundaries.AddRange(update.Boundaries);
        }

        public async Task<SchedulerUpdate> Pump()
        {
            var update = await Scheduler.PumpAsync();
            Record(update);
            return update;
        }

        public async Task Until(Func<bool> predicate)
        {
            for (var attempt = 0; attempt < 10000 && !predicate(); attempt++)
            {
                await Pump();
                await Task.Yield();
            }
            Assert.True(predicate(), "Expected scheduler transition did not occur after draining asynchronous preparation.");
        }
    }

    private sealed class FakeOutput : IAudioOutput
    {
        private readonly object sync = new();
        private long generation;
        public PlaybackState State { get; private set; } = PlaybackState.Idle;
        public AudioCursor? Cursor { get; set; }
        public AudioCursor? CurrentCursor => Cursor;
        public AudioPlaybackRequest? Current { get; private set; }
        public List<AudioPlaybackRequest> Plays { get; } = [];
        public List<LocalAudioAsset> InstalledAssets { get; } = [];
        public CancellationToken GatedPlayToken { get; private set; }
        public Queue<OperationResult> PlayResults { get; } = new();
        public Queue<OperationResult> PauseResults { get; } = new();
        public int PauseCount { get; private set; }
        public int StopCount { get; private set; }
        public TaskCompletionSource<OperationResult>? PlayGate { get; set; }
        public TaskCompletionSource<OperationResult>? PauseGate { get; set; }
        public TaskCompletionSource<OperationResult>? StopGate { get; set; }
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PlayEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PauseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LatePlayFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LatePauseFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OperationResult> PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken)
        {
            long invocation;
            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested) return OperationResult.Cancelled();
                invocation = ++generation;
                Plays.Add(request);
            }
            var gated = PlayGate;
            OperationResult result;
            try
            {
                if (gated is { } gate)
                {
                    PlayGate = null;
                    GatedPlayToken = cancellationToken;
                    PlayEntered.TrySetResult();
                    result = await gate.Task;
                }
                else result = PlayResults.TryDequeue(out var queued) ? queued : OperationResult.Succeeded();
                lock (sync)
                {
                    // T050 rejects preparation after Stop/new Play or request cancellation before
                    // installing a session. The fake must not invent a late unguarded device start.
                    if (invocation != generation || cancellationToken.IsCancellationRequested)
                        return OperationResult.Cancelled();
                    if (result.IsSuccess)
                    {
                        Current = request;
                        InstalledAssets.Add(request.Asset);
                        Cursor = request.StartAt;
                        State = PlaybackState.BasePlaying;
                    }
                    else State = PlaybackState.Error;
                    return result;
                }
            }
            finally { if (gated is not null) LatePlayFinished.TrySetResult(); }
        }

        public async Task<OperationResult> PauseAsync(CancellationToken cancellationToken)
        {
            long invocation;
            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested) return OperationResult.Cancelled();
                invocation = generation;
                PauseCount++;
            }
            var gated = PauseGate;
            try
            {
                OperationResult result;
                if (gated is { } gate)
                {
                    PauseGate = null;
                    PauseEntered.TrySetResult();
                    result = await gate.Task;
                }
                else result = PauseResults.TryDequeue(out var queued) ? queued : OperationResult.Succeeded();
                lock (sync)
                {
                    // Fade completion uses the same generation/token guard as the T050 backend.
                    if (invocation != generation || cancellationToken.IsCancellationRequested)
                        return OperationResult.Cancelled();
                    State = result.IsSuccess ? PlaybackState.Paused : PlaybackState.Error;
                    return result;
                }
            }
            finally { if (gated is not null) LatePauseFinished.TrySetResult(); }
        }
        public Task<OperationResult> ResumeAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested) return Task.FromResult(OperationResult.Cancelled());
                State = PlaybackState.BasePlaying;
                return Task.FromResult(OperationResult.Succeeded());
            }
        }
        public async Task<OperationResult> StopAsync(CancellationToken cancellationToken)
        {
            long invocation;
            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested) return OperationResult.Cancelled();
                invocation = ++generation;
                StopCount++;
            }
            OperationResult result;
            if (StopGate is { } gate)
            {
                StopGate = null;
                StopEntered.TrySetResult();
                result = await gate.Task;
            }
            else result = OperationResult.Succeeded();
            lock (sync)
            {
                if (generation != invocation) return OperationResult.Cancelled();
                State = result.IsSuccess ? PlaybackState.Stopped : PlaybackState.Error;
                return result;
            }
        }
        public Task<OperationResult> SetEnvironmentMixAsync(EnvironmentMixSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Succeeded());
        public void Complete() { lock (sync) State = PlaybackState.Idle; }
        public void Fail() { lock (sync) State = PlaybackState.Error; }
    }
}