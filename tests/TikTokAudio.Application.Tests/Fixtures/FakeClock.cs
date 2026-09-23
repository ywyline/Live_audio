using TikTokAudio.Application.Contracts;

namespace TikTokAudio.Application.Tests.Fixtures;

public sealed class FakeClock : IClock
{
    private readonly object _gate = new();
    private readonly List<PendingDelay> _pending = [];
    private readonly DateTimeOffset _initialUtc;
    private long _elapsedTicks;

    public FakeClock(DateTimeOffset? initialUtc = null)
    {
        _initialUtc = initialUtc ?? DateTimeOffset.UnixEpoch;
    }

    public MonotonicTimestamp Now
    {
        get
        {
            lock (_gate)
            {
                return new MonotonicTimestamp(_elapsedTicks);
            }
        }
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _initialUtc.AddTicks(_elapsedTicks);
            }
        }
    }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return ValueTask.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingDelay pending;
        lock (_gate)
        {
            pending = new PendingDelay(_elapsedTicks + delay.Ticks, completion);
            _pending.Add(pending);
        }

        pending.Registration = cancellationToken.Register(
            static state => ((PendingDelay)state!).Cancel(),
            pending);
        return new ValueTask(completion.Task);
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        List<PendingDelay> ready;
        lock (_gate)
        {
            _elapsedTicks += amount.Ticks;
            ready = _pending.Where(item => item.DueTicks <= _elapsedTicks).ToList();
            foreach (var item in ready)
            {
                _pending.Remove(item);
            }
        }

        foreach (var item in ready)
        {
            item.Complete();
        }
    }

    public int PendingDelayCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    private sealed class PendingDelay(long dueTicks, TaskCompletionSource<object?> completion)
    {
        public long DueTicks { get; } = dueTicks;
        public TaskCompletionSource<object?> Completion { get; } = completion;
        public CancellationTokenRegistration Registration { get; set; }

        public void Complete()
        {
            Registration.Dispose();
            Completion.TrySetResult(null);
        }

        public void Cancel()
        {
            Registration.Dispose();
            Completion.TrySetCanceled();
        }
    }
}
