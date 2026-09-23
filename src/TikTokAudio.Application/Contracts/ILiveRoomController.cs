using TikTokAudio.Domain;

namespace TikTokAudio.Application.Contracts;

public interface ILiveRoomController
{
    RoomControlState State { get; }
    RoomControlCapabilities Capabilities { get; }

    Task<ControlActionResult> ShowProductAsync(ProductTarget target, RoomOperationContext context, CancellationToken cancellationToken);
    Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)> ReadProductVisibilityAsync(RoomOperationContext context, CancellationToken cancellationToken);
    Task<ControlActionResult> SendTextAsync(string text, RoomOperationContext context, CancellationToken cancellationToken);
}
