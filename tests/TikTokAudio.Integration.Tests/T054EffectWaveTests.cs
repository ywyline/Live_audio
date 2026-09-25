using NAudio.Wave;
using TikTokAudio.Application.Playback;
using TikTokAudio.Infrastructure.Audio;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class T054EffectWaveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LiveAudio-T054-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "voice.wav");
    private static readonly PlaybackEffectSelection Neutral = new(1, 0, 0, 0, false, -35);

    [Fact]
    public async Task BalancedEqPreservesSourceFramesAndBoundsPeak()
    {
        WriteStereo(500, 30000);
        await EffectWaveProcessor.ProcessAsync(Source, Neutral with { StereoBalance = 0.6, EqDecibels = 12 }, default);
        using var reader = new WaveFileReader(Source);
        Assert.Equal(500 * reader.WaveFormat.BlockAlign, reader.Length);
        byte[] data = new byte[reader.Length];
        Assert.Equal(data.Length, reader.Read(data, 0, data.Length));
        for (int i = 0; i < data.Length; i += 4)
        {
            short left = BitConverter.ToInt16(data, i);
            short right = BitConverter.ToInt16(data, i + 2);
            Assert.InRange(left, short.MinValue, short.MaxValue);
            Assert.True(Math.Abs(left) < Math.Abs(right));
            Assert.True(Math.Abs(right) < 32767);
        }
        Assert.False(File.Exists(Source + ".effect.tmp"));
    }

    [Fact]
    public async Task SameSelectionIsDeterministicAndCancelledProcessingLeavesSourceUntouched()
    {
        WriteStereo(100, 9000);
        byte[] original = File.ReadAllBytes(Source);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EffectWaveProcessor.ProcessAsync(Source, Neutral with { EqDecibels = -6 }, cancelled.Token));
        Assert.Equal(original, File.ReadAllBytes(Source));
        await EffectWaveProcessor.ProcessAsync(Source, Neutral with { EqDecibels = -6 }, default);
        byte[] first = File.ReadAllBytes(Source);
        File.WriteAllBytes(Source, original);
        await EffectWaveProcessor.ProcessAsync(Source, Neutral with { EqDecibels = -6 }, default);
        Assert.Equal(first, File.ReadAllBytes(Source));
    }

    [Theory]
    [InlineData(0.97, 0)]
    [InlineData(1.03, 0)]
    [InlineData(1, -0.3)]
    [InlineData(1, 0.3)]
    [InlineData(0.97, -0.3)]
    [InlineData(1.03, 0.3)]
    public async Task TimeAndPitchEffectsRenderExpectedDurationFrequencyAndSourceMap(double speed, double pitch)
    {
        const int sourceFrames = 48000;
        const double sourceFrequency = 440;
        WriteStereoTone(sourceFrames, sourceFrequency, 12000);
        var selection = Neutral with { Speed = speed, PitchSemitones = pitch, EffectSeed = 99 };

        Assert.True(EffectWaveProcessor.Supports(selection));
        var map = await EffectWaveProcessor.ProcessAsync(Source, selection, default);

        Assert.Equal(sourceFrames, map.SourceFrames);
        Assert.Equal((long)Math.Round(sourceFrames / speed, MidpointRounding.AwayFromZero), map.RenderedFrames);
        long renderedCursor = map.SourceToRendered(1700);
        Assert.InRange(Math.Abs(map.RenderedToSource(renderedCursor) - 1700), 0, 1);
        using var reader = new WaveFileReader(Source);
        Assert.Equal(map.RenderedFrames * reader.WaveFormat.BlockAlign, reader.Length);
        double expectedFrequency = sourceFrequency * Math.Pow(2, pitch / 12);
        Assert.InRange(DominantFrequency(reader), expectedFrequency - 4, expectedFrequency + 4);
    }

    [Fact]
    public async Task NonNeutralRenderingIsDeterministicAcrossPreparationAndResume()
    {
        WriteStereoTone(12000, 320, 10000);
        byte[] original = File.ReadAllBytes(Source);
        var selection = Neutral with
        {
            Speed = 1.02, PitchSemitones = -0.2, StereoBalance = 0.05,
            EqDecibels = 0.5, EnvironmentEnabled = true, EffectSeed = 1234
        };

        await EffectWaveProcessor.ProcessAsync(Source, selection, default);
        byte[] first = File.ReadAllBytes(Source);
        File.WriteAllBytes(Source, original);
        await EffectWaveProcessor.ProcessAsync(Source, selection, default);

        Assert.Equal(first, File.ReadAllBytes(Source));
    }

    [Fact]
    public async Task RenderLimitFailureLeavesSourceUntouched()
    {
        WriteStereoTone(2000, 440, 8000);
        byte[] original = File.ReadAllBytes(Source);

        await Assert.ThrowsAsync<InvalidDataException>(() => EffectWaveProcessor.ProcessAsync(Source,
            Neutral with { Speed = 0.5 }, original.Length, default));

        Assert.Equal(original, File.ReadAllBytes(Source));
        Assert.Empty(Directory.EnumerateFiles(root, "*.effect-*.tmp"));
    }

    [Fact]
    public async Task MonoBalanceRejectionLeavesOriginalAndNoTemporaryOutput()
    {
        Directory.CreateDirectory(root);
        using (var writer = new WaveFileWriter(Source, new WaveFormat(24000, 16, 1)))
            writer.Write(new byte[480], 0, 480);
        byte[] before = File.ReadAllBytes(Source);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            EffectWaveProcessor.ProcessAsync(Source, Neutral with { StereoBalance = 0.5 }, default));
        Assert.Equal(before, File.ReadAllBytes(Source));
        Assert.Empty(Directory.EnumerateFiles(root, "*.effect-*.tmp"));
    }

    [Theory]
    [InlineData(-12)]
    [InlineData(12)]
    public async Task HighLevelAlternatingSignalKeepsHeadroomAfterEq(double decibels)
    {
        Directory.CreateDirectory(root);
        using (var writer = new WaveFileWriter(Source, new WaveFormat(24000, 16, 2)))
        {
            byte[] data = new byte[1600 * 4];
            for (int i = 0; i < data.Length; i += 2)
                BitConverter.TryWriteBytes(data.AsSpan(i, 2), (short)((i / 4) % 2 == 0 ? 32760 : -32760));
            writer.Write(data, 0, data.Length);
        }
        await EffectWaveProcessor.ProcessAsync(Source, Neutral with { EqDecibels = decibels }, default);
        using var reader = new WaveFileReader(Source);
        byte[] processed = new byte[reader.Length];
        Assert.Equal(processed.Length, reader.Read(processed, 0, processed.Length));
        Assert.True(Enumerable.Range(0, processed.Length / 2)
            .All(i => Math.Abs((int)BitConverter.ToInt16(processed, i * 2)) < 32760));
    }

    [Fact]
    public async Task EnvironmentMixIsDeterministicBoundedAndChangesTheRenderedSignal()
    {
        WriteStereo(400, 1200);
        byte[] original = File.ReadAllBytes(Source);
        var selection = Neutral with
        {
            EnvironmentEnabled = true, EnvironmentRelativeDecibels = -35, EffectSeed = 17
        };

        Assert.True(EffectWaveProcessor.Supports(selection));
        await EffectWaveProcessor.ProcessAsync(Source, selection, default);
        byte[] first = File.ReadAllBytes(Source);
        Assert.NotEqual(original, first);

        File.WriteAllBytes(Source, original);
        await EffectWaveProcessor.ProcessAsync(Source, selection, default);
        Assert.Equal(first, File.ReadAllBytes(Source));

        File.WriteAllBytes(Source, original);
        await EffectWaveProcessor.ProcessAsync(Source, selection with { EffectSeed = 18 }, default);
        Assert.NotEqual(first, File.ReadAllBytes(Source));

        using var reader = new WaveFileReader(Source);
        byte[] samples = new byte[reader.Length];
        Assert.Equal(samples.Length, reader.Read(samples, 0, samples.Length));
        Assert.True(Enumerable.Range(0, samples.Length / 2)
            .All(i => Math.Abs((int)BitConverter.ToInt16(samples, i * 2)) <= 32767));
    }

    [Fact]
    public async Task PreviewUsesOwnedCacheAndReleasesItWithoutTouchingMaterial()
    {
        string materials = Path.Combine(root, "materials");
        string cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(materials);
        WriteStereo(100, 10000);
        string material = Path.Combine(materials, "voice.wav");
        File.Move(Source, material);
        byte[] original = File.ReadAllBytes(material);
        var options = new AudioOutputOptions
        {
            MaterialDirectory = materials, CacheDirectory = cache, DeviceId = "synthetic-device",
            MaxInputBytes = 100000, MaxDecodedBytes = 100000
        };
        string previewPath;
        using (var preview = await AudioEffectPreview.CreateAsync(options, material,
            Neutral with { Speed = 0.98, PitchSemitones = 0.2, StereoBalance = -0.3, EqDecibels = -4 }))
        {
            previewPath = preview.Path;
            Assert.True(File.Exists(previewPath));
            Assert.Equal(100, preview.SourceFrames);
            Assert.Equal(102, preview.RenderedFrames);
            Assert.Equal(24000, preview.SampleRate);
            Assert.NotEqual(original, File.ReadAllBytes(previewPath));
        }
        Assert.False(File.Exists(previewPath));
        Assert.Equal(original, File.ReadAllBytes(material));
        Assert.Empty(Directory.EnumerateDirectories(cache, "session-*"));
    }

    private void WriteStereo(int frames, short amplitude)
    {
        Directory.CreateDirectory(root);
        using var writer = new WaveFileWriter(Source, new WaveFormat(24000, 16, 2));
        byte[] samples = new byte[frames * 4];
        for (int i = 0; i < samples.Length; i += 2)
            BitConverter.TryWriteBytes(samples.AsSpan(i, 2), amplitude);
        writer.Write(samples, 0, samples.Length);
    }

    private void WriteStereoTone(int frames, double frequency, short amplitude)
    {
        Directory.CreateDirectory(root);
        using var writer = new WaveFileWriter(Source, new WaveFormat(24000, 16, 2));
        byte[] samples = new byte[frames * 4];
        for (int frame = 0; frame < frames; frame++)
        {
            short sample = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequency * frame / 24000));
            BitConverter.TryWriteBytes(samples.AsSpan(frame * 4, 2), sample);
            BitConverter.TryWriteBytes(samples.AsSpan(frame * 4 + 2, 2), sample);
        }
        writer.Write(samples, 0, samples.Length);
    }

    private static double DominantFrequency(WaveFileReader reader)
    {
        const int sampleCount = 4096;
        long middleFrame = reader.Length / reader.WaveFormat.BlockAlign / 2;
        long firstFrame = Math.Max(0, middleFrame - sampleCount / 2);
        reader.Position = firstFrame * reader.WaveFormat.BlockAlign;
        byte[] bytes = new byte[sampleCount * reader.WaveFormat.BlockAlign];
        int read = reader.Read(bytes, 0, bytes.Length);
        int frames = read / reader.WaveFormat.BlockAlign;
        double bestFrequency = 0;
        double bestPower = double.NegativeInfinity;
        for (double frequency = 420; frequency <= 465; frequency += 0.25)
        {
            double real = 0;
            double imaginary = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                double sample = BitConverter.ToInt16(bytes, frame * reader.WaveFormat.BlockAlign) / 32768d;
                double angle = 2 * Math.PI * frequency * frame / reader.WaveFormat.SampleRate;
                real += sample * Math.Cos(angle);
                imaginary -= sample * Math.Sin(angle);
            }
            double power = real * real + imaginary * imaginary;
            if (power > bestPower)
            {
                bestPower = power;
                bestFrequency = frequency;
            }
        }
        return bestFrequency;
    }

    public void Dispose()
    {
        string temp = Path.GetFullPath(Path.GetTempPath());
        string resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("LiveAudio-T054-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe test directory cleanup.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}

