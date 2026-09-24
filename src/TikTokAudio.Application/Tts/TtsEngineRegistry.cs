using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tts;

public sealed record TtsEngineConfiguration
{
    public string EngineId { get; }
    public ITtsProvider Provider { get; }
    public string? DefaultVoiceId { get; }

    public TtsEngineConfiguration(string engineId, ITtsProvider provider, string? defaultVoiceId = null)
    {
        if (string.IsNullOrWhiteSpace(engineId)) throw new ArgumentException("引擎标识不能为空。", nameof(engineId));
        ArgumentNullException.ThrowIfNull(provider);
        if (!string.Equals(engineId, provider.EngineId, StringComparison.Ordinal))
            throw new ArgumentException("配置标识必须与引擎提供方标识一致。", nameof(engineId));
        if (defaultVoiceId is not null && string.IsNullOrWhiteSpace(defaultVoiceId))
            throw new ArgumentException("默认音色不能为空白。", nameof(defaultVoiceId));
        EngineId = engineId;
        Provider = provider;
        DefaultVoiceId = defaultVoiceId;
    }
}
public sealed record TtsEngineSnapshot(string EngineId, EngineRevision Revision, string? DefaultVoiceId, bool IsPlaybackLocked);

public sealed class TtsEngineRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TtsEngineConfiguration> _configurations;
    private TtsEngineConfiguration _active;
    private EngineRevision _revision;
    private int _playbackLocks;

    public TtsEngineRegistry(IEnumerable<TtsEngineConfiguration> configurations, string initialEngineId)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        _configurations = configurations.ToDictionary(x => x.EngineId, StringComparer.Ordinal);
        if (_configurations.Count == 0) throw new ArgumentException("至少需要配置一个语音引擎。", nameof(configurations));
        if (!_configurations.TryGetValue(initialEngineId, out _active!))
            throw new ArgumentException("初始语音引擎未在配置中注册。", nameof(initialEngineId));
        _revision = new EngineRevision(0);
    }

    public TtsEngineSnapshot Snapshot
    {
        get { lock (_gate) return ToSnapshot(); }
    }

    public ITtsProvider ActiveProvider
    {
        get { lock (_gate) return _active.Provider; }
    }

    public TtsEngineSnapshot BeginPlaybackUse()
    {
        lock (_gate)
        {
            _playbackLocks++;
            return ToSnapshot();
        }
    }

    public void EndPlaybackUse()
    {
        lock (_gate)
        {
            if (_playbackLocks == 0) throw new InvalidOperationException("没有可结束的播放占用。");
            _playbackLocks--;
        }
    }

    public OperationResult<TtsEngineSnapshot> Switch(string engineId)
    {
        if (string.IsNullOrWhiteSpace(engineId)) return new(OperationStatus.Failed, null, "引擎标识不能为空。");
        lock (_gate)
        {
            if (!_configurations.TryGetValue(engineId, out var next))
                return new(OperationStatus.Unsupported, null, "请求的语音引擎未配置。");
            if (_playbackLocks > 0)
                return new(OperationStatus.Unknown, null, "当前音频尚未播放完毕，暂不能切换语音引擎。");
            if (ReferenceEquals(next, _active)) return new(OperationStatus.Succeeded, ToSnapshot(), null);
            _active = next;
            _revision = new EngineRevision(checked(_revision.Value + 1));
            return new(OperationStatus.Succeeded, ToSnapshot(), null);
        }
    }

    public bool TryGet(string engineId, out TtsEngineConfiguration? configuration)
    {
        lock (_gate) return _configurations.TryGetValue(engineId, out configuration);
    }

    private TtsEngineSnapshot ToSnapshot() => new(_active.EngineId, _revision, _active.DefaultVoiceId, _playbackLocks > 0);
}


