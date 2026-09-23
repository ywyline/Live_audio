using TikTokAudio.Domain;

namespace TikTokAudio.Application.Contracts;

public interface ILiveEventSource
{
    LiveSourceState State { get; }
    LiveSourceCapabilities Capabilities { get; }

    Task<OperationResult> ConnectAsync(LiveConnectionRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<LiveEvent> ReadEventsAsync(CancellationToken cancellationToken);
    Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken);
}
