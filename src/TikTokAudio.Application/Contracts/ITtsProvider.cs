using TikTokAudio.Domain;

namespace TikTokAudio.Application.Contracts;

public interface ITtsProvider
{
    string EngineId { get; }
    EngineRevision Revision { get; }

    Task<TtsHealth> GetHealthAsync(CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyList<TtsVoice>>> GetVoicesAsync(CancellationToken cancellationToken);
    Task<TtsSynthesisOutcome> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken);
}
