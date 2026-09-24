using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Events;
using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Interactions;

/// <summary>
/// Bridges interaction rules to local TTS and the single voice scheduler.
/// It owns the reservation-to-request mapping so business completion is written only
/// after the scheduler reports a fully finished interruption.
/// </summary>
public sealed class InteractionPlaybackCoordinator
{
    private readonly InteractionRuleCoordinator rules;
    private readonly AudioPlaybackScheduler scheduler;
    private readonly TtsEngineRegistry engines;
    private readonly ITtsAudioCache cache;
    private readonly IClock clock;
    private readonly Func<TtsEngineSnapshot, TtsGenerationSettings> settingsFactory;
    private readonly ILiveEventProcessor? eventProcessor;
    private readonly string segmenterVersion;
    private readonly object gate = new();
    private readonly Dictionary<string, PendingRequest> requests = new(StringComparer.Ordinal);
    private readonly HashSet<string> settledReservations = new(StringComparer.Ordinal);

    public InteractionPlaybackCoordinator(
        InteractionRuleCoordinator rules,
        AudioPlaybackScheduler scheduler,
        TtsEngineRegistry engines,
        ITtsAudioCache cache,
        IClock clock,
        Func<TtsEngineSnapshot, TtsGenerationSettings> settingsFactory,
        ILiveEventProcessor? eventProcessor = null,
        string segmenterVersion = "interaction-v1")
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.engines = engines ?? throw new ArgumentNullException(nameof(engines));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        if (string.IsNullOrWhiteSpace(segmenterVersion)) throw new ArgumentException("分段器版本不能为空。", nameof(segmenterVersion));
        this.eventProcessor = eventProcessor;
        this.segmenterVersion = segmenterVersion;
    }

    /// <summary>Claims the next voice candidate and submits its local preparation to the scheduler.</summary>
    public async Task<OperationResult> QueueNextAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var decision = rules.TrySelectNext();
        if (decision is null) return OperationResult.Succeeded("当前没有可播放互动。");
        return await QueueCandidateAsync(decision.Candidate, sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits an already leased candidate. The caller must have obtained it from TrySelectNext.
    /// </summary>
    public async Task<OperationResult> QueueCandidateAsync(
        InteractionCandidate candidate,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (gate)
        {
            if (requests.ContainsKey(candidate.CandidateId))
                return OperationResult.Failed("互动请求标识重复。");
        }
        if (candidate.SessionId is { } candidateSession && !string.Equals(candidateSession.ToString("D"), sessionId, StringComparison.Ordinal))
            return await RejectAsync(candidate, "互动场次与播放会话不一致。", cancellationToken).ConfigureAwait(false);
        if (!rules.CanPlay(candidate, InteractionChannel.Voice))
            return await RejectAsync(candidate, "互动租约已失效或已过期。", cancellationToken).ConfigureAwait(false);

        var snapshot = engines.BeginPlaybackUse();
        PendingRequest? pending = null;
        try
        {
            var provider = engines.ActiveProvider;
            if (provider.EngineId != snapshot.EngineId || provider.Revision != snapshot.Revision)
                return await RejectWithEngineAsync(candidate, snapshot, "语音引擎快照已变化。", cancellationToken).ConfigureAwait(false);

            var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pending = new PendingRequest(candidate, sessionId, snapshot, requestCts);
            var request = new InteractionRequest(
                candidate.CandidateId,
                sessionId,
                ToPriority(candidate.Kind),
                new MonotonicTimestamp(clock.Now.Ticks),
                candidate.TimeToLive,
                token => PrepareAsync(candidate, provider, snapshot, token));
            var queued = await scheduler.QueueInteractionAsync(request, requestCts.Token).ConfigureAwait(false);
            if (!queued.Result.IsSuccess)
            {
                requestCts.Dispose();
                pending = null;
                rules.Release(candidate, InteractionChannel.Voice);
                engines.EndPlaybackUse();
                await CancelReservationOnceAsync(candidate, CancellationToken.None).ConfigureAwait(false);
                return queued.Result;
            }

            lock (gate)
            {
                if (requests.ContainsKey(candidate.CandidateId))
                    throw new InvalidOperationException("互动请求标识重复。");
                requests.Add(candidate.CandidateId, pending);
            }
            return OperationResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (pending is not null) pending.Cancellation.Dispose();
            rules.Release(candidate, InteractionChannel.Voice);
            engines.EndPlaybackUse();
            await CancelReservationOnceAsync(candidate, CancellationToken.None).ConfigureAwait(false);
            return OperationResult.Cancelled("互动入队已取消。");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            if (pending is not null) pending.Cancellation.Dispose();
            rules.Release(candidate, InteractionChannel.Voice);
            engines.EndPlaybackUse();
            await CancelReservationOnceAsync(candidate, CancellationToken.None).ConfigureAwait(false);
            return OperationResult.Failed($"互动入队失败：{error.Message}");
        }
    }

    /// <summary>Drives the scheduler and settles every terminal interaction completion.</summary>
    public async Task<InteractionPlaybackUpdate> PumpAsync(CancellationToken cancellationToken = default)
    {
        var discards = rules.DrainDiscards();
        foreach (var discard in discards)
            await HandleDiscardAsync(discard, cancellationToken).ConfigureAwait(false);

        var update = await scheduler.PumpAsync(cancellationToken).ConfigureAwait(false);
        var settlements = new List<InteractionSettlement>(update.Interactions.Count);
        foreach (var completion in update.Interactions)
            settlements.Add(await SettleAsync(completion, cancellationToken).ConfigureAwait(false));

        foreach (var discard in rules.DrainDiscards())
            await HandleDiscardAsync(discard, cancellationToken).ConfigureAwait(false);
        return new(update, settlements);
    }

    public async Task<InteractionPlaybackUpdate> StopAsync(CancellationToken cancellationToken = default)
    {
        var update = await scheduler.StopAsync(cancellationToken).ConfigureAwait(false);
        var settlements = new List<InteractionSettlement>(update.Interactions.Count);
        foreach (var completion in update.Interactions)
            settlements.Add(await SettleAsync(completion, cancellationToken).ConfigureAwait(false));
        foreach (var discard in rules.DrainDiscards())
            await HandleDiscardAsync(discard, cancellationToken).ConfigureAwait(false);
        return new(update, settlements);
    }

    private async Task<OperationResult<LocalAudioAsset>> PrepareAsync(
        InteractionCandidate candidate,
        ITtsProvider provider,
        TtsEngineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!rules.IsFresh(candidate)) return new(OperationStatus.Cancelled, null, "互动已过期。");
        var settings = settingsFactory(snapshot);
        if (settings.Identity is null || settings.Identity.EngineId != snapshot.EngineId ||
            provider.EngineId != snapshot.EngineId || provider.Revision != snapshot.Revision)
            return new(OperationStatus.Cancelled, null, "互动准备使用了过期语音引擎。");

        var document = new TtsScriptDocument(
            candidate.RenderedText,
            segmenterVersion,
            [new TtsScriptSegment(0, candidate.RenderedText, null)]);
        var generator = new TtsPreGenerator(provider, cache, 1);
        var prepared = await generator.PrepareAsync(document, 0, 1, settings, cancellationToken).ConfigureAwait(false);
        if (prepared.Status != OperationStatus.Succeeded || !prepared.Snapshot.CanStartPlayback)
            return new(prepared.Status, null, prepared.Detail ?? "互动语音尚未准备好。");
        var asset = prepared.Snapshot.Segments[0].Asset;
        if (asset.EngineId != snapshot.EngineId)
            return new(OperationStatus.Cancelled, null, "忽略旧引擎生成的互动语音。");
        if (!rules.BeginPlayback(candidate))
            return new(OperationStatus.Cancelled, null, "互动租约在音频准备期间失效。");
        return new(OperationStatus.Succeeded, asset);
    }

    private async Task<InteractionSettlement> SettleAsync(
        InteractionCompletion completion,
        CancellationToken cancellationToken)
    {
        PendingRequest? pending;
        lock (gate) requests.Remove(completion.RequestId, out pending);
        if (pending is null) return new(completion.RequestId, completion.Status, OperationStatus.Failed, "找不到互动请求。");

        try
        {
            if (!string.Equals(completion.SessionId, pending.SessionId, StringComparison.Ordinal))
            {
                if (TrySettlePending(pending))
                {
                    rules.Release(pending.Candidate, InteractionChannel.Voice);
                    var cancelled = await CancelReservationOnceAsync(pending.Candidate, CancellationToken.None).ConfigureAwait(false);
                    return new(completion.RequestId, OperationStatus.Cancelled, cancelled.Status,
                        "互动完成回报的 SessionId 不匹配。");
                }
                return new(completion.RequestId, OperationStatus.Cancelled, OperationStatus.Cancelled,
                    "互动完成回报的 SessionId 不匹配。");
            }

            if (completion.Status == OperationStatus.Succeeded && TrySettlePending(pending))
            {
                rules.MarkVoiceCompleted(pending.Candidate);
                var recorded = await CompleteReservationOnceAsync(pending.Candidate, CancellationToken.None).ConfigureAwait(false);
                return new(completion.RequestId, completion.Status, recorded.Status, recorded.Detail);
            }

            if (TrySettlePending(pending))
            {
                rules.Release(pending.Candidate, InteractionChannel.Voice);
                var cancelled = await CancelReservationOnceAsync(pending.Candidate, CancellationToken.None).ConfigureAwait(false);
                return new(completion.RequestId, completion.Status, cancelled.Status, completion.Detail ?? cancelled.Detail);
            }
            return new(completion.RequestId, completion.Status, OperationStatus.Cancelled, completion.Detail);
        }
        finally
        {
            pending.Cancellation.Dispose();
            engines.EndPlaybackUse();
        }
    }

    private async Task HandleDiscardAsync(InteractionDiscard discard, CancellationToken cancellationToken)
    {
        PendingRequest? pending;
        lock (gate) requests.TryGetValue(discard.Candidate.CandidateId, out pending);
        if (pending is not null)
        {
            if (TrySettlePending(pending))
            {
                await CancelReservationOnceAsync(discard.Candidate, CancellationToken.None).ConfigureAwait(false);
            }
            try { await pending.Cancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            return;
        }
        await CancelReservationOnceAsync(discard.Candidate, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<OperationResult> RejectAsync(InteractionCandidate candidate, string detail, CancellationToken cancellationToken)
    {
        rules.Release(candidate, InteractionChannel.Voice);
        await CancelReservationOnceAsync(candidate, CancellationToken.None).ConfigureAwait(false);
        return OperationResult.Cancelled(detail);
    }

    private async Task<OperationResult> RejectWithEngineAsync(
        InteractionCandidate candidate,
        TtsEngineSnapshot snapshot,
        string detail,
        CancellationToken cancellationToken)
    {
        rules.Release(candidate, InteractionChannel.Voice);
        engines.EndPlaybackUse();
        await CancelReservationOnceAsync(candidate, CancellationToken.None).ConfigureAwait(false);
        return OperationResult.Cancelled(detail);
    }

    private async Task<OperationResult> CompleteReservationOnceAsync(InteractionCandidate candidate, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!settledReservations.Add(candidate.CandidateId)) return OperationResult.Succeeded("reservation 已结算。");
        }
        if (eventProcessor is null || candidate.ReservationId is not { } reservationId || reservationId == Guid.Empty)
            return OperationResult.Succeeded();
        return await eventProcessor.CompleteAsync(reservationId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<OperationResult> CancelReservationOnceAsync(InteractionCandidate candidate, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!settledReservations.Add(candidate.CandidateId)) return OperationResult.Cancelled("reservation 已结算。");
        }
        if (eventProcessor is null || candidate.ReservationId is not { } reservationId || reservationId == Guid.Empty)
            return OperationResult.Cancelled();
        return await eventProcessor.CancelAsync(reservationId, CancellationToken.None).ConfigureAwait(false);
    }

    private bool TrySettlePending(PendingRequest pending)
    {
        lock (gate)
        {
            if (pending.ReservationSettled) return false;
            pending.ReservationSettled = true;
            return true;
        }
    }

    private static InteractionPriority ToPriority(InteractionKind kind) => kind switch
    {
        InteractionKind.Follow => InteractionPriority.Follow,
        InteractionKind.Keyword => InteractionPriority.Keyword,
        _ => InteractionPriority.Welcome
    };

    private sealed class PendingRequest(InteractionCandidate candidate, string sessionId, TtsEngineSnapshot engine, CancellationTokenSource cancellation)
    {
        public InteractionCandidate Candidate { get; } = candidate;
        public string SessionId { get; } = sessionId;
        public TtsEngineSnapshot Engine { get; } = engine;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool ReservationSettled { get; set; }
    }
}

public sealed record InteractionSettlement(
    string RequestId,
    OperationStatus PlaybackStatus,
    OperationStatus ReservationStatus,
    string? Detail);

public sealed record InteractionPlaybackUpdate(
    SchedulerUpdate Scheduler,
    IReadOnlyList<InteractionSettlement> Settlements);










