using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Control;

/// <summary>
/// One bounded product-control lane, driven by the host's pump. Own the controller exclusively;
/// a cancelled pump does not cancel an already submitted platform operation.
/// </summary>
public sealed partial class ProductControlCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly ILiveRoomController _controller;
    private readonly IClock _clock;
    private readonly ProductControlOptions _options;
    private RoomControlState _state = RoomControlState.Disabled;
    private RoomOperationContext? _context;
    private ProductTarget? _target;
    private long _epoch;
    private bool _enabled;
    private bool _disposed;
    private ProductTargetOrigin? _pendingOrigin;
    private WorkKind? _next;
    private MonotonicTimestamp? _workDue;
    private MonotonicTimestamp? _renewalDue;
    private DateTimeOffset? _lastShownAt;
    private ControlActionResult? _lastResult;
    private string? _detail;
    private int _retryCount;
    private PendingOperation? _inFlight;
    private ProductControlIgnoredResponse? _lastIgnored;

    public ProductControlCoordinator(ILiveRoomController controller, IClock clock, ProductControlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(clock);
        _controller = controller;
        _clock = clock;
        _options = options ?? new ProductControlOptions();
        _options.Validate();
    }

    public ProductControlSnapshot Snapshot
    {
        get { lock (_gate) return SnapshotLocked(); }
    }

    public OperationResult Start(Guid sessionId, string roomId)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(roomId))
            return OperationResult.Failed("A session and room are required.");
        PendingOperation? cancel;
        lock (_gate)
        {
            if (_disposed) return OperationResult.Failed("Product control is disposed.");
            cancel = InvalidateLocked();
            _context = new RoomOperationContext(sessionId, roomId, new ProductEpoch(_epoch));
            _target = null;
            _enabled = true;
            _state = RoomControlState.Ready;
        }
        Cancel(cancel);
        return OperationResult.Succeeded();
    }

    public OperationResult SetTarget(ProductTarget target, ProductTargetOrigin origin = ProductTargetOrigin.ProductEnter)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.LocalProductId) || string.IsNullOrWhiteSpace(target.PlatformProductId)
            || !Enum.IsDefined(origin)) return OperationResult.Failed("Invalid product target or origin.");
        PendingOperation? cancel;
        lock (_gate)
        {
            if (_disposed || !_enabled || _context is null)
                return OperationResult.Failed("Product control is not running.");
            if (_pendingOrigin is { } pending && origin < pending)
                return OperationResult.Cancelled("A higher-priority target is pending submission.");
            cancel = InvalidateLocked();
            _target = target;
            _pendingOrigin = origin;
            _next = WorkKind.Show;
            _state = RoomControlState.Pending;
        }
        Cancel(cancel);
        return OperationResult.Succeeded();
    }

    public OperationResult Pause() => Suspend("Product control is paused.");
    public OperationResult Disconnect() => Suspend("Room connection is suspended.");

    private OperationResult Suspend(string detail)
    {
        PendingOperation? cancel;
        lock (_gate)
        {
            if (_disposed || _context is null) return OperationResult.Failed("No active session.");
            cancel = InvalidateLocked();
            _enabled = false;
            _state = RoomControlState.Suspended;
            _detail = detail;
        }
        Cancel(cancel);
        return OperationResult.Succeeded();
    }

    public OperationResult Resume(Guid sessionId, string roomId)
    {
        lock (_gate)
        {
            if (_disposed || _context is null || _enabled || _context.SessionId != sessionId || _context.RoomId != roomId)
                return OperationResult.Failed("Resume requires the same suspended session and room.");
            _enabled = true;
            _next = _target is null ? null : WorkKind.ResumeRead;
            _state = _target is null ? RoomControlState.Ready : RoomControlState.Pending;
            _detail = null;
            return OperationResult.Succeeded();
        }
    }

    public OperationResult Stop()
    {
        PendingOperation? cancel;
        lock (_gate)
        {
            cancel = StopLocked();
        }
        Cancel(cancel);
        return OperationResult.Succeeded();
    }

    public void Dispose()
    {
        PendingOperation? cancel;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cancel = StopLocked();
        }
        Cancel(cancel);
    }

    private PendingOperation? StopLocked()
    {
        var cancel = InvalidateLocked();
        _enabled = false;
        _context = null;
        _target = null;
        _state = RoomControlState.Disabled;
        return cancel;
    }

    private PendingOperation? InvalidateLocked()
    {
        _epoch = checked(_epoch + 1);
        if (_context is not null) _context = _context with { ProductEpoch = new ProductEpoch(_epoch) };
        _next = null;
        _workDue = null;
        _renewalDue = null;
        _lastShownAt = null;
        _lastResult = null;
        _detail = null;
        _pendingOrigin = null;
        _retryCount = 0;
        return _inFlight;
    }

    public Task<ProductControlSnapshot> PumpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PendingOperation? cancel = null;
        ProductControlSnapshot snapshot;
        lock (_gate)
        {
            ConsumeCompletedLocked();
            if (_inFlight is { TimedOut: false } pending && _clock.Now.Ticks >= pending.Deadline.Ticks)
            {
                pending.TimedOut = true;
                cancel = pending;
                if (IsCurrentLocked(pending))
                {
                    _state = RoomControlState.NeedsConfirmation;
                    _renewalDue = null;
                    _lastResult = new ControlActionResult(ControlActionStatus.Unknown, "Control request timed out; awaiting lane release.");
                    _detail = _lastResult.Value.Detail;
                }
            }

            if (_inFlight is null && _enabled && !_disposed && _context is not null && _target is not null)
            {
                if (_next is null && _renewalDue is { } due && _clock.Now.Ticks >= due.Ticks)
                {
                    _next = WorkKind.Show;
                    _retryCount = 0;
                }
                if (_next is { } work && (_workDue is null || _clock.Now.Ticks >= _workDue.Value.Ticks))
                    SubmitLocked(work);
                ConsumeCompletedLocked();
            }
            // Priority applies only to commands coalesced within this pump batch, even when
            // an older non-cooperative request still owns the physical lane.
            _pendingOrigin = null;
            snapshot = SnapshotLocked();
        }
        Cancel(cancel);
        return Task.FromResult(snapshot);
    }

    private void SubmitLocked(WorkKind work)
    {
        _next = null;
        _workDue = null;
        _renewalDue = null;
        _pendingOrigin = null;
        if (work == WorkKind.Show && !_controller.Capabilities.ShowProduct)
        {
            SetFailureLocked(RoomControlState.Unsupported, ControlActionStatus.Unsupported, "Product display is unsupported.");
            return;
        }
        if (work != WorkKind.Show && !_controller.Capabilities.ReadProductVisibility)
        {
            SetFailureLocked(RoomControlState.NeedsConfirmation, ControlActionStatus.Unsupported, "Visibility cannot be verified.");
            return;
        }
        // Readback is also allowed when the adapter explicitly requests confirmation.
        if (_controller.State != RoomControlState.Ready &&
            !(work != WorkKind.Show && _controller.State == RoomControlState.NeedsConfirmation))
        {
            SetFailureLocked(RoomControlState.Suspended, ControlActionStatus.Unknown, "Controller is not ready; explicit recovery is required.");
            _enabled = false;
            return;
        }

        var operation = new PendingOperation(work, _context!, _target!, Add(_clock.Now, _options.RequestTimeout));
        _inFlight = operation;
        _state = RoomControlState.Pending;
        _detail = null;
        // Submission is linearized under the state lock: no target/session change can slip
        // between the final context check and the adapter call. Adapters must return promptly.
        _ = ObserveAsync(operation);
    }

    private bool IsCurrentLocked(PendingOperation operation) =>
        !_disposed && _enabled && operation.Context == _context && operation.Target == _target;

    private ProductControlSnapshot SnapshotLocked() => new(
        _state, _context, _target, _lastShownAt, _renewalDue, _lastResult,
        _inFlight is not null, _detail, _lastIgnored);

    private static MonotonicTimestamp Add(MonotonicTimestamp time, TimeSpan duration) =>
        new(time.Ticks > long.MaxValue - duration.Ticks ? long.MaxValue : time.Ticks + duration.Ticks);

    private void Cancel(PendingOperation? operation)
    {
        if (operation is null) return;
        try { operation.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The observed operation already finished. */ }
        catch (AggregateException)
        {
            lock (_gate) _detail = "Adapter cancellation callback failed; the lane remains reserved until completion.";
        }
    }

    private enum WorkKind { Show, ReconcileRead, ResumeRead }

    private sealed class PendingOperation(WorkKind kind, RoomOperationContext context, ProductTarget target, MonotonicTimestamp deadline)
    {
        public WorkKind Kind { get; } = kind;
        public RoomOperationContext Context { get; } = context;
        public ProductTarget Target { get; } = target;
        public MonotonicTimestamp Deadline { get; } = deadline;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool TimedOut { get; set; }
        public Completion? Completed { get; set; }
    }

    private sealed record Completion(ControlActionResult Result, ProductVisibility? Visibility,
        MonotonicTimestamp Received, DateTimeOffset ReceivedAt);
}
