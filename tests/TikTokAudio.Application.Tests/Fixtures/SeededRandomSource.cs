using TikTokAudio.Application.Contracts;

namespace TikTokAudio.Application.Tests.Fixtures;

public sealed class SeededRandomSource : IRandomSource
{
    private readonly Random _random;

    public SeededRandomSource(int seed)
    {
        _random = new Random(seed);
    }

    public int NextInt32(int minInclusive, int maxExclusive)
    {
        return _random.Next(minInclusive, maxExclusive);
    }

    public double NextDouble()
    {
        return _random.NextDouble();
    }
}
