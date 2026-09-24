using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Media;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Playback;

public sealed class PreRecordedPlaybackPlanner : IBasePlaybackPlan
{
    private readonly object gate = new();
    private readonly IRandomSource seedSource;
    private PlannerCatalog catalog;
    private PlannerBag[] bags;
    private PlannerRandom effects;
    private PlanRevision revision;
    private int position = -1;
    private PlaybackPlanItem? current;
    private AudioCursor cursor = AudioCursor.Start;
    private HashSet<string> consumed = new(StringComparer.Ordinal);
    private bool resumePending;

    public PreRecordedPlaybackPlanner(ProductDirectoryImport catalog, PlanRevision revision,
        IRandomSource random, IReadOnlyDictionary<int, string>? productMappings = null)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (revision.Value < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        seedSource = random;
        this.catalog = new PlannerCatalog(catalog, productMappings);
        this.revision = revision;
        bags = CreateBags(this.catalog);
        effects = new PlannerRandom(PlannerRandom.Seed(seedSource));
    }

    public BasePlaybackMode Mode => BasePlaybackMode.PreRecorded;
    public BasePlanAvailability Availability => BasePlanAvailability.Ready;
    public string? Detail => null;
    public void CancelPendingPreparation() { }
    public OperationResult SaveCursor(AudioCursor sourceCursor)
    {
        try { CaptureSnapshot(sourceCursor); return OperationResult.Succeeded(); }
        catch (ArgumentException) { return OperationResult.Failed("源采样游标无效。"); }
        catch (InvalidOperationException) { return OperationResult.Failed("尚未选择可保存的片段。"); }
    }
    public PlanRevision Revision { get { lock (gate) return revision; } }
    public PlaybackPlanItem? CurrentItem { get { lock (gate) return current is null ? null : VisibleItem(); } }
    public AudioCursor CurrentCursor { get { lock (gate) return cursor; } }

