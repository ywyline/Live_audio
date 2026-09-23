using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests.Fixtures;

public sealed class SimulatedLiveEventSource : ILiveEventSource
{
    private readonly Channel<LiveEvent> _events = Channel.CreateUnbounded<LiveEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentQueue<LiveEvent> _injected = new();
    private readonly string _sourceId;
    private Guid? _sessionId;
    private string? _roomId;

    public SimulatedLiveEventSource(string sourceId = "simulated")
    {
        _sourceId = sourceId;
        Capabilities = new LiveSourceCapabilities(
            ReadEnter: true,
            ReadFollow: true,
            ReadLike: true,
            ReadComment: true,
            ReadRoomStatus: true);
    }

    public LiveSourceState State { get; private set; } = LiveSourceState.Disconnected;

    public LiveSourceCapabilities Capabilities { get; }

    public Task<OperationResult> ConnectAsync(
        LiveConnectionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sessionId = request.SessionId;
        _roomId = request.RoomId;
        State = LiveSourceState.Connected;
        return Task.FromResult(OperationResult.Succeeded("simulated"));
    }

    public async IAsyncEnumerable<LiveEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_events.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    public Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = LiveSourceState.Disconnected;
        _events.Writer.TryComplete();
        return Task.FromResult(OperationResult.Succeeded("simulated"));
    }

    public void Inject(LiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        if (State != LiveSourceState.Connected)
        {
            throw new InvalidOperationException("The simulated source must be connected before injection.");
        }

        if (_sessionId is not null && liveEvent.SessionId != _sessionId)
        {
            throw new ArgumentException("The event session does not match the connected session.", nameof(liveEvent));
        }

        if (_roomId is not null && !string.Equals(liveEvent.RoomId, _roomId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The event room does not match the connected room.", nameof(liveEvent));
        }

        _injected.Enqueue(liveEvent);
        if (!_events.Writer.TryWrite(liveEvent))
        {
            throw new InvalidOperationException("The simulated event source is closed.");
        }
    }

    public IReadOnlyCollection<LiveEvent> InjectedEvents => _injected.ToArray();
}
