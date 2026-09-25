using System.Net;

namespace TikTokAudio.Infrastructure.Tts;

public sealed class VieNeuTtsOptions
{
    public string EngineId { get; init; } = "vieneu-v3-turbo";
    public required Uri Endpoint { get; init; }
    public required string OutputDirectory { get; init; }
    public string? ApiKey { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxAudioBytes { get; init; } = 32 * 1024 * 1024;

    internal VieNeuTtsOptions ValidateAndCopy()
    {
        if (string.IsNullOrWhiteSpace(EngineId) || EngineId.Length > 128 || EngineId.Any(char.IsControl))
        {
            throw new ArgumentException("TTS ????????????", nameof(EngineId));
        }
        if (Endpoint is null || !Endpoint.IsAbsoluteUri ||
            (Endpoint.Scheme != Uri.UriSchemeHttp && Endpoint.Scheme != Uri.UriSchemeHttps) ||
            Endpoint.UserInfo.Length != 0 || Endpoint.Query.Length != 0 || Endpoint.Fragment.Length != 0 ||
            Endpoint.AbsolutePath != "/")
        {
            throw new ArgumentException("TTS 地址必须是无路径、凭据或查询参数的本机 HTTP 地址。", nameof(Endpoint));
        }

        var host = Endpoint.DnsSafeHost;
        var isLocalhost = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!isLocalhost && (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address)))
        {
            throw new ArgumentException("TTS 只允许本机回环地址。", nameof(Endpoint));
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory))
        {
            throw new ArgumentException("音频输出目录必须是选定的绝对路径。", nameof(OutputDirectory));
        }
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentException("TTS 超时须大于零且不超过一小时。", nameof(RequestTimeout));
        }
        if (MaxAudioBytes < 2 || MaxAudioBytes > 256 * 1024 * 1024)
        {
            throw new ArgumentException("音频大小上限须为 2 字节至 256 MiB。", nameof(MaxAudioBytes));
        }
        if (ApiKey is not null && (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsControl) || ApiKey.Length > 4096))
        {
            throw new ArgumentException("本机服务密钥为空或格式无效。", nameof(ApiKey));
        }

        return new VieNeuTtsOptions
        {
            EngineId = EngineId,
            // Avoid DNS/proxy resolution for the localhost alias.
            Endpoint = isLocalhost ? new UriBuilder(Endpoint) { Host = "127.0.0.1" }.Uri : Endpoint,
            OutputDirectory = Path.GetFullPath(OutputDirectory),
            ApiKey = ApiKey,
            RequestTimeout = RequestTimeout,
            MaxAudioBytes = MaxAudioBytes
        };
    }
}
