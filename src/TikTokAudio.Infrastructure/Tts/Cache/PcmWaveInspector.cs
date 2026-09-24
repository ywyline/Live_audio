using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TikTokAudio.Infrastructure.Tts.Cache;

internal sealed record WaveInfo(long FileBytes, int SampleRate, int Channels, long DataBytes, string Sha256)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)DataBytes / (SampleRate * Channels * 2L));
}

internal static class PcmWaveInspector
{
    public static async Task<WaveInfo> InspectAsync(string path, long maximumBytes, CancellationToken token)
    {
        CachePaths.Check(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 46 || stream.Length > maximumBytes || stream.Length > uint.MaxValue)
            throw new InvalidDataException("WAV大小无效。");
        var header = new byte[16];
        await stream.ReadExactlyAsync(header.AsMemory(0, 12), token);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != stream.Length - 8)
            throw new InvalidDataException("WAV头无效。");
        var channels = 0;
        var sampleRate = 0;
        long dataBytes = 0;
        var chunks = 0;
        while (stream.Position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++chunks > 4096 || stream.Length - stream.Position < 8)
                throw new InvalidDataException("WAV块无效。");
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), token);
            var chunkBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var end = checked(stream.Position + chunkBytes + (chunkBytes & 1));
            if (end > stream.Length) throw new InvalidDataException("WAV块被截断。");
            if (header.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (sampleRate != 0 || chunkBytes is not (16 or 18))
                    throw new InvalidDataException("WAV格式块无效。");
                await stream.ReadExactlyAsync(header, token);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                var rate = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 1 || channels is < 1 or > 32 ||
                    rate is < 1 or > 384000 || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14)) != 16 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)) != channels * 2 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != rate * channels * 2)
                    throw new InvalidDataException("仅支持有效PCM16 WAV。");
                sampleRate = (int)rate;
                if (chunkBytes == 18)
                {
                    await stream.ReadExactlyAsync(header.AsMemory(0, 2), token);
                    if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 0)
                        throw new InvalidDataException("WAV扩展块无效。");
                }
            }
            else if (header.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (dataBytes != 0 || chunkBytes == 0) throw new InvalidDataException("WAV数据块无效。");
                dataBytes = chunkBytes;
            }
            stream.Position = end;
        }
        if (sampleRate == 0 || dataBytes == 0 || dataBytes % (channels * 2) != 0)
            throw new InvalidDataException("WAV采样数据无效。");
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, token);
        token.ThrowIfCancellationRequested();
        return new WaveInfo(stream.Length, sampleRate, channels, dataBytes, Convert.ToHexStringLower(hash));
    }
}
