using System.Buffers.Binary;
using System.Text;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Tts.Cache;
using Xunit;

namespace TikTokAudio.Integration.Tests;

public sealed class TtsPreparationWorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LiveAudio-T042-Workflow-" + Guid.NewGuid().ToString("N"));
    private string Staging => Path.Combine(root, "staging");
    private string CacheRoot => Path.Combine(root, "cache");
    private static readonly TtsGenerationSettings Settings = new(new("fixture-engine", "1", "fixture-model", "weights-v1"),
        "fixture-voice", "vi-VN", 1, 0, 1, "none-v1");

    [Fact]
    public async Task OperatorScriptGeneratesMarkerFreeWavsAndRestartsEntirelyFromDisk()
    {
        var importer = new OperatorScriptImporter(new(4096, 20, 200));
        var imported = importer.Import(Encoding.UTF8.GetBytes("Xin chào. @[1] Cảm ơn! @[2] Cảm ơn! @[1]"),
            new Dictionary<int, string> { [1] = "one", [2] = "two" });
        Assert.Equal(OperationStatus.Succeeded, imported.Status);
        Assert.Single(imported.Warnings);
        var document = imported.Document!;
        var provider = new WaveProvider(Staging);
        var first = await new TtsPreGenerator(provider, Cache(), 20).PrepareAsync(document, 0, document.Segments.Count, Settings, default);
        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.True(first.Snapshot.CanStartPlayback);
        Assert.Equal(new[] { "Xin chào.", "Cảm ơn!" }, provider.Texts);
        Assert.All(provider.Texts, text => Assert.DoesNotContain("@[", text));
        Assert.Equal(new string?[] { null, "one", "two" }, first.Snapshot.Segments.Select(s => s.Segment.Boundary?.ProductId));
        Assert.NotEqual(first.Snapshot.Segments[1].Segment.Boundary!.BoundaryId, first.Snapshot.Segments[2].Segment.Boundary!.BoundaryId);
        Assert.Equal(first.Snapshot.Segments[1].Asset.Path, first.Snapshot.Segments[2].Asset.Path);
        Assert.Empty(Directory.GetFiles(Staging));
        Assert.Equal(2, Directory.GetDirectories(CacheRoot).Length);

        var offline = new WaveProvider(Staging) { Offline = true };
        var restarted = await new TtsPreGenerator(offline, Cache(), 20).PrepareAsync(document, 0, document.Segments.Count, Settings, default);
        Assert.Equal(OperationStatus.Succeeded, restarted.Status);
        Assert.True(restarted.Snapshot.CanStartPlayback);
        Assert.Empty(offline.Texts);
        Assert.Equal(first.Snapshot.Segments.Select(s => s.Asset.Path), restarted.Snapshot.Segments.Select(s => s.Asset.Path));
        Assert.All(restarted.Snapshot.Segments, segment =>
        {
            var bytes = File.ReadAllBytes(segment.Asset.Path);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(48000, segment.Asset.SampleRate);
            Assert.Equal(TimeSpan.FromSeconds(.001), segment.Asset.Duration);
        });
    }

    [Fact]
    public async Task LateCancelledEngineFileIsRemovedAndRetryIsClean()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new WaveProvider(Staging) { AfterWrite = cancellation.Cancel };
        var document = new OperatorScriptImporter(new(1000, 10, 100)).Import(Encoding.UTF8.GetBytes("Xin chào."),
            new Dictionary<int, string>()).Document!;
        var generator = new TtsPreGenerator(provider, Cache(), 10);
        var cancelled = await generator.PrepareAsync(document, 0, 1, Settings, cancellation.Token);
        Assert.Equal(OperationStatus.Cancelled, cancelled.Status);
        Assert.Empty(Directory.GetFiles(Staging));
        Assert.Empty(Directory.GetDirectories(CacheRoot));
        Assert.Empty(generator.Snapshot.Segments);
        provider.AfterWrite = null;
        var retry = await generator.PrepareAsync(document, 0, 1, Settings, default);
        Assert.True(retry.Snapshot.CanStartPlayback);
        Assert.Empty(Directory.GetFiles(Staging));
        Assert.Single(Directory.GetDirectories(CacheRoot));
    }

    [Fact]
    public async Task HashDamagedDiskAudioNeverReachesReadySnapshot()
    {
        var provider = new WaveProvider(Staging);
        var document = new OperatorScriptImporter(new(1000, 10, 100)).Import(Encoding.UTF8.GetBytes("Xin chào."),
            new Dictionary<int, string>()).Document!;
        var generator = new TtsPreGenerator(provider, Cache(), 10);
        var first = await generator.PrepareAsync(document, 0, 1, Settings, default);
        var audio = first.Snapshot.Segments[0].Asset.Path;
        var bytes = File.ReadAllBytes(audio);
        bytes[44] ^= 0x7f;
        File.WriteAllBytes(audio, bytes);
        var second = await generator.PrepareAsync(document, 0, 1, Settings, default);
        Assert.Equal(OperationStatus.Failed, second.Status);
        Assert.False(second.Snapshot.CanStartPlayback);
        Assert.Empty(second.Snapshot.Segments);
        Assert.Single(provider.Texts);
    }

    private FileTtsAudioCache Cache() => new(new TtsAudioCacheOptions
    {
        CacheDirectory = CacheRoot, StagingDirectory = Staging,
        MaxEntries = 20, MaxTotalBytes = 100000, MaxAssetBytes = 10000
    });

    private sealed class WaveProvider(string staging) : ITtsProvider
    {
        public string EngineId => "fixture-engine";
        public EngineRevision Revision => new(1);
        public List<string> Texts { get; } = [];
        public bool Offline { get; init; }
        public Action? AfterWrite { get; set; }
        public async Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken token)
        {
            if (Offline) throw new InvalidOperationException("Offline fixture must never synthesize.");
            Texts.Add(request.Text);
            Directory.CreateDirectory(staging);
            var path = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".wav");
            var bytes = new byte[140];
            "RIFF"u8.CopyTo(bytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
            "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 48000);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 96000);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
            "data"u8.CopyTo(bytes.AsSpan(36));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), bytes.Length - 44);
            bytes[44] = 42;
            await File.WriteAllBytesAsync(path, bytes, token);
            AfterWrite?.Invoke();
            return new(OperationStatus.Succeeded, new(new(path, "wav", EngineId, null, 48000), EngineId, Revision), null);
        }
        public Task<TtsHealth> GetHealthAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken token) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        var allowedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "LiveAudio-T042-Workflow-";
        if (!resolved.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
