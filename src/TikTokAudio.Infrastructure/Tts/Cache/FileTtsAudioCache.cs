using System.Text.Json;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Tts.Cache;

public sealed class FileTtsAudioCache : ITtsAudioCache
{
    private const int MaximumMetadataBytes = 8192;
    private readonly string cacheDirectory;
    private readonly string stagingDirectory;
    private readonly int maxEntries;
    private readonly long maxTotalBytes;
    private readonly long maxAssetBytes;

    public FileTtsAudioCache(TtsAudioCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        cacheDirectory = CachePaths.Root(options.CacheDirectory);
        stagingDirectory = CachePaths.Root(options.StagingDirectory);
        if (cacheDirectory.Equals(stagingDirectory, StringComparison.OrdinalIgnoreCase) ||
            CachePaths.IsInside(cacheDirectory, stagingDirectory) || CachePaths.IsInside(stagingDirectory, cacheDirectory))
            throw new ArgumentException("缓存目录与生成暂存目录必须彼此独立。", nameof(options));
        if (options.MaxEntries <= 0 || options.MaxTotalBytes <= 0 || options.MaxAssetBytes is < 46 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options));
        maxEntries = options.MaxEntries;
        maxTotalBytes = options.MaxTotalBytes;
        maxAssetBytes = options.MaxAssetBytes;
    }

    public async Task<OperationResult<LocalAudioAsset>> FindAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            ValidateKey(key);
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = AcquireLease();
            var asset = await ReadEntryAsync(key, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(OperationStatus.Succeeded, asset);
        }
        catch (OperationCanceledException) { return new(OperationStatus.Cancelled, null, "缓存读取已取消。"); }
        catch (Exception error) when (Expected(error)) { return new(OperationStatus.Failed, null, "缓存不可读或完整性校验失败。"); }
    }

    public async Task<OperationResult<LocalAudioAsset>> StoreAsync(
        string key, LocalAudioAsset generatedAsset, CancellationToken cancellationToken)
    {
        string? ownedSource = null;
        string? temporary = null;
        string? published = null;
        FileStream? lease = null;
        OperationResult<LocalAudioAsset> result;
        try
        {
            ArgumentNullException.ThrowIfNull(generatedAsset);
            ownedSource = CachePaths.OwnedSource(stagingDirectory, generatedAsset.Path, mustExist: true);
            ValidateKey(key);
            if (string.IsNullOrWhiteSpace(generatedAsset.EngineId) || generatedAsset.EngineId.Length > 256)
                throw new InvalidDataException("引擎标识无效。");
            cancellationToken.ThrowIfCancellationRequested();
            lease = AcquireLease();
            var existing = await ReadEntryAsync(key, cancellationToken);
            if (existing is not null)
            {
                if (!existing.EngineId.Equals(generatedAsset.EngineId, StringComparison.Ordinal))
                    throw new InvalidDataException("缓存引擎标识不一致。");
                cancellationToken.ThrowIfCancellationRequested();
                result = new(OperationStatus.Succeeded, existing);
            }
            else
            {
                // Admit before copying so crash remnants cannot cause unbounded temporary growth.
                CheckCapacity(cancellationToken, reservedEntries: 1);
                temporary = Path.Combine(cacheDirectory, ".pending-" + Guid.NewGuid().ToString("N"));
                CachePaths.Check(temporary);
                Directory.CreateDirectory(temporary);
                var audio = Path.Combine(temporary, "audio.wav");
                await CopyBoundedAsync(ownedSource, audio, cancellationToken);
                var wave = await PcmWaveInspector.InspectAsync(audio, maxAssetBytes, cancellationToken);
                var metadata = new CacheMetadata(1, key, generatedAsset.EngineId, wave);
                var json = JsonSerializer.SerializeToUtf8Bytes(metadata);
                if (json.Length > MaximumMetadataBytes) throw new InvalidDataException("缓存元数据过大。");
                await File.WriteAllBytesAsync(Path.Combine(temporary, "metadata.json"), json, cancellationToken);
                CheckCapacity(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                CachePaths.Check(temporary);
                var final = Path.Combine(cacheDirectory, key);
                CachePaths.Check(final);
                Directory.Move(temporary, final);
                published = final;
                temporary = null;
                cancellationToken.ThrowIfCancellationRequested();
                result = new(OperationStatus.Succeeded, Asset(final, metadata));
            }
        }
        catch (OperationCanceledException) { result = new(OperationStatus.Cancelled, null, "缓存写入已取消。"); }
        catch (Exception error) when (Expected(error)) { result = new(OperationStatus.Failed, null, "缓存写入失败或容量已满。"); }

        // Ownership is transferred only after the source passes the directory/reparse checks.
        // Cleanup has no cancellation token so cancelled generation cannot leave owned source files.
        try
        {
            if (ownedSource is not null) CachePaths.DeleteSource(stagingDirectory, ownedSource);
        }
        catch (Exception error) when (Expected(error))
        {
            result = new(OperationStatus.Failed, null, "暂存音频清理失败。");
        }
        if (cancellationToken.IsCancellationRequested && result.Status == OperationStatus.Succeeded)
            result = new(OperationStatus.Cancelled, null, "缓存写入已取消。");
        try
        {
            if (temporary is not null) CachePaths.DeleteEntry(cacheDirectory, temporary);
            if (published is not null && result.Status != OperationStatus.Succeeded)
                CachePaths.DeleteEntry(cacheDirectory, published);
        }
        catch (Exception error) when (Expected(error))
        {
            result = new(OperationStatus.Failed, null, "未完成缓存清理失败。");
        }
        finally { lease?.Dispose(); }
        return result;
    }

    public Task<OperationResult> DiscardAsync(LocalAudioAsset generatedAsset)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(generatedAsset);
            CachePaths.DeleteSource(stagingDirectory, generatedAsset.Path);
            return Task.FromResult(OperationResult.Succeeded());
        }
        catch (Exception error) when (Expected(error))
        {
            return Task.FromResult(OperationResult.Failed("暂存音频不可清理。"));
        }
    }

    private FileStream AcquireLease()
    {
        CachePaths.Check(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, ".cache.lock");
        CachePaths.Check(path);
        // The file stays on disk; its exclusive handle coordinates independent cache instances.
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private async Task<LocalAudioAsset?> ReadEntryAsync(string key, CancellationToken token)
    {
        var directory = Path.Combine(cacheDirectory, key);
        CachePaths.Check(directory);
        if (File.Exists(directory)) throw new InvalidDataException("缓存条目不是目录。");
        if (!Directory.Exists(directory)) return null;
        var metadataPath = Path.Combine(directory, "metadata.json");
        CachePaths.Check(metadataPath);
        await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        if (stream.Length is <= 0 or > MaximumMetadataBytes) throw new InvalidDataException("缓存元数据无效。");
        var json = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(json, token);
        if (stream.ReadByte() != -1) throw new InvalidDataException("缓存元数据长度改变。");
        var metadata = JsonSerializer.Deserialize<CacheMetadata>(json, new JsonSerializerOptions { MaxDepth = 8 });
        if (metadata is null || metadata.Version != 1 || metadata.Key != key ||
            string.IsNullOrWhiteSpace(metadata.EngineId) || metadata.EngineId.Length > 256 || metadata.Wave is null)
            throw new InvalidDataException("缓存元数据无效。");
        var wave = await PcmWaveInspector.InspectAsync(Path.Combine(directory, "audio.wav"), maxAssetBytes, token);
        if (wave != metadata.Wave) throw new InvalidDataException("缓存音频哈希或格式不一致。");
        return Asset(directory, metadata);
    }

    private async Task CopyBoundedAsync(string source, string destination, CancellationToken token)
    {
        CachePaths.Check(source);
        CachePaths.Check(destination);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maxAssetBytes) throw new InvalidDataException("音频超过单文件限额。");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > maxAssetBytes) throw new InvalidDataException("音频超过单文件限额。");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        await output.FlushAsync(token);
        token.ThrowIfCancellationRequested();
    }

    private void CheckCapacity(CancellationToken token, int reservedEntries = 0)
    {
        long entries = reservedEntries;
        long bytes = 0;
        foreach (var directory in Directory.EnumerateDirectories(cacheDirectory))
        {
            token.ThrowIfCancellationRequested();
            if (++entries > maxEntries) throw new InvalidDataException("缓存条目数已满。");
            CachePaths.Check(directory);
            var files = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                CachePaths.Check(path);
                if (++files > 3 || Directory.Exists(path)) throw new InvalidDataException("缓存目录结构无效。");
                bytes = checked(bytes + new FileInfo(path).Length);
                if (bytes > maxTotalBytes) throw new InvalidDataException("缓存容量已满。");
            }
        }
        var rootFiles = 0;
        foreach (var path in Directory.EnumerateFiles(cacheDirectory))
        {
            token.ThrowIfCancellationRequested();
            if (++rootFiles > maxEntries + 1L) throw new InvalidDataException("缓存目录结构无效。");
            CachePaths.Check(path);
            if (Path.GetFileName(path) == ".cache.lock") continue;
            bytes = checked(bytes + new FileInfo(path).Length);
            if (bytes > maxTotalBytes) throw new InvalidDataException("缓存容量已满。");
        }
    }

    private static LocalAudioAsset Asset(string directory, CacheMetadata metadata) =>
        new(Path.Combine(directory, "audio.wav"), "wav", metadata.EngineId, metadata.Wave.Duration, metadata.Wave.SampleRate);

    private static void ValidateKey(string key)
    {
        if (key is null || key.Length != 64 || key.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("缓存键必须为64位小写十六进制。");
    }

    private static bool Expected(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or ArgumentException or JsonException or OverflowException or NotSupportedException;

    private sealed record CacheMetadata(int Version, string Key, string EngineId, WaveInfo Wave);
}
