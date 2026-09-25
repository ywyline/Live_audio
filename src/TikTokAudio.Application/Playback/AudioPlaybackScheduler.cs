using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Playback;

/// <summary>
/// Owns one output and one base playback plan. The host calls PumpAsync after preparation
/// and output state changes (or on a bounded polling loop). Excess concurrent commands
/// return Failed/busy instead of creating an unbounded command queue; Stop and ChangeSession
/// invalidate immediately. Cancellation arguments control admission and bounded queue waits;
/// accepted transitions complete under the session token and are aborted by Stop, not by
/// cancelling their command token. QueueInteraction additionally retains its token for the
/// entire request lifetime. No other host may mutate the output/planner during playback.
/// </summary>
public sealed class AudioPlaybackScheduler
{
    private IBasePlaybackPlan planner;
    private readonly IAudioOutput output;
    private readonly IClock clock;
    private readonly PlaybackEffectOptions effectOptions;
    private readonly int capacity;
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly object sync = new();
    private readonly List<PendingInteraction> pending = [];
    private CancellationTokenSource? lifetime;
    private CancellationToken lifetimeToken;
    private long generation;
    private long roomVersion;
    private string? session;
    private PlaybackState state = PlaybackState.Stopped;
    private PlaybackPlanItem? baseItem;
    private AudioCursor baseCursor = AudioCursor.Start;
    private PendingInteraction? active;
    private bool suspendedBase;
    private bool paused;
    private int preparing;
    private int admittedCommands;
    private string? cleanupFailure;
    private Task<OperationResult> stopBarrier = Task.FromResult(OperationResult.Succeeded());

    public AudioPlaybackScheduler(IBasePlaybackPlan planner, IAudioOutput output, IClock clock, int capacity = 22,
        PlaybackEffectOptions? effectOptions = null)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(clock);
        if (capacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.planner = planner;
        this.output = output;
        this.clock = clock;
        this.capacity = capacity;
        this.effectOptions = effectOptions ?? new PlaybackEffectOptions();
        this.effectOptions.Validate();
    }

    public string? Detail { get { lock (sync) return state == PlaybackState.Preparing && baseItem is null ? planner.Detail ?? "等待合成。" : null; } }
    public PlaybackState State { get { lock (sync) return state; } }
    public PlaybackPlanItem? CurrentBaseItem { get { lock (sync) return baseItem; } }
    public AudioCursor BaseCursor { get { lock (sync) return baseCursor; } }
    public int PendingCount { get { lock (sync) return pending.Count; } }

