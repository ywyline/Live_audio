using System.Runtime.InteropServices;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Audio;

public sealed class SingleVoiceAudioOutput(IAudioSessionFactory factory) : IAudioOutput, IDisposable
{
    private readonly IAudioSessionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly object _gate = new();
    private IAudioPlaybackSession? _session;
    private CancellationTokenSource? _preparing;
    private CancellationTokenRegistration _playCancellation;
    private long _generation;
    private bool _disposed;
    private PlaybackState _state = PlaybackState.Idle;
    private AudioCursor? _lastCursor;

    public PlaybackState State { get { lock (_gate) return _state; } }
    public AudioCursor? CurrentCursor
    {
        get
        {
            lock (_gate)
            {
                if (_session is not null && _state != PlaybackState.Stopped)
                {
                    try { _lastCursor = Cursor(_session); }
                    catch (Exception e) when (IsExpected(e)) { _state = PlaybackState.Error; }
                }
                return _lastCursor;
            }
        }
    }

    // Success means playback started; natural completion is reflected by State/CurrentCursor.
    // Preparation runs outside the state lock so a Stop can invalidate it immediately.
    public async Task<OperationResult> PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested) return OperationResult.Cancelled();
        CancellationTokenSource cancellation;
        CancellationTokenSource? previous;
        long generation;
        lock (_gate)
        {
            if (_disposed) return OperationResult.Failed("音频输出已释放。");
            generation = ++_generation;
            previous = _preparing;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _preparing = cancellation;
        }
        CancelPreparation(previous);
        IAudioPlaybackSession? prepared = null;
        var installed = false;
        var replacing = false;
        try
        {
            prepared = await _factory.PrepareAsync(request, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || generation != _generation || cancellation.IsCancellationRequested)
                    return OperationResult.Cancelled("播放准备已取消或已被新请求替换。");
                if (prepared.SampleRate <= 0 || prepared.LengthSamples <= 0)
                    return OperationResult.Failed("音频没有可播放的源采样。");
                // Stop/dispose the previous voice before any new voice is started.
                replacing = true;
                ReleaseCurrent();
                _session = prepared;
                installed = true;
                prepared = null;
                _session.Stopped += OnStopped;
                _state = PlaybackState.BasePlaying;
                _lastCursor = Cursor(_session);
                _session.Play();
                var playing = _session;
                _playCancellation = cancellationToken.Register(() => CancelPlayback(playing));
                return cancellationToken.IsCancellationRequested
                    ? OperationResult.Cancelled() : OperationResult.Succeeded();
            }
        }
        catch (OperationCanceledException) { return OperationResult.Cancelled("播放准备已取消。"); }
        catch (Exception e) when (IsExpected(e))
        {
            lock (_gate)
            {
                if (generation != _generation || _disposed) return OperationResult.Cancelled();
                if (installed)
                {
                    try { ReleaseCurrent(); }
                    catch (Exception stopError) when (IsExpected(stopError))
                    { return OperationResult.Failed("播放启动及设备停止失败，需要重新选择音频设备。"); }
                    finally { _state = PlaybackState.Error; }
                }
                else if (replacing) _state = PlaybackState.Error;
                return OperationResult.Failed("音频准备或设备播放失败，请检查素材与所选设备。");
            }
        }
        finally
        {
            prepared?.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_preparing, cancellation)) _preparing = null;
            }
            cancellation.Dispose();
        }
    }

    public async Task<OperationResult> PauseAsync(CancellationToken cancellationToken)
    {
        IAudioPlaybackSession session;
        long generation;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested) return OperationResult.Cancelled();
            if (_disposed) return OperationResult.Failed("音频输出已释放。");
            if (_state == PlaybackState.Paused) return OperationResult.Succeeded();
            if (_state != PlaybackState.BasePlaying || _session is null)
                return OperationResult.Failed("当前没有正在播放的主语音。");
            session = _session;
            generation = _generation;
        }
        try
        {
            // Never retain the output lock across the fade: emergency Stop must stay immediate.
            if (session is IFadingAudioPlaybackSession fading)
                await fading.FadeOutAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || generation != _generation || !ReferenceEquals(session, _session) || cancellationToken.IsCancellationRequested)
                {
                    // A newer Play can still be preparing while this same old voice is audible.
                    if (ReferenceEquals(session, _session) && session is IFadingAudioPlaybackSession cancelledFade)
                        cancelledFade.RestoreVolume();
                    return OperationResult.Cancelled("暂停已取消或被新播放或停止取代。");
                }
                if (_state == PlaybackState.Paused) return OperationResult.Succeeded();
                if (_state != PlaybackState.BasePlaying)
                    return OperationResult.Failed("淡出期间音频已结束或发生故障。");
                session.Pause();
                _lastCursor = Cursor(session);
                _state = PlaybackState.Paused;
                return OperationResult.Succeeded();
            }
        }
        catch (OperationCanceledException) { return OperationResult.Cancelled("淡出已取消。"); }
        catch (Exception e) when (IsExpected(e))
        {
            lock (_gate)
            {
                if (_disposed || generation != _generation || !ReferenceEquals(session, _session))
                    return OperationResult.Cancelled();
                _state = PlaybackState.Error;
                return OperationResult.Failed("音频淡出或暂停失败。");
            }
        }
    }

    public Task<OperationResult> ResumeAsync(CancellationToken cancellationToken) => Change(cancellationToken, () =>
    {
        if (_state == PlaybackState.BasePlaying) return OperationResult.Succeeded();
        if (_state != PlaybackState.Paused || _session is null)
            return OperationResult.Failed("当前没有可恢复的主语音。");
        _state = PlaybackState.BasePlaying;
        _session.Resume();
        return OperationResult.Succeeded();
    });

    public Task<OperationResult> StopAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(OperationResult.Cancelled());
        CancellationTokenSource? pending;
        OperationResult result;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(OperationResult.Failed("音频输出已释放。"));
            ++_generation;
            pending = _preparing;
            _preparing = null;
            try { var located = ReleaseCurrent(); _state = PlaybackState.Stopped; result = OperationResult.Succeeded(located ? null : "设备未返回最终位置，保留最近源游标。"); }
            catch (Exception e) when (IsExpected(e))
            { _state = PlaybackState.Error; result = OperationResult.Failed("停止音频设备失败。"); }
        }
        CancelPreparation(pending);
        return Task.FromResult(result);
    }

    public Task<OperationResult> SetEnvironmentMixAsync(EnvironmentMixSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Change(cancellationToken, () => settings.Enabled
            ? OperationResult.Unsupported("环境音混音尚未启用。") : OperationResult.Succeeded());
    }

    private Task<OperationResult> Change(CancellationToken token, Func<OperationResult> action)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested) return Task.FromResult(OperationResult.Cancelled());
            if (_disposed) return Task.FromResult(OperationResult.Failed("音频输出已释放。"));
            try { return Task.FromResult(action()); }
            catch (Exception e) when (IsExpected(e))
            { _state = PlaybackState.Error; return Task.FromResult(OperationResult.Failed("音频设备操作失败。")); }
        }
    }

    private void OnStopped(object? sender, AudioSessionStoppedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _session)) return;
            var located = TryCaptureCursor(_session!);
            _state = args.Error is null && located ? PlaybackState.Idle : PlaybackState.Error;
            // Avoid disposing the driver on its own completion callback thread.
            // The completed session is released by the next Play, Stop or Dispose.
        }
    }

    private void CancelPlayback(IAudioPlaybackSession? playing)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(playing, _session)) return;
            try { ReleaseCurrent(); _state = PlaybackState.Stopped; }
            catch (Exception e) when (IsExpected(e)) { _state = PlaybackState.Error; }
        }
    }

    private bool ReleaseCurrent()
    {
        _playCancellation.Unregister();
        _playCancellation = default;
        if (_session is null) return true;
        var previous = _session;
        previous.Stopped -= OnStopped;
        TryCaptureCursor(previous);
        // Keep ownership if Stop fails; a later Stop/Dispose can retry it.
        previous.Stop();
        var located = TryCaptureCursor(previous);
        previous.Dispose();
        _session = null;
        return located;
    }

    private bool TryCaptureCursor(IAudioPlaybackSession session)
    {
        try { _lastCursor = Cursor(session); return true; }
        catch (Exception e) when (IsExpected(e)) { return false; }
    }

    private static AudioCursor Cursor(IAudioPlaybackSession session) =>
        new(Math.Clamp(session.PositionSamples, 0, session.LengthSamples), session.SampleRate);

    private static void CancelPreparation(CancellationTokenSource? pending)
    {
        // Completion may dispose the source between detaching and cancellation.
        try { pending?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static bool IsExpected(Exception e) => e is IOException or InvalidDataException or NAudio.MmException or ArgumentException or InvalidOperationException
        or NotSupportedException or UnauthorizedAccessException or COMException;

    public void Dispose()
    {
        CancellationTokenSource? pending = null;
        try
        {
            lock (_gate)
            {
                if (_disposed && _session is null) return;
                _disposed = true;
                ++_generation;
                pending = _preparing;
                _preparing = null;
                try { ReleaseCurrent(); _state = PlaybackState.Stopped; }
                catch (Exception e) when (IsExpected(e))
                {
                    _state = PlaybackState.Error;
                    // Driver disposal is still required when Stop or position sampling fails.
                    // Retain ownership if Dispose itself fails, so a later call can retry.
                    _session?.Dispose();
                    _session = null;
                    throw;
                }
            }
        }
        finally { CancelPreparation(pending); }
    }
}
