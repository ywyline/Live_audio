using TikTokAudio.Application.Contracts;
using TikTokAudio.Application.Tts;
using TikTokAudio.Domain;

namespace TikTokAudio.Application.Playback;

public sealed record PlaybackRecoveryState(
    int Version,
    DateTimeOffset CapturedAtUtc,
    BasePlaybackMode Mode,
    PlanRevision PlanRevision,
    PlaybackCheckpoint Checkpoint,
    PreRecordedPlannerSnapshot? PreRecordedSnapshot,
    string? EngineId,
    EngineRevision? EngineRevision,
    string? DefaultVoiceId)
{
    public const int CurrentVersion = 1;

    public bool IsValid => Version == CurrentVersion &&
        Enum.IsDefined(Mode) && Checkpoint is not null && PlanRevision.Value >= 0 && Checkpoint.Mode == Mode &&
        Checkpoint.PlanRevision == PlanRevision &&
        (Mode != BasePlaybackMode.PreRecorded || PreRecordedSnapshot is not null) &&
        (Mode != BasePlaybackMode.TtsScript || PreRecordedSnapshot is null) &&
        (EngineId is null || !string.IsNullOrWhiteSpace(EngineId)) &&
        (EngineRevision is null || EngineRevision.Value.Value >= 0);
}

public sealed record PlaybackRecoveryPreview(
    PlaybackRecoveryState State,
    bool RequiresManualConfirmation,
    bool WillPlayAudio,
    bool WillInvokePlatformActions)
{
    public bool CanRestorePlanner => State.IsValid;
}

/// <summary>Persistence boundary for local recovery state; implementations must not execute playback or platform actions.</summary>
public interface IPlaybackRecoveryStore
{
    Task<PlaybackRecoveryState?> LoadAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveAsync(PlaybackRecoveryState state, CancellationToken cancellationToken = default);
}

/// <summary>
/// Captures local playback state and exposes a non-mutating crash preview. Restoration is explicit and local only.
/// </summary>
public sealed class PlaybackRecoveryCoordinator(IPlaybackRecoveryStore store)
{
    private readonly IPlaybackRecoveryStore store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<OperationResult> CapturePreRecordedAsync(
        PreRecordedPlaybackPlanner planner, AudioCursor sourceCursor, TtsEngineSnapshot? engine = null,
        DateTimeOffset? capturedAtUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planner);
        var snapshot = planner.CaptureSnapshot(sourceCursor);
        return await store.SaveAsync(new PlaybackRecoveryState(
            PlaybackRecoveryState.CurrentVersion, capturedAtUtc ?? DateTimeOffset.UtcNow, BasePlaybackMode.PreRecorded,
            snapshot.Checkpoint.PlanRevision, snapshot.Checkpoint, snapshot, engine?.EngineId, engine?.Revision,
            engine?.DefaultVoiceId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> CaptureTtsAsync(
        TtsPlaybackPlanner planner, AudioCursor sourceCursor, TtsEngineSnapshot? engine = null,
        DateTimeOffset? capturedAtUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planner);
        var checkpoint = planner.CaptureCheckpoint(sourceCursor);
        return await store.SaveAsync(new PlaybackRecoveryState(
            PlaybackRecoveryState.CurrentVersion, capturedAtUtc ?? DateTimeOffset.UtcNow, BasePlaybackMode.TtsScript,
            checkpoint.PlanRevision, checkpoint, null, engine?.EngineId, engine?.Revision,
            engine?.DefaultVoiceId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads state for display only. It never restores a planner or starts any external action.</summary>
    public async Task<PlaybackRecoveryPreview?> CreatePreviewAsync(CancellationToken cancellationToken = default)
    {
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is null) return null;
        if (!state.IsValid) throw new InvalidDataException("本地恢复状态版本或内容无效。");
        return new PlaybackRecoveryPreview(state, true, false, false);
    }

    /// <summary>Explicit user-confirmed local restore for a pre-recorded planner; no audio output is started.</summary>
    public static OperationResult RestorePreRecordedAfterConfirmation(
        PreRecordedPlaybackPlanner planner, PlaybackRecoveryPreview preview)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.RequiresManualConfirmation || !preview.CanRestorePlanner ||
            preview.State.PreRecordedSnapshot is null || preview.State.Mode != BasePlaybackMode.PreRecorded)
            return OperationResult.Failed("恢复预览不可用于预制计划恢复。");
        return planner.RestoreSnapshot(preview.State.PreRecordedSnapshot);
    }

    /// <summary>Explicit user-confirmed local restore for a prepared TTS planner; no audio output is started.</summary>
    public static Task<OperationResult> RestoreTtsAfterConfirmationAsync(
        TtsPlaybackPlanner planner, PlaybackRecoveryPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.RequiresManualConfirmation || !preview.CanRestorePlanner ||
            preview.State.Mode != BasePlaybackMode.TtsScript)
            return Task.FromResult(OperationResult.Failed("恢复预览不可用于稿件计划恢复。"));
        return planner.RestoreCheckpointAsync(preview.State.Checkpoint, cancellationToken);
    }
}
