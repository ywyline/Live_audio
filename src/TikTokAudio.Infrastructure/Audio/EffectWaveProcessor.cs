using NAudio.Wave;
using TikTokAudio.Application.Playback;

namespace TikTokAudio.Infrastructure.Audio;

internal readonly record struct EffectFrameMap
{
    internal EffectFrameMap(long sourceFrames, long renderedFrames)
    {
        if (sourceFrames <= 0) throw new ArgumentOutOfRangeException(nameof(sourceFrames));
        if (renderedFrames <= 0) throw new ArgumentOutOfRangeException(nameof(renderedFrames));
        SourceFrames = sourceFrames;
        RenderedFrames = renderedFrames;
    }

    internal long SourceFrames { get; }
    internal long RenderedFrames { get; }

    internal long SourceToRendered(long sourceFrame) =>
        Scale(sourceFrame, SourceFrames, RenderedFrames);

    internal long RenderedToSource(long renderedFrame) =>
        Scale(renderedFrame, RenderedFrames, SourceFrames);

    private static long Scale(long value, long sourceLength, long targetLength)
    {
        if (value <= 0) return 0;
        if (value >= sourceLength) return targetLength;
        return (long)Math.Round((decimal)value * targetLength / sourceLength,
            0, MidpointRounding.AwayFromZero);
    }
}

// Offline processing keeps all effects deterministic and returns the explicit
// rendered/source mapping required by the WASAPI source cursor.
internal static class EffectWaveProcessor
{
    private const int MaximumGrainFrames = 8192;

    internal static bool Supports(PlaybackEffectSelection selection) =>
        selection is not null &&
        double.IsFinite(selection.Speed) && selection.Speed is >= 0.5 and <= 2 &&
        double.IsFinite(selection.PitchSemitones) && selection.PitchSemitones is >= -6 and <= 6 &&
        double.IsFinite(selection.StereoBalance) && selection.StereoBalance is >= -1 and <= 1 &&
        double.IsFinite(selection.EqDecibels) && selection.EqDecibels is >= -12 and <= 12 &&
        double.IsFinite(selection.EnvironmentRelativeDecibels) &&
        selection.EnvironmentRelativeDecibels is >= -90 and <= -35;

    internal static Task<EffectFrameMap> ProcessAsync(string path, PlaybackEffectSelection selection,
        CancellationToken cancellationToken) =>
        ProcessAsync(path, selection, long.MaxValue, cancellationToken);

    internal static Task<EffectFrameMap> ProcessAsync(string path, PlaybackEffectSelection selection,
        long maxOutputBytes, CancellationToken cancellationToken) =>
        Task.Run(() => Process(path, selection, maxOutputBytes, cancellationToken), cancellationToken);

