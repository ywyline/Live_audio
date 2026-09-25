using System.Text.Json;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Tts;

/// <summary>Loads only local, independently deployed TTS provider definitions.</summary>
public sealed class TtsEngineConfigurationLoader
{
    private const long MaxConfigurationBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16
    };

    public async Task<OperationResult<TtsEngineRegistry>> LoadAsync(string configurationPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configurationPath) || !Path.IsPathFullyQualified(configurationPath))
                return new(OperationStatus.Failed, null, "TTS 配置路径必须是绝对路径。");

            var fullPath = Path.GetFullPath(configurationPath);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return new(OperationStatus.Failed, null, "TTS 配置文件不存在。");
            if (info.Length <= 0 || info.Length > MaxConfigurationBytes)
                return new(OperationStatus.Failed, null, "TTS 配置文件大小无效。");

            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<TtsEngineConfigurationDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document is null || document.Engines is null || document.Engines.Count == 0 ||
                string.IsNullOrWhiteSpace(document.InitialEngineId))
                return new(OperationStatus.Failed, null, "TTS 配置必须包含初始引擎和至少一个引擎。");

            var configurations = new List<TtsEngineConfiguration>(document.Engines.Count);
            foreach (var entry in document.Engines)
            {
                if (!string.Equals(entry.Provider, "vieneu", StringComparison.OrdinalIgnoreCase))
                    return new(OperationStatus.Unsupported, null, "TTS 配置包含当前未适配的 provider。");
                if (entry.Endpoint is null || string.IsNullOrWhiteSpace(entry.EngineId) ||
                    string.IsNullOrWhiteSpace(entry.OutputDirectory))
                    return new(OperationStatus.Failed, null, "TTS 引擎配置缺少标识、地址或输出目录。");

                var options = new VieNeuTtsOptions
                {
                    EngineId = entry.EngineId,
                    Endpoint = entry.Endpoint,
                    OutputDirectory = entry.OutputDirectory,
                    RequestTimeout = TimeSpan.FromSeconds(entry.RequestTimeoutSeconds),
                    MaxAudioBytes = entry.MaxAudioBytes
                };
                var provider = new VieNeuTtsProvider(options, EngineRevision.Initial);
                configurations.Add(new TtsEngineConfiguration(entry.EngineId, provider, entry.DefaultVoiceId));
            }

            return new(OperationStatus.Succeeded,
                new TtsEngineRegistry(configurations, document.InitialEngineId), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(OperationStatus.Cancelled, null, "TTS 配置读取已取消。");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or
            InvalidOperationException or FormatException or OverflowException)
        {
            return new(OperationStatus.Failed, null, "TTS 配置无效或无法读取。");
        }
    }
}

public sealed class TtsEngineConfigurationDocument
{
    public string? InitialEngineId { get; init; }
    public List<TtsEngineConfigurationEntry>? Engines { get; init; }
}

public sealed class TtsEngineConfigurationEntry
{
    public string? EngineId { get; init; }
    public string Provider { get; init; } = "vieneu";
    public Uri? Endpoint { get; init; }
    public string? OutputDirectory { get; init; }
    public string? DefaultVoiceId { get; init; }
    public double RequestTimeoutSeconds { get; init; } = 60;
    public int MaxAudioBytes { get; init; } = 32 * 1024 * 1024;
}


