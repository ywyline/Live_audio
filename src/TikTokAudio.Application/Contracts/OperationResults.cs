using TikTokAudio.Domain;

namespace TikTokAudio.Application.Contracts;

public readonly record struct OperationResult(OperationStatus Status, string? Detail = null)
{
    public bool IsSuccess => Status == OperationStatus.Succeeded;

    public static OperationResult Succeeded(string? detail = null) => new(OperationStatus.Succeeded, detail);
    public static OperationResult Failed(string? detail = null) => new(OperationStatus.Failed, detail);
    public static OperationResult Unknown(string? detail = null) => new(OperationStatus.Unknown, detail);
    public static OperationResult Cancelled(string? detail = null) => new(OperationStatus.Cancelled, detail);
    public static OperationResult Unsupported(string? detail = null) => new(OperationStatus.Unsupported, detail);
}

public readonly record struct OperationResult<T>(OperationStatus Status, T? Value, string? Detail = null)
{
    public bool IsSuccess => Status == OperationStatus.Succeeded;
}

public readonly record struct ControlActionResult(ControlActionStatus Status, string? Detail = null)
{
    public bool IsConfirmed => Status == ControlActionStatus.Confirmed;
}
