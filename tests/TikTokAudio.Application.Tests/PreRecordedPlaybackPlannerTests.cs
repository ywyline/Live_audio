using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Media;
using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests;

public sealed class PreRecordedPlaybackPlannerTests
{
    [Fact]
    public async Task NumericOrderAndUnequalGroupCountsCompleteEachCycle()
    {
        var planner = Planner(Catalog(Product(10, Group(10, 2, 1), Group(10, 1, 1)),
            Product(2, Group(2, 1, 1)), Product(1, Group(1, 10, 1), Group(1, 2, 1), Group(1, 1, 1))));
        var expected = new[] { "1/1", "1/2", "1/10", "2/1", "10/1", "10/2" };
        for (var cycle = 0; cycle < 3; cycle++)
        {
            foreach (var group in expected)
            {
                var item = await Next(planner);
                Assert.Equal(group, item.GroupId);
                Assert.Equal(group.Split('/')[0], item.ProductId);
                Assert.Equal(cycle, item.Cycle);
                Assert.Equal(BasePlaybackMode.PreRecorded, item.Mode);
                Assert.Equal(PlanRevision.Initial, item.PlanRevision);
            }
        }
    }

    [Fact]
    public async Task EachGroupHasIndependentBagsWithoutEarlyRepeatsOrCrossBagDuplicates()
    {
        var planner = Planner(Catalog(Product(1, Group(1, 1, 3), Group(1, 2, 2))));
        var first = new List<string>();
        var second = new List<string>();
        for (var cycle = 0; cycle < 60; cycle++)
        {
            first.Add((await Next(planner)).ClipId!);
            second.Add((await Next(planner)).ClipId!);
        }
        AssertBags(first, 3);
        AssertBags(second, 2);
    }

