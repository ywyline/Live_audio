using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Tts;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class VieNeuTtsProviderTests : IDisposable
{
    private const string Engine = "vieneu-v3-turbo";
    private const string Secret = "synthetic-test-key";
    private static readonly EngineRevision Revision = new(7);
    private readonly string outputDirectory = Path.Combine(Path.GetTempPath(), "LiveAudio-T041", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SynthesisPreservesVietnameseAndReturnsValidatedLocalWave()
    {
        var text = "Xin cha\u0300o, cảm ơn bạn!";
        byte[] pcm = [0, 0, 10, 0, 20, 0, 30, 0];
        var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Secret, request.Headers.Authorization?.Parameter);
            if (request.RequestUri.AbsolutePath == "/v1/models")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Models();
            }

            Assert.Equal("/v1/audio/speech", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = json.RootElement;
            Assert.Equal(text.Normalize(NormalizationForm.FormC), body.GetProperty("input").GetString());
            Assert.Equal("Hải Đăng", body.GetProperty("voice").GetString());
            Assert.Equal(Engine, body.GetProperty("model").GetString());
            Assert.Equal("pcm", body.GetProperty("response_format").GetString());
            Assert.Equal(48000, body.GetProperty("sample_rate").GetInt32());
            return Pcm(pcm);
        });
        using var provider = Create(handler, apiKey: Secret);

        var outcome = await provider.SynthesizeAsync(Request() with { Text = text }, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, outcome.Status);
        Assert.True(outcome.IsReady);
        var result = Assert.IsType<TtsSynthesisResult>(outcome.Result);
        Assert.Equal(Engine, result.EngineId);
        Assert.Equal(Revision, result.EngineRevision);
        Assert.Equal(Engine, provider.EngineId);
        Assert.Equal(Revision, provider.Revision);
        Assert.Equal(Engine, result.Asset.EngineId);
        Assert.Equal(48000, result.Asset.SampleRate);
        Assert.Equal("wav", result.Asset.Format.ToLowerInvariant());
        Assert.Equal(TimeSpan.FromSeconds(4d / 48000), result.Asset.Duration);
        Assert.Equal(Path.GetFullPath(outputDirectory), Path.GetDirectoryName(result.Asset.Path));
        Assert.Equal(".wav", Path.GetExtension(result.Asset.Path));
        Assert.Single(Directory.GetFiles(outputDirectory));
        using var wave = new BinaryReader(File.OpenRead(result.Asset.Path));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wave.ReadBytes(4)));
        Assert.Equal(36 + pcm.Length, wave.ReadInt32());
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wave.ReadBytes(8)));
        Assert.Equal(16, wave.ReadInt32());
        Assert.Equal(1, wave.ReadInt16());
        Assert.Equal(1, wave.ReadInt16());
        Assert.Equal(48000, wave.ReadInt32());
        Assert.Equal(96000, wave.ReadInt32());
        Assert.Equal(2, wave.ReadInt16());
        Assert.Equal(16, wave.ReadInt16());
        Assert.Equal("data", Encoding.ASCII.GetString(wave.ReadBytes(4)));
        Assert.Equal(pcm.Length, wave.ReadInt32());
        Assert.Equal(pcm, wave.ReadBytes(pcm.Length));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task HealthRequiresExpectedModelAndSampleRate()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/health" => Json("""{"status":"ok","backend":"onnx","sample_rate":48000}"""),
                "/v1/models" => Models(),
                _ => throw new InvalidOperationException("Unexpected route")
            });
        });
        using var provider = Create(handler);

        var health = await provider.GetHealthAsync(CancellationToken.None);

        Assert.Equal(TtsHealthStatus.Healthy, health.Status);
        Assert.Equal(Engine, health.EngineId);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task VoicesPreserveUnicodeAndDoNotInventLanguageSupport()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/v1/voices", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"data":[{"id":"Hải Đăng","name":"Hải Đăng"},{"id":"Mai","name":"Mai dịu dàng"}]}"""));
        });
        using var provider = Create(handler);

        var voices = await provider.GetVoicesAsync(CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, voices.Status);
        Assert.Collection(voices.Value!,
            voice => Assert.Equal(new TtsVoice("Hải Đăng", "vi-VN", "Hải Đăng"), voice),
            voice => Assert.Equal(new TtsVoice("Mai", "vi-VN", "Mai dịu dàng"), voice));
    }

    [Fact]
    public async Task OldRevisionNeverContactsTheEngine()
    {
        var handler = UnexpectedHandler();
        using var provider = Create(handler);

        var outcome = await provider.SynthesizeAsync(Request() with { EngineRevision = new(6) }, CancellationToken.None);

        Assert.Equal(OperationStatus.Cancelled, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.Equal(0, handler.RequestCount);
        AssertNoAssets();
    }

    [Theory]
    [InlineData("en-US", 1, 0, 1)]
    [InlineData("vi-VN", 1.2, 0, 1)]
    [InlineData("vi-VN", 1, 2, 1)]
    [InlineData("vi-VN", 1, 0, 0.5)]
    [InlineData("vi-VN", double.NaN, 0, 1)]
    public async Task UnsupportedParametersNeverContactTheEngine(string language, double rate, double pitch, double volume)
    {
        var handler = UnexpectedHandler();
        using var provider = Create(handler);

        var outcome = await provider.SynthesizeAsync(Request() with
        {
            Language = language, Rate = rate, Pitch = pitch, Volume = volume
        }, CancellationToken.None);

        Assert.Equal(OperationStatus.Unsupported, outcome.Status);
        Assert.Equal(0, handler.RequestCount);
        AssertNoAssets();
    }

    [Theory]
    [InlineData(404, OperationStatus.Unsupported)]
    [InlineData(405, OperationStatus.Unsupported)]
    [InlineData(501, OperationStatus.Unsupported)]
    [InlineData(401, OperationStatus.Failed)]
    [InlineData(429, OperationStatus.Failed)]
    [InlineData(500, OperationStatus.Failed)]
    public async Task HttpErrorsAreExplicitAndDoNotLeakSensitiveBodies(int status, OperationStatus expected)
    {
        const string privateText = "private-synthetic-input";
        var handler = SynthesisHandler(() => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent($"{Secret} {privateText} engine-traceback")
        });
        using var provider = Create(handler, apiKey: Secret);

        var result = await provider.SynthesizeAsync(Request() with { Text = privateText }, CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Result);
        Assert.DoesNotContain(Secret, result.Detail ?? "");
        Assert.DoesNotContain(privateText, result.Detail ?? "");
        Assert.DoesNotContain("engine-traceback", result.Detail ?? "");
        AssertNoAssets();
    }

    [Fact]
    public async Task RedirectIsNotTreatedAsAudioOrFollowed()
    {
        var handler = SynthesisHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.invalid/collect");
            return response;
        });
        using var provider = Create(handler, apiKey: Secret);

        var result = await provider.SynthesizeAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(2, handler.RequestCount);
        AssertNoAssets();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(18)]
    public async Task EmptyOddOrOversizedAudioNeverCreatesAnAsset(int length)
    {
        var handler = SynthesisHandler(() => Pcm(new byte[length]));
        using var provider = Create(handler, maxBytes: 16);

        var result = await provider.SynthesizeAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Null(result.Result);
        AssertNoAssets();
    }

    [Theory]
    [InlineData("audio/wav", "48000", OperationStatus.Unsupported)]
    [InlineData("application/json", "48000", OperationStatus.Failed)]
    [InlineData("audio/pcm", "24000", OperationStatus.Unsupported)]
    [InlineData("audio/pcm", null, OperationStatus.Failed)]
    [InlineData("audio/pcm", "invalid", OperationStatus.Failed)]
    public async Task IncorrectFormatOrSampleRateDoesNotProduceMislabelledAudio(string contentType, string? sampleRate, OperationStatus expectedStatus)
    {
        var handler = SynthesisHandler(() =>
        {
            var response = Pcm([1, 0, 2, 0]);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            response.Headers.Remove("X-Sample-Rate");
            if (sampleRate is not null)
                response.Headers.Add("X-Sample-Rate", sampleRate);
            return response;
        });
        using var provider = Create(handler);

        var result = await provider.SynthesizeAsync(Request(), CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        AssertNoAssets();
    }

    [Fact]
    public async Task StreamLimitAppliesWithoutContentLength()
    {
        var handler = SynthesisHandler(() => Pcm(new ControlledStream(new byte[18], StreamEnd.End)));
        using var provider = Create(handler, maxBytes: 16);

        var result = await provider.SynthesizeAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        AssertNoAssets();
    }

    [Fact]
    public async Task StreamFailureRemovesPartialOutputAndDoesNotLeakExceptionMessage()
    {
        var handler = SynthesisHandler(() => Pcm(new ControlledStream([1, 0], StreamEnd.Throw)));
        using var provider = Create(handler);

        var result = await provider.SynthesizeAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.DoesNotContain(Secret, result.Detail ?? "");
        AssertNoAssets();
    }

    [Fact]
    public async Task CancellationDuringStreamingRemovesPartialOutputAndReleasesBusyState()
    {
        using var cancellation = new CancellationTokenSource();
        var blocked = new ControlledStream([1, 0, 2, 0], StreamEnd.Block);
        var synthesisCount = 0;
        var handler = SynthesisHandler(() => Interlocked.Increment(ref synthesisCount) == 1
            ? Pcm(blocked) : Pcm([3, 0, 4, 0]));
        using var provider = Create(handler);
        var pending = provider.SynthesizeAsync(Request(), cancellation.Token);
        await blocked.ReachedEnd.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.Result);
        AssertNoAssets();
        var next = await provider.SynthesizeAsync(Request(), CancellationToken.None);
        Assert.True(next.IsReady);
    }

    [Fact]
    public async Task CancellationAtEndOfBodyCannotCommitALateAsset()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new ControlledStream([1, 0, 2, 0], StreamEnd.End, cancellation.Cancel);
        var handler = SynthesisHandler(() => Pcm(stream));
        using var provider = Create(handler);

        var outcome = await provider.SynthesizeAsync(Request(), cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, outcome.Status);
        AssertNoAssets();
    }

    [Fact]
    public async Task TimeoutIncludesBodyStreamingAndDoesNotCommitPartialOutput()
    {
        var stream = new ControlledStream([1, 0, 2, 0], StreamEnd.Block);
        var handler = SynthesisHandler(() => Pcm(stream));
        using var provider = Create(handler, timeout: TimeSpan.FromMilliseconds(100));

        var result = await provider.SynthesizeAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Null(result.Result);
        AssertNoAssets();
    }

    [Fact]
    public async Task ConcurrentSynthesisFailsImmediatelyWithoutAnExtraEngineRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new ControlledStream([1, 0], StreamEnd.Block);
        var handler = SynthesisHandler(() => Pcm(stream));
        using var provider = Create(handler);
        var first = provider.SynthesizeAsync(Request(), cancellation.Token);
        await stream.ReachedEnd.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var second = await provider.SynthesizeAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(OperationStatus.Failed, second.Status);
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            cancellation.Cancel();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
        AssertNoAssets();
    }

    [Fact]
    public async Task UnknownModelCannotMasqueradeAsConfiguredEngine()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json("""{"data":[{"id":"other-engine"}]}""")));
        using var provider = Create(handler);

        var outcome = await provider.SynthesizeAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationStatus.Unsupported, outcome.Status);
        Assert.Equal(1, handler.RequestCount);
        AssertNoAssets();
    }

    [Fact]
    public async Task CallerCancellationHasDistinctHealthAndVoiceSemantics()
    {
        var handler = UnexpectedHandler();
        using var provider = Create(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetHealthAsync(cancellation.Token));
        var voices = await provider.GetVoicesAsync(cancellation.Token);
        var synthesis = await provider.SynthesizeAsync(Request(), cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, voices.Status);
        Assert.Equal(OperationStatus.Cancelled, synthesis.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("http://example.invalid:17863")]
    [InlineData("http://192.168.1.2:17863")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("http://user:pass@127.0.0.1:17863")]
    [InlineData("http://127.0.0.1:17863/prefix")]
    [InlineData("http://127.0.0.1:17863/?key=value")]
    [InlineData("http://127.0.0.1:17863/#fragment")]
    public void EndpointsCannotSendTextOrCredentialsOutsideConfiguredLoopbackRoot(string endpoint)
    {
        using var handler = UnexpectedHandler();
        Assert.ThrowsAny<ArgumentException>(() => new VieNeuTtsProvider(new VieNeuTtsOptions
        {
            Endpoint = new Uri(endpoint), OutputDirectory = outputDirectory
        }, Revision, handler));
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("http://127.0.0.1:17863")]
    [InlineData("https://localhost:17863")]
    [InlineData("http://[::1]:17863")]
    public void LoopbackEndpointsCanBeConfiguredWithoutContactingService(string endpoint)
    {
        using var handler = UnexpectedHandler();
        using var provider = new VieNeuTtsProvider(new VieNeuTtsOptions
        {
            Endpoint = new Uri(endpoint), OutputDirectory = outputDirectory
        }, Revision, handler);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    public async Task MalformedVoiceResponsesAreFailures(string body)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(body)));
        using var provider = Create(handler);

        var result = await provider.GetVoicesAsync(CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task NetworkFailuresHaveSafeDetailsAndLeaveNoOutput()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException(Secret));
        using var provider = Create(handler);

        var health = await provider.GetHealthAsync(CancellationToken.None);
        var voices = await provider.GetVoicesAsync(CancellationToken.None);
        var audio = await provider.SynthesizeAsync(Request(), CancellationToken.None);

        Assert.Equal(TtsHealthStatus.Unavailable, health.Status);
        Assert.Equal(OperationStatus.Failed, voices.Status);
        Assert.Equal(OperationStatus.Failed, audio.Status);
        Assert.DoesNotContain(Secret, health.Detail ?? "");
        Assert.DoesNotContain(Secret, voices.Detail ?? "");
        Assert.DoesNotContain(Secret, audio.Detail ?? "");
        AssertNoAssets();
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(-1, 1000)]
    [InlineData(1024, 0)]
    [InlineData(1024, -1)]
    public void InvalidResourceLimitsAreRejected(int maxBytes, int timeoutMilliseconds)
    {
        using var handler = UnexpectedHandler();
        Assert.ThrowsAny<ArgumentException>(() => Create(handler, maxBytes: maxBytes,
            timeout: TimeSpan.FromMilliseconds(timeoutMilliseconds)));
        Assert.Equal(0, handler.RequestCount);
    }
    private VieNeuTtsProvider Create(HttpMessageHandler handler, string? apiKey = null, int maxBytes = 1024,
        TimeSpan? timeout = null) => new(new VieNeuTtsOptions
        {
            Endpoint = new Uri("http://localhost:17863"),
            OutputDirectory = outputDirectory,
            ApiKey = apiKey,
            MaxAudioBytes = maxBytes,
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(10)
        }, Revision, handler);

    private static TtsSynthesisRequest Request() => new("Xin chào mọi người.", "Hải Đăng", "vi-VN", 1, 0, 1, Revision);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Models() => Json("""{"data":[{"id":"vieneu-v3-turbo"}]}""");

    private static HttpResponseMessage Pcm(byte[] bytes) => AudioResponse(new ByteArrayContent(bytes));
    private static HttpResponseMessage Pcm(Stream stream) => AudioResponse(new StreamContent(stream));

    private static HttpResponseMessage AudioResponse(HttpContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/pcm");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        response.Headers.Add("X-Sample-Rate", "48000");
        return response;
    }

    private static StubHandler SynthesisHandler(Func<HttpResponseMessage> speech) => new((request, _) =>
        Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => Models(),
            "/v1/audio/speech" => speech(),
            _ => throw new InvalidOperationException("Unexpected route")
        }));

    private static StubHandler UnexpectedHandler() => new((_, _) => throw new InvalidOperationException("HTTP must not be called"));

    private void AssertNoAssets() => Assert.Empty(Directory.Exists(outputDirectory)
        ? Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories) : []);

    public void Dispose()
    {
        // The fixture owns only its GUID-named child directory.
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return respond(request, cancellationToken);
        }
    }

    private enum StreamEnd { End, Throw, Block }

    private sealed class ControlledStream(byte[] prefix, StreamEnd end, Action? beforeEnd = null) : Stream
    {
        private int position;
        public TaskCompletionSource ReachedEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - position);
                prefix.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }

            ReachedEnd.TrySetResult();
            beforeEnd?.Invoke();
            if (end == StreamEnd.Throw)
                throw new IOException(Secret);
            if (end == StreamEnd.Block)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous reads");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
