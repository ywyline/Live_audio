using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Infrastructure.Tts;

public sealed class VieNeuTtsProvider : ITtsProvider, IDisposable
{
    private const string ModelId = "vieneu-v3-turbo";
    private const int MaxJsonBytes = 256 * 1024;
    private readonly VieNeuTtsOptions _options;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _synthesisGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public string EngineId => _options.EngineId;
    public EngineRevision Revision { get; }

    public VieNeuTtsProvider(VieNeuTtsOptions options, EngineRevision revision)
        : this(options, revision, null) { }

    internal VieNeuTtsProvider(VieNeuTtsOptions options, EngineRevision revision, HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.ValidateAndCopy();
        if (revision.Value < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<TtsHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CreateDeadline(cancellationToken);
            using var health = await GetJsonAsync("health", deadline.Token);
            var root = health.RootElement;
            if (root.GetProperty("status").GetString() != "ok")
                return new(TtsHealthStatus.Unavailable, EngineId, "本机语音引擎尚未就绪。");
            if (root.GetProperty("sample_rate").GetInt32() != PcmWaveWriter.SampleRate)
                return new(TtsHealthStatus.Unsupported, EngineId, "本机引擎的采样率不受支持。");
            await VerifyModelAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return new(TtsHealthStatus.Healthy, EngineId, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsExpected(exception))
        {
            var failure = DescribeFailure(exception);
            return new(failure.Status == OperationStatus.Unsupported ? TtsHealthStatus.Unsupported : TtsHealthStatus.Unavailable,
                EngineId, failure.Detail);
        }
    }

    public async Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CreateDeadline(cancellationToken);
            using var document = await GetJsonAsync("v1/voices", deadline.Token);
            var voices = new List<TtsVoice>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var name = item.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !ids.Add(id) || voices.Count >= 1024)
                    throw new InvalidDataException();
                voices.Add(new TtsVoice(id, "vi-VN", name));
            }
            if (voices.Count == 0) throw new InvalidDataException();
            deadline.Token.ThrowIfCancellationRequested();
            return new(OperationStatus.Succeeded, voices.AsReadOnly());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(OperationStatus.Cancelled, null, "音色查询已取消。");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            var failure = DescribeFailure(exception);
            return new(failure.Status, null, failure.Detail);
        }
    }

    public async Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gateHeld = false;
        string? temporaryPath = null;
        string? finalPath = null;
        var committed = false;
        TtsSynthesisOutcome outcome;
        try
        {
            using var deadline = CreateDeadline(cancellationToken);
            var validation = ValidateRequest(request);
            if (validation is not null) return validation;
            gateHeld = await _synthesisGate.WaitAsync(0, deadline.Token);
            if (!gateHeld) return new(OperationStatus.Failed, null, "本机语音合成正在进行，请稍后重试。");

            await VerifyModelAsync(deadline.Token);
            using var message = CreateMessage(HttpMethod.Post, "v1/audio/speech");
            message.Content = JsonContent.Create(new
            {
                model = ModelId,
                input = request.Text.Normalize(NormalizationForm.FormC),
                voice = request.VoiceId.Normalize(NormalizationForm.FormC),
                response_format = "pcm",
                stream_format = "audio",
                sample_rate = PcmWaveWriter.SampleRate
            });
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            EnsureSuccess(response);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            if (!string.Equals(mediaType, "audio/pcm", StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException(OperationStatus.Unsupported, "本机引擎返回的音频格式不受支持。");
            if (!response.Headers.TryGetValues("X-Sample-Rate", out var rates))
                throw new InvalidDataException();
            var rateValues = rates.ToArray();
            if (rateValues.Length != 1 || !int.TryParse(rateValues[0], out var sampleRate) || sampleRate <= 0)
                throw new InvalidDataException();
            if (sampleRate != PcmWaveWriter.SampleRate)
                throw new ProtocolException(OperationStatus.Unsupported, "本机引擎返回的采样率不受支持。");
            if (response.Content.Headers.ContentLength is long contentLength &&
                (contentLength <= 0 || contentLength % 2 != 0 || contentLength > _options.MaxAudioBytes))
                throw new InvalidDataException();

            Directory.CreateDirectory(_options.OutputDirectory);
            var name = Guid.NewGuid().ToString("N");
            temporaryPath = Path.Combine(_options.OutputDirectory, name + ".part");
            finalPath = Path.Combine(_options.OutputDirectory, name + ".wav");
            long bytes;
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous))
            {
                bytes = await PcmWaveWriter.WriteAsync(input, output, _options.MaxAudioBytes, deadline.Token);
            }
            if (response.Content.Headers.ContentLength is long expectedBytes && expectedBytes != bytes)
                throw new InvalidDataException();
            deadline.Token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath);
            committed = true;
            deadline.Token.ThrowIfCancellationRequested();
            var asset = new LocalAudioAsset(finalPath, "wav", EngineId,
                TimeSpan.FromSeconds(bytes / (PcmWaveWriter.SampleRate * 2d)), PcmWaveWriter.SampleRate);
            outcome = new(OperationStatus.Succeeded, new TtsSynthesisResult(asset, EngineId, Revision), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = new(OperationStatus.Cancelled, null, "语音合成已取消。");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            var failure = DescribeFailure(exception);
            outcome = new(failure.Status, null, failure.Detail);
        }
        finally
        {
            if (gateHeld) _synthesisGate.Release();
        }

        // Only paths created by this request are eligible for cleanup.
        try
        {
            if (temporaryPath is not null) File.Delete(temporaryPath);
            if (committed && !outcome.IsReady && finalPath is not null) File.Delete(finalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(OperationStatus.Failed, null, "语音请求未完成，临时音频清理失败，请检查输出目录权限。");
        }
        return outcome;
    }

    private TtsSynthesisOutcome? ValidateRequest(TtsSynthesisRequest request)
    {
        if (request.EngineRevision != Revision)
            return new(OperationStatus.Cancelled, null, "旧引擎版本的合成请求已忽略。");
        if (request.Language is not ("vi" or "vi-VN") || request.Rate != 1 || request.Pitch != 0 || request.Volume != 1)
            return new(OperationStatus.Unsupported, null, "此引擎当前仅支持越南语及原始语速、音调和音量。");
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 20_000 ||
            string.IsNullOrWhiteSpace(request.VoiceId) || request.VoiceId.Length > 256)
            return new(OperationStatus.Failed, null, "稿件或音色为空，或超过长度上限。");
        // Normalize also validates UTF-16 before any network request.
        _ = request.Text.Normalize(NormalizationForm.FormC);
        _ = request.VoiceId.Normalize(NormalizationForm.FormC);
        return null;
    }

    private CancellationTokenSource CreateDeadline(CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
        deadline.CancelAfter(_options.RequestTimeout);
        return deadline;
    }

    private HttpRequestMessage CreateMessage(HttpMethod method, string relativePath)
    {
        var message = new HttpRequestMessage(method, new Uri(_options.Endpoint, relativePath));
        if (_options.ApiKey is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        return message;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = CreateMessage(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength > MaxJsonBytes) throw new InvalidDataException();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            if (bytes.Length + count > MaxJsonBytes) throw new InvalidDataException();
            bytes.Write(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }

    private async Task VerifyModelAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("v1/models", cancellationToken);
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == ModelId) return;
        }
        throw new ProtocolException(OperationStatus.Unsupported, "本机服务未提供已适配的 VieNeu 模型。");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var status = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
            ? OperationStatus.Unsupported : OperationStatus.Failed;
        var detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "本机语音服务鉴权失败，请检查密钥配置。",
            HttpStatusCode.TooManyRequests => "本机语音服务繁忙，请稍后重试。",
            _ => $"本机语音服务返回 HTTP {(int)response.StatusCode}。"
        };
        throw new ProtocolException(status, detail);
    }

    private static bool IsExpected(Exception exception) => exception is
        ProtocolException or HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        OperationCanceledException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException;

    private static (OperationStatus Status, string Detail) DescribeFailure(Exception exception) => exception switch
    {
        ProtocolException protocol => (protocol.Status, protocol.Message),
        OperationCanceledException => (OperationStatus.Failed, "本机语音请求超时或服务已关闭。"),
        HttpRequestException => (OperationStatus.Failed, "无法连接或读取本机语音服务，请检查服务状态。"),
        UnauthorizedAccessException => (OperationStatus.Failed, "无法访问选定的音频输出目录。"),
        _ => (OperationStatus.Failed, "本机语音响应或音频文件无效，请检查服务与输出目录。")
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Keep synchronization primitives alive for in-flight operations to unwind safely.
            _lifetime.Cancel();
            _client.Dispose();
        }
    }

    private sealed class ProtocolException(OperationStatus status, string detail) : Exception(detail)
    {
        public OperationStatus Status { get; } = status;
    }
}
