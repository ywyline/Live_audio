namespace TikTokAudio.Application.Contracts;

public readonly record struct MonotonicTimestamp(long Ticks);

public interface IClock
{
    MonotonicTimestamp Now { get; }
    DateTimeOffset UtcNow { get; }
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IRandomSource
{
    int NextInt32(int minInclusive, int maxExclusive);
    double NextDouble();
}
