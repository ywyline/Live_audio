using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;
using TikTokAudio.Application.Tts;

namespace TikTokAudio.Application.Tests.Tts;

public sealed class TtsEngineRegistryTests
{
    [Fact]
    public void SwitchIncrementsRevisionAndSelectsConfiguredProvider()
    {
        var one = Provider("one");
        var two = Provider("two");
        var registry = new TtsEngineRegistry([
            new TtsEngineConfiguration("one", one, "v1"),
            new TtsEngineConfiguration("two", two, "v2")], "one");

        var result = registry.Switch("two");

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal("two", registry.Snapshot.EngineId);
        Assert.Equal(new EngineRevision(1), registry.Snapshot.Revision);
        Assert.Same(two, registry.ActiveProvider);
    }

    [Fact]
    public void ActivePlaybackRejectsSwitchWithoutChangingRevision()
    {
        var registry = Registry();
        var before = registry.Snapshot;
        registry.BeginPlaybackUse();

        var result = registry.Switch("two");

        Assert.Equal(OperationStatus.Unknown, result.Status);
        var blocked = registry.Snapshot;
        Assert.Equal(before.EngineId, blocked.EngineId);
        Assert.Equal(before.Revision, blocked.Revision);
        Assert.True(blocked.IsPlaybackLocked);
        registry.EndPlaybackUse();
        Assert.Equal(OperationStatus.Succeeded, registry.Switch("two").Status);
    }

    [Fact]
    public void UnknownEngineIsUnsupportedAndSameEngineIsNoOp()
    {
        var registry = Registry();
        Assert.Equal(OperationStatus.Unsupported, registry.Switch("missing").Status);
        Assert.Equal(OperationStatus.Succeeded, registry.Switch("one").Status);
        Assert.Equal(EngineRevision.Initial, registry.Snapshot.Revision);
    }

    [Fact]
    public void ConfigurationRequiresMatchingProviderId()
    {
        Assert.Throws<ArgumentException>(() => new TtsEngineConfiguration("one", Provider("two")));
    }

    private static TtsEngineRegistry Registry() => new([
        new TtsEngineConfiguration("one", Provider("one")),
        new TtsEngineConfiguration("two", Provider("two"))], "one");

    private static ITtsProvider Provider(string id) => new FakeProvider(id);

    private sealed class FakeProvider(string id) : ITtsProvider
    {
        public string EngineId => id;
        public EngineRevision Revision => EngineRevision.Initial;
        public Task<TtsHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new TtsHealth(TtsHealthStatus.Healthy, id, null));
        public Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken cancellationToken) => Task.FromResult(new OperationResult<IReadOnlyList<TtsVoice>>(OperationStatus.Succeeded, [], null));
        public Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken) => Task.FromResult(new TtsSynthesisOutcome(OperationStatus.Unsupported, null, null));
    }
}