    public Task<PlaybackPlanItem?> SelectNextAsync(PlaybackPlannerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesContext(context)) return Task.FromResult<PlaybackPlanItem?>(null);
            if (resumePending)
            {
                resumePending = false;
                return Task.FromResult<PlaybackPlanItem?>(VisibleItem());
            }
            int next = position + 1;
            long cycle = current?.Cycle ?? 0;
            if (next == catalog.Groups.Length)
            {
                if (cycle == long.MaxValue) return Task.FromResult<PlaybackPlanItem?>(null);
                cycle++;
                next = 0;
            }
            var group = catalog.Groups[next];
            string clipId = bags[next].Draw(group);
            long effectSeed = (long)(effects.Next() & long.MaxValue);
            current = MakeItem(next, clipId, cycle, effectSeed);
            position = next;
            cursor = new AudioCursor(0, current.Asset.SampleRate);
            consumed.Clear();
            return Task.FromResult<PlaybackPlanItem?>(current);
        }
    }

    // The scheduler calls this only after audio start succeeds. This method performs no platform action.
    public OperationResult<IReadOnlyList<ProductBoundary>> AcknowledgePlaybackStarted(PlaybackPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            if (current is null || !SameItem(current, item))
                return new(OperationStatus.Failed, null, "播放确认不属于当前计划片段。");
            var boundaries = current.Boundaries.Where(boundary => consumed.Add(boundary.BoundaryId)).ToArray();
            return new(OperationStatus.Succeeded, Array.AsReadOnly(boundaries));
        }
    }

    public PreRecordedPlannerSnapshot CaptureSnapshot(AudioCursor sourceCursor)
    {
        lock (gate)
        {
            if (current is null) throw new InvalidOperationException("尚未选择可保存的片段。");
            if (!ValidCursor(sourceCursor, current.Asset)) throw new ArgumentException("源采样游标无效。", nameof(sourceCursor));
            cursor = NormalizeCursor(sourceCursor, current.Asset);
            var checkpoint = new PlaybackCheckpoint(current.Mode, revision, current.ProductId, current.GroupId,
                current.ClipId, cursor, current.Cycle, current.EffectSeed, consumed.ToFrozenSet(StringComparer.Ordinal));
            var bagSnapshots = bags.Select((bag, index) => new PlannerBagSnapshot(catalog.Groups[index].GroupId,
                Array.AsReadOnly(bag.Remaining.ToArray()), bag.Last, bag.Random.State)).ToArray();
            return new PreRecordedPlannerSnapshot(catalog.Fingerprint, checkpoint,
                Array.AsReadOnly(bagSnapshots), effects.State);
        }
    }

    public Task<OperationResult> RestoreCheckpointAsync(PlaybackCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (gate)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(OperationResult.Cancelled());
            if (current is null || !CheckpointMatches(checkpoint, current) ||
                !ValidCursor(checkpoint.Cursor, current.Asset) || !ValidConsumed(checkpoint, current))
                return Task.FromResult(OperationResult.Failed("检查点与当前已选片段不符；跨实例恢复须提供完整快照。"));
            consumed.UnionWith(checkpoint.ConsumedMarkerIds);
            cursor = NormalizeCursor(checkpoint.Cursor, current.Asset);
            resumePending = true;
            return Task.FromResult(OperationResult.Succeeded());
        }
    }

    public OperationResult RestoreSnapshot(PreRecordedPlannerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            if (snapshot.CatalogFingerprint != catalog.Fingerprint || snapshot.Checkpoint is null || snapshot.Bags is null)
                return OperationResult.Failed("快照与当前素材版本不符。");
            var checkpoint = snapshot.Checkpoint;
            if (checkpoint.Mode != BasePlaybackMode.PreRecorded || checkpoint.PlanRevision != revision || checkpoint.Cycle < 0 ||
                checkpoint.EffectSeed < 0 || snapshot.Bags.Count != bags.Length)
                return OperationResult.Failed("快照模式、修订或袋数量无效。");
            int index = Array.FindIndex(catalog.Groups, group => group.GroupId == checkpoint.GroupId &&
                PlannerCatalog.ProductId(group) == checkpoint.ProductId);
            if (index < 0 || !catalog.Groups[index].Clips.Any(clip => clip.ClipId == checkpoint.ClipId))
                return OperationResult.Failed("快照片段不在当前计划中。");
            var restored = MakeItem(index, checkpoint.ClipId!, checkpoint.Cycle, checkpoint.EffectSeed);
            if (!ValidCursor(checkpoint.Cursor, restored.Asset) || !ValidConsumed(checkpoint, restored))
                return OperationResult.Failed("快照游标或边界记录无效。");
            if (current is not null && (checkpoint.Cycle < current.Cycle ||
                (checkpoint.Cycle == current.Cycle && index < position) ||
                (checkpoint.Cycle == current.Cycle && index == position && !SameItem(current, restored))))
                return OperationResult.Failed("不能用旧快照回退已经推进的计划。");
            var restoredBags = new PlannerBag[bags.Length];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bag in snapshot.Bags)
            {
                if (bag is null || bag.RemainingClipIds is null || !seen.Add(bag.GroupId))
                    return OperationResult.Failed("快照包含无效或重复洗牌袋。");
                int bagIndex = Array.FindIndex(catalog.Groups, group => group.GroupId == bag.GroupId);
                if (bagIndex < 0) return OperationResult.Failed("快照包含未知子组。");
                var group = catalog.Groups[bagIndex];
                var ids = group.Clips.Select(clip => clip.ClipId).ToHashSet(StringComparer.Ordinal);
                int drawsInBag = (int)(((ulong)checkpoint.Cycle + (bagIndex <= index ? 1UL : 0UL)) % (ulong)ids.Count);
                int expectedRemaining = drawsInBag == 0 ? 0 : ids.Count - drawsInBag;
                bool wasVisited = checkpoint.Cycle > 0 || bagIndex <= index;
                if (bag.RemainingClipIds.Count != expectedRemaining || bag.RemainingClipIds.Any(id => !ids.Contains(id)) ||
                    bag.RemainingClipIds.Distinct(StringComparer.Ordinal).Count() != bag.RemainingClipIds.Count ||
                    (wasVisited ? bag.LastClipId is null || !ids.Contains(bag.LastClipId) : bag.LastClipId is not null) ||
                    bag.RemainingClipIds.Contains(bag.LastClipId!) ||
                    (bagIndex == index && bag.LastClipId != checkpoint.ClipId))
                    return OperationResult.Failed("洗牌袋与播放进度或素材不一致。");
                restoredBags[bagIndex] = new PlannerBag(new PlannerRandom(bag.RandomState), bag.RemainingClipIds, bag.LastClipId);
            }
            var restoredConsumed = checkpoint.ConsumedMarkerIds.ToHashSet(StringComparer.Ordinal);
            if (current is not null && SameItem(current, restored)) restoredConsumed.UnionWith(consumed);
            // Commit only after the complete snapshot has been validated and copied.
            bags = restoredBags;
            effects = new PlannerRandom(snapshot.EffectRandomState);
            current = restored;
            position = index;
            consumed = restoredConsumed;
            cursor = NormalizeCursor(checkpoint.Cursor, restored.Asset);
            resumePending = true;
            return OperationResult.Succeeded();
        }
    }

    public OperationResult ReplacePlan(ProductDirectoryImport replacement, PlanRevision newRevision,
        IReadOnlyDictionary<int, string>? productMappings = null)
    {
        lock (gate)
        {
            if (newRevision.Value <= revision.Value) return OperationResult.Failed("新计划修订号必须严格递增。");
            PlannerCatalog next;
            try { next = new PlannerCatalog(replacement, productMappings); }
            catch (ArgumentException error) { return OperationResult.Failed(error.Message); }
            var nextBags = CreateBags(next);
            var nextEffects = new PlannerRandom(PlannerRandom.Seed(seedSource));
            catalog = next;
            bags = nextBags;
            effects = nextEffects;
            revision = newRevision;
            current = null;
            position = -1;
            cursor = AudioCursor.Start;
            consumed.Clear();
            resumePending = false;
            return OperationResult.Succeeded();
        }
    }

    private PlannerBag[] CreateBags(PlannerCatalog source) => source.Groups
        .Select(_ => new PlannerBag(new PlannerRandom(PlannerRandom.Seed(seedSource)))).ToArray();

    private bool MatchesContext(PlaybackPlannerContext context) =>
        context.Mode == BasePlaybackMode.PreRecorded && context.PlanRevision == revision &&
        (current is null
            ? context.ProductId is null && context.GroupId is null && context.Cycle == 0
            : context.ProductId == current.ProductId && context.GroupId == current.GroupId && context.Cycle == current.Cycle);

    private PlaybackPlanItem MakeItem(int index, string clipId, long cycle, long effectSeed)
    {
        var group = catalog.Groups[index];
        var clip = group.Clips.Single(clip => clip.ClipId == clipId);
        string? target = clip.ProductOverrideNumber is int number ? catalog.Mappings[number]
            : group.Number == 1 ? group.PlatformProductId : null;
        ProductBoundary[] boundaries = target is null ? [] : [new ProductBoundary(target,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", catalog.Fingerprint,
                revision.Value.ToString(CultureInfo.InvariantCulture), cycle.ToString(CultureInfo.InvariantCulture),
                index.ToString(CultureInfo.InvariantCulture), clipId)))))];
        return new PlaybackPlanItem(clip.Asset, BasePlaybackMode.PreRecorded, revision, PlannerCatalog.ProductId(group),
            group.GroupId, clipId, cycle, effectSeed, Array.AsReadOnly(boundaries));
    }

    private PlaybackPlanItem VisibleItem() => current! with
    {
        Boundaries = Array.AsReadOnly(current!.Boundaries.Where(boundary => !consumed.Contains(boundary.BoundaryId)).ToArray())
    };

    private static bool SameItem(PlaybackPlanItem first, PlaybackPlanItem second) =>
        first.Mode == second.Mode && first.PlanRevision == second.PlanRevision && first.ProductId == second.ProductId &&
        first.GroupId == second.GroupId && first.ClipId == second.ClipId && first.Cycle == second.Cycle &&
        first.EffectSeed == second.EffectSeed && first.Asset == second.Asset;

    private static bool CheckpointMatches(PlaybackCheckpoint checkpoint, PlaybackPlanItem item) =>
        checkpoint.Mode == item.Mode && checkpoint.PlanRevision == item.PlanRevision && checkpoint.ProductId == item.ProductId &&
        checkpoint.GroupId == item.GroupId && checkpoint.ClipId == item.ClipId && checkpoint.Cycle == item.Cycle &&
        checkpoint.EffectSeed == item.EffectSeed;

    private static bool ValidConsumed(PlaybackCheckpoint checkpoint, PlaybackPlanItem item) =>
        checkpoint.ConsumedMarkerIds is not null && checkpoint.ConsumedMarkerIds.Count <= item.Boundaries.Count &&
        checkpoint.ConsumedMarkerIds.All(id => item.Boundaries.Any(boundary => boundary.BoundaryId == id));

    private static bool ValidCursor(AudioCursor value, LocalAudioAsset asset) =>
        value.SourceSampleOffset >= 0 && (value.SampleRate == asset.SampleRate ||
            value is { SourceSampleOffset: 0, SampleRate: null }) &&
        value.SourceSampleOffset <= decimal.Round((decimal)asset.Duration!.Value.Ticks * asset.SampleRate!.Value /
            TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero);

    private static AudioCursor NormalizeCursor(AudioCursor value, LocalAudioAsset asset) => new(value.SourceSampleOffset, asset.SampleRate);
}
