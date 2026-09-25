using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Playback;

/// <summary>Sequential playback of trusted operator script segments prepared by the local TTS pipeline.</summary>
public sealed class TtsPlaybackPlanner : IBasePlaybackPlan
{
    private readonly object gate = new();
    private readonly TtsScriptDocument document;
    private readonly PreparedTtsSegment?[] prepared;
    private readonly string documentId;
    private readonly bool loop;
    private readonly HashSet<string> consumed = new(StringComparer.Ordinal);
    private int position = -1;
    private long cycle;
    private PlaybackPlanItem? current;
    private AudioCursor cursor = AudioCursor.Start;
    private bool resumePending;
    private bool completed;
    private string? detail;
    private TtsPreparationState preparationState;
    private string? engineId;
    private string? currentProductId;
    private TtsPreGenerator? source;
    private PreparationRun? running;
    private long preparationEpoch;

    public TtsPlaybackPlanner(TtsScriptDocument document, PlanRevision revision, bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (revision.Value < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(document.SegmenterVersion) || document.OriginalText is null ||
            document.Segments is null || document.Segments.Count == 0)
            throw new ArgumentException("稿件必须包含有效的分段。", nameof(document));
        var segments = document.Segments.ToArray();
        var markers = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment is null || segment.Index != index || string.IsNullOrWhiteSpace(segment.Text) ||
                (segment.Boundary is { } boundary && (string.IsNullOrWhiteSpace(boundary.ProductId) ||
                    string.IsNullOrWhiteSpace(boundary.BoundaryId) || !markers.Add(boundary.BoundaryId))))
                throw new ArgumentException("稿件分段索引、文本或商品标记无效。", nameof(document));
        }
        this.document = document with { Segments = Array.AsReadOnly(segments) };
        documentId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this.document))));
        prepared = new PreparedTtsSegment?[segments.Length];
        Revision = revision;
        this.loop = loop;
    }

    public BasePlaybackMode Mode => BasePlaybackMode.TtsScript;
    public PlanRevision Revision { get; }
    public PlaybackPlanItem? CurrentItem { get { lock (gate) return current is null ? null : VisibleItem(); } }
    public AudioCursor CurrentCursor { get { lock (gate) return cursor; } }
    public BasePlanAvailability Availability
    {
        get
        {
            lock (gate)
            {
                RefreshPreparation();
                if (completed) return BasePlanAvailability.Completed;
                if (current is not null || CandidateReady()) return BasePlanAvailability.Ready;
                return preparationState == TtsPreparationState.Failed
                    ? BasePlanAvailability.Failed : BasePlanAvailability.WaitingForSynthesis;
            }
        }
    }
    public string? Detail { get { lock (gate) { RefreshPreparation(); return detail; } } }

    /// <summary>Imports trusted same-plan preparation. Segments accumulate across bounded batches and cannot be replaced.</summary>
    public OperationResult ApplyPreparation(PlanRevision revision, TtsPreparationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            if (revision != Revision) return OperationResult.Cancelled("忽略旧计划的预生成结果。");
            if (running is not null) return OperationResult.Failed("预生成任务进行中，不能从其他来源导入音频。");
            return Import(snapshot);
        }
    }

    /// <summary>The planner exclusively owns the supplied generator while its batch runs; poll availability to import progressive readiness.</summary>
    public Task<TtsPreparationResult> PrepareAsync(TtsPreGenerator generator, TtsGenerationSettings settings,
        int startIndex, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(settings);
        lock (gate)
        {
            if (running is not null || (source is not null && !ReferenceEquals(source, generator)))
                return Task.FromResult(FailedPreparation("已有预生成任务或生成来源与当前计划不符。"));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromResult(new TtsPreparationResult(OperationStatus.Cancelled,
                    new(TtsPreparationState.Cancelled, [], 0, "预生成已取消。"), "预生成已取消。"));
            if (startIndex < 0 || startIndex >= prepared.Length || count <= 0 || count > prepared.Length - startIndex)
                return Task.FromResult(FailedPreparation("预生成分段范围无效。"));
            source = generator;
            var run = new PreparationRun(++preparationEpoch, generator.Snapshot, startIndex, count,
                new CancellationTokenSource());
            running = run;
            preparationState = TtsPreparationState.WaitingForSynthesis;
            detail = "等待合成。";
            run.Registration = cancellationToken.Register(() => CancelFromCaller(run));
            return Task.Run(() => ExecutePreparationAsync(generator, settings, run));
        }
    }

    public void CancelPendingPreparation()
    {
        lock (gate)
        {
            preparationEpoch++;
            if (running is null) return;
            preparationState = TtsPreparationState.Cancelled;
            detail = "预生成已取消。";
            // CancelAsync sets cancellation immediately, without waiting for user/provider callbacks under the command lock.
            CancelRun(running);
        }
    }

    private void CancelFromCaller(PreparationRun run)
    {
        lock (gate)
        {
            if (!ReferenceEquals(running, run) || run.Finishing) return;
            preparationEpoch++;
            preparationState = TtsPreparationState.Cancelled;
            detail = "预生成已取消。";
            CancelRun(run);
        }
    }

    private static void CancelRun(PreparationRun run)
    {
        if (!run.Finishing && !run.Token.IsCancellationRequested)
            run.Cancellation = run.Token.CancelAsync();
    }
    public Task<PlaybackPlanItem?> SelectNextAsync(PlaybackPlannerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Mode != Mode || context.PlanRevision != Revision || completed)
                return Task.FromResult<PlaybackPlanItem?>(null);
            RefreshPreparation();
            if (resumePending && current is not null)
            {
                resumePending = false;
                return Task.FromResult<PlaybackPlanItem?>(VisibleItem());
            }
            var next = position + 1;
            var nextCycle = cycle;
            if (next == prepared.Length)
            {
                if (!loop)
                {
                    current = null;
                    completed = true;
                    detail = null;
                    return Task.FromResult<PlaybackPlanItem?>(null);
                }
                if (cycle == long.MaxValue)
                {
                    current = null;
                    preparationState = TtsPreparationState.Failed;
                    detail = "循环序号已达到上限。";
                    return Task.FromResult<PlaybackPlanItem?>(null);
                }
                next = 0;
                nextCycle++;
            }
            if (!CandidateReady())
            {
                current = null;
                detail ??= "等待合成。";
                return Task.FromResult<PlaybackPlanItem?>(null);
            }
            position = next;
            cycle = nextCycle;
            var segment = document.Segments[next];
            var boundaries = segment.Boundary is { } marker
                ? new[] { new ProductBoundary(marker.ProductId,
                    $"tts:{documentId}:{Revision.Value.ToString(CultureInfo.InvariantCulture)}:{cycle.ToString(CultureInfo.InvariantCulture)}:{marker.BoundaryId}") }
                : [];
            current = new(prepared[next]!.Asset, Mode, Revision, segment.Boundary?.ProductId ?? currentProductId, documentId,
                $"{documentId}:{next.ToString(CultureInfo.InvariantCulture)}", cycle, 0, Array.AsReadOnly(boundaries));
            cursor = new(0, current.Asset.SampleRate);
            consumed.Clear();
            detail = null;
            return Task.FromResult<PlaybackPlanItem?>(current);
        }
    }

    public OperationResult<IReadOnlyList<ProductBoundary>> AcknowledgePlaybackStarted(PlaybackPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            if (current is null || !SameItem(current, item))
                return new(OperationStatus.Failed, null, "播放确认不属于当前稿件片段。");
            currentProductId = current.ProductId;
            return new(OperationStatus.Succeeded,
                Array.AsReadOnly(current.Boundaries.Where(boundary => consumed.Add(boundary.BoundaryId)).ToArray()));
        }
    }

    public OperationResult SaveCursor(AudioCursor sourceCursor)
    {
        lock (gate)
        {
            if (current is null || !ValidCursor(sourceCursor, current.Asset))
                return OperationResult.Failed("源采样游标不属于当前稿件片段。");
            cursor = new(sourceCursor.SourceSampleOffset, current.Asset.SampleRate);
            return OperationResult.Succeeded();
        }
    }

    public PlaybackCheckpoint CaptureCheckpoint(AudioCursor sourceCursor)
    {
        lock (gate)
        {
            if (current is null) throw new InvalidOperationException("No playable script segment is selected.");
            if (!ValidCursor(sourceCursor, current.Asset))
                throw new ArgumentException("The source sample cursor is invalid.", nameof(sourceCursor));
            cursor = new(sourceCursor.SourceSampleOffset, current.Asset.SampleRate);
            return new PlaybackCheckpoint(current.Mode, Revision, current.ProductId, current.GroupId,
                current.ClipId, cursor, current.Cycle, current.EffectSeed,
                new HashSet<string>(consumed, StringComparer.Ordinal));
        }
    }

    public Task<OperationResult> RestoreCheckpointAsync(PlaybackCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (gate)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(OperationResult.Cancelled());
            if (current is null || checkpoint.Mode != Mode || checkpoint.PlanRevision != Revision ||
                checkpoint.ProductId != current.ProductId || checkpoint.GroupId != current.GroupId ||
                checkpoint.ClipId != current.ClipId || checkpoint.Cycle != current.Cycle || checkpoint.EffectSeed != 0 ||
                !ValidCursor(checkpoint.Cursor, current.Asset) || checkpoint.ConsumedMarkerIds is null ||
                checkpoint.ConsumedMarkerIds.Count > current.Boundaries.Count ||
                checkpoint.ConsumedMarkerIds.Any(id => !current.Boundaries.Any(boundary => boundary.BoundaryId == id)))
                return Task.FromResult(OperationResult.Failed("检查点不属于当前已选稿件片段。"));
            consumed.UnionWith(checkpoint.ConsumedMarkerIds);
            cursor = new(checkpoint.Cursor.SourceSampleOffset, current.Asset.SampleRate);
            resumePending = true;
            return Task.FromResult(OperationResult.Succeeded());
        }
    }

    private bool CandidateReady()
    {
        int next = position + 1;
        if (next == prepared.Length)
        {
            if (!loop || cycle == long.MaxValue) return false;
            next = 0;
        }
        if (prepared[next] is null) return false;
        int successor = next + 1;
        return successor < prepared.Length ? prepared[successor] is not null : !loop || prepared[0] is not null;
    }

    private void RefreshPreparation()
    {
        if (running is not { } run || run.Epoch != preparationEpoch || run.Token.IsCancellationRequested) return;
        var snapshot = source!.Snapshot;
        if (ReferenceEquals(snapshot, run.LastSnapshot)) return;
        run.LastSnapshot = snapshot;
        var imported = Import(snapshot, run.StartIndex, run.Count);
        if (!imported.IsSuccess)
        {
            preparationState = TtsPreparationState.Failed;
            detail = imported.Detail;
        }
    }

    private OperationResult Import(TtsPreparationSnapshot snapshot, int start = 0, int? count = null)
    {
        if (!Enum.IsDefined(snapshot.State) || snapshot.Segments is null || snapshot.Segments.Count > prepared.Length ||
            snapshot.RequiredBeforePlayback < 0 || snapshot.RequiredBeforePlayback > 2)
            return OperationResult.Failed("预生成快照无效。");
        var entries = snapshot.Segments.ToArray();
        var seen = new HashSet<int>();
        string? expectedEngine = engineId;
        foreach (var entry in entries)
        {
            if (entry is null || entry.Segment is not { } segment || segment.Index < start ||
                segment.Index >= start + (count ?? prepared.Length) || !seen.Add(segment.Index) ||
                segment != document.Segments[segment.Index] || !ValidAsset(entry.Asset) || string.IsNullOrWhiteSpace(entry.CacheKey))
                return OperationResult.Failed("预生成音频不属于当前稿件，或包含重复、无效的分段。");
            expectedEngine ??= entry.Asset.EngineId;
            if (entry.Asset.EngineId != expectedEngine || (prepared[segment.Index] is { } existing && existing != entry))
                return OperationResult.Failed("不能替换当前稿件已准备的音频或混用其他引擎。");
        }
        foreach (var entry in entries) prepared[entry.Segment.Index] = entry;
        engineId = expectedEngine;
        preparationState = snapshot.State;
        detail = snapshot.Detail;
        return OperationResult.Succeeded();
    }

    private async Task<TtsPreparationResult> ExecutePreparationAsync(TtsPreGenerator generator,
        TtsGenerationSettings settings, PreparationRun run)
    {
        try
        {
            var result = await generator.PrepareAsync(document, run.StartIndex, run.Count, settings, run.Token.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (run.Epoch != preparationEpoch || run.Token.IsCancellationRequested)
                    return new(OperationStatus.Cancelled, new(TtsPreparationState.Cancelled, [], 0, "忽略已取消的预生成结果。"), "忽略已取消的预生成结果。");
                var imported = Import(result.Snapshot, run.StartIndex, run.Count);
                if (!imported.IsSuccess)
                {
                    preparationState = TtsPreparationState.Failed;
                    detail = imported.Detail;
                    return FailedPreparation(imported.Detail!);
                }
                return result;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (gate)
            {
                if (run.Epoch != preparationEpoch || run.Token.IsCancellationRequested)
                    return new(OperationStatus.Cancelled, new(TtsPreparationState.Cancelled, [], 0, "预生成已取消。"), "预生成已取消。");
                preparationState = TtsPreparationState.Failed;
                detail = "预生成任务失败，请检查本机引擎与缓存。";
                return FailedPreparation(detail);
            }
        }
        finally
        {
            Task cancellation;
            lock (gate)
            {
                run.Finishing = true;
                cancellation = run.Cancellation;
            }
            await run.Registration.DisposeAsync().ConfigureAwait(false);
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (gate) detail = "预生成取消回调失败。";
            }
            lock (gate)
            {
                if (ReferenceEquals(running, run)) running = null;
                run.Token.Dispose();
            }
        }
    }

    private PlaybackPlanItem VisibleItem() => current! with
    { Boundaries = Array.AsReadOnly(current!.Boundaries.Where(boundary => !consumed.Contains(boundary.BoundaryId)).ToArray()) };

    private static TtsPreparationResult FailedPreparation(string message) =>
        new(OperationStatus.Failed, new(TtsPreparationState.Failed, [], 0, message), message);

    private static bool ValidAsset(LocalAudioAsset? asset) => asset is not null &&
        !string.IsNullOrWhiteSpace(asset.Path) && !string.IsNullOrWhiteSpace(asset.Format) &&
        !string.IsNullOrWhiteSpace(asset.EngineId) && asset.SampleRate > 0 && asset.Duration > TimeSpan.Zero;

    private static bool ValidCursor(AudioCursor value, LocalAudioAsset asset) => value.SourceSampleOffset >= 0 &&
        (value.SampleRate == asset.SampleRate || value is { SourceSampleOffset: 0, SampleRate: null }) &&
        value.SourceSampleOffset <= decimal.Round((decimal)asset.Duration!.Value.Ticks * asset.SampleRate!.Value /
            TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero);

    private static bool SameItem(PlaybackPlanItem left, PlaybackPlanItem right) =>
        left.Mode == right.Mode && left.PlanRevision == right.PlanRevision && left.ProductId == right.ProductId &&
        left.GroupId == right.GroupId && left.ClipId == right.ClipId && left.Cycle == right.Cycle &&
        left.EffectSeed == right.EffectSeed && left.Asset == right.Asset;

    private sealed class PreparationRun(long epoch, TtsPreparationSnapshot snapshot, int start, int count, CancellationTokenSource token)
    {
        public long Epoch { get; } = epoch;
        public TtsPreparationSnapshot LastSnapshot { get; set; } = snapshot;
        public int StartIndex { get; } = start;
        public int Count { get; } = count;
        public CancellationTokenSource Token { get; } = token;
        public CancellationTokenRegistration Registration { get; set; }
        public Task Cancellation { get; set; } = Task.CompletedTask;
        public bool Finishing { get; set; }
    }
}