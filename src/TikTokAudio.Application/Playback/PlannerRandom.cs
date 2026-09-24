using TikTokAudio.Application.Contracts;

namespace TikTokAudio.Application.Playback;

// Versioned by the catalog fingerprint. State is explicit so restart does not reseed bags.
internal sealed class PlannerRandom(ulong state)
{
    public ulong State { get; private set; } = state;

    public ulong Next()
    {
        unchecked
        {
            State += 0x9E3779B97F4A7C15UL;
            ulong value = State;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    public int NextIndex(int count)
    {
        ulong bound = (ulong)count;
        ulong threshold = unchecked(0UL - bound) % bound;
        ulong value;
        do { value = Next(); } while (value < threshold);
        return (int)(value % bound);
    }

    public static ulong Seed(IRandomSource random) =>
        ((ulong)(uint)random.NextInt32(0, int.MaxValue) << 32) | (uint)random.NextInt32(0, int.MaxValue);
}

internal sealed class PlannerBag(PlannerRandom random, IEnumerable<string>? remaining = null, string? last = null)
{
    public PlannerRandom Random { get; } = random;
    public List<string> Remaining { get; } = remaining?.ToList() ?? [];
    public string? Last { get; private set; } = last;

    public string Draw(PlannerGroup group)
    {
        if (Remaining.Count == 0)
        {
            Remaining.AddRange(group.Clips.Select(clip => clip.ClipId));
            for (int index = Remaining.Count - 1; index > 0; index--)
            {
                int other = Random.NextIndex(index + 1);
                (Remaining[index], Remaining[other]) = (Remaining[other], Remaining[index]);
            }
            if (Remaining.Count > 1 && Remaining[0] == Last)
            {
                int other = 1 + Random.NextIndex(Remaining.Count - 1);
                (Remaining[0], Remaining[other]) = (Remaining[other], Remaining[0]);
            }
        }
        Last = Remaining[0];
        Remaining.RemoveAt(0);
        return Last;
    }
}
