using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tests.Fixtures;

public sealed record SimulatedControlAction(
    string Kind,
    ProductTarget? Product,
    string? Text,
    RoomOperationContext Context);

public sealed class SimulatedLiveRoomController : ILiveRoomController
{
    private readonly List<SimulatedControlAction> _actions = [];
    private ProductVisibility? _visibility;

    public SimulatedLiveRoomController(
        ControlActionStatus productStatus = ControlActionStatus.Confirmed,
        ControlActionStatus textStatus = ControlActionStatus.Confirmed)
    {
        ProductStatus = productStatus;
        TextStatus = textStatus;
        Capabilities = new RoomControlCapabilities(
            ShowProduct: true,
            ReadProductVisibility: true,
            SendText: true);
    }

    public RoomControlState State { get; private set; } = RoomControlState.Ready;

    public RoomControlCapabilities Capabilities { get; }

    public ControlActionStatus ProductStatus { get; set; }

    public ControlActionStatus TextStatus { get; set; }

    public IReadOnlyList<SimulatedControlAction> Actions => _actions;

    public Task<ControlActionResult> ShowProductAsync(
        ProductTarget target,
        RoomOperationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _actions.Add(new SimulatedControlAction("ShowProduct", target, null, context));
        if (ProductStatus == ControlActionStatus.Confirmed)
        {
            _visibility = new ProductVisibility(target, true, null);
        }

        return Task.FromResult(new ControlActionResult(ProductStatus, "simulated"));
    }

    public Task<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)> ReadProductVisibilityAsync(
        RoomOperationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<(ControlActionStatus Status, ProductVisibility? Visibility, string? Detail)>(
            (
                _visibility is null ? ControlActionStatus.Unknown : ControlActionStatus.Confirmed,
                _visibility,
                "simulated"));
    }

    public Task<ControlActionResult> SendTextAsync(
        string text,
        RoomOperationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _actions.Add(new SimulatedControlAction("SendText", null, text, context));
        return Task.FromResult(new ControlActionResult(TextStatus, "simulated"));
    }
}
