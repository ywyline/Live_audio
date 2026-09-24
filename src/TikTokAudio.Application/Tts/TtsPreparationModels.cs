using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Tts;

public sealed record TtsScriptImportLimits(int MaxInputBytes, int MaxSegments, int MaxSegmentLength);
public sealed record TtsScriptSegment(int Index, string Text, ProductBoundary? Boundary);
public sealed record TtsScriptDocument(string OriginalText, string SegmenterVersion, IReadOnlyList<TtsScriptSegment> Segments);
public sealed record TtsScriptImportResult(
    OperationStatus Status, TtsScriptDocument? Document, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

// Versions identify deployed engine/model artifacts, independently of the session revision.
public sealed record TtsEngineIdentity(string EngineId, string EngineVersion, string ModelId, string ModelVersion);
public sealed record TtsGenerationSettings(
    TtsEngineIdentity Identity, string VoiceId, string Language,
    double Rate, double Pitch, double Volume, string PronunciationDictionaryVersion);

public interface ITtsAudioCache
{
    // A null value with Succeeded means a miss. Corrupt entries must never be returned as hits.
    Task<OperationResult<LocalAudioAsset>> FindAsync(string key, CancellationToken cancellationToken);

    // Transfers ownership of a generated asset inside the configured staging directory.
    // Implementations validate, atomically commit, and clean owned staging files on failure/cancellation.
    Task<OperationResult<LocalAudioAsset>> StoreAsync(string key, LocalAudioAsset generatedAsset, CancellationToken cancellationToken);

    // Cancellation must not interrupt cleanup of owned staging assets.
    Task<OperationResult> DiscardAsync(LocalAudioAsset generatedAsset);
}

public sealed record PreparedTtsSegment(TtsScriptSegment Segment, LocalAudioAsset Asset, string CacheKey);
public enum TtsPreparationState { Idle, WaitingForSynthesis, Ready, Failed, Cancelled }
public sealed record TtsPreparationSnapshot(
    TtsPreparationState State, IReadOnlyList<PreparedTtsSegment> Segments, int RequiredBeforePlayback, string? Detail)
{
    public bool CanStartPlayback => State == TtsPreparationState.Ready && RequiredBeforePlayback > 0 && Segments.Count >= RequiredBeforePlayback;
}
public sealed record TtsPreparationResult(OperationStatus Status, TtsPreparationSnapshot Snapshot, string? Detail);
