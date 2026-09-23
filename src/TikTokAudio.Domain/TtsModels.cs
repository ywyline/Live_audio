namespace TikTokAudio.Domain;

public enum TtsHealthStatus
{
    Healthy,
    Unavailable,
    Degraded,
    Unsupported
}

public sealed record TtsHealth(TtsHealthStatus Status, string EngineId, string? Detail);

public sealed record TtsVoice(string VoiceId, string Language, string DisplayName);

public sealed record TtsSynthesisRequest(
    string Text,
    string VoiceId,
    string Language,
    double Rate,
    double Pitch,
    double Volume,
    EngineRevision EngineRevision);

public sealed record TtsSynthesisResult(
    LocalAudioAsset Asset,
    string EngineId,
    EngineRevision EngineRevision);

public sealed record TtsSynthesisOutcome(
    OperationStatus Status,
    TtsSynthesisResult? Result,
    string? Detail)
{
    public bool IsReady => Status == OperationStatus.Succeeded && Result is not null;
}
