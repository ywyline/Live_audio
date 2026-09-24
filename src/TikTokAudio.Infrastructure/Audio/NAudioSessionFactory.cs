using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Audio;

[SupportedOSPlatform("windows")]
public sealed class NAudioSessionFactory : IAudioSessionFactory
{
    private readonly AudioOutputOptions options;
    private readonly SemaphoreSlim capacity;

    public NAudioSessionFactory(AudioOutputOptions options)
    {
        AudioFilePreparation.ValidateOptions(options);
        this.options = options;
        capacity = new SemaphoreSlim(options.MaxPreparedSessions, options.MaxPreparedSessions);
    }

    public static IReadOnlyList<AudioOutputDevice> EnumerateDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioOutputDevice>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) result.Add(new AudioOutputDevice(device.ID, device.FriendlyName));
        }
        return result.AsReadOnly();
    }

    public async Task<IAudioPlaybackSession> PrepareAsync(AudioPlaybackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Asset);
        cancellationToken.ThrowIfCancellationRequested();
        if (!capacity.Wait(0)) throw new InvalidOperationException("The configured prepared-session limit is reached.");
        PreparedAudioFile? file = null;
        try
        {
            file = await AudioFilePreparation.PrepareAsync(options, request.Asset.Path, cancellationToken).ConfigureAwait(false);
            if (request.StartAt.SourceSampleOffset < 0 || request.StartAt.SourceSampleOffset > file.LengthSamples ||
                (request.StartAt.SampleRate is int rate && rate != file.SampleRate) ||
                (request.StartAt.SourceSampleOffset != 0 && request.StartAt.SampleRate is null))
                throw new ArgumentException("The source cursor is outside the file or uses a different sample rate.", nameof(request));
            PreparedAudioFile owned = file;
            IAudioPlaybackSession session = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prepared = new WasapiPlaybackSession(options, owned, request.StartAt.SourceSampleOffset, () => capacity.Release());
                if (cancellationToken.IsCancellationRequested)
                {
                    // Ownership transfers only after this delegate returns successfully.
                    prepared.DisposeWithoutCapacityRelease();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return (IAudioPlaybackSession)prepared;
            }, cancellationToken).ConfigureAwait(false);
            file = null;
            return session;
        }
        catch
        {
            try { file?.Dispose(); }
            finally { capacity.Release(); }
            throw;
        }
    }

    private sealed class WasapiPlaybackSession : IAudioPlaybackSession, IFadingAudioPlaybackSession
    {
        private readonly object gate = new();
        private readonly AudioOutputOptions options;
        private readonly PreparedAudioFile file;
        private readonly Action releaseCapacity;
        private readonly MMDevice device;
        private WasapiOut? output;
        private WaveFileReader? reader;
        private long cursor;
        private long outputStart;
        private long generation;
        private bool playing;
        private bool stopped;
        private bool disposed;
        private WasapiOut? fadedOutput;
        private float[]? volumesBeforeFade;

        public WasapiPlaybackSession(AudioOutputOptions options, PreparedAudioFile file, long start, Action releaseCapacity)
        {
            this.options = options;
            this.file = file;
            this.releaseCapacity = releaseCapacity;
            cursor = start;
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(options.DeviceId);
            try
            {
                if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
                    throw new InvalidOperationException("The selected output device is unavailable.");
                CreateOutput();
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }

        public int SampleRate => file.SampleRate;
        public long LengthSamples => file.LengthSamples;
        public event EventHandler<AudioSessionStoppedEventArgs>? Stopped;
        public long PositionSamples
        {
            get { lock (gate) { RefreshCursor(); return cursor; } }
        }

        public void Play()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (stopped) throw new InvalidOperationException("A stopped audio session cannot be restarted.");
                if (playing) return;
                if (cursor == LengthSamples)
                {
                    playing = true;
                    QueueStopped(generation, null);
                    return;
                }
                if (output is null) CreateOutput();
                output!.Play();
                playing = true;
            }
        }

        public async Task FadeOutAsync(CancellationToken cancellationToken)
        {
            WasapiOut ownedOutput;
            long ownedGeneration;
            float[] originalVolumes;
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!playing || stopped || output is null)
                    throw new InvalidOperationException("The stream is not playing.");
                ownedOutput = output;
                ownedGeneration = generation;
                RestoreVolume();
                originalVolumes = output.AudioStreamVolume.GetAllVolumes();
                fadedOutput = ownedOutput;
                volumesBeforeFade = originalVolumes;
            }
            try
            {
                await AudioFade.FadeOutAsync(gain =>
                {
                    lock (gate)
                    {
                        if (disposed || stopped || !playing || ownedGeneration != generation ||
                            !ReferenceEquals(output, ownedOutput))
                            throw new OperationCanceledException("The faded stream is no longer current.");
                        ownedOutput.AudioStreamVolume.SetAllVolumes(
                            Array.ConvertAll(originalVolumes, volume => volume * gain));
                    }
                }, Task.Delay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (gate)
                {
                    // A cancelled fade must not leave a still-running stream muted.
                    if (!disposed && !stopped && playing && ownedGeneration == generation &&
                        ReferenceEquals(output, ownedOutput))
                        ownedOutput.AudioStreamVolume.SetAllVolumes(originalVolumes);
                }
                throw;
            }
        }
        public void RestoreVolume()
        {
            lock (gate)
            {
                if (!disposed && !stopped && playing && output is not null &&
                    ReferenceEquals(output, fadedOutput) && volumesBeforeFade is not null)
                    output.AudioStreamVolume.SetAllVolumes(volumesBeforeFade);
                fadedOutput = null;
                volumesBeforeFade = null;
            }
        }

        public void Resume() => Play();

        public void Pause()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!playing || stopped) return;
                try { RefreshCursor(); }
                finally
                {
                    playing = false;
                    ReleaseOutput();
                }
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (disposed || stopped) return;
                try { RefreshCursor(); }
                finally
                {
                    stopped = true;
                    playing = false;
                    ReleaseOutput();
                }
            }
        }

        private void CreateOutput()
        {
            WaveFileReader? newReader = null;
            WasapiOut? newOutput = null;
            try
            {
                newReader = new WaveFileReader(file.Path);
                newReader.Position = checked(cursor * newReader.WaveFormat.BlockAlign);
                newOutput = new WasapiOut(device, AudioClientShareMode.Shared, true, options.LatencyMilliseconds);
                long currentGeneration = ++generation;
                newOutput.PlaybackStopped += (_, args) => QueueStopped(currentGeneration, args.Exception);
                newOutput.Init(newReader);
                outputStart = cursor;
                reader = newReader;
                output = newOutput;
            }
            catch
            {
                newOutput?.Dispose();
                newReader?.Dispose();
                throw;
            }
        }

        private void RefreshCursor()
        {
            if (!playing || output is null || stopped || disposed) return;
            // NAudio 2.2.1 GetPosition is the WASAPI device clock converted to
            // output-format bytes. Decoder Position includes prebuffered frames.

            WaveFormat format = output.OutputWaveFormat;
            long bytes = output.GetPosition();
            long rendered = (long)((decimal)bytes * SampleRate / format.AverageBytesPerSecond);
            cursor = Math.Clamp(Math.Max(cursor, outputStart + rendered), 0, LengthSamples);
        }

        private void QueueStopped(long callbackGeneration, Exception? error) =>
            ThreadPool.QueueUserWorkItem(_ => Complete(callbackGeneration, error));

        private void Complete(long callbackGeneration, Exception? error)
        {
            EventHandler<AudioSessionStoppedEventArgs>? handler;
            lock (gate)
            {
                if (disposed || stopped || !playing || callbackGeneration != generation) return;
                if (error is null) cursor = LengthSamples;
                else
                {
                    try { RefreshCursor(); }
                    catch (Exception cursorError) { error = new AggregateException(error, cursorError); }
                }
                playing = false;
                stopped = true;
                handler = Stopped;
            }
            handler?.Invoke(this, new AudioSessionStoppedEventArgs(error));
        }

        private void ReleaseOutput()
        {
            ++generation;
            WasapiOut? releasingOutput = output;
            WaveFileReader? releasingReader = reader;
            output = null;
            reader = null;
            try { releasingOutput?.Dispose(); }
            finally { releasingReader?.Dispose(); }
        }

        public void Dispose() => DisposeCore(true);
        internal void DisposeWithoutCapacityRelease() => DisposeCore(false);

        private void DisposeCore(bool release)
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                playing = false;
                stopped = true;
                try { ReleaseOutput(); }
                finally
                {
                    try { device.Dispose(); }
                    finally
                    {
                        try { file.Dispose(); }
                        finally { if (release) releaseCapacity(); }
                    }
                }
            }
        }
    }
}
