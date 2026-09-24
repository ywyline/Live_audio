using System.Buffers.Binary;

namespace TikTokAudio.Infrastructure.Tts;

internal static class PcmWaveWriter
{
    internal const int SampleRate = 48_000;
    internal const int HeaderSize = 44;

    public static async Task<long> WriteAsync(Stream pcm, FileStream output, int maxBytes, CancellationToken cancellationToken)
    {
        await output.WriteAsync(new byte[HeaderSize], cancellationToken);
        var buffer = new byte[16 * 1024];
        long bytes = 0;
        while (true)
        {
            var count = await pcm.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            bytes += count;
            if (bytes > maxBytes) throw new InvalidDataException("合成音频超出大小上限。");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes == 0 || bytes % 2 != 0)
        {
            throw new InvalidDataException("合成音频不是完整的 PCM16 数据。");
        }

        var header = new byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), checked((int)bytes + 36));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), checked((int)bytes));
        output.Position = 0;
        await output.WriteAsync(header, cancellationToken);
        await output.FlushAsync(cancellationToken);
        if (output.Length != bytes + HeaderSize) throw new InvalidDataException("WAV 长度校验失败。");
        return bytes;
    }
}
