using System.Buffers.Binary;
using System.Text.Json.Nodes;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Tts.Cache;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class TtsAudioCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LiveAudio-cache-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(root, "cache");
    private string Staging => Path.Combine(root, "staging");
    private static readonly string Key = new('a', 64);
    private static readonly string OtherKey = new('b', 64);

    [Fact]
    public async Task ValidWavSurvivesRestartAndUsesMeasuredMetadata()
    {
        var source = Source(Wave());
        var stored = await Create().StoreAsync(Key, source, default);
        Assert.Equal(OperationStatus.Succeeded, stored.Status);
        Assert.False(File.Exists(source.Path));
        Assert.Equal("wav", stored.Value!.Format);
        Assert.Equal(48000, stored.Value.SampleRate);
        Assert.Equal(TimeSpan.FromSeconds(0.01), stored.Value.Duration);
        Assert.Equal("external-engine", stored.Value.EngineId);
        var hit = await Create().FindAsync(Key, default);
        Assert.Equal(OperationStatus.Succeeded, hit.Status);
        Assert.Equal(stored.Value, hit.Value);
        Assert.Equal(Wave(), await File.ReadAllBytesAsync(hit.Value!.Path));
    }

    [Fact]
    public async Task MissingKeyIsSuccessfulMiss()
    {
        var result = await Create().FindAsync(Key, default);
        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task CorruptAudioDoesNotHitEvenWhenSizeAndWaveStructureRemainValid()
    {
        var stored = await Create().StoreAsync(Key, Source(Wave()), default);
        var bytes = await File.ReadAllBytesAsync(stored.Value!.Path);
        bytes[^1] ^= 0x7f;
        await File.WriteAllBytesAsync(stored.Value.Path, bytes);
        var result = await Create().FindAsync(Key, default);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("engine")]
    [InlineData("wave")]
    [InlineData("oversized")]
    public async Task CorruptMetadataNeverReturnsAudio(string corruption)
    {
        await Create().StoreAsync(Key, Source(Wave()), default);
        var path = Path.Combine(Cache, Key, "metadata.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        if (corruption == "key") json["Key"] = OtherKey;
        if (corruption == "engine") json["EngineId"] = "";
        if (corruption == "wave") json["Wave"]!["SampleRate"] = 24000;
        await File.WriteAllTextAsync(path, corruption == "oversized" ? new string('x', 9000) : json.ToJsonString());
        var result = await Create().FindAsync(Key, default);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task IncompletePublishedDirectoryIsNotAHitOrOverwritten()
    {
        var directory = Path.Combine(Cache, Key);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "audio.wav"), Wave());
        Assert.Equal(OperationStatus.Failed, (await Create().FindAsync(Key, default)).Status);
        var source = Source(Wave());
        Assert.Equal(OperationStatus.Failed, (await Create().StoreAsync(Key, source, default)).Status);
        Assert.False(File.Exists(source.Path));
        Assert.True(File.Exists(Path.Combine(directory, "audio.wav")));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("length")]
    [InlineData("format")]
    [InlineData("blockalign")]
    [InlineData("byterate")]
    [InlineData("channels")]
    [InlineData("bits")]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("odd")]
    public async Task MalformedWaveIsRejectedAndAllOwnedFilesAreRemoved(string corruption)
    {
        var bytes = Wave();
        switch (corruption)
        {
            case "header": bytes[0] = 0; break;
            case "length": bytes[4]--; break;
            case "format": bytes[20] = 3; break;
            case "blockalign": bytes[32] = 4; break;
            case "byterate": bytes[28] ^= 1; break;
            case "channels": bytes[22] = 0; break;
            case "bits": bytes[34] = 8; break;
            case "empty": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 0); break;
            case "truncated": bytes = bytes[..^2]; break;
            case "odd": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 959); break;
        }
        var source = Source(bytes);
        var result = await Create().StoreAsync(Key, source, default);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Null(result.Value);
        Assert.False(File.Exists(source.Path));
        Assert.Empty(Directory.GetDirectories(Cache));
        Assert.Null((await Create().FindAsync(Key, default)).Value);
    }

    [Fact]
    public async Task EntryQuotaDoesNotEvictExistingAudio()
    {
        var cache = Create(entries: 1);
        var original = await cache.StoreAsync(Key, Source(Wave()), default);
        var source = Source(Wave());
        var rejected = await cache.StoreAsync(OtherKey, source, default);
        Assert.Equal(OperationStatus.Failed, rejected.Status);
        Assert.False(File.Exists(source.Path));
        Assert.Equal(original.Value, (await cache.FindAsync(Key, default)).Value);
        Assert.Null((await cache.FindAsync(OtherKey, default)).Value);
        Assert.Single(Directory.GetDirectories(Cache));
    }

    [Theory]
    [InlineData(50, 10000)]
    [InlineData(10000, 50)]
    public async Task ByteLimitsRejectBeforePublishing(long assetBytes, long totalBytes)
    {
        var source = Source(Wave());
        var result = await Create(assetBytes: assetBytes, totalBytes: totalBytes).StoreAsync(Key, source, default);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(File.Exists(source.Path));
        Assert.Empty(Directory.GetDirectories(Cache));
    }

    [Fact]
    public async Task AbandonedTemporaryDirectoriesConsumeQuotaButNeverHit()
    {
        Directory.CreateDirectory(Path.Combine(Cache, ".pending-abandoned"));
        var cache = Create(entries: 1);
        var source = Source(Wave());
        Assert.Equal(OperationStatus.Failed, (await cache.StoreAsync(Key, source, default)).Status);
        Assert.False(File.Exists(source.Path));
        Assert.True(Directory.Exists(Path.Combine(Cache, ".pending-abandoned")));
        Assert.Null((await cache.FindAsync(Key, default)).Value);
    }

    [Fact]
    public async Task PreCancelledStoreCleansSourceWithoutPublishing()
    {
        var source = Source(Wave());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await Create().StoreAsync(Key, source, cts.Token);
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.False(File.Exists(source.Path));
        Assert.False(Directory.Exists(Path.Combine(Cache, Key)));
        Assert.Equal(OperationStatus.Cancelled, (await Create().FindAsync(Key, cts.Token)).Status);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("ABCDEF")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA")]
    public async Task InvalidKeyCannotEscapeCacheAndStillCleansOwnedSource(string key)
    {
        var source = Source(Wave());
        var cache = Create();
        Assert.Equal(OperationStatus.Failed, (await cache.StoreAsync(key, source, default)).Status);
        Assert.False(File.Exists(source.Path));
        Assert.Equal(OperationStatus.Failed, (await cache.FindAsync(key, default)).Status);
    }

    [Fact]
    public async Task StoreAndDiscardNeverDeleteAnExternalFile()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "external.wav");
        await File.WriteAllBytesAsync(path, Wave());
        var source = new LocalAudioAsset(path, "wav", "external-engine", null, null);
        var cache = Create();
        Assert.Equal(OperationStatus.Failed, (await cache.StoreAsync(Key, source, default)).Status);
        Assert.Equal(OperationStatus.Failed, (await cache.DiscardAsync(source)).Status);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SimilarPrefixDirectoryIsNotOwned()
    {
        Directory.CreateDirectory(Staging + "-external");
        var path = Path.Combine(Staging + "-external", "audio.wav");
        await File.WriteAllBytesAsync(path, Wave());
        var source = new LocalAudioAsset(path, "wav", "external-engine", null, null);
        Assert.Equal(OperationStatus.Failed, (await Create().DiscardAsync(source)).Status);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DiscardDeletesOwnedSourceAndIsIdempotent()
    {
        var source = Source(Wave());
        var cache = Create();
        Assert.Equal(OperationStatus.Succeeded, (await cache.DiscardAsync(source)).Status);
        Assert.False(File.Exists(source.Path));
        Assert.Equal(OperationStatus.Succeeded, (await cache.DiscardAsync(source)).Status);
    }

    [Fact]
    public async Task DuplicateStoreReusesOriginalAndDeletesNewSource()
    {
        var cache = Create(entries: 1);
        var first = await cache.StoreAsync(Key, Source(Wave()), default);
        var source = Source(Wave());
        var duplicate = await Create(entries: 1).StoreAsync(Key, source, default);
        Assert.Equal(OperationStatus.Succeeded, duplicate.Status);
        Assert.Equal(first.Value, duplicate.Value);
        Assert.False(File.Exists(source.Path));
        Assert.Single(Directory.GetDirectories(Cache));
    }

    [Fact]
    public async Task ExclusiveLeasePreventsConcurrentInstancesFromPublishing()
    {
        Directory.CreateDirectory(Cache);
        using var lease = new FileStream(Path.Combine(Cache, ".cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var source = Source(Wave());
        Assert.Equal(OperationStatus.Failed, (await Create().StoreAsync(Key, source, default)).Status);
        Assert.False(File.Exists(source.Path));
        Assert.Empty(Directory.GetDirectories(Cache));
    }

    [Fact]
    public void OverlappingOrRelativeDirectoriesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new FileTtsAudioCache(Options(Cache, Cache)));
        Assert.Throws<ArgumentException>(() => new FileTtsAudioCache(Options(Cache, Path.Combine(Cache, "child"))));
        Assert.Throws<ArgumentException>(() => new FileTtsAudioCache(Options("relative", Staging)));
    }

    [Fact]
    public async Task StereoPcmDurationUsesChannelCount()
    {
        var bytes = Wave();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 192000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 4);
        var result = await Create().StoreAsync(Key, Source(bytes), default);
        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(0.005), result.Value!.Duration);
    }

    [Fact]
    public async Task PaddedUnknownChunkIsAcceptedWithoutBeingTreatedAsAudio()
    {
        var original = Wave();
        var bytes = new byte[original.Length + 10];
        original.AsSpan(0, 36).CopyTo(bytes);
        "JUNK"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 1);
        bytes[44] = 42;
        original.AsSpan(36).CopyTo(bytes.AsSpan(46));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        var result = await Create().StoreAsync(Key, Source(bytes), default);
        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(0.01), result.Value!.Duration);
    }
    [Fact]
    public void VolumeRootCannotBypassCacheAndStagingSeparation()
    {
        var volume = Path.GetPathRoot(Path.GetFullPath(root))!;
        // Construction only: this must never create files or locks in the volume root.
        Assert.Throws<ArgumentException>(() => new FileTtsAudioCache(Options(volume, Staging)));
        Assert.Throws<ArgumentException>(() => new FileTtsAudioCache(Options(Cache, volume)));
    }

    private FileTtsAudioCache Create(int entries = 8, long assetBytes = 10000, long totalBytes = 100000) =>
        new(new TtsAudioCacheOptions { CacheDirectory = Cache, StagingDirectory = Staging,
            MaxEntries = entries, MaxAssetBytes = assetBytes, MaxTotalBytes = totalBytes });

    private static TtsAudioCacheOptions Options(string cache, string staging) =>
        new() { CacheDirectory = cache, StagingDirectory = staging, MaxEntries = 8, MaxAssetBytes = 10000, MaxTotalBytes = 100000 };

    private LocalAudioAsset Source(byte[] bytes)
    {
        Directory.CreateDirectory(Staging);
        var path = Path.Combine(Staging, Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(path, bytes);
        // The cache must measure WAV metadata instead of trusting these deliberately wrong hints.
        return new(path, "claimed-format", "external-engine", TimeSpan.FromDays(1), 123);
    }

    private static byte[] Wave()
    {
        var bytes = new byte[1004];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 96000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 960);
        bytes[44] = 42;
        return bytes;
    }

    public void Dispose()
    {
        if (!root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected fixture root.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