    private static EffectFrameMap Process(string path, PlaybackEffectSelection selection,
        long maxOutputBytes, CancellationToken token)
    {
        if (!Supports(selection)) throw new NotSupportedException("The requested effect parameters are unsupported.");
        if (maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        token.ThrowIfCancellationRequested();
        string temporary = path + ".effect-" + Guid.NewGuid().ToString("N") + ".tmp";
        string pitchTemporary = path + ".pitch-" + Guid.NewGuid().ToString("N") + ".tmp";
        EffectFrameMap map;
        try
        {
            using (var input = new WaveFileReader(path))
            {
                var format = input.WaveFormat;
                if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 ||
                    format.Channels is < 1 or > 32 || format.SampleRate <= 0 || format.BlockAlign <= 0 ||
                    input.Length <= 0 || input.Length % format.BlockAlign != 0 ||
                    (selection.StereoBalance != 0 && format.Channels != 2))
                    throw new NotSupportedException("Effects require complete 16-bit PCM frames; balance requires stereo.");

                long sourceFrames = input.Length / format.BlockAlign;
                long renderedFrames = Math.Max(1,
                    checked((long)Math.Round(sourceFrames / selection.Speed, MidpointRounding.AwayFromZero)));
                long renderedBytes = checked(renderedFrames * format.BlockAlign);
                if (renderedBytes > maxOutputBytes)
                    throw new InvalidDataException("Rendered effects exceed the configured decoded-audio limit.");
                map = new EffectFrameMap(sourceFrames, renderedFrames);

                using var output = new WaveFileWriter(temporary, format);
                var writer = new EffectSampleWriter(output, format, selection);
                if (selection.Speed == 1 && selection.PitchSemitones == 0)
                    ProcessFramePreserving(input, writer, token);
                else
                {
                    WaveFileReader stretchInput = input;
                    WaveFileReader? pitched = null;
                    try
                    {
                        if (selection.PitchSemitones != 0)
                        {
                            WritePitchResampled(input, pitchTemporary, selection.PitchSemitones,
                                maxOutputBytes, token);
                            pitched = new WaveFileReader(pitchTemporary);
                            stretchInput = pitched;
                        }
                        ProcessTimeStretch(stretchInput, writer, map.RenderedFrames, token);
                    }
                    finally
                    {
                        pitched?.Dispose();
                    }
                }
                writer.Complete();
            }

            token.ThrowIfCancellationRequested();
            using (var verified = new WaveFileReader(temporary))
            using (var original = new WaveFileReader(path))
            {
                if (verified.Length != checked(map.RenderedFrames * original.WaveFormat.BlockAlign) ||
                    verified.WaveFormat.SampleRate != original.WaveFormat.SampleRate ||
                    verified.WaveFormat.Channels != original.WaveFormat.Channels ||
                    verified.WaveFormat.BlockAlign != original.WaveFormat.BlockAlign ||
                    verified.WaveFormat.Encoding != original.WaveFormat.Encoding ||
                    verified.WaveFormat.BitsPerSample != original.WaveFormat.BitsPerSample ||
                    verified.Length % verified.WaveFormat.BlockAlign != 0)
                    throw new InvalidDataException("Effect processing produced an invalid rendered frame layout.");
            }
            token.ThrowIfCancellationRequested();
            File.Replace(temporary, path, null);
            return map;
        }
        finally
        {
            if (File.Exists(pitchTemporary)) File.Delete(pitchTemporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ProcessFramePreserving(WaveFileReader input, EffectSampleWriter writer,
        CancellationToken token)
    {
        int block = input.WaveFormat.BlockAlign;
        byte[] buffer = new byte[block * Math.Max(1, 8192 / block)];
        double[] frame = new double[input.WaveFormat.Channels];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (count % block != 0) throw new InvalidDataException("Audio ends inside a frame.");
            for (int offset = 0; offset < count; offset += block)
            {
                for (int channel = 0; channel < frame.Length; channel++)
                    frame[channel] = BitConverter.ToInt16(buffer, offset + channel * 2) / 32768d;
                writer.WriteFrame(frame);
            }
        }
    }

    private static void WritePitchResampled(WaveFileReader input, string destination,
        double semitones, long maxOutputBytes, CancellationToken token)
    {
        int block = input.WaveFormat.BlockAlign;
        int channels = input.WaveFormat.Channels;
        long sourceFrames = input.Length / block;
        double factor = Math.Pow(2, semitones / 12);
        long outputFrames = Math.Max(1,
            checked((long)Math.Round(sourceFrames / factor, MidpointRounding.AwayFromZero)));
        if (checked(outputFrames * block) > maxOutputBytes)
            throw new InvalidDataException("Pitch processing exceeds the configured decoded-audio limit.");

        const int chunkFrames = 2048;
        int maximumInputFrames = checked((int)Math.Ceiling(chunkFrames * factor) + 3);
        byte[] inputBytes = new byte[checked(maximumInputFrames * block)];
        byte[] outputBytes = new byte[checked(chunkFrames * block)];
        using var output = new WaveFileWriter(destination, input.WaveFormat);
        for (long outputStart = 0; outputStart < outputFrames; outputStart += chunkFrames)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(chunkFrames, outputFrames - outputStart);
            double firstPosition = Math.Min(sourceFrames - 1, outputStart * factor);
            double lastPosition = Math.Min(sourceFrames - 1, (outputStart + count - 1) * factor);
            long sourceStart = (long)Math.Floor(firstPosition);
            long sourceEnd = (long)Math.Ceiling(lastPosition);
            int sourceCount = checked((int)(sourceEnd - sourceStart + 1));
            int bytesNeeded = checked(sourceCount * block);
            input.Position = checked(sourceStart * block);
            ReadExactly(input, inputBytes, bytesNeeded);

            for (int frame = 0; frame < count; frame++)
            {
                double position = Math.Min(sourceFrames - 1, (outputStart + frame) * factor);
                long lower = (long)Math.Floor(position);
                long upper = Math.Min(lower + 1, sourceFrames - 1);
                double fraction = position - lower;
                int lowerOffset = checked((int)(lower - sourceStart) * block);
                int upperOffset = checked((int)(upper - sourceStart) * block);
                int outputOffset = frame * block;
                for (int channel = 0; channel < channels; channel++)
                {
                    double a = BitConverter.ToInt16(inputBytes, lowerOffset + channel * 2);
                    double b = BitConverter.ToInt16(inputBytes, upperOffset + channel * 2);
                    short sample = (short)Math.Clamp(Math.Round(a + (b - a) * fraction),
                        short.MinValue, short.MaxValue);
                    BitConverter.TryWriteBytes(outputBytes.AsSpan(outputOffset + channel * 2, 2), sample);
                }
            }
            output.Write(outputBytes, 0, count * block);
        }
    }

    private static void ProcessTimeStretch(WaveFileReader input, EffectSampleWriter writer,
        long renderedFrames, CancellationToken token)
    {
        int channels = input.WaveFormat.Channels;
        int block = input.WaveFormat.BlockAlign;
        long inputFrames = input.Length / block;
        int grainFrames = Math.Clamp((int)Math.Round(input.WaveFormat.SampleRate * 0.04), 64, MaximumGrainFrames);
        if ((grainFrames & 1) != 0) grainFrames++;
        int overlapFrames = grainFrames / 2;
        int hop = grainFrames - overlapFrames;
        int search = Math.Clamp((int)Math.Round(input.WaveFormat.SampleRate * 0.004), 8, grainFrames / 4);
        long lastCandidate = Math.Max(0, inputFrames - grainFrames);
        byte[] sourceBuffer = new byte[checked((grainFrames + search * 2 + 2) * block)];
        double[] frame = new double[channels];
        var overlap = new OverlapBuffer(grainFrames + hop + 4, channels);
        long segment = 0;

        for (long outputStart = 0; outputStart < renderedFrames; outputStart = checked(outputStart + hop), segment++)
        {
            if ((segment & 31) == 0) token.ThrowIfCancellationRequested();
            overlap.FlushUntil(outputStart, writer);
            long nominal = (long)Math.Round((decimal)outputStart * inputFrames / renderedFrames,
                0, MidpointRounding.AwayFromZero);
            nominal = Math.Clamp(nominal, 0, lastCandidate);
            long minimum = Math.Max(0, nominal - search);
            long maximum = Math.Min(lastCandidate, nominal + search);
            long sourceStart = minimum;
            long sourceEnd = Math.Min(inputFrames - 1, maximum + grainFrames - 1);
            int sourceCount = checked((int)(sourceEnd - sourceStart + 1));
            int bytesNeeded = checked(sourceCount * block);
            input.Position = checked(sourceStart * block);
            ReadExactly(input, sourceBuffer, bytesNeeded);

            long candidate = segment == 0 || maximum == minimum
                ? nominal
                : SelectCandidate(sourceBuffer, sourceStart, minimum, maximum, block, channels,
                    overlapFrames, overlap, outputStart, nominal);
            for (int i = 0; i < grainFrames && outputStart + i < renderedFrames; i++)
            {
                long inputFrame = Math.Min(candidate + i, inputFrames - 1);
                int inputOffset = checked((int)(inputFrame - sourceStart) * block);
                for (int channel = 0; channel < channels; channel++)
                    frame[channel] = BitConverter.ToInt16(sourceBuffer, inputOffset + channel * 2) / 32768d;
                double weight = 1;
                if (segment != 0 && i < overlapFrames)
                    weight = (i + 0.5) / overlapFrames;
                else if (i >= grainFrames - overlapFrames)
                    weight = (grainFrames - i - 0.5) / overlapFrames;
                overlap.AddFrame(outputStart + i, frame, weight);
            }
        }
        overlap.FlushUntil(renderedFrames, writer);
    }

    private static long SelectCandidate(byte[] source, long sourceStart, long minimum, long maximum,
        int block, int channels, int overlapFrames, OverlapBuffer overlap, long outputStart, long nominal)
    {
        long best = nominal;
        double bestScore = double.NegativeInfinity;
        for (long candidate = minimum; candidate <= maximum; candidate += 2)
        {
            double score = Correlation(source, sourceStart, candidate, block, channels,
                overlapFrames, overlap, outputStart, 8);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        long refineStart = Math.Max(minimum, best - 2);
        long refineEnd = Math.Min(maximum, best + 2);
        for (long candidate = refineStart; candidate <= refineEnd; candidate++)
        {
            double score = Correlation(source, sourceStart, candidate, block, channels,
                overlapFrames, overlap, outputStart, 4);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return bestScore > -0.5 ? best : nominal;
    }

    private static double Correlation(byte[] source, long sourceStart, long candidate,
        int block, int channels, int overlapFrames, OverlapBuffer overlap, long outputStart, int stride)
    {
        double product = 0;
        double sourcePower = 0;
        double outputPower = 0;
        for (int i = 0; i < overlapFrames; i += stride)
        {
            int offset = checked((int)(candidate + i - sourceStart) * block);
            double sourceSample = 0;
            for (int channel = 0; channel < channels; channel++)
                sourceSample += BitConverter.ToInt16(source, offset + channel * 2) / 32768d;
            sourceSample /= channels;
            double outputSample = overlap.GetAverage(outputStart + i);
            product += sourceSample * outputSample;
            sourcePower += sourceSample * sourceSample;
            outputPower += outputSample * outputSample;
        }
        if (sourcePower < 1e-12 || outputPower < 1e-12) return double.NegativeInfinity;
        return product / Math.Sqrt(sourcePower * outputPower);
    }

    private static void ReadExactly(Stream input, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = input.Read(buffer, offset, count - offset);
            if (read == 0) throw new InvalidDataException("Audio ended inside an effect grain.");
            offset += read;
        }
    }

    private sealed class OverlapBuffer(int capacity, int channels)
    {
        private readonly double[] samples = new double[checked(capacity * channels)];
        private readonly double[] weights = new double[capacity];
        private long firstFrame;

        internal void AddFrame(long outputFrame, double[] frame, double weight)
        {
            if (outputFrame < firstFrame || outputFrame >= firstFrame + capacity)
                throw new InvalidDataException("Effect overlap exceeded its bounded buffer.");
            int slot = (int)(outputFrame % capacity);
            weights[slot] += weight;
            int offset = slot * channels;
            for (int channel = 0; channel < channels; channel++)
                samples[offset + channel] += frame[channel] * weight;
        }

        internal double GetAverage(long outputFrame)
        {
            if (outputFrame < firstFrame || outputFrame >= firstFrame + capacity)
                throw new InvalidDataException("Effect correlation exceeded its bounded buffer.");
            int slot = (int)(outputFrame % capacity);
            double weight = weights[slot];
            if (weight <= double.Epsilon) return 0;
            int offset = slot * channels;
            double total = 0;
            for (int channel = 0; channel < channels; channel++)
                total += samples[offset + channel] / weight;
            return total / channels;
        }

        internal void FlushUntil(long exclusiveFrame, EffectSampleWriter writer)
        {
            if (exclusiveFrame < firstFrame || exclusiveFrame > firstFrame + capacity)
                throw new InvalidDataException("Invalid effect overlap flush boundary.");
            var frame = new double[channels];
            while (firstFrame < exclusiveFrame)
            {
                int slot = (int)(firstFrame % capacity);
                double weight = weights[slot];
                if (weight <= double.Epsilon)
                    throw new InvalidDataException("Effect processing left an uncovered output frame.");
                int offset = slot * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    frame[channel] = samples[offset + channel] / weight;
                    samples[offset + channel] = 0;
                }
                weights[slot] = 0;
                writer.WriteFrame(frame);
                firstFrame++;
            }
        }
    }

    private sealed class EffectSampleWriter
    {
        private readonly WaveFileWriter output;
        private readonly int channels;
        private readonly double[] low;
        private readonly double alpha;
        private readonly double lowGain;
        private readonly double environmentGain;
        private readonly double headroom;
        private readonly double left;
        private readonly double right;
        private readonly byte[] buffer;
        private int buffered;
        private ulong noiseState;

        internal EffectSampleWriter(WaveFileWriter output, WaveFormat format, PlaybackEffectSelection selection)
        {
            this.output = output;
            channels = format.Channels;
            low = new double[channels];
            alpha = 1 - Math.Exp(-2 * Math.PI * 600 / format.SampleRate);
            lowGain = Math.Pow(10, selection.EqDecibels / 20);
            environmentGain = selection.EnvironmentEnabled
                ? Math.Pow(10, selection.EnvironmentRelativeDecibels / 20)
                : 0;
            headroom = 1 / (1 + Math.Abs(lowGain - 1) + environmentGain);
            left = Math.Min(1, 1 - selection.StereoBalance);
            right = Math.Min(1, 1 + selection.StereoBalance);
            noiseState = Seed(selection);
            buffer = new byte[format.BlockAlign * Math.Max(1, 8192 / format.BlockAlign)];
        }

        internal void WriteFrame(double[] frame)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                double source = frame[channel];
                low[channel] += alpha * (source - low[channel]);
                double equalized = source + (lowGain - 1) * low[channel];
                double balance = channels == 2 ? (channel == 0 ? left : right) : 1;
                double environment = environmentGain == 0 ? 0 : NextNoise(ref noiseState) * environmentGain;
                double mixed = equalized * balance + environment;
                short sample = (short)Math.Clamp(Math.Round(mixed * headroom * 32767), -32768, 32767);
                BitConverter.TryWriteBytes(buffer.AsSpan(buffered, 2), sample);
                buffered += 2;
            }
            if (buffered == buffer.Length) Flush();
        }

        internal void Complete() => Flush();

        private void Flush()
        {
            if (buffered == 0) return;
            output.Write(buffer, 0, buffered);
            buffered = 0;
        }
    }

    private static ulong Seed(PlaybackEffectSelection selection)
    {
        ulong seed = unchecked((ulong)selection.EffectSeed) ^ 0xD1B54A32D192ED03UL;
        seed ^= unchecked((ulong)BitConverter.DoubleToInt64Bits(selection.EnvironmentRelativeDecibels));
        seed ^= unchecked((ulong)BitConverter.DoubleToInt64Bits(selection.EqDecibels));
        seed ^= unchecked((ulong)BitConverter.DoubleToInt64Bits(selection.StereoBalance));
        return seed == 0 ? 1 : seed;
    }

    private static double NextNoise(ref ulong state)
    {
        unchecked
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            ulong value = state * 2685821657736338717UL;
            return ((value >> 11) * (1.0 / (1UL << 53))) * 2 - 1;
        }
    }
}
