# T054 effects acceptance (known naturalness limitation accepted)

Started 2026-09-24; accepted 2026-09-25; S17/T054 DONE; main/9912252aa6edbe2fce515dd3b193d9f29de8fce4 at start; requirements 0.1.1.

Latest user decision: “这个问题先通过，继续后续开发”. Combined with the prior pronunciation/intelligibility confirmation, the user accepts this slice with the known machine-like/naturalness limitation. T054 is DONE; this does not claim human-equivalent naturalness, implement voice cloning, accept a second engine, or complete platform/long-run acceptance. Earlier pending-listening statements below are historical and superseded by this decision. Source code and prior test/build evidence are unchanged.

Subsequent S18 regression maintenance: the existing interaction-resume test could exhaust its fixed 100 yields before background preparation ran. Only its readiness wait was corrected (explicit preparation signal, actual state condition and bounded test timeout); all effect/cursor/boundary assertions and audio production code remain unchanged. Final related checks passed 76/76 and solution checks 645/645 with a zero-warning/error build; see `T070-product-control-report.md` for the failure and correction evidence.

Application.Playback selects bounded deterministic parameters from the checkpointed EffectSeed for pre-recorded clips only. Disabled effects, an enabled but acoustically neutral configuration, and TTS bypass the optional effect output. The scheduler passes selected effects to the output; a missing capability returns Unsupported without acknowledging the clip boundary and leaves scheduler state Error. A simulated interruption/resume retains both selection and source cursor without re-emitting the boundary.

Infrastructure prepares a disposable, bounded PCM16 cache asset. Pitch is resampled locally, then a deterministic overlap-aligned time scaler produces the configured playback duration while preserving the selected pitch. Processing returns an explicit source/rendered frame map: saved source cursors map into the rendered file on resume, while device progress maps back to source frames. Recommended-range tests verify duration, independent pitch frequency and a 1700-frame resume round trip with at most one source-frame rounding difference.

Stereo balance, low-frequency EQ and deterministic EffectSeed-based noise are applied after time/pitch processing. Gain reservation prevents saturation at the quantizer. Output and intermediate files are bounded by the configured decoded-audio limit; processing validates the rendered WAV before same-directory atomic replacement. Cancellation, size rejection or invalid channel layouts leave the source cache file unchanged. `AudioEffectPreview` publicly produces an owned disposable rendered WAV with source/rendered frame counts but never starts device playback. The environment effect is synthesized noise, not an external ambient recording.

Focused checks on 2026-09-25: T054 Application 11/11 and Integration 17/17; solution regression 580/580 (Application 374, Integration 206); `dotnet build TikTokAudio.slnx -c Debug --no-restore` passed with 0 warnings/0 errors. `git diff --check` passed. Tests cover deterministic non-neutral re-rendering, recommended speed/pitch boundaries, speed pitch-preservation, pitch direction, output bounds, clipping headroom, environment seeds, preview ownership and scheduler resume. All use synthetic in-memory output or generated WAV; no actual device, TTS engine, account, platform, download or network was used.

Remaining acceptance: use the preview path with representative Vietnamese speech and perform/document a real intelligibility and artifact listening review under explicit device-test authorization. Automated duration/frequency evidence cannot replace hearing speech quality. Keep T054 ACTIVE until that result is recorded; do not start T090 or claim T054 DONE from the synthetic evidence.

## 2026-09-25 authorized device audition (verdict pending)

The user replied “继续” after the handoff identified explicit device-listening authorization as the only next action. The only active render endpoint was `扬声器 (Realtek(R) Audio)`. An external one-use helper, kept outside the repository at `%LOCALAPPDATA%\LiveAudio\t054-audition-20260925`, used the public preview API and performed no TTS synthesis or network access.

It played the same existing representative sample in this order: original (149,760 frames / 3.12s), recommended low boundary (speed 0.97, pitch -0.3 semitone, EQ -1dB, seeded -35dB noise; 154,392 frames / 3.22s), then recommended high boundary (speed 1.03, pitch +0.3 semitone, EQ +1dB, seeded -35dB noise; 145,398 frames / 3.03s). Stereo balance was neutral because the Vietnamese source is mono. All three device playbacks completed, owned preview/playback caches were released, and the helper exited 0.

Machine evidence and the three persistent WAVs are in `%LOCALAPPDATA%\LiveAudio\t054-audition-20260925\run-20260925-095928\audition-evidence.json`.

The user then reviewed files 2 and 3 and reported: “发音正确，用户能听懂，但是不够自然，能听出来是机器声音”. This passes pronunciation and intelligibility but does not accept naturalness. The exact feedback is preserved separately in `listening-review.json`; the machine-generated evidence file remains unchanged. It is not yet known whether the machine-like character is already present in the original TTS sample or is materially increased by time/pitch/EQ/noise processing. T054 stays ACTIVE; the next smallest check is an A/B comparison with `01-original.wav`, followed by effect-range or processor tuning only if the processed variants are worse.

## 2026-09-24 historical processor recheck (resolved locally on 2026-09-25)

- Rechecked the local PATH for `ffmpeg`, `rubberband`, and `sox`; none is available. No package install, model download, external service, device playback, or network action was attempted.
- At that point, speed/pitch effects still needed a bounded processor with explicit source-frame mapping so pause/resume could not confuse processed-frame positions with source-frame positions. The 2026-09-25 implementation above resolves this code gap without installing a dependency.
- T054 remains ACTIVE for the separate real-listening gate described above.

Processor recheck: the installed NAudio 2.2.1 assemblies expose no reusable pitch-shifting, time-stretch, or varispeed processor.
