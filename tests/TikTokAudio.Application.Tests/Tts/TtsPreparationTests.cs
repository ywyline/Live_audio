using System.Globalization;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;
using Xunit;

namespace TikTokAudio.Application.Tests.Tts;

public sealed class TtsPreparationTests
{
    private static readonly TtsGenerationSettings Settings = new(new("fake-local", "1.0", "model", "sha256-weight-v1"),
        "Hải Đăng", "vi-VN", 1, 0, 1, "none-v1");

    [Fact]
    public void KeyIncludesEveryIdentityParameterAndUsesUnambiguousEncoding()
    {
        var baseline = TtsCacheKey.Create("Xin chào.", Settings, "split-v1");
        var variants = new[]
        {
            Settings with { Identity = Settings.Identity with { EngineId = "other" } },
            Settings with { Identity = Settings.Identity with { EngineVersion = "2" } },
            Settings with { Identity = Settings.Identity with { ModelId = "other" } },
            Settings with { Identity = Settings.Identity with { ModelVersion = "2" } },
            Settings with { VoiceId = "other" }, Settings with { Language = "vi" },
            Settings with { Rate = 1.1 }, Settings with { Pitch = 2 }, Settings with { Volume = .5 },
            Settings with { PronunciationDictionaryVersion = "dict-v2" }
        };
        Assert.All(variants, settings => Assert.NotEqual(baseline, TtsCacheKey.Create("Xin chào.", settings, "split-v1")));
        Assert.NotEqual(baseline, TtsCacheKey.Create("Xin chào!", Settings, "split-v1"));
        Assert.NotEqual(baseline, TtsCacheKey.Create("Xin chào.", Settings, "split-v2"));
        Assert.NotEqual(TtsCacheKey.Create("a|b", Settings with { VoiceId = "c" }, "split-v1"),
            TtsCacheKey.Create("a", Settings with { VoiceId = "b|c" }, "split-v1"));
        Assert.Matches("^[a-f0-9]{64}$", baseline);
    }