    [Fact]
    public async Task SingleClipCanRepeatButEachLoopGetsFreshProductBoundary()
    {
        var planner = Planner();
        var boundaries = new HashSet<string>();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var item = await Next(planner);
            Assert.Equal("1/1/clip-0.wav", item.ClipId);
            Assert.Equal(cycle, item.Cycle);
            var boundary = Assert.Single(item.Boundaries);
            Assert.Equal("platform-1", boundary.ProductId);
            Assert.True(boundaries.Add(boundary.BoundaryId));
            Assert.Single(Confirmed(planner, item));
        }
    }

    [Fact]
    public async Task SelectionDoesNotConsumeBoundaryAndStartConfirmationIsIdempotent()
    {
        var planner = Planner();
        var item = await Next(planner);
        var before = planner.CaptureSnapshot(AudioCursor.Start);
        Assert.Empty(before.Checkpoint.ConsumedMarkerIds);
        AssertEquivalent(item, planner.CurrentItem!);
        var emitted = Assert.Single(Confirmed(planner, item));
        Assert.Equal(Assert.Single(item.Boundaries), emitted);
        Assert.Empty(Confirmed(planner, item));
        Assert.Contains(emitted.BoundaryId, planner.CaptureSnapshot(AudioCursor.Start).Checkpoint.ConsumedMarkerIds);
    }

    [Fact]
    public async Task OnlyGroupOneProducesImplicitProductBoundary()
    {
        var planner = Planner(Catalog(Product(1, Group(1, 1, 1), Group(1, 2, 1), Group(1, 10, 1)),
            Product(2, Group(2, 1, 1), Group(2, 2, 1))));
        Assert.Single((await Next(planner)).Boundaries);
        Assert.Empty((await Next(planner)).Boundaries);
        Assert.Empty((await Next(planner)).Boundaries);
        Assert.Single((await Next(planner)).Boundaries);
        Assert.Empty((await Next(planner)).Boundaries);
        Assert.Single((await Next(planner)).Boundaries);
    }

    [Fact]
    public async Task ExplicitMetadataOverridesImplicitMappingAndWorksOnLaterGroup()
    {
        var planner = Planner(Catalog(Product(1,
            Group(1, 1, 1, 10), Group(1, 2, 1, 10))),
            new Dictionary<int, string> { [1] = "platform-1", [10] = "external-10" });
        foreach (var group in new[] { "1/1", "1/2" })
        {
            var item = await Next(planner);
            Assert.Equal("1", item.ProductId);
            Assert.Equal(group, item.GroupId);
            Assert.Equal("external-10", Assert.Single(item.Boundaries).ProductId);
            Assert.Equal("external-10", Assert.Single(Confirmed(planner, item)).ProductId);
        }
    }

    [Fact]
    public async Task FileNameMarkerHasNoProductCommandMeaning()
    {
        var group = Group(1, 2, 1);
        var clip = group.Clips[0] with { ClipId = "1/2/@[10].wav" };
        var planner = Planner(Catalog(Product(1, Group(1, 1, 1), group with { Clips = [clip] })));
        await Next(planner);
        Assert.Empty((await Next(planner)).Boundaries);
    }

    [Fact]
    public async Task ReplacedSelectionCannotAcknowledgeOldBoundary()
    {
        var planner = Planner(Catalog(Product(1, Group(1, 1, 1), Group(1, 2, 1))));
        var old = await Next(planner);
        await Next(planner);
        Assert.NotEqual(OperationStatus.Succeeded, planner.AcknowledgePlaybackStarted(old).Status);
        Assert.Empty(planner.CaptureSnapshot(AudioCursor.Start).Checkpoint.ConsumedMarkerIds);
    }

    [Fact]
    public async Task PreCancelledSelectionDoesNotDrawOrAdvance()
    {
        var catalog = Catalog(Product(1, Group(1, 1, 4), Group(1, 2, 3)));
        var planner = Planner(catalog);
        var reference = Planner(catalog);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planner.SelectNextAsync(Context(planner), cancellation.Token));
        Assert.Null(planner.CurrentItem);
        for (var i = 0; i < 10; i++)
        {
            AssertEquivalent(await Next(reference), await Next(planner));
        }
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("revision")]
    [InlineData("product")]
    [InlineData("group")]
    [InlineData("cycle")]
    public async Task StaleContextIsRejectedWithoutAdvancing(string field)
    {
        var catalog = Catalog(Product(1, Group(1, 1, 3), Group(1, 2, 2)));
        var planner = Planner(catalog);
        var reference = Planner(catalog);
        await Next(planner);
        await Next(reference);
        var context = Context(planner);
        context = field switch
        {
            "mode" => context with { Mode = BasePlaybackMode.TtsScript },
            "revision" => context with { PlanRevision = new PlanRevision(55) },
            "product" => context with { ProductId = "2" },
            "group" => context with { GroupId = "1/10" },
            _ => context with { Cycle = 8 }
        };
        await AssertSelectionRejected(planner, context);
        AssertEquivalent(await Next(reference), await Next(planner));
    }

    [Fact]
    public async Task SnapshotRestoresCursorCurrentClipAndFutureBagsAcrossDifferentRandomSources()
    {
        var catalog = Catalog(Product(1, Group(1, 1, 4), Group(1, 2, 3)), Product(2, Group(2, 1, 2)));
        var original = Planner(catalog);
        for (var i = 0; i < 17; i++) await Next(original);
        var current = original.CurrentItem!;
        var cursor = new AudioCursor(12345, 24000);
        var snapshot = original.CaptureSnapshot(cursor);
        var restored = new PreRecordedPlaybackPlanner(catalog, PlanRevision.Initial, new SeededRandomSource(9999));
        Assert.Equal(OperationStatus.Succeeded, restored.RestoreSnapshot(snapshot).Status);
        Assert.Equal(cursor, restored.CurrentCursor);
        AssertEquivalent(current, restored.CurrentItem!);
        AssertEquivalent(current, await Next(restored));
        Assert.Equal(cursor, restored.CurrentCursor);
        for (var i = 0; i < 60; i++) AssertEquivalent(await Next(original), await Next(restored));
    }

    [Fact]
    public async Task SnapshotRestorationDoesNotReplayConsumedProductBoundary()
    {
        var catalog = Catalog(Product(1, Group(1, 1, 2)));
        var original = Planner(catalog);
        var item = await Next(original);
        var consumed = Assert.Single(Confirmed(original, item));
        var snapshot = original.CaptureSnapshot(new AudioCursor(6000, 24000));
        var restored = Planner(catalog);
        Assert.Equal(OperationStatus.Succeeded, restored.RestoreSnapshot(snapshot).Status);
        var replay = await Next(restored);
        Assert.Equal(item.ClipId, replay.ClipId);
        Assert.Equal(item.EffectSeed, replay.EffectSeed);
        Assert.Empty(replay.Boundaries);
        Assert.Empty(Confirmed(restored, replay));
        var following = await Next(restored);
        Assert.Equal(item.Cycle + 1, following.Cycle);
        Assert.NotEqual(consumed.BoundaryId, Assert.Single(following.Boundaries).BoundaryId);
    }

    [Fact]
    public async Task SnapshotBeforeStartPreservesUnconsumedBoundaryForExplicitAcknowledgement()
    {
        var original = Planner();
        var selected = await Next(original);
        var restored = Planner();
        Assert.Equal(OperationStatus.Succeeded, restored.RestoreSnapshot(original.CaptureSnapshot(AudioCursor.Start)).Status);
        var replay = await Next(restored);
        Assert.Equal(Assert.Single(selected.Boundaries), Assert.Single(replay.Boundaries));
        Assert.Single(Confirmed(restored, replay));
        Assert.Empty(Confirmed(restored, replay));
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("revision")]
    [InlineData("clip")]
    [InlineData("cursor")]
    [InlineData("sampleRate")]
    [InlineData("cycle")]
    [InlineData("missingBag")]
    [InlineData("unknownClip")]
    [InlineData("duplicateClip")]
    [InlineData("unknownConsumedBoundary")]
    public async Task InvalidSnapshotIsRejectedAtomically(string mutation)
    {
        var catalog = Catalog(Product(1, Group(1, 1, 3), Group(1, 2, 2)));
        var planner = Planner(catalog);
        var reference = Planner(catalog);
        await Next(planner);
        await Next(reference);
        var before = planner.CurrentItem;
        var valid = planner.CaptureSnapshot(new AudioCursor(2000, 24000));
        var cursorBefore = planner.CurrentCursor;
        var firstBag = valid.Bags[0];
        var bad = mutation switch
        {
            "fingerprint" => valid with { CatalogFingerprint = "other" },
            "revision" => valid with { Checkpoint = valid.Checkpoint with { PlanRevision = new PlanRevision(99) } },
            "clip" => valid with { Checkpoint = valid.Checkpoint with { ClipId = "missing.wav" } },
            "cursor" => valid with { Checkpoint = valid.Checkpoint with { Cursor = new AudioCursor(-1, 24000) } },
            "sampleRate" => valid with { Checkpoint = valid.Checkpoint with { Cursor = new AudioCursor(1, 0) } },
            "cycle" => valid with { Checkpoint = valid.Checkpoint with { Cycle = -1 } },
            "missingBag" => valid with { Bags = [] },
            "unknownClip" => valid with { Bags = [firstBag with { RemainingClipIds = ["missing.wav"] }, .. valid.Bags.Skip(1)] },
            "duplicateClip" => valid with { Bags = [firstBag with { RemainingClipIds = [firstBag.RemainingClipIds[0], firstBag.RemainingClipIds[0]] }, .. valid.Bags.Skip(1)] },
            _ => valid with { Checkpoint = valid.Checkpoint with { ConsumedMarkerIds = new HashSet<string> { "forged-boundary" } } }
        };
        Assert.NotEqual(OperationStatus.Succeeded, planner.RestoreSnapshot(bad).Status);
        if (before is null) Assert.Null(planner.CurrentItem); else AssertEquivalent(before, planner.CurrentItem!);
        Assert.Equal(cursorBefore, planner.CurrentCursor);
        for (var i = 0; i < 12; i++) AssertEquivalent(await Next(reference), await Next(planner));
    }

    [Fact]
    public async Task CatalogChangeWithSameRevisionCannotAcceptSnapshot()
    {
        var original = Planner();
        await Next(original);
        var other = Planner(Catalog(Product(1, Group(1, 1, 2))));
        Assert.NotEqual(OperationStatus.Succeeded, other.RestoreSnapshot(original.CaptureSnapshot(AudioCursor.Start)).Status);
        Assert.Null(other.CurrentItem);
    }

    [Fact]
    public async Task CheckpointPreservesCurrentDrawAndSeedAndCannotUnconsumeBoundary()
    {
        var planner = Planner(Catalog(Product(1, Group(1, 1, 3), Group(1, 2, 2))));
        var item = await Next(planner);
        var beforeAcknowledgement = planner.CaptureSnapshot(new AudioCursor(9000, 24000)).Checkpoint;
        Assert.Single(Confirmed(planner, item));
        Assert.Equal(OperationStatus.Succeeded, (await planner.RestoreCheckpointAsync(beforeAcknowledgement, CancellationToken.None)).Status);
        var replay = await Next(planner);
        Assert.Equal(item.ClipId, replay.ClipId);
        Assert.Equal(item.EffectSeed, replay.EffectSeed);
        Assert.Equal(beforeAcknowledgement.Cursor, planner.CurrentCursor);
        Assert.Empty(replay.Boundaries);
        Assert.Empty(Confirmed(planner, replay));
        Assert.Equal("1/2", (await Next(planner)).GroupId);
    }

    [Fact]
    public async Task CheckpointAloneCannotReconstructMissingShuffleHistory()
    {
        var original = Planner();
        await Next(original);
        var fresh = Planner();
        Assert.NotEqual(OperationStatus.Succeeded,
            (await fresh.RestoreCheckpointAsync(original.CaptureSnapshot(AudioCursor.Start).Checkpoint, CancellationToken.None)).Status);
        Assert.Null(fresh.CurrentItem);
    }

    [Fact]
    public async Task CancelledCheckpointRestoreLeavesStateUnchanged()
    {
        var planner = Planner();
        var item = await Next(planner);
        var checkpoint = planner.CaptureSnapshot(new AudioCursor(100, 24000)).Checkpoint;
        var beforeCursor = planner.CurrentCursor;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await planner.RestoreCheckpointAsync(checkpoint, cancellation.Token);
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        AssertEquivalent(item, planner.CurrentItem!);
        Assert.Equal(beforeCursor, planner.CurrentCursor);
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("cycle")]
    [InlineData("revision")]
    [InlineData("group")]
    [InlineData("clip")]
    [InlineData("cursor")]
    public async Task CheckpointMustMatchCurrentSelection(string field)
    {
        var planner = Planner();
        var current = await Next(planner);
        var checkpoint = planner.CaptureSnapshot(AudioCursor.Start).Checkpoint;
        checkpoint = field switch
        {
            "seed" => checkpoint with { EffectSeed = checkpoint.EffectSeed ^ 1 },
            "cycle" => checkpoint with { Cycle = checkpoint.Cycle + 1 },
            "revision" => checkpoint with { PlanRevision = new PlanRevision(4) },
            "group" => checkpoint with { GroupId = "1/2" },
            "clip" => checkpoint with { ClipId = "1/1/other.wav" },
            _ => checkpoint with { Cursor = new AudioCursor(-1, 24000) }
        };
        Assert.NotEqual(OperationStatus.Succeeded, (await planner.RestoreCheckpointAsync(checkpoint, CancellationToken.None)).Status);
        AssertEquivalent(current, planner.CurrentItem!);
    }

    [Fact]
    public async Task ReplacingPlanInvalidatesAllOldArtifactsAndStartsNewPlan()
    {
        var planner = Planner();
        var old = await Next(planner);
        var context = Context(planner);
        var snapshot = planner.CaptureSnapshot(new AudioCursor(100, 24000));
        Assert.Equal(OperationStatus.Succeeded,
            planner.ReplacePlan(Catalog(Product(10, Group(10, 1, 2))), new PlanRevision(1)).Status);
        Assert.Equal(new PlanRevision(1), planner.Revision);
        Assert.Null(planner.CurrentItem);
        Assert.NotEqual(OperationStatus.Succeeded, planner.AcknowledgePlaybackStarted(old).Status);
        Assert.NotEqual(OperationStatus.Succeeded, planner.RestoreSnapshot(snapshot).Status);
        Assert.NotEqual(OperationStatus.Succeeded, (await planner.RestoreCheckpointAsync(snapshot.Checkpoint, CancellationToken.None)).Status);
        await AssertSelectionRejected(planner, context);
        var next = await Next(planner);
        Assert.Equal("10", next.ProductId);
        Assert.Equal(0, next.Cycle);
        Assert.Equal(new PlanRevision(1), next.PlanRevision);
        Assert.Equal("platform-10", Assert.Single(next.Boundaries).ProductId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReplacementRevisionMustStrictlyIncrease(long revision)
    {
        var planner = Planner();
        var selected = await Next(planner);
        Assert.NotEqual(OperationStatus.Succeeded, planner.ReplacePlan(Catalog(Product(2, Group(2, 1, 1))), new PlanRevision(revision)).Status);
        AssertEquivalent(selected, planner.CurrentItem!);
        Assert.Equal(PlanRevision.Initial, planner.Revision);
    }

    [Fact]
    public async Task InvalidReplacementLeavesCurrentPlanUsable()
    {
        var planner = Planner();
        var selected = await Next(planner);
        Assert.NotEqual(OperationStatus.Succeeded, planner.ReplacePlan(Catalog(), new PlanRevision(1)).Status);
        AssertEquivalent(selected, planner.CurrentItem!);
        Assert.Equal(PlanRevision.Initial, planner.Revision);
        Assert.Equal("1", (await Next(planner)).ProductId);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("issues")]
    [InlineData("blocked")]
    [InlineData("emptyGroup")]
    [InlineData("noGroups")]
    [InlineData("duplicateProduct")]
    [InlineData("duplicateGroup")]
    [InlineData("duplicateClip")]
    [InlineData("missingGroupOne")]
    [InlineData("missingMapping")]
    [InlineData("missingOverride")]
    public void InvalidCatalogCannotStart(string defect)
    {
        var group = Group(1, 1, 1);
        var product = Product(1, group);
        var catalog = defect switch
        {
            "empty" => Catalog(),
            "issues" => Catalog(product) with { Issues = [new(MediaImportIssueCode.InvalidAudio, "bad.wav", 1, "synthetic")] },
            "blocked" => Catalog(product with { CanStart = false }),
            "emptyGroup" => Catalog(Product(1, group with { Clips = [] })),
            "noGroups" => Catalog(product with { Groups = [] }),
            "duplicateProduct" => Catalog(product, product),
            "duplicateGroup" => Catalog(Product(1, group, group)),
            "duplicateClip" => Catalog(Product(1, group with { Clips = [group.Clips[0], group.Clips[0]] })),
            "missingGroupOne" => Catalog(Product(1, Group(1, 2, 1))),
            "missingMapping" => Catalog(product with { PlatformProductId = null }),
            _ => Catalog(Product(1, Group(1, 1, 1, 99)))
        };
        Assert.ThrowsAny<ArgumentException>(() => Planner(catalog));
    }

    [Fact]
    public void SuppliedMappingCannotDisagreeWithImportedBaseMapping()
    {
        Assert.ThrowsAny<ArgumentException>(() => Planner(Catalog(Product(1, Group(1, 1, 1))),
            new Dictionary<int, string> { [1] = "different-platform-id" }));
    }

    [Fact]
    public async Task ConstructorOwnsCopyOfCatalogCollectionsAndMappings()
    {
        var clips = new List<ImportedAudioClip> { Clip(1, 1, 0, 10), Clip(1, 1, 1, 10) };
        var groups = new List<ImportedAudioGroup> { new(1, "1/1", clips) };
        var products = new List<ImportedAudioProduct> { new(1, "1", "platform-1", groups, true) };
        var mappings = new Dictionary<int, string> { [1] = "platform-1", [10] = "external-10" };
        var planner = Planner(new ProductDirectoryImport("memory-root", products, []), mappings);
        clips.Clear();
        groups.Clear();
        products.Clear();
        mappings[10] = "tampered";
        for (var i = 0; i < 4; i++)
        {
            var item = await Next(planner);
            Assert.Equal("1/1", item.GroupId);
            Assert.Equal("external-10", Assert.Single(item.Boundaries).ProductId);
        }
    }

    [Fact]
    public void CannotCaptureSnapshotBeforeSelectingClip()
    {
        Assert.Throws<InvalidOperationException>(() => Planner().CaptureSnapshot(AudioCursor.Start));
    }

    [Theory]
    [InlineData(-1, 24000)]
    [InlineData(1, 0)]
    [InlineData(1, -24000)]
    public async Task CannotCaptureInvalidSourceCursor(long offset, int rate)
    {
        var planner = Planner();
        await Next(planner);
        Assert.ThrowsAny<ArgumentException>(() => planner.CaptureSnapshot(new AudioCursor(offset, rate)));
    }

    [Fact]
    public async Task OldSnapshotOfSameItemCannotUnconsumeAcknowledgedBoundary()
    {
        var planner = Planner();
        var selected = await Next(planner);
        var beforeStart = planner.CaptureSnapshot(new AudioCursor(100, 24000));
        Assert.Single(Confirmed(planner, selected));
        Assert.Equal(OperationStatus.Succeeded, planner.RestoreSnapshot(beforeStart).Status);
        var resumed = await Next(planner);
        Assert.Empty(resumed.Boundaries);
        Assert.Empty(Confirmed(planner, resumed));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("duration")]
    [InlineData("sampleRate")]
    [InlineData("mapping")]
    public async Task CatalogFingerprintIncludesAssetParametersAndMapping(string field)
    {
        var group = Group(1, 1, 1);
        var product = Product(1, group);
        var original = Planner(Catalog(product));
        await Next(original);
        var asset = group.Clips[0].Asset;
        asset = field switch
        {
            "path" => asset with { Path = "memory-root/other.wav" },
            "duration" => asset with { Duration = TimeSpan.FromSeconds(20) },
            "sampleRate" => asset with { SampleRate = 48000 },
            _ => asset
        };
        var changedGroup = group with { Clips = [group.Clips[0] with { Asset = asset }] };
        var changed = product with { Groups = [changedGroup], PlatformProductId = field == "mapping" ? "other-product" : product.PlatformProductId };
        var receiver = Planner(Catalog(changed));
        Assert.NotEqual(OperationStatus.Succeeded, receiver.RestoreSnapshot(original.CaptureSnapshot(AudioCursor.Start)).Status);
        Assert.Null(receiver.CurrentItem);
    }

    [Fact]
    public async Task SnapshotCannotRollRunningPlannerBackToEarlierGroupOrCycle()
    {
        var planner = Planner(Catalog(Product(1, Group(1, 1, 2), Group(1, 2, 1))));
        await Next(planner);
        var old = planner.CaptureSnapshot(AudioCursor.Start);
        var secondGroup = await Next(planner);
        Assert.NotEqual(OperationStatus.Succeeded, planner.RestoreSnapshot(old).Status);
        AssertEquivalent(secondGroup, planner.CurrentItem!);
        var nextCycle = await Next(planner);
        Assert.NotEqual(OperationStatus.Succeeded, planner.RestoreSnapshot(old).Status);
        AssertEquivalent(nextCycle, planner.CurrentItem!);
    }
    [Fact]
    public async Task ReplacementMissingGroupOneCannotDisplaceValidPlan()
    {
        var planner = Planner();
        var selected = await Next(planner);
        var invalid = Catalog(Product(2, Group(2, 2, 1), Group(2, 3, 1)));
        Assert.NotEqual(OperationStatus.Succeeded, planner.ReplacePlan(invalid, new PlanRevision(1)).Status);
        Assert.Equal(PlanRevision.Initial, planner.Revision);
        AssertEquivalent(selected, planner.CurrentItem!);
        Assert.Equal("platform-1", Assert.Single(Confirmed(planner, selected)).ProductId);
        Assert.Equal("1", (await Next(planner)).ProductId);
    }

    [Fact]
    public async Task GapsAfterGroupOneRemainValidAndKeepNumericOrder()
    {
        var planner = Planner(Catalog(Product(1, Group(1, 7, 1), Group(1, 1, 1), Group(1, 3, 1))));
        Assert.Equal("1/1", (await Next(planner)).GroupId);
        Assert.Equal("1/3", (await Next(planner)).GroupId);
        Assert.Equal("1/7", (await Next(planner)).GroupId);
        var loop = await Next(planner);
        Assert.Equal("1/1", loop.GroupId);
        Assert.Equal(1, loop.Cycle);
    }
    private static PreRecordedPlaybackPlanner Planner(ProductDirectoryImport? catalog = null,
        IReadOnlyDictionary<int, string>? mappings = null) =>
        new(catalog ?? Catalog(Product(1, Group(1, 1, 1))), PlanRevision.Initial, new SeededRandomSource(1234), mappings);

    private static ProductDirectoryImport Catalog(params ImportedAudioProduct[] products) => new("memory-root", products, []);
    private static ImportedAudioProduct Product(int number, params ImportedAudioGroup[] groups) =>
        new(number, number.ToString(System.Globalization.CultureInfo.InvariantCulture), $"platform-{number}", groups, true);
    private static ImportedAudioGroup Group(int product, int group, int count, int? productOverride = null) =>
        new(group, $"{product}/{group}", Enumerable.Range(0, count).Select(i => Clip(product, group, i, productOverride)).ToArray());
    private static ImportedAudioClip Clip(int product, int group, int index, int? productOverride = null)
    {
        var id = $"{product}/{group}/clip-{index}.wav";
        return new(id, new LocalAudioAsset($"memory-root/{id}", "wav", "prerecorded", TimeSpan.FromSeconds(10), 24000), productOverride);
    }

    private static PlaybackPlannerContext Context(PreRecordedPlaybackPlanner planner) =>
        new(BasePlaybackMode.PreRecorded, planner.Revision, planner.CurrentItem?.ProductId,
            planner.CurrentItem?.GroupId, planner.CurrentItem?.Cycle ?? 0);
    private static async Task<PlaybackPlanItem> Next(PreRecordedPlaybackPlanner planner) =>
        Assert.IsType<PlaybackPlanItem>(await planner.SelectNextAsync(Context(planner), CancellationToken.None));
    private static IReadOnlyList<ProductBoundary> Confirmed(PreRecordedPlaybackPlanner planner, PlaybackPlanItem item)
    {
        var result = planner.AcknowledgePlaybackStarted(item);
        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.NotNull(result.Value);
        return result.Value;
    }
    private static void AssertEquivalent(PlaybackPlanItem expected, PlaybackPlanItem actual)
    {
        Assert.Equal(expected.Asset, actual.Asset);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.PlanRevision, actual.PlanRevision);
        Assert.Equal(expected.ProductId, actual.ProductId);
        Assert.Equal(expected.GroupId, actual.GroupId);
        Assert.Equal(expected.ClipId, actual.ClipId);
        Assert.Equal(expected.Cycle, actual.Cycle);
        Assert.Equal(expected.EffectSeed, actual.EffectSeed);
        Assert.Equal(expected.Boundaries.ToArray(), actual.Boundaries.ToArray());
    }
    private static void AssertBags(IReadOnlyList<string> history, int bagSize)
    {
        for (var offset = 0; offset < history.Count; offset += bagSize)
        {
            Assert.Equal(bagSize, history.Skip(offset).Take(bagSize).Distinct().Count());
            if (offset > 0) Assert.NotEqual(history[offset - 1], history[offset]);
        }
    }
    private static async Task AssertSelectionRejected(PreRecordedPlaybackPlanner planner, PlaybackPlannerContext context)
    {
        var before = planner.CurrentItem;
        try
        {
            Assert.Null(await planner.SelectNextAsync(context, CancellationToken.None));
        }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        if (before is null) Assert.Null(planner.CurrentItem); else AssertEquivalent(before, planner.CurrentItem!);
    }
}
