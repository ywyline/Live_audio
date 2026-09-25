using TikTokAudio.Domain;
using TikTokAudio.Application.Contracts;

namespace TikTokAudio.Application.Playback;

public readonly record struct EffectRange
{
    public EffectRange(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        Minimum = minimum;
        Maximum = maximum;
    }

    public double Minimum { get; }
    public double Maximum { get; }
}

public sealed record PlaybackEffectOptions
{
    public bool Enabled { get; init; }
    public EffectRange Speed { get; init; } = new(0.97, 1.03);
    public EffectRange PitchSemitones { get; init; } = new(-0.3, 0.3);
    public EffectRange StereoBalance { get; init; } = new(-0.1, 0.1);
    public EffectRange EqDecibels { get; init; } = new(-1, 1);
    public bool EnvironmentEnabled { get; init; }
    public double EnvironmentRelativeDecibels { get; init; } = -35;

    public void Validate()
    {
        Check(Speed, 0.5, 2, nameof(Speed));
        Check(PitchSemitones, -6, 6, nameof(PitchSemitones));
        Check(StereoBalance, -1, 1, nameof(StereoBalance));
        Check(EqDecibels, -12, 12, nameof(EqDecibels));
        if (!double.IsFinite(EnvironmentRelativeDecibels) || EnvironmentRelativeDecibels is < -90 or > -35)
            throw new ArgumentOutOfRangeException(nameof(EnvironmentRelativeDecibels));
    }

    private static void Check(EffectRange value, double minimum, double maximum, string name)
    {
        if (value.Minimum < minimum || value.Maximum > maximum)
            throw new ArgumentOutOfRangeException(name);
    }
}

public sealed record PlaybackEffectSelection(
    double Speed, double PitchSemitones, double StereoBalance, double EqDecibels,
    bool EnvironmentEnabled, double EnvironmentRelativeDecibels, long EffectSeed = 0)
{
    public static PlaybackEffectSelection Bypass { get; } = new(1, 0, 0, 0, false, -35, 0);
}

public static class PlaybackEffects
{
    // The planner's effect seed is checkpointed. Each parameter gets its own draw
    // so selecting the same item after an interruption cannot change its sound.
    public static PlaybackEffectSelection Select(PlaybackPlanItem item, PlaybackEffectOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!options.Enabled || item.Mode != BasePlaybackMode.PreRecorded)
            return PlaybackEffectSelection.Bypass;

        ulong state = unchecked((ulong)item.EffectSeed);
        var selection = new PlaybackEffectSelection(
            Draw(ref state, options.Speed), Draw(ref state, options.PitchSemitones),
            Draw(ref state, options.StereoBalance), Draw(ref state, options.EqDecibels),
            options.EnvironmentEnabled, options.EnvironmentRelativeDecibels, item.EffectSeed);
        return selection is { Speed: 1, PitchSemitones: 0, StereoBalance: 0, EqDecibels: 0,
            EnvironmentEnabled: false }
            ? PlaybackEffectSelection.Bypass
            : selection;
    }

    private static double Draw(ref ulong state, EffectRange range)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong value = state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return range.Minimum + (range.Maximum - range.Minimum) * ((value >> 11) * (1.0 / (1UL << 53)));
        }
    }
}

// Optional capability; frozen IAudioOutput remains unchanged.
public interface IEffectAudioOutput : IAudioOutput
{
    Task<OperationResult> PlayWithEffectsAsync(AudioPlaybackRequest request,
        PlaybackEffectSelection selection, CancellationToken cancellationToken);
}
