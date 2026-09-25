using System.Collections.Concurrent;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Control;
using TikTokAudio.Application.Events;
using TikTokAudio.Application.Interactions;
using TikTokAudio.Application.Media;
using TikTokAudio.Application.Playback;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Persistence;
using Xunit;

namespace TikTokAudio.Integration.Tests;

// Explicit test-only composition: no recovery host, audio device, file decoder or network client.
internal sealed class T100PlaybackHarness : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "t100-playback-" + Guid.NewGuid().ToString("N"));
    private IAsyncEnumerator<LiveEvent>? _reader;
    private int _eventNumber;
    internal Guid SessionId { get; } = Guid.NewGuid();
    internal const string RoomId = "synthetic-t100-room";
    internal string SessionText => SessionId.ToString("D");
    internal FakeClock Clock { get; } = new();
    internal SimulatedLiveEventSource Source { get; private set; } = new();
    internal SimulatedLiveRoomController Controller { get; } = new();
    internal MemoryOutput Output { get; } = new();
    internal MemoryProvider Provider { get; } = new();
    internal TaskCompletionSource AssetDiscarded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal LocalDataPaths Paths { get; }
    internal SqliteStateStore Store { get; }
    internal EventDeduplicationCoordinator Events { get; }
    internal InteractionRuleCoordinator Rules { get; }
    internal PreRecordedPlaybackPlanner Plan { get; }
    internal AudioPlaybackScheduler Scheduler { get; }
    internal InteractionPlaybackCoordinator Voice { get; }
    internal TextDispatchCoordinator Text { get; }
    internal List<InteractionSettlement> Settlements { get; } = [];
    internal List<ProductBoundary> Boundaries { get; } = [];
    internal List<ActionLedgerEntry> TextLedger { get; } = [];

    private T100PlaybackHarness(InteractionPolicyOptions? options, IEnumerable<KeywordRule>? rules)
    {
        Paths = LocalDataPaths.ForDevelopment(_root);
        Store = new(Paths, new(1, 65536));
        Events = new(Store);
        Rules = new(Clock, rules ?? [new KeywordRule("question", ["giá"], template: "{userName}: {comment}")], options,
            new SeededRandomSource(100));
        Rules.SetContext(SessionId, RoomId);
        var asset = new LocalAudioAsset("synthetic-base.wav", "wav", "pre-recorded", TimeSpan.FromMinutes(3), 24000);
        var catalog = new ProductDirectoryImport("synthetic-catalog",
            [new ImportedAudioProduct(1, "1", "platform-1",
                [new ImportedAudioGroup(1, "1/1", [new ImportedAudioClip("base-clip", asset, null)])], true)], []);
        Plan = new(catalog, PlanRevision.Initial, new SeededRandomSource(101));
        Scheduler = new(Plan, Output, Clock);
        var registry = new TtsEngineRegistry([new TtsEngineConfiguration(Provider.EngineId, Provider, "voice")], Provider.EngineId);
        Voice = new(Rules, Scheduler, registry, new MemoryCache(AssetDiscarded), Clock,
            snapshot => new(new(snapshot.EngineId, "1", "synthetic", "1"), "voice", "vi", 1, 0, 1, "none"), Events);
        Text = new(Controller, Clock, new SeededRandomSource(102), new(10, 20, 10, TimeSpan.FromSeconds(5), TimeSpan.Zero), Rules);
    }

    internal static async Task<T100PlaybackHarness> CreateAsync(InteractionPolicyOptions? options = null, IEnumerable<KeywordRule>? rules = null)
    {
        var harness = new T100PlaybackHarness(options, rules);
        try
        {
            Assert.True((await harness.Store.InitializeAsync()).IsSuccess);
            await harness.ConnectAsync();
            Assert.True(harness.Text.Start(harness.SessionId, RoomId).IsSuccess);
            var started = await harness.Scheduler.StartAsync(harness.SessionText);
            Assert.True(started.Result.IsSuccess);
            harness.Boundaries.AddRange(started.Boundaries);
            return harness;
        }
        catch { await harness.DisposeAsync(); throw; }
    }

    private async Task ConnectAsync()
    {
        var request = new LiveConnectionRequest(SessionId, RoomId);
        Assert.True((await Events.ReconnectAsync(request)).IsSuccess);
        Assert.True((await Source.ConnectAsync(request, CancellationToken.None)).IsSuccess);
        _reader = Source.ReadEventsAsync(CancellationToken.None).GetAsyncEnumerator();
    }

    internal async Task ReconnectAsync()
    {
        await Source.DisconnectAsync(CancellationToken.None);
        if (_reader is not null) await _reader.DisposeAsync();
        Source = new SimulatedLiveEventSource();
        await ConnectAsync();
    }

    internal LiveEvent Event(LiveEventType type, string? user = "synthetic-user", string? content = null, string? displayName = null) =>
        new("simulated", SessionId, RoomId, type, $"event-{++_eventNumber}", user, displayName ?? user,
            Clock.UtcNow, Clock.UtcNow, content, null, null, EventIdentityQuality.Complete);

    internal async Task<(EventProcessResult Admission, InteractionCandidate? Candidate)> DeliverAsync(LiveEvent item)
    {
        Source.Inject(item);
        Assert.True(await _reader!.MoveNextAsync());
        Assert.Equal(item, _reader.Current);
        var admitted = await Events.ProcessAsync(_reader.Current);
        var candidate = admitted.ShouldProcess ? Rules.Enqueue(item, admitted).Candidate : null;
        return (admitted, candidate);
    }

    internal async Task QueueAsync() => Assert.True((await Voice.QueueNextAsync(SessionText)).IsSuccess);

    internal async Task PumpAsync()
    {
        var update = await Voice.PumpAsync();
        Settlements.AddRange(update.Settlements);
        Boundaries.AddRange(update.Scheduler.Boundaries);
        await Text.PumpAsync();
        var ledger = Text.DrainLedger();
        if (ledger.Count > 0)
        {
            Assert.True((await Store.AppendActionLedgerBatchAsync(ledger)).IsSuccess);
            TextLedger.AddRange(ledger);
        }
    }

    internal async Task UntilAsync(Func<bool> condition)
    {
        // Wall time is only a stalled-test guard; all product deadlines use FakeClock.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await PumpAsync();
            if (!condition()) await Task.Delay(1, timeout.Token);
        }
    }

    internal async Task<SqliteStateStore> ReopenAsync()
    {
        var reopened = new SqliteStateStore(Paths, new(1, 65536));
        Assert.True((await reopened.InitializeAsync()).IsSuccess);
        return reopened;
    }

    public async ValueTask DisposeAsync()
    {
        Text.Dispose();
        await Voice.StopAsync();
        if (_reader is not null) await _reader.DisposeAsync();
        await Source.DisconnectAsync(CancellationToken.None);
        var full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
            !Path.GetFileName(full).StartsWith("t100-playback-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(full)[14..], "N", out _)) throw new InvalidOperationException("Unsafe synthetic cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    internal sealed class MemoryOutput : IAudioOutput
    {
        public PlaybackState State { get; private set; } = PlaybackState.Idle;
        public AudioCursor? CurrentCursor { get; set; } = AudioCursor.Start;
        internal List<AudioPlaybackRequest> Plays { get; } = [];
        internal int PauseCount { get; private set; }
        internal bool FailInteraction { get; set; }
        public Task<OperationResult> PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotEqual(PlaybackState.BasePlaying, State); // A running voice must first pause, stop or finish.
            if (FailInteraction && request.Asset.EngineId == "memory-engine") return Task.FromResult(OperationResult.Failed("synthetic output failure"));
            Plays.Add(request);
            CurrentCursor = request.StartAt;
            State = PlaybackState.BasePlaying;
            return Task.FromResult(OperationResult.Succeeded());
        }
        public Task<OperationResult> PauseAsync(CancellationToken cancellationToken)
        {
            PauseCount++;
            State = PlaybackState.Paused;
            return Task.FromResult(OperationResult.Succeeded());
        }
        public Task<OperationResult> ResumeAsync(CancellationToken cancellationToken) { State = PlaybackState.BasePlaying; return Task.FromResult(OperationResult.Succeeded()); }
        public Task<OperationResult> StopAsync(CancellationToken cancellationToken) { State = PlaybackState.Stopped; return Task.FromResult(OperationResult.Succeeded()); }
        public Task<OperationResult> SetEnvironmentMixAsync(EnvironmentMixSettings settings, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Succeeded());
        internal void Complete() { State = PlaybackState.Idle; CurrentCursor = new(24000, 24000); }
    }

    internal sealed class MemoryProvider : ITtsProvider
    {
        public string EngineId => "memory-engine";
        public EngineRevision Revision => EngineRevision.Initial;
        internal ConcurrentQueue<TtsSynthesisRequest> Requests { get; } = new();
        internal Func<TtsSynthesisRequest, CancellationToken, Task<TtsSynthesisOutcome>>? Handler { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            Entered.TrySetResult();
            return Handler?.Invoke(request, cancellationToken) ?? Task.FromResult(Success());
        }
        internal TtsSynthesisOutcome Success() => new(OperationStatus.Succeeded,
            new(new LocalAudioAsset("synthetic-interaction.wav", "wav", EngineId, TimeSpan.FromSeconds(1), 24000), EngineId, Revision), null);
        public Task<TtsHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new TtsHealth(TtsHealthStatus.Healthy, EngineId, null));
        public Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult<IReadOnlyList<TtsVoice>>(OperationStatus.Succeeded, []));
    }

    private sealed class MemoryCache(TaskCompletionSource discarded) : ITtsAudioCache
    {
        private readonly ConcurrentDictionary<string, LocalAudioAsset> _assets = new(StringComparer.Ordinal);
        public Task<OperationResult<LocalAudioAsset>> FindAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, _assets.GetValueOrDefault(key)));
        public Task<OperationResult<LocalAudioAsset>> StoreAsync(string key, LocalAudioAsset generatedAsset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _assets[key] = generatedAsset;
            return Task.FromResult(new OperationResult<LocalAudioAsset>(OperationStatus.Succeeded, generatedAsset));
        }
        public Task<OperationResult> DiscardAsync(LocalAudioAsset generatedAsset)
        {
            discarded.TrySetResult();
            return Task.FromResult(OperationResult.Succeeded());
        }
    }
}
