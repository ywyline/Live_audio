using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Control;

public sealed partial class ProductControlCoordinator
{
    private async Task ObserveAsync(PendingOperation operation)
    {
        ControlActionResult result;
        ProductVisibility? visibility = null;
        try
        {
            if (operation.Kind == WorkKind.Show)
                result = await _controller.ShowProductAsync(operation.Target, operation.Context, operation.Cancellation.Token).ConfigureAwait(false);
            else
            {
                var read = await _controller.ReadProductVisibilityAsync(operation.Context, operation.Cancellation.Token).ConfigureAwait(false);
                result = new ControlActionResult(read.Status, read.Detail);
                visibility = read.Visibility;
            }
        }
        catch (OperationCanceledException)
        {
            result = new ControlActionResult(ControlActionStatus.Cancelled, "Adapter request was cancelled; no platform outcome is assumed.");
        }
        catch (Exception exception)
        {
            result = new ControlActionResult(ControlActionStatus.Unknown, $"Adapter request failed ({exception.GetType().Name}); outcome unknown.");
        }

        // Timestamp receipt here, not in a later pump. No platform wall-clock value drives scheduling.
        var completion = new Completion(result, visibility, _clock.Now, _clock.UtcNow);
        lock (_gate) operation.Completed = completion;
        operation.Cancellation.Dispose();
    }

    private void ConsumeCompletedLocked()
    {
        if (_inFlight is not { Completed: { } completion } operation) return;
        _inFlight = null;
        if (!IsCurrentLocked(operation))
        {
            _lastIgnored = new(operation.Context, operation.Target, completion.Result, completion.ReceivedAt);
            return;
        }

        // Even without an intervening pump, receipt after the deadline is an uncertain outcome.
        if (operation.TimedOut || completion.Received.Ticks >= operation.Deadline.Ticks)
        {
            _lastIgnored = new(operation.Context, operation.Target, completion.Result, completion.ReceivedAt);
            _lastResult = new ControlActionResult(ControlActionStatus.Unknown, "Late response requires visibility reconciliation.");
            if (operation.Kind == WorkKind.Show) RequireReadbackLocked();
            else SetFailureLocked(RoomControlState.NeedsConfirmation, ControlActionStatus.Unknown, "Visibility request timed out.");
            return;
        }

        _lastResult = completion.Result;
        _detail = completion.Result.Detail;
        if (operation.Kind != WorkKind.Show)
        {
            ApplyReadLocked(operation.Kind, completion);
            return;
        }

        switch (completion.Result.Status)
        {
            case ControlActionStatus.Confirmed:
                ConfirmLocked(completion);
                break;
            case ControlActionStatus.Rejected when _options.AllowRejectedRetry && _retryCount == 0:
                _retryCount++;
                _next = WorkKind.Show;
                _workDue = Add(completion.Received, _options.RetryDelay);
                _state = RoomControlState.Pending;
                break;
            case ControlActionStatus.Rejected:
                _state = RoomControlState.Error;
                break;
            case ControlActionStatus.Unsupported:
                _state = RoomControlState.Unsupported;
                break;
            default:
                RequireReadbackLocked();
                break;
        }
    }

    private void ApplyReadLocked(WorkKind kind, Completion completion)
    {
        if (completion.Result.Status == ControlActionStatus.Confirmed && completion.Visibility is { } visibility)
        {
            if (visibility.IsVisible && visibility.Target == _target)
            {
                ConfirmLocked(completion);
                return;
            }
            if (kind == WorkKind.ResumeRead)
            {
                // Explicit resume may correct a proven mismatch. Unknown-action reconciliation
                // never automatically resubmits a write when the target cannot be confirmed.
                _next = WorkKind.Show;
                _state = RoomControlState.Pending;
                return;
            }
        }
        SetFailureLocked(RoomControlState.NeedsConfirmation, ControlActionStatus.Unknown, "Current product visibility is unconfirmed; operator action is required.");
    }

    private void ConfirmLocked(Completion completion)
    {
        _lastShownAt = completion.ReceivedAt;
        _renewalDue = Add(completion.Received, _options.RenewalInterval);
        _retryCount = 0;
        _state = RoomControlState.Ready;
    }

    private void RequireReadbackLocked()
    {
        _renewalDue = null;
        _lastShownAt = null;
        if (_controller.Capabilities.ReadProductVisibility)
        {
            _next = WorkKind.ReconcileRead;
            _state = RoomControlState.Pending;
        }
        else
        {
            _state = RoomControlState.NeedsConfirmation;
            _detail = "Visibility readback is unsupported; operator confirmation is required.";
        }
    }

    private void SetFailureLocked(RoomControlState state, ControlActionStatus status, string detail)
    {
        _state = state;
        _lastResult = new ControlActionResult(status, detail);
        _detail = detail;
        _renewalDue = null;
        _lastShownAt = null;
    }
}
