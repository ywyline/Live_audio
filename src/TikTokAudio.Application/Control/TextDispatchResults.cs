using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Interactions;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Control;

public sealed partial class TextDispatchCoordinator
{
    private async Task ObserveAsync(InFlight operation)
    {
        ControlActionStatus status;
        string? failureDetail = null;
        try
        {
            var result = await _controller.SendTextAsync(operation.Message.Text, operation.Context,
                operation.Cancellation.Token).ConfigureAwait(false);
            status = Enum.IsDefined(result.Status) ? result.Status : ControlActionStatus.Unknown;
        }
        catch (OperationCanceledException)
        {
            status = ControlActionStatus.Unknown;
            failureDetail = "Text adapter cancelled; delivery outcome unknown.";
        }
        catch (Exception error)
        {
            status = ControlActionStatus.Unknown;
            failureDetail = $"Text adapter failed ({error.GetType().Name}); delivery outcome unknown.";
        }

        var received = _clock.Now;
        var receivedUtc = _clock.UtcNow;
        lock (_gate)
        {
            operation.Completed = true;
            var current = !_disposed && _enabled && operation.Generation == _generation && operation.Context == _context;
            var late = received.Ticks >= operation.Deadline.Ticks;
            if (operation.Recorded)
            {
                _lastIgnored = new(operation.Message.ActionId, status, receivedUtc);
            }
            else if (!current || late)
            {
                _lastIgnored = new(operation.Message.ActionId, status, receivedUtc);
                RecordLocked(operation, ControlActionStatus.Unknown, receivedUtc, "Late or invalidated text response; outcome remains unknown.", false);
            }
            else
            {
                // A cancelled request is not proof that an already submitted message was absent.
                if (status == ControlActionStatus.Cancelled) status = ControlActionStatus.Unknown;
                RecordLocked(operation, status, receivedUtc, failureDetail ?? $"Text send outcome: {status}.", true);
            }
        }
        operation.Cancellation.Dispose();
    }

    private void RecordLocked(InFlight operation, ControlActionStatus status, DateTimeOffset receivedUtc,
        string detail, bool acceptSuccess)
    {
        if (operation.Recorded) return;
        operation.Recorded = true;
        _ledger.Enqueue(new(operation.Message.ActionId, operation.Context.SessionId,
            $"Text:{operation.Message.Source}", status, receivedUtc, detail));
        var candidate = operation.Message.Candidate;
        if (candidate is not null)
        {
            if (acceptSuccess && status == ControlActionStatus.Confirmed) _rules!.MarkTextCompleted(candidate);
            else _rules!.Release(candidate, InteractionChannel.Text);
        }
        if (operation.Generation != _generation || operation.Context != _context || !_enabled) return;
        _lastResult = new(status, detail);
        _detail = detail;
        if (acceptSuccess && status == ControlActionStatus.Confirmed) _lastSentAt = receivedUtc;
        _state = status switch
        {
            ControlActionStatus.Confirmed => RoomControlState.Ready,
            ControlActionStatus.Unsupported => RoomControlState.Unsupported,
            ControlActionStatus.Rejected => RoomControlState.Error,
            _ => RoomControlState.NeedsConfirmation
        };
    }
}
