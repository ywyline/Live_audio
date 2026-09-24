using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.Versioning;

namespace TikTokAudio.Infrastructure.Audio;

internal static class AudioFilePreparation
{
    internal static void ValidateOptions(AudioOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string material = AbsolutePath(options.MaterialDirectory);
        string cache = AbsolutePath(options.CacheDirectory);
        if (Contains(material, cache) || Contains(cache, material))
            throw new ArgumentException("Material and cache directories must be independent.");
        RejectReparsePoints(material);
        RejectReparsePoints(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DeviceId);
        if (options.MaxInputBytes <= 0 || options.MaxDecodedBytes <= 0 ||
            options.MaxDecodedBytes > uint.MaxValue - 128L || options.MaxPreparedSessions <= 0 ||
            options.LatencyMilliseconds is < 10 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    internal static Task<PreparedAudioFile> PrepareAsync(AudioOutputOptions options, string sourcePath,
        CancellationToken cancellationToken) => Task.Run(() => Prepare(options, sourcePath, cancellationToken), cancellationToken);

    private static PreparedAudioFile Prepare(AudioOutputOptions options, string sourcePath, CancellationToken token)
    {
        ValidateOptions(options);
        token.ThrowIfCancellationRequested();
        string material = AbsolutePath(options.MaterialDirectory);
        string source = AbsolutePath(sourcePath);
        if (!Contains(material, source) || string.Equals(source, material, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Audio source must be inside the selected material directory.", nameof(sourcePath));
        RejectReparsePoints(source);
        string cache = AbsolutePath(options.CacheDirectory);
        Directory.CreateDirectory(cache);
        RejectReparsePoints(cache);
        string directory = Path.Combine(cache, "session-" + Guid.NewGuid().ToString("N"));
        RejectReparsePoints(Path.Combine(cache, ".prepare.lock"));
        using (var admission = new FileStream(Path.Combine(cache, ".prepare.lock"), FileMode.OpenOrCreate,
                   FileAccess.ReadWrite, FileShare.None))
        {
            if (Directory.EnumerateDirectories(cache, "session-*").Take(options.MaxPreparedSessions).Count() >= options.MaxPreparedSessions)
                throw new IOException("The prepared audio cache is full; existing sessions or crash remnants must be released first.");
            Directory.CreateDirectory(directory);
        }
        try
        {
            string input = Path.Combine(directory, "input" + Path.GetExtension(source));
            using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (sourceStream.Length <= 0 || sourceStream.Length > options.MaxInputBytes)
                    throw new InvalidDataException("Audio input exceeds the configured limit or is empty.");
                using var copied = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = sourceStream.Read(buffer)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    total = checked(total + count);
                    if (total > options.MaxInputBytes) throw new InvalidDataException("Audio input exceeds the configured limit.");
                    copied.Write(buffer, 0, count);
                }
            }
            token.ThrowIfCancellationRequested();
            string output = Path.Combine(directory, "audio.wav");
            int sampleRate;
            long frames;
            using (WaveStream reader = OpenReader(input))
            {
                WaveFormat sourceFormat = reader.WaveFormat;
                if (sourceFormat.SampleRate <= 0 || sourceFormat.Channels is < 1 or > 32 || sourceFormat.BlockAlign <= 0)
                    throw new InvalidDataException("Invalid audio format.");
                if (reader.Length % sourceFormat.BlockAlign != 0)
                    throw new InvalidDataException("Audio input ends inside a sample frame.");
                var provider = new SampleToWaveProvider16(reader.ToSampleProvider());
                using var writer = new WaveFileWriter(output, provider.WaveFormat);
                int block = provider.WaveFormat.BlockAlign;
                byte[] decoded = new byte[16384 - 16384 % block];
                long bytes = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int count = provider.Read(decoded, 0, decoded.Length);
                    if (count == 0) break;
                    if (count % block != 0) throw new InvalidDataException("Decoder returned an incomplete sample frame.");
                    bytes = checked(bytes + count);
                    if (bytes > options.MaxDecodedBytes) throw new InvalidDataException("Decoded audio exceeds the configured limit.");
                    writer.Write(decoded, 0, count);
                }
                if (bytes == 0) throw new InvalidDataException("Audio has no decodable frames.");
                // PCM WAV readers expose the declared data size; detect a truncated data chunk.
                if (reader is WaveFileReader && reader.Position != reader.Length)
                    throw new InvalidDataException("Audio data is truncated.");
                sampleRate = provider.WaveFormat.SampleRate;
                frames = bytes / block;
            }
            File.Delete(input);
            token.ThrowIfCancellationRequested();
            return new PreparedAudioFile(directory, output, sampleRate, frames);
        }
        catch
        {
            DeleteOwnedDirectory(directory, cache);
            throw;
        }
    }

    private static WaveStream OpenReader(string path)
    {
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var reader = new WaveFileReader(path);
            if (reader.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)
                return reader;
            reader.Dispose();
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Compressed audio decoding requires Windows codecs.");
        return OpenWindowsReader(path);
    }

    [SupportedOSPlatform("windows")]
    private static WaveStream OpenWindowsReader(string path) => new AudioFileReader(path);

    private static string AbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.AsSpan(Path.GetPathRoot(path)?.Length ?? 0).Contains(':') ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "." or ".."))
            throw new ArgumentException("Only absolute local paths without traversal or alternate data streams are allowed.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool Contains(string root, string candidate) =>
        string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static void RejectReparsePoints(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not allowed in audio paths.");
        }
    }

    internal static void DeleteOwnedDirectory(string directory, string cacheRoot)
    {
        string resolved = Path.GetFullPath(directory);
        string cache = Path.GetFullPath(cacheRoot);
        if (!Contains(cache, resolved) || string.Equals(cache, resolved, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("session-", StringComparison.Ordinal))
            throw new IOException("Refusing to remove a directory outside the owned audio cache.");
        if (Directory.Exists(resolved))
        {
            RejectReparsePoints(resolved);
            foreach (string file in Directory.EnumerateFiles(resolved))
            {
                RejectReparsePoints(file);
                File.Delete(file);
            }
            Directory.Delete(resolved, false);
        }
    }
}

internal sealed class PreparedAudioFile(string directory, string path, int sampleRate, long lengthSamples) : IDisposable
{
    private bool disposed;
    public string Path { get; } = path;
    public int SampleRate { get; } = sampleRate;
    public long LengthSamples { get; } = lengthSamples;

    public void Dispose()
    {
        if (disposed) return;
        AudioFilePreparation.DeleteOwnedDirectory(directory, System.IO.Path.GetDirectoryName(directory)!);
        disposed = true;
    }
}
