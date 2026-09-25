using System.Text.Json;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Control;
using TikTokAudio.Application.Tests.Fixtures;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Persistence;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class T100ControlPersistenceTests
{
    private static readonly Guid SessionId = Guid.Parse("2a2f6497-1c80-487c-9371-5dad9c175100");
    private const string RoomId = "t100-synthetic-room";
    private static readonly TimeSpan MessageTtl = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task ProductRenewalsAndTextIntervalsPersistTogetherThroughEightySecondProductSwitch()
    {
        using var h = await ControlHarness.CreateAsync();
        Succeeded(h.Product.SetTarget(Product(1)));
        Succeeded(h.Text.QueueManual("first synthetic message", MessageTtl));
        Succeeded(h.Text.QueueManual("second synthetic message", MessageTtl));
        await h.PumpAsync();
        var firstText = Assert.Single(await h.PersistTextLedgerAsync());
        Assert.Single(h.Shows);

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.PumpAsync();
        var secondText = Assert.Single(await h.PersistTextLedgerAsync());
        Assert.Equal(2, h.Shows.Length);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.PumpAsync();
        Assert.Equal(3, h.Shows.Length);
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        Succeeded(h.Product.SetTarget(Product(2)));
        await h.PumpAsync();
        await h.PersistProductAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.PumpAsync();

        Assert.Equal(new[] { Product(1), Product(1), Product(1), Product(2) }, h.Shows.Select(item => item.Product));
        Assert.Equal(2, h.Texts.Length);
        var reopened = await h.ReopenStoreAsync();
        var product = await ReadProductAsync(reopened);
        Assert.Equal(Product(2), product.Target);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(80), product.LastShownAt);
        Assert.Equal(TimeSpan.FromSeconds(110).Ticks, product.RenewalDue?.Ticks);
        Assert.Equal(DateTimeOffset.UnixEpoch, (await reopened.LoadActionLedgerAsync(firstText.ActionId))?.RecordedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), (await reopened.LoadActionLedgerAsync(secondText.ActionId))?.RecordedAtUtc);
        Assert.Equal(RoomId, (await reopened.LoadSessionAsync(SessionId))?.RoomId);
    }

    [Fact]
    public async Task UnknownProductAndTextRemainUnknownAfterReopenAndAreNeverAutomaticallyReplayed()
    {
        using var h = await ControlHarness.CreateAsync(productStatus: ControlActionStatus.Unknown,
            textStatus: ControlActionStatus.Unknown);
        Succeeded(h.Product.SetTarget(Product(1)));
        Succeeded(h.Text.QueueManual("uncertain synthetic text", MessageTtl));
        await h.PumpAsync();
        await h.PumpAsync();
        var uncertain = Assert.Single(await h.PersistTextLedgerAsync());
        await h.PersistProductAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.PumpAsync();

        Assert.Single(h.Shows);
        Assert.Single(h.Texts);
        Assert.Equal(1, h.Controller.VisibilityReads);
        var reopened = await h.ReopenStoreAsync();
        var persistedProduct = await ReadProductAsync(reopened);
        Assert.Equal(RoomControlState.NeedsConfirmation, persistedProduct.State);
        Assert.Null(persistedProduct.RenewalDue);
        Assert.Equal(ControlActionStatus.Unknown, persistedProduct.LastResult?.Status);
        Assert.Equal(ControlActionStatus.Unknown, (await reopened.LoadActionLedgerAsync(uncertain.ActionId))?.Status);

        var freshController = new SimulatedLiveRoomController();
        using var freshProduct = new ProductControlCoordinator(freshController, h.Clock);
        using var freshText = new TextDispatchCoordinator(freshController, h.Clock, new SeededRandomSource(100), TextOptions());
        await freshProduct.PumpAsync();
        await freshText.PumpAsync();
        Assert.Empty(freshController.Actions);
        Assert.Equal(RoomControlState.Disabled, freshProduct.Snapshot.State);
        Assert.Equal(RoomControlState.Disabled, freshText.Snapshot.State);
    }

    [Fact]
    public async Task LateCancelledProductResponseCannotOverwriteLatestPersistedEpoch()
    {
        using var h = await ControlHarness.CreateAsync();
        h.Controller.DelayNextShow = true;
        h.Product.SetTarget(Product(1));
        await h.PumpAsync();
        var delayed = Assert.IsType<DeferredResponse>(h.Controller.DelayedShow);
        var oldEpoch = h.Product.Snapshot.Context!.ProductEpoch;
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Product.SetTarget(Product(2), ProductTargetOrigin.Manual);
        await h.PumpAsync();
        Succeeded(h.Product.SetTarget(Product(3), ProductTargetOrigin.ProductEnter));
        await h.PumpAsync();
        await h.PersistProductAsync("pending");
        Assert.Single(h.Shows);
        Assert.True(delayed.Token.IsCancellationRequested);
        Assert.Null(h.Product.Snapshot.LastShownAt);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        delayed.Complete(ControlActionStatus.Confirmed);
        await h.PumpAsync();
        await h.PersistProductAsync();

        Assert.Equal(new[] { Product(1), Product(3) }, h.Shows.Select(item => item.Product));
        var reopened = await h.ReopenStoreAsync();
        var pending = await ReadProductAsync(reopened, "pending");
        var current = await ReadProductAsync(reopened);
        Assert.Equal(Product(3), pending.Target);
        Assert.Null(pending.LastShownAt);
        Assert.Equal(Product(3), current.Target);
        Assert.NotEqual(oldEpoch, current.Context!.ProductEpoch);
        Assert.Equal(Product(1), current.LastIgnoredResponse?.Target);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), current.LastShownAt);
        Assert.Equal(TimeSpan.FromSeconds(32).Ticks, current.RenewalDue?.Ticks);
    }

    [Fact]
    public async Task TimedOutProductRequiresReadbackBeforeConfirmedRenewalStateCanBePersisted()
    {
        using var h = await ControlHarness.CreateAsync();
        h.Controller.DelayNextShow = true;
        h.Product.SetTarget(Product(1));
        await h.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await h.PumpAsync();
        await h.PersistProductAsync("timeout");
        Assert.Equal(0, h.Controller.VisibilityReads);
        Assert.True(h.Product.Snapshot.HasInFlightOperation);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsType<DeferredResponse>(h.Controller.DelayedShow).Complete(ControlActionStatus.Confirmed);
        await h.PumpAsync();
        await h.PersistProductAsync("readback");

        Assert.Single(h.Shows);
        Assert.Equal(1, h.Controller.VisibilityReads);
        var reopened = await h.ReopenStoreAsync();
        var timeout = await ReadProductAsync(reopened, "timeout");
        var confirmed = await ReadProductAsync(reopened, "readback");
        Assert.Equal(ControlActionStatus.Unknown, timeout.LastResult?.Status);
        Assert.Null(timeout.RenewalDue);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(6), confirmed.LastShownAt);
        Assert.Equal(TimeSpan.FromSeconds(36).Ticks, confirmed.RenewalDue?.Ticks);
        h.Clock.Advance(TimeSpan.FromSeconds(29));
        await h.PumpAsync();
        Assert.Single(h.Shows);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.PumpAsync();
        Assert.Equal(2, h.Shows.Length);
    }

    [Fact]
    public async Task PersistedTextTimeoutCannotBePromotedByLateConfirmationWhileNextMessageCanProceed()
    {
        using var h = await ControlHarness.CreateAsync();
        h.Controller.DelayNextText = true;
        h.Text.QueueManual("late first", MessageTtl);
        h.Text.QueueManual("independent second", MessageTtl);
        await h.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await h.PumpAsync();
        var timedOut = Assert.Single(await h.PersistTextLedgerAsync());
        Assert.Equal(ControlActionStatus.Unknown, timedOut.Status);
        Assert.True(h.Text.Snapshot.HasInFlightOperation);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.PumpAsync();
        Assert.Single(h.Texts);

        Assert.IsType<DeferredResponse>(h.Controller.DelayedText).Complete(ControlActionStatus.Confirmed);
        Assert.Empty(h.Text.DrainLedger());
        Assert.Equal(timedOut.ActionId, h.Text.Snapshot.LastIgnoredResponse?.ActionId);
        await h.PumpAsync();
        var next = Assert.Single(await h.PersistTextLedgerAsync());

        Assert.Equal(new[] { "late first", "independent second" }, h.Texts.Select(item => item.Text));
        var reopened = await h.ReopenStoreAsync();
        Assert.Equal(ControlActionStatus.Unknown, (await reopened.LoadActionLedgerAsync(timedOut.ActionId))?.Status);
        Assert.Equal(ControlActionStatus.Confirmed, (await reopened.LoadActionLedgerAsync(next.ActionId))?.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(35), next.RecordedAtUtc);
        Assert.Equal(0, h.Controller.VisibilityReads);
    }

    [Fact]
    public async Task DisconnectSuspendsBothChannelsAndResumeChecksProductThenSchedulesOnlyFutureText()
    {
        using var h = await ControlHarness.CreateAsync();
        h.Product.SetTarget(Product(1));
        h.Text.ConfigureSchedules([Schedule(30, 600)]);
        h.Text.QueueManual("before disconnect", MessageTtl);
        await h.PumpAsync();
        var first = Assert.Single(await h.PersistTextLedgerAsync());
        h.Text.QueueManual("queued before disconnect", MessageTtl);
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Succeeded(h.Product.Disconnect());
        Succeeded(h.Text.Disconnect());
        h.Clock.Advance(TimeSpan.FromSeconds(90));
        await h.PumpAsync();
        Assert.Single(h.Shows);
        Assert.Single(h.Texts);

        Succeeded(h.Product.Resume(SessionId, RoomId));
        Succeeded(h.Text.Resume(SessionId, RoomId));
        await h.PumpAsync();
        Assert.Equal(1, h.Controller.VisibilityReads);
        Assert.Single(h.Shows);
        Assert.Single(h.Texts);
        Assert.Equal(TimeSpan.FromSeconds(130).Ticks, h.Product.Snapshot.RenewalDue?.Ticks);
        h.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        await h.PumpAsync();
        Assert.Single(h.Texts);
        h.Clock.Advance(TimeSpan.FromTicks(1));
        await h.PumpAsync();
        var next = Assert.Single(await h.PersistTextLedgerAsync());
        await h.PersistProductAsync();

        Assert.Equal(new[] { "before disconnect", "scheduled synthetic text" }, h.Texts.Select(item => item.Text));
        Assert.Equal(2, h.Shows.Length);
        var reopened = await h.ReopenStoreAsync();
        Assert.Equal(ControlActionStatus.Confirmed, (await reopened.LoadActionLedgerAsync(first.ActionId))?.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(130), (await reopened.LoadActionLedgerAsync(next.ActionId))?.RecordedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(130), (await ReadProductAsync(reopened)).LastShownAt);
    }

    [Fact]
    public async Task StopAndRoomReplacementKeepLateOldOutcomesOutOfNewSessionState()
    {
        using var h = await ControlHarness.CreateAsync();
        h.Controller.DelayNextShow = true;
        h.Controller.DelayNextText = true;
        h.Product.SetTarget(Product(1));
        h.Text.QueueManual("old session text", MessageTtl);
        await h.PumpAsync();
        var oldShow = Assert.IsType<DeferredResponse>(h.Controller.DelayedShow);
        var oldText = Assert.IsType<DeferredResponse>(h.Controller.DelayedText);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Succeeded(h.Product.Stop());
        Succeeded(h.Text.Stop());
        var invalidated = Assert.Single(await h.PersistTextLedgerAsync());
        await h.PersistProductAsync("stopped");
        Assert.False(h.Product.Resume(SessionId, RoomId).IsSuccess);
        Assert.False(h.Text.Resume(SessionId, RoomId).IsSuccess);

        var newSession = Guid.NewGuid();
        Succeeded(await h.Store.SaveSessionAsync(new SessionState(newSession, "replacement-room", h.Clock.UtcNow)));
        Succeeded(h.Product.Start(newSession, "replacement-room"));
        Succeeded(h.Text.Start(newSession, "replacement-room"));
        h.Product.SetTarget(Product(2));
        h.Text.QueueManual("new session text", MessageTtl);
        h.Clock.Advance(TimeSpan.FromSeconds(29));
        await h.PumpAsync();
        Assert.Single(h.Shows);
        Assert.Single(h.Texts);
        Assert.True(oldShow.Token.IsCancellationRequested);
        Assert.True(oldText.Token.IsCancellationRequested);
        oldShow.Complete(ControlActionStatus.Confirmed);
        oldText.Complete(ControlActionStatus.Confirmed);
        await h.PumpAsync();
        var current = Assert.Single(await h.PersistTextLedgerAsync());
        await h.PersistProductAsync();

        var reopened = await h.ReopenStoreAsync();
        var stoppedProduct = await ReadProductAsync(reopened, "stopped");
        Assert.Equal(RoomControlState.Disabled, stoppedProduct.State);
        Assert.Null(stoppedProduct.Target);
        Assert.Null(stoppedProduct.RenewalDue);
        var oldEntry = Assert.IsType<ActionLedgerEntry>(await reopened.LoadActionLedgerAsync(invalidated.ActionId));
        Assert.Equal(SessionId, oldEntry.SessionId);
        Assert.Equal(ControlActionStatus.Unknown, oldEntry.Status);
        Assert.Equal(newSession, (await reopened.LoadActionLedgerAsync(current.ActionId))?.SessionId);
        Assert.Equal(newSession, (await ReadProductAsync(reopened)).Context?.SessionId);
        Assert.Equal(Product(2), h.Shows[1].Product);
        Assert.Equal(newSession, h.Shows[1].Context.SessionId);
        Assert.Equal(newSession, h.Texts[1].Context.SessionId);
        Assert.Equal(2, h.Shows.Length);
        Assert.Equal(2, h.Texts.Length);
    }

    [Fact]
    public async Task CancelledLedgerWriteCanRetrySameDrainedBatchWithoutResendingPlatformMessage()
    {
        using var h = await ControlHarness.CreateAsync();
        h.Text.QueueManual("persist once", MessageTtl);
        await h.PumpAsync();
        var pendingTransfer = h.Text.DrainLedger();
        var entry = Assert.Single(pendingTransfer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(OperationStatus.Cancelled,
            (await h.Store.AppendActionLedgerBatchAsync(pendingTransfer, cancellation.Token)).Status);
        Assert.Null(await h.Store.LoadActionLedgerAsync(entry.ActionId));
        Succeeded(await h.Store.AppendActionLedgerBatchAsync(pendingTransfer));
        Succeeded(await h.Store.AppendActionLedgerBatchAsync(pendingTransfer));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.PumpAsync();

        Assert.Single(h.Texts);
        Assert.Empty(h.Text.DrainLedger());
        var reopened = await h.ReopenStoreAsync();
        Assert.Equal(entry, await reopened.LoadActionLedgerAsync(entry.ActionId));
    }

    [Fact]
    public async Task LedgerBackpressureAllowsNextSendOnlyAfterBoundedRecordsAreTransferred()
    {
        using var h = await ControlHarness.CreateAsync(TextOptions() with { LedgerCapacity = 1 });
        h.Text.QueueManual("first bounded message", MessageTtl);
        h.Text.QueueManual("second bounded message", MessageTtl);
        await h.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.PumpAsync();
        Assert.Single(h.Texts);
        Assert.Equal(1, h.Text.Snapshot.QueuedCount);
        Assert.Equal(1, h.Text.Snapshot.PendingLedgerCount);

        var first = Assert.Single(await h.PersistTextLedgerAsync());
        await h.PumpAsync();
        var second = Assert.Single(await h.PersistTextLedgerAsync());

        Assert.Equal(2, h.Texts.Length);
        var reopened = await h.ReopenStoreAsync();
        Assert.Equal(first, await reopened.LoadActionLedgerAsync(first.ActionId));
        Assert.Equal(second, await reopened.LoadActionLedgerAsync(second.ActionId));
        Assert.NotEqual(first.ActionId, second.ActionId);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), second.RecordedAtUtc);
    }

    [Fact]
    public async Task ExpiredTimedTextIsNotRecordedWhileProductRenewalsContinue()
    {
        using var h = await ControlHarness.CreateAsync();
        h.Product.SetTarget(Product(1));
        h.Text.ConfigureSchedules([Schedule(1, 20)]);
        h.Text.QueueManual("submitted manual text", MessageTtl);
        await h.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.PumpAsync();
        Assert.Equal(1, h.Text.Snapshot.QueuedCount);
        h.Clock.Advance(TimeSpan.FromSeconds(29));
        await h.PumpAsync();
        var actual = Assert.Single(await h.PersistTextLedgerAsync());
        await h.PersistProductAsync();

        Assert.Single(h.Texts);
        Assert.Equal(0, h.Text.Snapshot.QueuedCount);
        Assert.Equal(2, h.Shows.Length);
        var reopened = await h.ReopenStoreAsync();
        Assert.Equal("Text:Manual", (await reopened.LoadActionLedgerAsync(actual.ActionId))?.ActionType);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), (await ReadProductAsync(reopened)).LastShownAt);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.PumpAsync();
        Assert.Equal(3, h.Shows.Length);
        Assert.Single(h.Texts);
        Assert.Empty(h.Text.DrainLedger());
    }

    [Fact]
    public async Task ManualTargetWinsSameRenewalBoundaryAndOnlyActualWinningStateIsPersisted()
    {
        using var h = await ControlHarness.CreateAsync();
        h.Product.SetTarget(Product(1));
        await h.PumpAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        h.Product.SetTarget(Product(2), ProductTargetOrigin.ProductEnter);
        h.Product.SetTarget(Product(3), ProductTargetOrigin.ScriptMarker);
        h.Product.SetTarget(Product(4), ProductTargetOrigin.Manual);
        await h.PumpAsync();
        await h.PersistProductAsync();

        Assert.Equal(new[] { Product(1), Product(4) }, h.Shows.Select(item => item.Product));
        var reopened = await h.ReopenStoreAsync();
        var stored = await ReadProductAsync(reopened);
        Assert.Equal(Product(4), stored.Target);
        Assert.Equal(ControlActionStatus.Confirmed, stored.LastResult?.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), stored.LastShownAt);
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, stored.RenewalDue?.Ticks);
        Assert.Empty(h.Texts);
    }

    private static ProductTarget Product(int id) => new($"local-{id}", $"synthetic-platform-{id}");
    private static TextDispatchOptions TextOptions() => new(8, 8, 4, TimeSpan.FromSeconds(5), TimeSpan.Zero);
    private static TimedTextSchedule Schedule(int intervalSeconds, int endsAtSeconds) => new("synthetic-schedule",
        new[] { "scheduled synthetic text" }, TimeSpan.FromSeconds(intervalSeconds), MessageTtl,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(endsAtSeconds));
    private static void Succeeded(OperationResult result) => Assert.True(result.IsSuccess, result.Detail);

    private static async Task<ProductControlSnapshot> ReadProductAsync(SqliteStateStore store, string documentId = "current")
    {
        var document = Assert.IsType<LocalStateDocument>(await store.LoadDocumentAsync(LocalDocumentKind.ProductState, documentId));
        return Assert.IsType<ProductControlSnapshot>(JsonSerializer.Deserialize<ProductControlSnapshot>(document.Json));
    }

    private sealed class ControlHarness : IDisposable
    {
        private readonly string _name = "t100-" + Guid.NewGuid().ToString("N");
        private readonly string _temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string _root;
        public LocalDataPaths Paths { get; }
        public FakeClock Clock { get; } = new();
        public FaultController Controller { get; }
        public ProductControlCoordinator Product { get; }
        public TextDispatchCoordinator Text { get; }
        public SqliteStateStore Store { get; }
        public SimulatedControlAction[] Shows => Controller.Simulated.Actions.Where(action => action.Kind == "ShowProduct").ToArray();
        public SimulatedControlAction[] Texts => Controller.Simulated.Actions.Where(action => action.Kind == "SendText").ToArray();

        private ControlHarness(TextDispatchOptions options, ControlActionStatus productStatus, ControlActionStatus textStatus)
        {
            _root = Path.GetFullPath(Path.Combine(_temporaryRoot, _name));
            Paths = LocalDataPaths.ForDevelopment(_root);
            Store = NewStore();
            Controller = new FaultController(new SimulatedLiveRoomController(productStatus, textStatus));
            Product = new ProductControlCoordinator(Controller, Clock);
            Text = new TextDispatchCoordinator(Controller, Clock, new SeededRandomSource(100), options);
        }

        public static async Task<ControlHarness> CreateAsync(TextDispatchOptions? options = null,
            ControlActionStatus productStatus = ControlActionStatus.Confirmed,
            ControlActionStatus textStatus = ControlActionStatus.Confirmed)
        {
            var harness = new ControlHarness(options ?? TextOptions(), productStatus, textStatus);
            try
            {
                Succeeded(await harness.Store.InitializeAsync());
                Succeeded(await harness.Store.SaveSessionAsync(new SessionState(SessionId, RoomId, harness.Clock.UtcNow)));
                Succeeded(harness.Product.Start(SessionId, RoomId));
                Succeeded(harness.Text.Start(SessionId, RoomId));
                return harness;
            }
            catch
            {
                harness.Dispose();
                throw;
            }
        }

        public async Task PumpAsync()
        {
            await Product.PumpAsync();
            await Text.PumpAsync();
        }

        // This is test-only persistence handoff; it does not restore or replay coordinator work.
        public Task<OperationResult> SaveProductDocumentAsync(string documentId) => Store.SaveDocumentAsync(
            new LocalStateDocument(LocalDocumentKind.ProductState, documentId, 1,
                JsonSerializer.Serialize(Product.Snapshot), Clock.UtcNow));

        public async Task PersistProductAsync(string documentId = "current") => Succeeded(await SaveProductDocumentAsync(documentId));

        public async Task<IReadOnlyList<ActionLedgerEntry>> PersistTextLedgerAsync()
        {
            var records = Text.DrainLedger();
            Succeeded(await Store.AppendActionLedgerBatchAsync(records));
            return records;
        }

        public async Task<SqliteStateStore> ReopenStoreAsync()
        {
            var store = NewStore();
            Succeeded(await store.InitializeAsync());
            return store;
        }

        private SqliteStateStore NewStore() => new(Paths, new SqliteStateStoreOptions(1, 1024 * 1024));

        public void Dispose()
        {
            Product.Dispose();
            Text.Dispose();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var target = Path.GetFullPath(_root);
            if (!string.Equals(Path.GetDirectoryName(target), _temporaryRoot, comparison) ||
                !string.Equals(Path.GetFileName(target), _name, StringComparison.Ordinal) ||
                !Guid.TryParseExact(_name[5..], "N", out _))
                throw new InvalidOperationException("Cleanup target is not this test's temporary GUID directory.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private sealed class FaultController(SimulatedLiveRoomController simulated) : ILiveRoomController
    {
        public SimulatedLiveRoomController Simulated { get; } = simulated;
        public RoomControlState State => Simulated.State;
        public RoomControlCapabilities Capabilities => Simulated.Capabilities;
        public bool DelayNextShow { get; set; }
        public bool DelayNextText { get; set; }
        public int VisibilityReads { get; private set; }
        public DeferredResponse? DelayedShow { get; private set; }
        public DeferredResponse? DelayedText { get; private set; }

        public Task<ControlActionResult> ShowProductAsync(ProductTarget target, RoomOperationContext context, CancellationToken cancellationToken)
        {
            var immediate = Simulated.ShowProductAsync(target, context, cancellationToken);
            if (!DelayNextShow) return immediate;
            DelayNextShow = false;
            DelayedShow = new DeferredResponse(cancellationToken);
            return DelayedShow.Task;
        }

        public Task<ControlActionResult> SendTextAsync(string text, RoomOperationContext context, CancellationToken cancellationToken)
        {
            var immediate = Simulated.SendTextAsync(text, context, cancellationToken);
            if (!DelayNextText) return immediate;
            DelayNextText = false;
            DelayedText = new DeferredResponse(cancellationToken);
            return DelayedText.Task;
        }

        public Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)> ReadProductVisibilityAsync(
            RoomOperationContext context, CancellationToken cancellationToken)
        {
            VisibilityReads++;
            return Simulated.ReadProductVisibilityAsync(context, cancellationToken);
        }
    }

    private sealed class DeferredResponse(CancellationToken token)
    {
        private readonly TaskCompletionSource<ControlActionResult> _completion = new();
        public CancellationToken Token { get; } = token;
        public Task<ControlActionResult> Task => _completion.Task;

        public void Complete(ControlActionStatus status)
        {
            var previous = SynchronizationContext.Current;
            try
            {
                // Deliver the controlled receipt before FakeClock advances again.
                SynchronizationContext.SetSynchronizationContext(null);
                _completion.SetResult(new ControlActionResult(status, "synthetic delayed acknowledgement"));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }
}