    [Fact]
    public void KeyIsNfcAndCultureStableWithoutRemovingVietnameseAccents()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("vi-VN");
            var key = TtsCacheKey.Create("chào", Settings, "v1");
            CultureInfo.CurrentCulture = new("en-US");
            Assert.Equal(key, TtsCacheKey.Create("cha\u0300o", Settings, "v1"));
            Assert.NotEqual(key, TtsCacheKey.Create("chao", Settings, "v1"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidRatesCannotCreateCacheIdentity(double rate) =>
        Assert.Throws<ArgumentException>(() => TtsCacheKey.Create("text", Settings with { Rate = rate }, "v1"));

    [Fact]
    public async Task CachedSegmentsWorkWithEngineOfflineAndKeepDistinctBoundaries()
    {
        var provider = new FakeProvider { Handler = (_, _) => throw new InvalidOperationException("offline") };
        var cache = new FakeCache();
        var document = Document("same", "same");
        document = document with { Segments = new[]
        {
            document.Segments[0] with { Boundary = new("product1", "boundary1") },
            document.Segments[1] with { Boundary = new("product2", "boundary2") }
        }};
        var key = TtsCacheKey.Create("same", Settings, document.SegmenterVersion);
        cache.Items[key] = Asset("already.wav");
        var generator = new TtsPreGenerator(provider, cache, 10);

        var result = await generator.PrepareAsync(document, 0, 2, Settings, default);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.True(result.Snapshot.CanStartPlayback);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(new[] { "boundary1", "boundary2" }, result.Snapshot.Segments.Select(s => s.Segment.Boundary!.BoundaryId));
        Assert.Equal(result.Snapshot.Segments[0].Asset, result.Snapshot.Segments[1].Asset);
    }

    [Fact]
    public async Task CurrentAndNextMustBeReadyBeforePlaybackAndSnapshotsStayImmutable()
    {
        var secondStarted = Signal();
        var allowSecond = Signal();
        var thirdStarted = Signal();
        var allowThird = Signal();
        var provider = new FakeProvider();
        provider.Handler = async (request, token) =>
        {
            if (request.Text == "second") { secondStarted.SetResult(); await allowSecond.Task.WaitAsync(token); }
            if (request.Text == "third") { thirdStarted.SetResult(); await allowThird.Task.WaitAsync(token); }
            return Success(request.Text);
        };
        var generator = new TtsPreGenerator(provider, new FakeCache(), 3);
        var pending = generator.PrepareAsync(Document("first", "second", "third"), 0, 3, Settings, default);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstSnapshot = generator.Snapshot;
        Assert.Single(firstSnapshot.Segments);
        Assert.Equal(TtsPreparationState.WaitingForSynthesis, firstSnapshot.State);
        Assert.False(firstSnapshot.CanStartPlayback);
        allowSecond.SetResult();
        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(generator.Snapshot.CanStartPlayback);
        Assert.Equal(2, generator.Snapshot.Segments.Count);
        Assert.Single(firstSnapshot.Segments);
        allowThird.SetResult();
        Assert.Equal(OperationStatus.Succeeded, (await pending).Status);
        Assert.Equal(3, generator.Snapshot.Segments.Count);
    }

    [Fact]
    public async Task LastSegmentCanStartAloneButPartialWindowCannotStartEarly()
    {
        var generator = new TtsPreGenerator(new FakeProvider(), new FakeCache(), 2);
        var document = Document("first", "last");
        var partial = await generator.PrepareAsync(document, 0, 1, Settings, default);
        Assert.Equal(OperationStatus.Succeeded, partial.Status);
        Assert.False(partial.Snapshot.CanStartPlayback);
        Assert.Equal(TtsPreparationState.WaitingForSynthesis, partial.Snapshot.State);
        var last = await generator.PrepareAsync(document, 1, 1, Settings, default);
        Assert.True(last.Snapshot.CanStartPlayback);
    }

    [Fact]
    public async Task FailureDoesNotBecomeCacheHitAndExplicitRetryGeneratesAgain()
    {
        var provider = new FakeProvider { Handler = (_, _) => Task.FromResult(new TtsSynthesisOutcome(OperationStatus.Failed, null, "error")) };
        var cache = new FakeCache();
        var generator = new TtsPreGenerator(provider, cache, 2);
        var failed = await generator.PrepareAsync(Document("text"), 0, 1, Settings, default);
        Assert.Equal(OperationStatus.Failed, failed.Status);
        Assert.Empty(cache.Items);
        Assert.False(generator.Snapshot.CanStartPlayback);
        provider.Handler = (request, _) => Task.FromResult(Success(request.Text));
        var retried = await generator.PrepareAsync(Document("text"), 0, 1, Settings, default);
        Assert.True(retried.Snapshot.CanStartPlayback);
        Assert.Equal(2, provider.Calls);
        Assert.Single(cache.Items);
    }

    [Fact]
    public async Task CancelledLateEngineSuccessIsDiscardedNeverStoredOrPublished()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider { Handler = (request, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Success(request.Text));
        }};
        var cache = new FakeCache();
        var generator = new TtsPreGenerator(provider, cache, 2);
        var result = await generator.PrepareAsync(Document("text"), 0, 1, Settings, cancellation.Token);
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Empty(cache.Items);
        Assert.Equal(0, cache.Stores);
        Assert.Single(cache.Discarded);
        Assert.Empty(generator.Snapshot.Segments);
    }

    [Fact]
    public async Task CancellationCleanupFailureIsReportedAsFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider { Handler = (request, _) =>
        {
            cancellation.Cancel(); return Task.FromResult(Success(request.Text));
        }};
        var cache = new FakeCache { DiscardStatus = OperationStatus.Failed };
        var result = await new TtsPreGenerator(provider, cache, 1).PrepareAsync(Document("text"), 0, 1, Settings, cancellation.Token);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(result.Snapshot.CanStartPlayback);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OldRevisionOrWrongEngineResultNeverPublishes(bool oldRevision)
    {
        var provider = new FakeProvider { Handler = (_, _) => Task.FromResult(new TtsSynthesisOutcome(OperationStatus.Succeeded,
            new(Asset("late.wav"), oldRevision ? "fake-local" : "wrong", new(oldRevision ? 0 : 1)), null)) };
        var cache = new FakeCache();
        var result = await new TtsPreGenerator(provider, cache, 1).PrepareAsync(Document("text"), 0, 1, Settings, default);
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(0, cache.Stores);
        Assert.Single(cache.Discarded);
        Assert.Empty(result.Snapshot.Segments);
    }

