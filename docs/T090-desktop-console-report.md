# T090 Desktop Console Report

Date: 2026-09-25
Slice: S24/T090
Status: DONE

## Delivered

- A WPF local console with a visible simulation-only banner.
- Mutually exclusive pre-recorded and local TXT/TTS base mode selection.
- Local material-root preflight and product-directory summary without claiming platform success.
- Local engine and device selectors with configuration/audition actions that remain simulation-only.
- Speed and pitch effect controls bounded to the existing application ranges.
- Current playback state, product/group/clip summary and progress.
- Start, pause all, resume, stop and emergency-stop commands. Stop invalidates the local playback state; no external action is replayed.

## Implementation boundary

`MainWindowViewModel` is an in-memory presentation state owner. It does not construct a live source, room controller, TTS provider, NAudio session or persistence writer. T091 platform controls, T082 external-action recovery and real device/TTS verification remain separate tasks.

## Verification

- `dotnet test tests/TikTokAudio.Desktop.Tests/TikTokAudio.Desktop.Tests.csproj -c Debug --no-restore`: 4/4 passed.
- `dotnet test TikTokAudio.slnx -c Debug --no-restore`: Application 571/571, Integration 388/388, Desktop 4/4 passed.
- `dotnet build TikTokAudio.slnx -c Debug --no-restore`: 0 warnings, 0 errors.
- `git diff --check`: passed; Git reported only expected LF/CRLF normalization warnings.
- Windows smoke command started `TikTokAudio.Desktop.exe`, kept it alive for two seconds, then closed it; exit output was empty after fixing the read-only `Progress` binding to `Mode=OneWay`.

## Limits

This proves local UI state and Windows startup only. It does not prove platform access, real audio-device playback, real TTS synthesis, product operations, text dispatch, or complete V1 readiness.
