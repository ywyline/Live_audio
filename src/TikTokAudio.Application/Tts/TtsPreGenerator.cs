using System.Text;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tts;

public sealed class TtsPreGenerator
{
    private readonly ITtsProvider _provider;
    private readonly ITtsAudioCache _cache;
    private readonly int _maxSegmentsPerBatch;
    private readonly string _engineId;
    private readonly EngineRevision _revision;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TtsPreparationSnapshot _snapshot = new(TtsPreparationState.Idle, Array.Empty<PreparedTtsSegment>(), 0, null);

    public TtsPreGenerator(ITtsProvider provider, ITtsAudioCache cache, int maxSegmentsPerBatch)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(cache);
        if (maxSegmentsPerBatch <= 0) throw new ArgumentOutOfRangeException(nameof(maxSegmentsPerBatch));
        _provider = provider;
        _cache = cache;
        _maxSegmentsPerBatch = maxSegmentsPerBatch;
        _engineId = provider.EngineId;
        _revision = provider.Revision;
    }

    public TtsPreparationSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public async Task<TtsPreparationResult> PrepareAsync(
        TtsScriptDocument document, int startIndex, int count, TtsGenerationSettings settings,
        CancellationToken cancellationToken)
    {
        var held = false;
        var required = 0;
        try
        {
            held = await _gate.WaitAsync(0, cancellationToken);
            if (!held) return new(OperationStatus.Failed, Snapshot, "已有预生成任务正在执行。");
            Publish(TtsPreparationState.WaitingForSynthesis, [], 0);
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(settings);
            if (document.Segments is null || startIndex < 0 || startIndex >= document.Segments.Count ||
                count <= 0 || count > _maxSegmentsPerBatch || count > document.Segments.Count - startIndex)
                return Finish(OperationStatus.Failed, TtsPreparationState.Failed, required, "分段范围无效或超过本批次上限。");
            if (settings.Identity is null || settings.Identity.EngineId != _engineId)
                return Finish(OperationStatus.Failed, TtsPreparationState.Failed, required, "生成配置与实际引擎不一致。");

            required = Math.Min(2, document.Segments.Count - startIndex);
            var selected = document.Segments.Skip(startIndex).Take(count).ToArray();
            // Preflight the whole batch before any cache or engine operation.
            var keys = selected.Select(segment => TtsCacheKey.Create(segment.Text, settings, document.SegmenterVersion)).ToArray();
            var prepared = new List<PreparedTtsSegment>(count);
            Publish(TtsPreparationState.WaitingForSynthesis, prepared, required);
            for (var index = 0; index < selected.Length; index++)
            {
                EnsureCurrent(cancellationToken);
                var asset = await PrepareAssetAsync(selected[index].Text, keys[index], settings, cancellationToken);
                if (asset.Status != OperationStatus.Succeeded || asset.Value is null)
                    return Finish(asset.Status == OperationStatus.Succeeded ? OperationStatus.Failed : asset.Status,
                        asset.Status == OperationStatus.Cancelled ? TtsPreparationState.Cancelled : TtsPreparationState.Failed,
                        required, asset.Detail ?? "音频尚未准备好，请重试。");
                EnsureCurrent(cancellationToken);
                prepared.Add(new(selected[index], asset.Value, keys[index]));
                Publish(prepared.Count >= required ? TtsPreparationState.Ready : TtsPreparationState.WaitingForSynthesis,
                    prepared, required);
            }
            EnsureCurrent(cancellationToken);
            return new(OperationStatus.Succeeded, Snapshot, null);
        }
        catch (OperationCanceledException)
        {
            return held
                ? Finish(OperationStatus.Cancelled, TtsPreparationState.Cancelled, required, "预生成已取消或引擎版本已过期。")
                : new(OperationStatus.Cancelled, Snapshot, "预生成请求已取消。");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            return held
                ? Finish(OperationStatus.Failed, TtsPreparationState.Failed, required, "预生成配置、缓存或音频无效。")
                : new(OperationStatus.Failed, Snapshot, "无法开始预生成。");
        }
        finally
        {
            if (held) _gate.Release();
        }
    }

    private async Task<OperationResult<LocalAudioAsset>> PrepareAssetAsync(
        string text, string key, TtsGenerationSettings settings, CancellationToken token)
    {
        var cached = await _cache.FindAsync(key, token);
        EnsureCurrent(token);
        if (cached.Status != OperationStatus.Succeeded) return cached;
        if (cached.Value is not null)
            return cached.Value.EngineId == _engineId
                ? cached : new(OperationStatus.Failed, null, "缓存引擎标识不一致。");

        LocalAudioAsset? generated = null;
        OperationResult cleanup = new(OperationStatus.Succeeded);
        OperationResult<LocalAudioAsset> result;
        try
        {
            EnsureCurrent(token);
            var outcome = await _provider.SynthesizeAsync(new(text.Normalize(NormalizationForm.FormC), settings.VoiceId, settings.Language,
                settings.Rate, settings.Pitch, settings.Volume, _revision), token);
            generated = outcome.Result?.Asset;
            EnsureCurrent(token);
            if (!outcome.IsReady)
                result = new(outcome.Status == OperationStatus.Succeeded ? OperationStatus.Failed : outcome.Status,
                    null, "本机合成未完成，请检查引擎后重试。");
            else if (outcome.Result!.EngineRevision != _revision || outcome.Result.EngineId != _engineId || generated!.EngineId != _engineId)
                result = new(OperationStatus.Cancelled, null, "忽略旧引擎或标识不一致的合成结果。");
            else
            {
                result = await _cache.StoreAsync(key, generated!, token);
                EnsureCurrent(token);
                if (result.Value is not null && result.Value.EngineId != _engineId)
                    result = new(OperationStatus.Failed, null, "缓存提交的引擎标识不一致。");
            }
        }
        catch (OperationCanceledException)
        {
            result = new(OperationStatus.Cancelled, null, "预生成已取消或引擎版本已过期。");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            result = new(OperationStatus.Failed, null, "生成音频或缓存提交失败。");
        }
        finally
        {
            // Cache owns validated staging paths; the application never deletes arbitrary provider paths.
            if (generated is not null) cleanup = await _cache.DiscardAsync(generated);
        }
        return cleanup.Status == OperationStatus.Succeeded
            ? result : new(OperationStatus.Failed, null, "生成临时文件清理失败，请检查目录权限。");
    }

    private void EnsureCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_provider.Revision != _revision || _provider.EngineId != _engineId)
            throw new OperationCanceledException("引擎版本已过期。");
    }

    private void Publish(TtsPreparationState state, IEnumerable<PreparedTtsSegment> segments, int required, string? detail = null) =>
        Volatile.Write(ref _snapshot, new(state, Array.AsReadOnly(segments.ToArray()), required, detail));

    private TtsPreparationResult Finish(OperationStatus status, TtsPreparationState state, int required, string detail)
    {
        Publish(state, [], required, detail);
        return new(status, Snapshot, detail);
    }
}
