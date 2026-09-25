using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Interactions;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Control;

/// <summary>
/// A bounded, host-pumped text lane. The host owns rule context/admission and transfers
/// DrainLedger results to storage. No other component may send text through this controller.
/// </summary>
public sealed partial class TextDispatchCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly ILiveRoomController _controller;
    private readonly IClock _clock;
    private readonly TextDispatchOptions _options;
    private readonly InteractionRuleCoordinator? _rules;
    private readonly TimedTextPlanner _planner;
    private readonly List<Message> _queue = [];
    private readonly Queue<ActionLedgerEntry> _ledger = new();
    private RoomOperationContext? _context;
    private RoomControlState _state = RoomControlState.Disabled;
    private long _generation;
    private bool _enabled;
    private bool _disposed;
    private long _dropped;
    private InFlight? _inFlight;
    private MonotonicTimestamp? _nextSendAt;
    private DateTimeOffset? _lastSentAt;
    private ControlActionResult? _lastResult;
    private TextDispatchLateResult? _lastIgnored;
    private string? _detail;

    public TextDispatchCoordinator(ILiveRoomController controller, IClock clock, IRandomSource random,
        TextDispatchOptions options, InteractionRuleCoordinator? rules = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _controller = controller;
        _clock = clock;
        _options = options;
        _rules = rules;
        _planner = new TimedTextPlanner(clock, random, options.ScheduleCapacity);
    }

    public TextDispatchSnapshot Snapshot { get { lock (_gate) return SnapshotLocked(); } }

    public void ConfigureSchedules(IEnumerable<TimedTextSchedule> schedules)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _planner.Configure(schedules);
            foreach (var item in _queue.Where(item => item.Source == TextDispatchSource.Timed).ToArray()) DropLocked(item);
        }
    }

    public OperationResult Start(Guid sessionId, string roomId)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(roomId)) return OperationResult.Failed("A session and room are required.");
        InFlight? cancel;
        lock (_gate)
        {
            if (_disposed) return OperationResult.Failed("Text dispatch is disposed.");
            cancel = InvalidateLocked();
            _context = new(sessionId, roomId, ProductEpoch.Initial);
            _enabled = true;
            _planner.Reset();
            _state = RoomControlState.Ready;
        }
        Cancel(cancel);
        return OperationResult.Succeeded();
    }

    public OperationResult QueueManual(string text, TimeSpan timeToLive)
    {
        if (timeToLive <= TimeSpan.Zero) return OperationResult.Failed("Message TTL must be positive.");
        string normalized;
        try { normalized = TimedTextContent.Normalize(text); }
        catch (ArgumentException) { return OperationResult.Failed("Message text is invalid."); }
        lock (_gate)
        {
            if (!_enabled || _disposed || _context is null) return OperationResult.Cancelled("Text dispatch is not running.");
            PruneLocked();
            if (_queue.Count >= _options.QueueCapacity) return OperationResult.Failed("Text queue is full.");
            _queue.Add(new(Guid.NewGuid(), TextDispatchSource.Manual, normalized, Add(_clock.Now, timeToLive)));
            return OperationResult.Succeeded();
        }
    }

    public OperationResult Pause() => Suspend("Text dispatch is paused.");
    public OperationResult Disconnect() => Suspend("Text dispatch is disconnected.");

    private OperationResult Suspend(string detail)
    {
        InFlight? cancel;
        lock (_gate)
        {
            if (_context is null || _disposed) return OperationResult.Failed("No active text session.");
            cancel = InvalidateLocked();
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
            if (_disposed || _enabled || _context is null || _context.SessionId != sessionId || _context.RoomId != roomId)
                return OperationResult.Failed("Resume requires the same suspended session and room.");
            // Voice may continue admitting comments while text is disconnected. Clear its
            // accumulated text side again, including candidates still waiting on cooldown.
            _dropped += _rules?.ClearTextPending() ?? 0;
            _planner.Reset();
            _enabled = true;
            _state = RoomControlState.Ready;
            _detail = null;
            return OperationResult.Succeeded();
        }
    }

    public OperationResult Stop()
    {
        InFlight? cancel;
        lock (_gate)
        {
            cancel = InvalidateLocked();
            _context = null;
            _state = RoomControlState.Disabled;
        }
        Cancel(cancel);
        return OperationResult.Succeeded();
    }

    public void Dispose()
    {
        InFlight? cancel;
        lock (_gate)
        {
            if (_disposed) return;
            cancel = InvalidateLocked();
            _disposed = true;
            _context = null;
            _state = RoomControlState.Disabled;
        }
        Cancel(cancel);
    }

    private InFlight? InvalidateLocked()
    {
        _generation++;
        _enabled = false;
        foreach (var item in _queue.ToArray()) DropLocked(item);
        if (_inFlight is { Recorded: false } operation)
            RecordLocked(operation, ControlActionStatus.Unknown, _clock.UtcNow, "Submitted text invalidated; outcome unknown.", false);
        _dropped += _rules?.ClearTextPending() ?? 0;
        _lastSentAt = null;
        _lastResult = null;
        _detail = null;
        // The channel's submission interval survives pause/reconnect/start, as does the
        // reserved physical lane. Lifecycle toggles must never bypass flood protection.
        return _inFlight;
    }

    public IReadOnlyList<ActionLedgerEntry> DrainLedger()
    {
        lock (_gate)
        {
            var entries = _ledger.ToArray();
            _ledger.Clear();
            return Array.AsReadOnly(entries);
        }
    }

    public Task<TextDispatchSnapshot> PumpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InFlight? cancel = null;
        TextDispatchSnapshot snapshot;
        lock (_gate)
        {
            if (_inFlight is { Completed: true }) _inFlight = null;
            if (_inFlight is { Recorded: false } operation && _clock.Now.Ticks >= operation.Deadline.Ticks)
            {
                RecordLocked(operation, ControlActionStatus.Unknown, _clock.UtcNow, "Text request timed out; outcome unknown.", false);
                _state = RoomControlState.NeedsConfirmation;
                cancel = operation;
            }
            PruneLocked();
            if (_enabled && !_disposed && _context is not null)
            {
                CollectLocked();
                if (_inFlight is null && _queue.Count > 0 && (_nextSendAt is null || _clock.Now.Ticks >= _nextSendAt.Value.Ticks))
                {
                    if (_ledger.Count >= _options.LedgerCapacity)
                        _detail = "Action ledger is full; transfer records before sending more text.";
                    else if (!_controller.Capabilities.SendText)
                    {
                        _state = RoomControlState.Unsupported;
                        _detail = "Text sending is unsupported.";
                    }
                    else if (_controller.State != RoomControlState.Ready)
                    {
                        cancel = InvalidateLocked();
                        _state = RoomControlState.Suspended;
                        _detail = "Controller is not ready; explicit resume is required.";
                    }
                    else SubmitLocked();
                }
            }
            if (_inFlight is { Completed: true }) _inFlight = null;
            snapshot = SnapshotLocked();
        }
        Cancel(cancel);
        return Task.FromResult(snapshot);
    }

    private void CollectLocked()
    {
        foreach (var timed in _planner.TakeDue())
        {
            if (_queue.Count >= _options.QueueCapacity) { _dropped++; continue; }
            _queue.Add(new(Guid.NewGuid(), TextDispatchSource.Timed, timed.Text, Add(_clock.Now, timed.TimeToLive),
                timed.ScheduleId, timed.EndsAtUtc));
        }
        if (_rules is null || _queue.Count >= _options.QueueCapacity) return;
        var decision = _rules.TrySelectTextNext();
        if (decision is null) return;
        var candidate = decision.Candidate;
        if (candidate.SessionId != _context!.SessionId || candidate.RoomId != _context.RoomId ||
            !_rules.CanPlay(candidate, InteractionChannel.Text) || string.IsNullOrWhiteSpace(decision.Text))
        {
            _rules.Release(candidate, InteractionChannel.Text);
            _dropped++;
            return;
        }
        _queue.Add(new(Guid.NewGuid(), TextDispatchSource.Keyword, decision.Text, Add(_clock.Now, candidate.TimeToLive), Candidate: candidate));
    }

    private bool FreshLocked(Message item) => _clock.Now.Ticks < item.ExpiresAt.Ticks &&
        (item.EndsAtUtc is null || _clock.UtcNow < item.EndsAtUtc) &&
        (item.ScheduleId is null || _planner.IsEnabled(item.ScheduleId)) &&
        (item.Candidate is null || _rules!.CanPlay(item.Candidate, InteractionChannel.Text));

    private void PruneLocked()
    {
        foreach (var item in _queue.Where(item => !FreshLocked(item)).ToArray()) DropLocked(item);
    }

    private void DropLocked(Message item)
    {
        _queue.Remove(item);
        if (item.Candidate is not null) _rules!.Release(item.Candidate, InteractionChannel.Text);
        _dropped++;
    }

    private void SubmitLocked()
    {
        // Recheck freshness under the same lock as context capture and the adapter call.
        PruneLocked();
        if (_queue.Count == 0) return;
        var item = _queue[0];
        _queue.RemoveAt(0);
        if (item.Candidate is not null && !_rules!.BeginTextSend(item.Candidate))
        {
            _rules.Release(item.Candidate, InteractionChannel.Text);
            _dropped++;
            return;
        }
        var now = _clock.Now;
        var operation = new InFlight(item, _context!, _generation, Add(now, _options.RequestTimeout));
        _inFlight = operation;
        _nextSendAt = Add(now, _options.EffectiveInterval);
        _state = RoomControlState.Pending;
        _detail = null;
        // One result slot was reserved by admission above. Adapters must return a Task
        // promptly; neither the task's completion nor cancellation releases a second lane.
        _ = ObserveAsync(operation);
    }

    private TextDispatchSnapshot SnapshotLocked() => new(_state, _context, _queue.Count,
        _inFlight is { Completed: false }, _lastSentAt, _nextSendAt, _ledger.Count, _dropped,
        _lastResult, _detail, _lastIgnored);

    private static MonotonicTimestamp Add(MonotonicTimestamp now, TimeSpan duration) =>
        new(now.Ticks > long.MaxValue - duration.Ticks ? long.MaxValue : now.Ticks + duration.Ticks);

    private void Cancel(InFlight? operation)
    {
        if (operation is null) return;
        try { operation.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* Completion already disposed the request. */ }
        catch (AggregateException)
        {
            lock (_gate) _detail = "Cancellation callback failed; lane stays reserved until completion.";
        }
    }

    private sealed record Message(Guid ActionId, TextDispatchSource Source, string Text,
        MonotonicTimestamp ExpiresAt, string? ScheduleId = null, DateTimeOffset? EndsAtUtc = null,
        InteractionCandidate? Candidate = null);

    private sealed class InFlight(Message message, RoomOperationContext context, long generation, MonotonicTimestamp deadline)
    {
        public Message Message { get; } = message;
        public RoomOperationContext Context { get; } = context;
        public long Generation { get; } = generation;
        public MonotonicTimestamp Deadline { get; } = deadline;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool Recorded { get; set; }
        public bool Completed { get; set; }
    }
}
