# Milestone 6 status

Milestone 6 is **in progress**. The earlier completion announcement and issue closures did not establish the original acceptance criteria. See the [issue-by-issue audit](milestone-6-audit.md) for all 45 issues and their unresolved requirements.

## Verified during the 2026-10-18 audit

Native Windows adapters now have direct runtime regression checks. `MediaFoundationAudioDecoder` decodes WAV/MP3 audio and an H.264 video thumbnail with actual format metadata, end-of-stream handling, bounded output and native cleanup. `WinMmMediaAudioOutput` uses the correct pointer-sized header layout, bounds queued PCM and implements lifecycle commands. Tests exercise real system codecs, concurrent decoding, corrupt/truncated input, queue failures, and silent submission to a local output device where available. Windows CI runs codec checks; Linux runs portable fake-driver and layout checks and skips native-only cases.

These adapters are explicitly host supplied through `FolioOptions.MediaDecoder` and `MediaAudioOutput`; `FolioView.LoadHtml` forwards them for in-process documents. Content-process decoder/output IPC is not wired. A single decoded thumbnail is not moving video playback.

## Remaining acceptance work

- WebGL resource state and limited triangle drawing exist, but GLSL validation/execution and a GPU backend do not.
- Media still needs timed video frames, synchronized audio/video and content-process IPC, full state-machine/sound-grant behavior and full process-budget verification. Source bytes plus decoded pixels/PCM now share document reservations checked before decoder creation, including reload, capability revocation and teardown. Poster/capture allocations and actual sandbox enforcement still need coverage. Mute/volume, script seeking, replay, loop tails and binding teardown now have direct regression tests.
- Workers use isolated engines, but their complete worker event-loop/global surface and zero-copy transfers are not established.
- WebAssembly is a JavaScript interpreter on Jint, not the native engine required by the issues; its compilation cache is an in-page map.
- Static iframe documents render, but the full scripted-child, capability inheritance and watchdog acceptance remains to be proven.
- Camera/microphone output is synthetic, and WebRTC is in-page loopback without a permitted device/network transport backend.
- Service-worker handlers run in the page engine, document subresource interception is incomplete, and the conformance runner's `reopen offline` step does not recreate the document with an offline loader.
- The capability matrix does not yet execute the complete real process sandbox suite once per feature.

Existing Release tests and D8–D14 images are useful regression checks for the behavior they exercise. Passing them does not resolve missing backend or lifecycle requirements. Dependency-first delivery remains the plan; replacing mature dependencies with Folio-owned implementations is a later, separate track.
