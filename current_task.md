# Current slice: S24 / T090 / DONE

Document baseline 0.1.0; requirements 0.1.1; 2026-09-25; Integrator owns integration.

T090 was the smallest READY slice after T010, T043, T054 and T055 became DONE. It is now complete. The local console provides base mode selection, material directory preflight, engine selection, audio device selection, effect controls, progress, start/pause/resume/stop/emergency-stop, and a visible simulation boundary.

## Evidence

- Dedicated Desktop tests: 4/4 passed.
- Related solution tests: Application 571/571, Integration 388/388, Desktop 4/4 passed.
- `dotnet build TikTokAudio.slnx -c Debug --no-restore`: 0 warnings, 0 errors.
- Windows executable startup/close smoke check passed after correcting the read-only progress binding to OneWay.
- `git diff --check` passed; only Git line-ending normalization warnings were reported.
- No real platform, account, credential, network, TTS service or audio device was accessed.

## Allowed paths used

`src/TikTokAudio.Desktop/App.xaml`; `src/TikTokAudio.Desktop/MainWindow.xaml`; `src/TikTokAudio.Desktop/MainWindow.xaml.cs`; `src/TikTokAudio.Desktop/MainWindowViewModel.cs`; `tests/TikTokAudio.Desktop.Tests/`; `TikTokAudio.slnx`; `docs/T090-desktop-console-report.md`; `tasks.md`; `current_task.md`; `handoff.md`; `changelog.md`.

## Stop point

Do not automatically start T091 or T101. Re-evaluate dependencies. T091 still requires T034/T062/T072/T074/T080/T090; T101 still requires T044/T054/T081/T090/T100. T082 remains blocked by T034/T072/T074, T044 remains DEFERRED, and T030 remains BLOCKED.