    public Task<SchedulerUpdate> StartAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return RunAsync((epoch, updates) => StartCoreAsync(epoch, sessionId, updates), cancellationToken);
    }

    private async Task<OperationResult> StartCoreAsync(long epoch, string sessionId, Updates updates, bool startPaused = false)
    {
        Task<OperationResult> barrier;
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            if (session is not null) return OperationResult.Failed("播放会话已经启动。");
            barrier = stopBarrier;
        }
        if (!barrier.IsCompleted) return OperationResult.Failed("音频停止尚未完成，请稍后重试。");
        var stopped = await barrier.ConfigureAwait(false);
        lock (sync)
        {
            if (!Current(epoch) || !ReferenceEquals(barrier, stopBarrier)) return OperationResult.Cancelled();
            if (!stopped.IsSuccess) return stopped;
            lifetime = new CancellationTokenSource();
            lifetimeToken = lifetime.Token;
            session = sessionId;
            paused = startPaused;
            suspendedBase = startPaused;
            baseItem = planner.CurrentItem;
            baseCursor = planner.CurrentCursor;
            state = startPaused ? PlaybackState.Paused : PlaybackState.Preparing;
        }
        if (startPaused) return OperationResult.Succeeded();
        return await PlayBaseAsync(epoch, updates).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicitly replaces the base plan in the same session. Invalidates old playback and
    /// preparation immediately; a failed Stop never starts the replacement. Revisions must
    /// increase. Switching while paused preserves pause until an explicit Resume.
    /// </summary>
    public async Task<SchedulerUpdate> SwitchBasePlanAsync(IBasePlaybackPlan nextPlan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nextPlan);
        if (cancellationToken.IsCancellationRequested) return Empty(OperationResult.Cancelled());
        var updates = new Updates();
        Task<OperationResult> stopping;
        string originalSession;
        bool wasPaused;
        long epoch, room;
        lock (sync)
        {
            if (session is null) return Empty(OperationResult.Failed("播放会话未启动。"));
            if (ReferenceEquals(nextPlan, planner) || nextPlan.Revision.Value <= planner.Revision.Value)
                return Empty(OperationResult.Failed("替换计划必须具有更高的修订号。"));
            epoch = ++generation;
            room = roomVersion;
            originalSession = session;
            wasPaused = paused;
            session = null;
            var old = lifetime;
            lifetime = null;
            planner.CancelPendingPreparation();
            CancelPending(updates, "基础播放模式或计划已切换。");
            CompleteActive(updates, OperationStatus.Cancelled, "基础播放模式或计划已切换。");
            planner = nextPlan;
            baseItem = null;
            baseCursor = AudioCursor.Start;
            suspendedBase = false;
            paused = false;
            state = PlaybackState.Preparing;
            stopping = SafeOutputAsync(() => output.StopAsync(CancellationToken.None));
            stopBarrier = stopping;
            if (old is not null) ReleaseSource(old);
        }
        var stopped = await stopping.ConfigureAwait(false);
        lock (sync)
        {
            if (!Current(epoch) || room != roomVersion) return FinishCommand(epoch, room, updates, OperationResult.Cancelled());
            if (!stopped.IsSuccess) return FinishCommand(epoch, room, updates, Fail(epoch, stopped.Detail ?? "停止旧基础计划失败。"));
        }
        var started = await FinishSwitchAsync(epoch, room, originalSession, wasPaused).ConfigureAwait(false);
        updates.Boundaries.AddRange(started.Boundaries);
        updates.Interactions.AddRange(started.Interactions);
        return FinishCommand(epoch, room, updates, started.Result);
    }
    // Only one accepted switch may wait here: admission clears session immediately.
    // It must finish even if ordinary command capacity was full at the instant of switch.
    private async Task<SchedulerUpdate> FinishSwitchAsync(long epoch, long room, string sessionId, bool wasPaused)
    {
        var updates = new Updates();
        await commands.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (!Current(epoch) || room != roomVersion) return FinishCommand(epoch, room, updates, OperationResult.Cancelled());
            }
            var result = await StartCoreAsync(epoch, sessionId, updates, wasPaused).ConfigureAwait(false);
            return FinishCommand(epoch, room, updates, result);
        }
        catch (OperationCanceledException) { return FinishCommand(epoch, room, updates, OperationResult.Cancelled()); }
        catch (Exception error)
        {
            return FinishCommand(epoch, room, updates, Fail(epoch, $"切换基础计划失败：{error.GetType().Name}"));
        }
        finally { commands.Release(); }
    }
    /// <summary>
    /// Accepts requests already eligible under upstream business rules. The rules owner
    /// retains its category queue and submits at most one outstanding request per category
    /// until the terminal outcome is observed and that category's cooldown has elapsed.
    /// </summary>
    public Task<SchedulerUpdate> QueueInteractionAsync(InteractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.PrepareAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        if (!Enum.IsDefined(request.Priority) || request.TimeToLive <= TimeSpan.Zero)
            return Task.FromResult(Empty(OperationResult.Failed("互动优先级或TTL无效。")));
        return RunAsync((epoch, updates) =>
        {
            lock (sync)
            {
                if (!Current(epoch) || session is null || paused || request.SessionId != session || Expired(request))
                    return Task.FromResult(OperationResult.Cancelled("互动已过期、会话失效或播放已暂停。"));
                if (pending.Any(item => item.Request.RequestId == request.RequestId) || active?.Request.RequestId == request.RequestId)
                    return Task.FromResult(OperationResult.Failed("互动请求标识重复。"));
                if (pending.Count >= capacity || Volatile.Read(ref preparing) >= capacity)
                    return Task.FromResult(OperationResult.Failed("互动准备队列已满。"));
                var item = new PendingInteraction(request, CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken));
                Interlocked.Increment(ref preparing);
                item.Preparation = Task.Run(async () =>
                {
                    try
                    {
                        lock (sync)
                        {
                            if (!Current(epoch) || session != request.SessionId || paused || Expired(request) || item.Token.IsCancellationRequested)
                                return new OperationResult<LocalAudioAsset>(OperationStatus.Cancelled, null, "互动在准备前失效。");
                        }
                        item.Token.ThrowIfCancellationRequested();
                        return await request.PrepareAsync(item.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return new(OperationStatus.Cancelled, null, "互动准备已取消。"); }
                    catch (Exception error) { return new(OperationStatus.Failed, null, $"互动准备异常：{error.GetType().Name}"); }
                    finally { Interlocked.Decrement(ref preparing); }
                });
                pending.Add(item);
                return Task.FromResult(OperationResult.Succeeded());
            }
        }, cancellationToken);
    }

    public Task<SchedulerUpdate> PumpAsync(CancellationToken cancellationToken = default) => RunAsync(PumpCoreAsync, cancellationToken);

    public Task<SchedulerUpdate> PauseAsync(CancellationToken cancellationToken = default) => RunAsync(async (epoch, updates) =>
    {
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            if (session is null) return OperationResult.Failed("播放会话未启动。");
            if (paused) return OperationResult.Succeeded();
            CancelPending(updates, "播放暂停，取消尚未开始的互动。");
            if (active is null && baseItem is null)
            {
                paused = true;
                state = PlaybackState.Paused;
                return OperationResult.Succeeded();
            }
        }
        var result = await CallOutputAsync(epoch, token => output.PauseAsync(token)).ConfigureAwait(false);
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            if (!result.IsSuccess) return SynchronizeOutputFailure(epoch, result);
            if (active is null && !CaptureCursor()) return Fail(epoch, "无法取得暂停后的基础源游标。");
            paused = true;
            state = PlaybackState.Paused;
            return result;
        }
    }, cancellationToken);

    public Task<SchedulerUpdate> ResumeAsync(CancellationToken cancellationToken = default) => RunAsync(async (epoch, updates) =>
    {
        bool needsBase;
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            if (session is null || !paused) return OperationResult.Failed("没有暂停中的播放会话。");
            needsBase = active is null && (suspendedBase || baseItem is null);
            if (needsBase) paused = false;
        }
        if (needsBase) return await PlayBaseAsync(epoch, updates).ConfigureAwait(false);
        var result = await CallOutputAsync(epoch, token => output.ResumeAsync(token)).ConfigureAwait(false);
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            if (!result.IsSuccess) return SynchronizeOutputFailure(epoch, result);
            paused = false;
            state = active is null ? PlaybackState.BasePlaying : PlaybackState.InterruptPlaying;
            return result;
        }
    }, cancellationToken);

    /// <summary>Invalidates old interactions before waiting for any ordinary command.</summary>
    public async Task<SchedulerUpdate> ChangeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (cancellationToken.IsCancellationRequested) return Empty(OperationResult.Cancelled());
        var updates = new Updates();
        Task<OperationResult>? stopping = null;
        long epoch, room;
        lock (sync)
        {
            epoch = generation;
            if (session is null) return Empty(OperationResult.Failed("播放会话未启动。"));
            room = ++roomVersion;
            session = sessionId;
            CancelPending(updates, "直播房间或场次已变化。");
            if (active is not null)
            {
                CompleteActive(updates, OperationStatus.Cancelled, "直播房间或场次已变化。");
                stopping = SafeOutputAsync(() => output.StopAsync(CancellationToken.None));
                stopBarrier = stopping;
                state = paused ? PlaybackState.Paused : PlaybackState.Preparing;
            }
        }
        if (stopping is null) return FinishCommand(epoch, room, updates, OperationResult.Succeeded());
        var result = await stopping.ConfigureAwait(false);
        lock (sync)
        {
            if (!Current(epoch) || room != roomVersion) return FinishCommand(epoch, room, updates, OperationResult.Cancelled());
            if (!result.IsSuccess) return FinishCommand(epoch, room, updates, Fail(epoch, result.Detail ?? "停止旧场次互动失败。"));
            if (paused) return FinishCommand(epoch, room, updates, result);
        }
        // An interrupted Pump may already own the command slot and perform restoration.
        // If bounded admission is full, restoration is deferred to the host's next Pump.
        var restoration = await RunAsync(async (runEpoch, events) =>
        {
            lock (sync)
            {
                if (runEpoch != epoch || !Current(epoch)) return OperationResult.Cancelled();
                if (!suspendedBase) return OperationResult.Succeeded();
            }
            return await PlayBaseAsync(epoch, events).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        updates.Boundaries.AddRange(restoration.Boundaries);
        updates.Interactions.AddRange(restoration.Interactions);
        if (restoration.Result.Detail == BusyDetail)
            return FinishCommand(epoch, room, updates, OperationResult.Succeeded("旧互动已取消；基础恢复由正在执行的调度或下一次Pump完成。"));
        return FinishCommand(epoch, room, updates, restoration.Result);
    }

    /// <summary>Invalidates immediately, bypassing normal command/preparation waits.</summary>
    public async Task<SchedulerUpdate> StopAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Empty(OperationResult.Cancelled());
        var updates = new Updates();
        Task<OperationResult> stopping;
        CancellationTokenSource? old;
        long epoch, room;
        lock (sync)
        {
            epoch = ++generation;
            room = roomVersion;
            old = lifetime;
            lifetime = null;
            session = null;
            paused = false;
            suspendedBase = false;
            state = PlaybackState.Stopped;
            planner.CancelPendingPreparation();
            CancelPending(updates, "播放已停止。");
            CompleteActive(updates, OperationStatus.Cancelled, "播放已停止。");
            stopping = SafeOutputAsync(() => output.StopAsync(CancellationToken.None));
            stopBarrier = stopping;
            if (old is not null) ReleaseSource(old);
        }
        var result = await stopping.ConfigureAwait(false);
        lock (sync)
        {
            if (!result.IsSuccess && Current(epoch)) state = PlaybackState.Error;
            return FinishCommand(epoch, room, updates, result);
        }
    }

    private async Task<OperationResult> PumpCoreAsync(long epoch, Updates updates)
    {
        PendingInteraction? cancelledActive = null;
        PlaybackState outputState;
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            if (session is null) return OperationResult.Cancelled("播放会话未启动。");
            if (paused) return OperationResult.Succeeded();
            outputState = output.State;
            if (active is not null)
            {
                if (active.Token.IsCancellationRequested || active.Request.SessionId != session)
                {
                    cancelledActive = active;
                    CompleteActive(updates, OperationStatus.Cancelled, "正在播放的互动已取消。");
                }
                else if (outputState == PlaybackState.Idle) CompleteActive(updates, OperationStatus.Succeeded, null);
                else if (outputState is PlaybackState.Error or PlaybackState.Stopped)
                    CompleteActive(updates, OperationStatus.Failed, "互动播放未正常完成。");
                else return OperationResult.Succeeded();
            }
            else if (baseItem is not null && !suspendedBase && outputState is PlaybackState.Error or PlaybackState.Stopped)
                return Fail(epoch, "基础音频输出故障或意外停止。");
        }
        if (cancelledActive is not null)
        {
            var stopped = await CallOutputAsync(epoch, token => output.StopAsync(token)).ConfigureAwait(false);
            lock (sync)
            {
                if (!Current(epoch)) return OperationResult.Cancelled();
                if (!stopped.IsSuccess) return Fail(epoch, stopped.Detail ?? "停止互动失败。");
            }
        }
        PendingInteraction[] completed;
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            completed = pending.Where(item => item.Preparation.IsCompleted).ToArray();
        }
        // Every task is already completed, so awaiting cannot hold up Stop or the host loop.
        foreach (var item in completed)
        {
            var prepared = await item.Preparation.ConfigureAwait(false);
            lock (sync)
            {
                if (!Current(epoch)) return OperationResult.Cancelled();
                item.Prepared = prepared;
            }
        }
        PendingInteraction? next;
        bool needsPause;
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            foreach (var item in pending.ToArray())
            {
                if (!Eligible(item)) RemovePending(item, updates, OperationStatus.Cancelled, "互动已取消或过期。");
                else if (item.Prepared is { } prepared && (!prepared.IsSuccess || prepared.Value is null))
                    RemovePending(item, updates, prepared.IsSuccess ? OperationStatus.Failed : prepared.Status, prepared.Detail ?? "互动没有可播放资产。");
            }
            next = pending.Where(item => item.Prepared is { IsSuccess: true, Value: not null })
                .OrderByDescending(item => item.Request.Priority).ThenBy(item => item.Request.ReceivedAt.Ticks).FirstOrDefault();
            needsPause = next is not null && baseItem is not null && !suspendedBase && outputState != PlaybackState.Idle;
            if (next is not null && baseItem is not null && !suspendedBase && outputState == PlaybackState.Idle)
            {
                if (output.CurrentCursor is not { } endedCursor || output.State == PlaybackState.Error)
                    return Fail(epoch, "基础结束后没有有效源游标。");
                baseCursor = endedCursor;
                suspendedBase = true;
            }
        }
        if (next is not null)
        {
            if (needsPause)
            {
                var pause = await CallOutputAsync(epoch, token => output.PauseAsync(token)).ConfigureAwait(false);
                lock (sync)
                {
                    if (!Current(epoch)) return OperationResult.Cancelled();
                    if (!pause.IsSuccess) return SynchronizeOutputFailure(epoch, pause);
                    if (!CaptureCursor()) return Fail(epoch, "插播前无法取得精确基础源游标。");
                    suspendedBase = true;
                }
            }
            bool invalid;
            lock (sync)
            {
                if (!Current(epoch)) return OperationResult.Cancelled();
                invalid = !pending.Contains(next) || !Eligible(next);
                if (invalid)
                {
                    if (pending.Contains(next)) RemovePending(next, updates, OperationStatus.Cancelled, "互动在准备播放时失效。");
                }
                else { pending.Remove(next); active = next; if (baseItem is null) suspendedBase = true; }
            }
            if (invalid) return await PlayBaseAsync(epoch, updates).ConfigureAwait(false);
            var asset = next.Prepared!.Value.Value!;
            var play = await PlayInteractionAsync(epoch, next, asset).ConfigureAwait(false);
            lock (sync)
            {
                if (!Current(epoch)) return OperationResult.Cancelled();
                if (ReferenceEquals(active, next) && play.IsSuccess && next.StartConfirmed && !next.Token.IsCancellationRequested)
                {
                    state = PlaybackState.InterruptPlaying;
                    return play;
                }
                if (ReferenceEquals(active, next)) CompleteActive(updates,
                    next.Token.IsCancellationRequested ? OperationStatus.Cancelled : play.Status, play.Detail);
            }
            var restored = await PlayBaseAsync(epoch, updates).ConfigureAwait(false);
            return restored.IsSuccess ? (play.IsSuccess ? OperationResult.Cancelled() : play) : restored;
        }
        bool restore;
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            restore = suspendedBase;
        }
        if (restore) return await PlayBaseAsync(epoch, updates).ConfigureAwait(false);
        if (outputState == PlaybackState.Idle || baseItem is null)
        {
            bool advance;
            lock (sync) advance = baseItem is not null && state == PlaybackState.BasePlaying;
            var selected = await SelectBaseIfNeededAsync(epoch, advance).ConfigureAwait(false);
            return selected.IsSuccess ? await PlayBaseAsync(epoch, updates).ConfigureAwait(false) : selected;
        }
        return OperationResult.Succeeded();
    }

    private async Task<OperationResult> PlayInteractionAsync(long epoch, PendingInteraction item, LocalAudioAsset asset)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(item.Token);
        var monitor = MonitorStartDeadlineAsync(epoch, item, deadline.Token);
        try
        {
            var result = await CallOutputAsync(epoch,
                token => output.PlayAsync(new(asset, AudioCursor.Start), token), item.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (!Current(epoch) || !ReferenceEquals(active, item)) return OperationResult.Cancelled();
                if (result.IsSuccess && !item.Token.IsCancellationRequested)
                {
                    if (Expired(item.Request)) CancelExpiredStart(item);
                    else item.StartConfirmed = true;
                }
                return item.DeadlineExpired ? OperationResult.Cancelled("互动在输出准备期间过期。") : result;
            }
        }
        finally
        {
            // The monitor owns only an IClock wait, never a preparation or audio task.
            // It is retired before the next interaction so deadline waits stay bounded.
            await deadline.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }
    }

    private async Task MonitorStartDeadlineAsync(long epoch, PendingInteraction item, CancellationToken token)
    {
        try
        {
            TimeSpan remaining;
            lock (sync)
            {
                if (!Current(epoch) || item.Released || !ReferenceEquals(active, item)) return;
                if (Expired(item.Request)) { CancelExpiredStart(item); return; }
                var ticks = (decimal)item.Request.TimeToLive.Ticks - (clock.Now.Ticks - (decimal)item.Request.ReceivedAt.Ticks);
                remaining = TimeSpan.FromTicks((long)Math.Clamp(ticks, 0, TimeSpan.MaxValue.Ticks));
            }
            await clock.DelayAsync(remaining, token).ConfigureAwait(false);
            lock (sync)
            {
                if (!token.IsCancellationRequested && Current(epoch) && !item.Released &&
                    ReferenceEquals(active, item) && !item.StartConfirmed && Expired(item.Request))
                    CancelExpiredStart(item);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (sync)
            {
                cleanupFailure = $"互动播放期限监测失败：{error.GetType().Name}";
                if (Current(epoch) && !item.Released && ReferenceEquals(active, item) && !item.StartConfirmed)
                    CancelExpiredStart(item);
            }
        }
    }

    // Called under sync, which also owns terminal source disposal eligibility.
    private void CancelExpiredStart(PendingInteraction item)
    {
        item.DeadlineExpired = true;
        _ = ObserveCancellationAsync(item.Cancellation.CancelAsync());
    }
    private async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (sync) cleanupFailure = $"取消回调异常：{error.GetType().Name}";
        }
    }
    private async Task<OperationResult> SelectBaseIfNeededAsync(long epoch, bool advance)
    {
        Task<PlaybackPlanItem?> selected;
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            if (baseItem is not null && !advance) return OperationResult.Succeeded();
            var item = baseItem;
            var context = item is null ? new PlaybackPlannerContext(planner.Mode, planner.Revision, null, null, 0)
                : new(item.Mode, item.PlanRevision, item.ProductId, item.GroupId, item.Cycle);
            selected = planner.SelectNextAsync(context, lifetimeToken);
        }
        var next = await selected.ConfigureAwait(false);
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            baseItem = next;
            baseCursor = planner.CurrentCursor;
            return next is null ? NoBaseAvailable(epoch) : OperationResult.Succeeded();
        }
    }

    private async Task<OperationResult> PlayBaseAsync(long epoch, Updates updates)
    {
        var selection = await SelectBaseIfNeededAsync(epoch, false).ConfigureAwait(false);
        if (!selection.IsSuccess) return selection;
        AudioPlaybackRequest request;
        PlaybackPlanItem item;
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            if (paused) return OperationResult.Succeeded();
            if (baseItem is null) return NoBaseAvailable(epoch);
            item = baseItem;
            request = new(item.Asset, baseCursor);
        }
        var effectSelection = PlaybackEffects.Select(item, effectOptions);
        var result = await CallOutputAsync(epoch, token => effectSelection == PlaybackEffectSelection.Bypass
            ? output.PlayAsync(request, token)
            : output is IEffectAudioOutput effectOutput
                ? effectOutput.PlayWithEffectsAsync(request, effectSelection, token)
                : Task.FromResult(OperationResult.Unsupported("The selected audio output does not support effects."))).ConfigureAwait(false);
        OperationResult<IReadOnlyList<ProductBoundary>> acknowledgement;
        lock (sync)
        {
            if (!Current(epoch)) return OperationResult.Cancelled();
            if (!result.IsSuccess) { state = PlaybackState.Error; return result; }
            acknowledgement = planner.AcknowledgePlaybackStarted(item);
            if (acknowledgement.IsSuccess)
            {
                updates.Boundaries.AddRange(acknowledgement.Value!);
                suspendedBase = false;
                state = PlaybackState.BasePlaying;
                return result;
            }
        }
        var stopped = await CallOutputAsync(epoch, token => output.StopAsync(token)).ConfigureAwait(false);
        return Fail(epoch, stopped.IsSuccess ? acknowledgement.Detail ?? "基础播放确认失败，输出已停止。" : "基础播放确认失败，且输出停止失败。");
    }

    // Called only with sync held; output cursor may itself mark output Error.
    private bool CaptureCursor()
    {
        var cursor = output.CurrentCursor;
        if (cursor is null || output.State != PlaybackState.Paused || baseItem is null) return false;
        if (!planner.SaveCursor(cursor.Value).IsSuccess) return false;
        baseCursor = cursor.Value;
        return true;
    }

    // A null selection represents either bounded waiting or normal one-shot completion.
    private OperationResult NoBaseAvailable(long epoch)
    {
        if (!Current(epoch)) return OperationResult.Cancelled();
        suspendedBase = false;
        switch (planner.Availability)
        {
            case BasePlanAvailability.WaitingForSynthesis:
                state = PlaybackState.Preparing;
                return OperationResult.Succeeded(planner.Detail ?? "等待合成。");
            case BasePlanAvailability.Completed:
                state = PlaybackState.Idle;
                return OperationResult.Succeeded("基础计划已播放完成。");
            default:
                return Fail(epoch, planner.Detail ?? "计划没有可播放片段。");
        }
    }
    private bool Eligible(PendingInteraction item) => !item.Token.IsCancellationRequested && item.Request.SessionId == session && !Expired(item.Request);
    private bool Expired(InteractionRequest request)
    {
        var now = clock.Now.Ticks;
        return request.ReceivedAt.Ticks > now || (decimal)now - request.ReceivedAt.Ticks >= request.TimeToLive.Ticks;
    }

    private const string BusyDetail = "播放调度正忙，请在当前命令完成后重试。";
    private async Task<SchedulerUpdate> RunAsync(Func<long, Updates, Task<OperationResult>> action, CancellationToken cancellationToken)
    {
        long epoch, room;
        lock (sync) { epoch = generation; room = roomVersion; }
        var updates = new Updates();
        try
        {
            if (Interlocked.Increment(ref admittedCommands) > capacity + 1)
            {
                Interlocked.Decrement(ref admittedCommands);
                return Empty(OperationResult.Failed(BusyDetail));
            }
            await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref admittedCommands);
            return Empty(OperationResult.Cancelled());
        }
        try
        {
            lock (sync)
            {
                if (!Current(epoch) || room != roomVersion || cancellationToken.IsCancellationRequested)
                    return FinishCommand(epoch, room, updates, OperationResult.Cancelled());
            }
            var result = await action(epoch, updates).ConfigureAwait(false);
            return FinishCommand(epoch, room, updates, result);
        }
        catch (OperationCanceledException) { return FinishCommand(epoch, room, updates, OperationResult.Cancelled()); }
        catch (Exception error)
        {
            lock (sync)
            {
                if (!Current(epoch) || room != roomVersion) return FinishCommand(epoch, room, updates, OperationResult.Cancelled());
                return FinishCommand(epoch, room, updates, Fail(epoch, $"播放调度异常：{error.GetType().Name}"));
            }
        }
        finally { commands.Release(); Interlocked.Decrement(ref admittedCommands); }
    }

    private SchedulerUpdate FinishCommand(long epoch, long room, Updates updates, OperationResult result)
    {
        lock (sync)
        {
            if (!Current(epoch) || room != roomVersion)
            {
                // Terminal outcomes were already removed from ownership: retain them with
                // their originating session so rule reservations can always be released.
                updates.Boundaries.Clear();
                return updates.Finish(OperationResult.Cancelled());
            }
            return updates.Finish(WithCleanupFailure(result));
        }
    }
    private async Task<OperationResult> CallOutputAsync(long epoch, Func<CancellationToken, Task<OperationResult>> call, CancellationToken? playbackToken = null)
    {
        while (true)
        {
            Task<OperationResult> barrier;
            CancellationToken token;
            lock (sync)
            {
                if (!Current(epoch)) return OperationResult.Cancelled();
                token = playbackToken ?? lifetimeToken;
                barrier = stopBarrier;
            }
            try
            {
                var stopped = await barrier.WaitAsync(token).ConfigureAwait(false);
                if (!stopped.IsSuccess) return stopped;
                Task<OperationResult> task;
                lock (sync)
                {
                    if (!Current(epoch) || token.IsCancellationRequested) return OperationResult.Cancelled();
                    if (!ReferenceEquals(barrier, stopBarrier)) continue;
                    task = SafeOutputAsync(() => call(token));
                }
                return await task.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return OperationResult.Cancelled(); }
        }
    }
    private static async Task<OperationResult> SafeOutputAsync(Func<Task<OperationResult>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (OperationCanceledException) { return OperationResult.Cancelled(); }
        catch (Exception error) { return OperationResult.Failed($"音频输出异常：{error.GetType().Name}"); }
    }

    private void CancelPending(Updates updates, string detail)
    {
        foreach (var item in pending.ToArray()) RemovePending(item, updates, OperationStatus.Cancelled, detail);
    }
    private void RemovePending(PendingInteraction item, Updates updates, OperationStatus status, string? detail)
    {
        pending.Remove(item);
        updates.Interactions.Add(new(item.Request.RequestId, status, detail, item.Request.SessionId));
        ReleaseInteraction(item);
    }
    private void CompleteActive(Updates updates, OperationStatus status, string? detail)
    {
        if (active is null) return;
        updates.Interactions.Add(new(active.Request.RequestId, status, detail, active.Request.SessionId));
        ReleaseInteraction(active);
        active = null;
    }
    private void ReleaseInteraction(PendingInteraction item)
    {
        if (item.Released) return;
        item.Released = true;
        _ = CleanupAsync(item.Cancellation, item.Preparation);
    }
    private void ReleaseSource(CancellationTokenSource source) => _ = CleanupAsync(source, null);
    private async Task CleanupAsync(CancellationTokenSource source, Task? preparation)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (sync) cleanupFailure = $"取消回调异常：{error.GetType().Name}";
        }
        finally
        {
            if (preparation is not null) await preparation.ConfigureAwait(false);
            source.Dispose();
        }
    }
    private OperationResult WithCleanupFailure(OperationResult result)
    {
        if (cleanupFailure is null) return result;
        var detail = cleanupFailure;
        cleanupFailure = null;
        return OperationResult.Failed(detail);
    }
    private OperationResult SynchronizeOutputFailure(long epoch, OperationResult result)
    {
        var observed = output.State;
        if (observed == PlaybackState.Paused) { paused = true; state = PlaybackState.Paused; }
        else if (observed is PlaybackState.Error or PlaybackState.Stopped) state = PlaybackState.Error;
        return result;
    }
    private bool Current(long epoch) { lock (sync) return generation == epoch; }
    private OperationResult Fail(long epoch, string detail) { lock (sync) { if (Current(epoch)) state = PlaybackState.Error; } return OperationResult.Failed(detail); }
    private static SchedulerUpdate Empty(OperationResult result) => new(result, Array.Empty<ProductBoundary>(), Array.Empty<InteractionCompletion>());

    private sealed class Updates
    {
        public List<ProductBoundary> Boundaries { get; } = [];
        public List<InteractionCompletion> Interactions { get; } = [];
        public SchedulerUpdate Finish(OperationResult result) => new(result, Boundaries.AsReadOnly(), Interactions.AsReadOnly());
    }
    private sealed class PendingInteraction(InteractionRequest request, CancellationTokenSource cancellation)
    {
        public InteractionRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken Token { get; } = cancellation.Token;
        public Task<OperationResult<LocalAudioAsset>> Preparation { get; set; } = null!;
        public OperationResult<LocalAudioAsset>? Prepared { get; set; }
        public bool Released { get; set; }
        public bool StartConfirmed { get; set; }
        public bool DeadlineExpired { get; set; }
    }
}