    [Fact]
    public async Task RegistryRevisionChangingInFlightInvalidatesReturnedAudio()
    {
        var entered = Signal();
        var release = Signal();
        var first = new FakeProvider
        {
            Handler = async (_, token) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return Success("late");
            }
        };
        var second = new FakeProvider { EngineId = "other-local" };
        var registry = new TtsEngineRegistry([
            new TtsEngineConfiguration(first.EngineId, first),
            new TtsEngineConfiguration(second.EngineId, second)], first.EngineId);
        var cache = new FakeCache();
        var preparation = new TtsPreGenerator(registry, cache, 1)
            .PrepareAsync(Document("text"), 0, 1, Settings, default);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationStatus.Succeeded, registry.Switch(second.EngineId).Status);
        release.SetResult();

        var result = await preparation;
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(0, cache.Stores);
        Assert.Single(cache.Discarded);
        Assert.Empty(result.Snapshot.Segments);
    }

    [Fact]
    public async Task ProviderRevisionChangingInFlightInvalidatesReturnedAudio()
    {
        var provider = new FakeProvider();
        provider.Handler = (_, _) => { provider.Revision = new(2); return Task.FromResult(Success("text")); };
        var cache = new FakeCache();
        var result = await new TtsPreGenerator(provider, cache, 1).PrepareAsync(Document("text"), 0, 1, Settings, default);
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(0, cache.Stores);
        Assert.Single(cache.Discarded);
    }

    [Fact]
    public async Task BusyAndPreCancelledCallsDoNotReplaceRunningSnapshot()
    {
        var entered = Signal();
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider { Handler = async (_, token) =>
        {
            entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Success("unused");
        }};
        var generator = new TtsPreGenerator(provider, new FakeCache(), 1);
        var first = generator.PrepareAsync(Document("text"), 0, 1, Settings, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = generator.Snapshot;
        var busy = await generator.PrepareAsync(Document("other"), 0, 1, Settings, default);
        var cancelled = await generator.PrepareAsync(Document("other"), 0, 1, Settings, new CancellationToken(true));
        Assert.Equal(OperationStatus.Failed, busy.Status);
        Assert.Equal(OperationStatus.Cancelled, cancelled.Status);
        Assert.Same(snapshot, generator.Snapshot);
        Assert.Equal(1, provider.Calls);
        cancellation.Cancel();
        Assert.Equal(OperationStatus.Cancelled, (await first).Status);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 3)]
    [InlineData(2, 1)]
    public async Task InvalidOrOversizedBatchDoesNoWork(int start, int count)
    {
        var provider = new FakeProvider();
        var cache = new FakeCache();
        var result = await new TtsPreGenerator(provider, cache, 1).PrepareAsync(Document("one", "two"), start, count, Settings, default);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, cache.Finds);
    }

    [Fact]
    public async Task DifferentModelVersionCannotReusePreviousAudio()
    {
        var provider = new FakeProvider();
        var generator = new TtsPreGenerator(provider, new FakeCache(), 1);
        var first = await generator.PrepareAsync(Document("text"), 0, 1, Settings, default);
        var next = await generator.PrepareAsync(Document("text"), 0, 1,
            Settings with { Identity = Settings.Identity with { ModelVersion = "new-weights" } }, default);
        Assert.Equal(2, provider.Calls);
        Assert.NotEqual(first.Snapshot.Segments[0].CacheKey, next.Snapshot.Segments[0].CacheKey);
    }

    [Fact]
    public async Task FailedCacheCommitCannotPublishEngineOutput()
    {
        var cache = new FakeCache { StoreStatus = OperationStatus.Failed };
        var result = await new TtsPreGenerator(new FakeProvider(), cache, 1).PrepareAsync(Document("text"), 0, 1, Settings, default);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Empty(result.Snapshot.Segments);
        Assert.Single(cache.Discarded);
    }

    [Fact]
    public void OpaqueIdentityStringsMustNotBeMergedByUnicodeNormalization()
    {
        var composed = Settings with { Identity = Settings.Identity with { ModelVersion = "révision" } };
        var decomposed = Settings with { Identity = Settings.Identity with { ModelVersion = "re\u0301vision" } };
        Assert.NotEqual(TtsCacheKey.Create("text", composed, "v1"), TtsCacheKey.Create("text", decomposed, "v1"));
        Assert.NotEqual(TtsCacheKey.Create("text", Settings, "révision"), TtsCacheKey.Create("text", Settings, "re\u0301vision"));
    }

    [Theory]
    [InlineData("find", false)]
    [InlineData("find", true)]
    [InlineData("store", false)]
    [InlineData("store", true)]
    public async Task CancellationOrRevisionChangeAtCacheReturnCannotPublish(string phase, bool changeRevision)
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider();
        Action interrupt = () => { if (changeRevision) provider.Revision = new(2); else cancellation.Cancel(); };
        var cache = new FakeCache { AfterFind = phase == "find" ? interrupt : null, AfterStore = phase == "store" ? interrupt : null };
        var result = await new TtsPreGenerator(provider, cache, 1).PrepareAsync(Document("text"), 0, 1, Settings, cancellation.Token);
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.False(result.Snapshot.CanStartPlayback);
        Assert.Empty(result.Snapshot.Segments);
        if (phase == "find") Assert.Equal(0, provider.Calls);
        else Assert.Single(cache.Discarded);
    }

    [Theory]
    [InlineData(OperationStatus.Unsupported)]
    [InlineData(OperationStatus.Unknown)]
    public async Task NonSuccessProviderStatusIsPreservedForCaller(OperationStatus status)
    {
        var provider = new FakeProvider { Handler = (_, _) => Task.FromResult(new TtsSynthesisOutcome(status, null, null)) };
        var result = await new TtsPreGenerator(provider, new FakeCache(), 1).PrepareAsync(Document("text"), 0, 1, Settings, default);
        Assert.Equal(status, result.Status);
        Assert.False(result.Snapshot.CanStartPlayback);
    }

    [Fact]
    public async Task SynthesizedTextUsesTheSameNormalizationAsCacheIdentity()
    {
        var provider = new FakeProvider { Handler = (request, _) =>
        {
            Assert.Equal("chào", request.Text);
            return Task.FromResult(Success(request.Text));
        }};
        var result = await new TtsPreGenerator(provider, new FakeCache(), 1).PrepareAsync(Document("cha\u0300o"), 0, 1, Settings, default);
        Assert.Equal(OperationStatus.Succeeded, result.Status);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static LocalAudioAsset Asset(string name) => new(name, "wav", "fake-local", TimeSpan.FromSeconds(1), 48000);
    private static TtsSynthesisOutcome Success(string name) => new(OperationStatus.Succeeded, new(Asset(name + ".wav"), "fake-local", new(1)), null);
    private static TtsScriptDocument Document(params string[] texts) => new(string.Join("\n", texts), "test-split-v1",
        Array.AsReadOnly(texts.Select((text, index) => new TtsScriptSegment(index, text, null)).ToArray()));

    private sealed class FakeProvider : ITtsProvider
    {
        public string EngineId { get; init; } = "fake-local";
        public EngineRevision Revision { get; set; } = new(1);
        public int Calls { get; private set; }
        public Func<TtsSynthesisRequest, CancellationToken, Task<TtsSynthesisOutcome>> Handler { get; set; } =
            (request, _) => Task.FromResult(Success(request.Text));
        public Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken token)
        { Calls++; return Handler(request, token); }
        public Task<TtsHealth> GetHealthAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FakeCache : ITtsAudioCache
    {
        public Dictionary<string, LocalAudioAsset> Items { get; } = [];
        public List<LocalAudioAsset> Discarded { get; } = [];
        public OperationStatus StoreStatus { get; init; } = OperationStatus.Succeeded;
        public OperationStatus DiscardStatus { get; init; } = OperationStatus.Succeeded;
        public Action? AfterFind { get; init; }
        public Action? AfterStore { get; init; }
        public int Stores { get; private set; }
        public int Finds { get; private set; }
        public Task<OperationResult<LocalAudioAsset>> FindAsync(string key, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Finds++; AfterFind?.Invoke(); return Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, Items.GetValueOrDefault(key))); }
        public Task<OperationResult<LocalAudioAsset>> StoreAsync(string key, LocalAudioAsset asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Stores++;
            if (StoreStatus != OperationStatus.Succeeded) return Task.FromResult(new OperationResult<LocalAudioAsset>(StoreStatus, null));
            Items[key] = asset;
            AfterStore?.Invoke();
            return Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, asset));
        }
        public Task<OperationResult> DiscardAsync(LocalAudioAsset asset)
        { Discarded.Add(asset); return Task.FromResult(new OperationResult(DiscardStatus)); }
    }
}
