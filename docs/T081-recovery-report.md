# T081 Local Playback Recovery Report

Date: 2026-09-25
Status: DONE
Scope: local, offline recovery preview and explicit planner restoration only.

## Implemented

- `PlaybackRecoveryCoordinator` captures pre-recorded planner snapshots and TTS checkpoints, stores the selected mode, plan revision, capture timestamp, and optional engine snapshot.
- `TtsPlaybackPlanner.CaptureCheckpoint` captures the current source cursor, selected segment, cycle, effect seed, plan revision, and consumed marker IDs.
- `SqlitePlaybackRecoveryStore` persists a versioned DTO document through the existing `SqliteStateStore` using `LocalDocumentKind.ShuffleBag`. It round-trips checkpoints, pre-recorded catalog fingerprint, shuffle bags, effect random state, consumed markers, engine ID, engine revision, and default voice.
- Loading validates document kind, document version, JSON shape, state version, mode, plan revision, and checkpoint content. Invalid or future documents are rejected without planner mutation.
- Recovery creates a read-only preview with `RequiresManualConfirmation=true`, `WillPlayAudio=false`, and `WillInvokePlatformActions=false`. Explicit confirmation is required before restoring a planner; confirmation still starts no audio and performs no platform action.

## Tests

Dedicated commands:

- `dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~T081RecoveryTests"` -> 4/4 passed.
- `dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~T081RecoveryTests"` -> 3/3 passed.

Related regression commands:

- Application test project -> 571/571 passed.
- Integration test project -> 388/388 passed.

The tests use in-memory stores, synthetic documents, and temporary SQLite directories. They do not access real network services, live platforms, credentials, TTS services, or audio devices.

## Build and static verification

- `dotnet build TikTokAudio.slnx -c Debug --no-restore` -> 0 warnings, 0 errors.
- `git diff --check` -> passed after removing unrelated trailing blank lines in the governance documents.
- New source and test files were checked as valid UTF-8.

## Explicit non-goals

- No automatic playback after crash.
- No automatic text dispatch or product action.
- No replay of Unknown external actions.
- No T082 platform-action recovery, T090 UI, second real TTS engine, or external integration.
