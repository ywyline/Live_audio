using NAudio.Wave;
using TikTokAudio.Infrastructure.Audio;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class AudioFilePreparationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LiveAudio-T050-" + Guid.NewGuid().ToString("N"));
    private string Materials => Path.Combine(root, "materials");
    private string Cache => Path.Combine(root, "cache");
    private AudioOutputOptions Options => new()
    {
        MaterialDirectory = Materials,
        CacheDirectory = Cache,
        DeviceId = "synthetic-device-never-opened",
        MaxInputBytes = 1_000_000,
        MaxDecodedBytes = 1_000_000
    };

    [Theory]
    [InlineData(8000, 1)]
    [InlineData(24000, 1)]
    [InlineData(48000, 2)]
    public async Task PreparedPcmRetainsSourceFrameCountAndIsSeekable(int sampleRate, int channels)
    {
        string input = WritePcm(sampleRate, channels, 400);
        string output;
        using (var file = await AudioFilePreparation.PrepareAsync(Options, input, default))
        {
            output = file.Path;
            Assert.Equal(sampleRate, file.SampleRate);
            Assert.Equal(400, file.LengthSamples);
            using var reader = new WaveFileReader(file.Path);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            Assert.Equal(channels, reader.WaveFormat.Channels);
            reader.Position = 100 * reader.WaveFormat.BlockAlign;
            Assert.Equal(100 * reader.WaveFormat.BlockAlign, reader.Position);
            Assert.True(reader.Read(new byte[reader.WaveFormat.BlockAlign], 0, reader.WaveFormat.BlockAlign) > 0);
        }
        Assert.False(File.Exists(output));
        Assert.True(File.Exists(input));
        AssertNoSessions();
    }

    [Fact]
    public async Task FloatWaveIsConvertedToPcm16()
    {
        Directory.CreateDirectory(Materials);
        string input = Path.Combine(Materials, "float.wav");
        using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1)))
            writer.WriteSamples([0.5f, -0.5f, 0.25f, 0f], 0, 4);
        using var prepared = await AudioFilePreparation.PrepareAsync(Options, input, default);
        using var reader = new WaveFileReader(prepared.Path);
        Assert.Equal(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(4, prepared.LengthSamples);
        byte[] bytes = new byte[8];
        Assert.Equal(8, reader.Read(bytes, 0, 8));
        Assert.InRange(BitConverter.ToInt16(bytes, 0), 16382, 16384);
        Assert.InRange(BitConverter.ToInt16(bytes, 2), -16384, -16382);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SizeLimitRejectsAndCleansOnlyOwnedFiles(bool inputLimit)
    {
        string input = WritePcm(8000, 1, 200);
        var options = inputLimit ? Options with { MaxInputBytes = 100 } : Options with { MaxDecodedBytes = 100 };
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioFilePreparation.PrepareAsync(options, input, default));
        Assert.True(File.Exists(input));
        AssertNoSessions();
    }

    [Fact]
    public async Task InvalidWaveDoesNotLeaveTemporaryFiles()
    {
        Directory.CreateDirectory(Materials);
        string input = Path.Combine(Materials, "broken.wav");
        await File.WriteAllTextAsync(input, "not a wave");
        await Assert.ThrowsAnyAsync<Exception>(() => AudioFilePreparation.PrepareAsync(Options, input, default));
        AssertNoSessions();
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task EmptyDecodedWaveIsRejected()
    {
        string input = WritePcm(8000, 1, 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioFilePreparation.PrepareAsync(Options, input, default));
        AssertNoSessions();
    }

    [Fact]
    public async Task PreCancelledPreparationDoesNotWriteCache()
    {
        string input = WritePcm(8000, 1, 50);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AudioFilePreparation.PrepareAsync(Options, input, cancellation.Token));
        Assert.False(Directory.Exists(Cache));
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task MissingSourceCleansItsOwnedDirectory()
    {
        Directory.CreateDirectory(Materials);
        await Assert.ThrowsAsync<FileNotFoundException>(() => AudioFilePreparation.PrepareAsync(Options, Path.Combine(Materials, "missing.wav"), default));
        AssertNoSessions();
    }

    [Fact]
    public async Task OutsideMaterialRootIsRejectedWithoutDeletingSource()
    {
        string input = WritePcm(8000, 1, 50);
        string outside = Path.Combine(root, "outside.wav");
        File.Copy(input, outside);
        await Assert.ThrowsAsync<ArgumentException>(() => AudioFilePreparation.PrepareAsync(Options, outside, default));
        Assert.True(File.Exists(outside));
        Assert.False(Directory.Exists(Cache));
    }

    [Fact]
    public async Task PrefixSiblingDoesNotCountAsInsideMaterialRoot()
    {
        string sibling = Materials + "-other";
        Directory.CreateDirectory(sibling);
        await Assert.ThrowsAsync<ArgumentException>(() => AudioFilePreparation.PrepareAsync(Options, Path.Combine(sibling, "audio.wav"), default));
    }

    [Theory]
    [InlineData("relative.wav")]
    [InlineData("../escape.wav")]
    public async Task RelativePathsAreRejected(string input)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => AudioFilePreparation.PrepareAsync(Options, input, default));
    }

    [Fact]
    public async Task ExplicitTraversalInsideMaterialRootIsAlsoRejected()
    {
        string input = Path.Combine(Materials, "sub", "..", "audio.wav");
        await Assert.ThrowsAsync<ArgumentException>(() => AudioFilePreparation.PrepareAsync(Options, input, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlappingRootsAreRejected(bool cacheBelowMaterial)
    {
        var options = cacheBelowMaterial ? Options with { CacheDirectory = Path.Combine(Materials, "cache") }
            : Options with { MaterialDirectory = Path.Combine(Cache, "materials") };
        Assert.Throws<ArgumentException>(() => AudioFilePreparation.ValidateOptions(options));
    }

    [Fact]
    public void VolumeRootContainsEveryChildAndIsRejected()
    {
        Assert.Throws<ArgumentException>(() => AudioFilePreparation.ValidateOptions(Options with { MaterialDirectory = Path.GetPathRoot(root)! }));
    }

    [Theory]
    [InlineData(0, 100, 2)]
    [InlineData(100, 0, 2)]
    [InlineData(100, 100, 0)]
    [InlineData(100, 4294967295L, 2)]
    public void InvalidLimitsAreRejected(long input, long decoded, int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioFilePreparation.ValidateOptions(
            Options with { MaxInputBytes = input, MaxDecodedBytes = decoded, MaxPreparedSessions = count }));
    }

    [Fact]
    public void ExplicitDeviceSelectionIsRequired()
    {
        Assert.Throws<ArgumentException>(() => AudioFilePreparation.ValidateOptions(Options with { DeviceId = " " }));
    }

    [Fact]
    public async Task CapacityIncludesExistingAndAbandonedSessions()
    {
        string input = WritePcm(8000, 1, 50);
        var options = Options with { MaxPreparedSessions = 1 };
        using (var first = await AudioFilePreparation.PrepareAsync(options, input, default))
        {
            await Assert.ThrowsAsync<IOException>(() => AudioFilePreparation.PrepareAsync(options, input, default));
            Assert.True(File.Exists(first.Path));
        }
        using (var afterDispose = await AudioFilePreparation.PrepareAsync(options, input, default))
            Assert.True(File.Exists(afterDispose.Path));
        Directory.CreateDirectory(Path.Combine(Cache, "session-abandoned"));
        await Assert.ThrowsAsync<IOException>(() => AudioFilePreparation.PrepareAsync(options, input, default));
        Assert.True(Directory.Exists(Path.Combine(Cache, "session-abandoned")));
    }

    [Fact]
    public async Task DisposeIsIdempotentAndNeverDeletesMaterial()
    {
        string input = WritePcm(8000, 1, 50);
        var file = await AudioFilePreparation.PrepareAsync(Options, input, default);
        file.Dispose();
        file.Dispose();
        Assert.True(File.Exists(input));
        AssertNoSessions();
    }

    [Fact]
    public async Task TruncatedPcmDataIsRejectedAndRemoved()
    {
        string input = WritePcm(8000, 1, 100);
        using (var stream = new FileStream(input, FileMode.Open, FileAccess.Write))
            stream.SetLength(stream.Length - 20);
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioFilePreparation.PrepareAsync(Options, input, default));
        AssertNoSessions();
    }

    [Fact]
    public async Task AlternateDataStreamPathsAreRejected()
    {
        string input = WritePcm(8000, 1, 50) + ":hidden";
        await Assert.ThrowsAsync<ArgumentException>(() => AudioFilePreparation.PrepareAsync(Options, input, default));
    }
    private string WritePcm(int sampleRate, int channels, int frames)
    {
        Directory.CreateDirectory(Materials);
        string input = Path.Combine(Materials, "input.wav");
        using var writer = new WaveFileWriter(input, new WaveFormat(sampleRate, 16, channels));
        byte[] bytes = new byte[frames * channels * 2];
        for (int index = 0; index < bytes.Length; index += 2)
            BitConverter.TryWriteBytes(bytes.AsSpan(index, 2), (short)(index % 1000));
        writer.Write(bytes, 0, bytes.Length);
        return input;
    }

    private void AssertNoSessions()
    {
        if (Directory.Exists(Cache)) Assert.Empty(Directory.EnumerateDirectories(Cache, "session-*"));
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(root);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("LiveAudio-T050-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe test directory cleanup.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
