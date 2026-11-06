# Folio feature completion — paused checkpoint, 2026-11-07

This is unfinished implementation, not release qualification. The user asked to finish all five remaining feature-blocker groups, then asked to pause near 95% of either Codex quota, push everything and provide a handoff for Claude/Gemini. At checkpoint preparation, the weekly meter was 94% used and the five-hour meter 40%. All coding agents have stopped. No quota-reset credit was redeemed.

## Checkout and approved decisions

- Repository: Yuuzulight/Folio; branch `fix/folio-feature-completion`.
- Local checkout: `C:\ManaAI\worktrees\folio-production-audit`.
- Base: merged main `26acc1c93fbfebae0f375ecfac7fc8da3ed8b839` (PR #700). Earlier integration PRs Mana #1520/#1521 are also merged. This phase changes Folio only; Mana has not been repinned to these unfinished changes.
- Remote WebRTC must be **Folio-owned**, including ICE/DTLS/SRTP/SCTP. Windows system APIs may supply device access, codecs and cryptography. No third-party WebRTC engine/library was authorized.
- GPU timeouts must preserve the document: use a **separate invisible GPU worker**, not whole-page restart. User accepted extra process/memory overhead.
- Camera/microphone must remain permission-gated. No actual camera or microphone was opened. Real-device and deliberate runaway-shader tests have not been authorized or executed.
- Ask the user at material design crossroads. Do not silently promote an incomplete feature, weaken acceptance, or treat in-page loopback as remote transport.
- No new stable release, installation, runtime update or merge is part of this checkpoint.

## Implemented or substantially changed

### Host policy and capture transport (#523/#548/#552)

`ContentProcessClient.cs`, new `ContentHostPolicy.cs`, `ContentDocumentRuntime.cs`, `FolioView.cs`:

- Host-owned, bounded ICE-origin policy snapshot reaches the child. Child load fields cannot widen it.
- Iframe navigation has a generation-scoped notification and bounded parent delivery queue. Cancellable UI dispatch supports callbacks that close/reload their own page; stale/malformed notifications are rejected.
- Capture uses bounded audio/video delivery and an owned send pump instead of unbounded async event sends. Sequencing/deduplication and per-kind stop commands are carried across the broker.
- Added optional `CaptureSequence` fields to `MediaAudioFrame` and `MediaCaptureVideoFrame`; zero means legacy/unsequenced. Native provider produces increasing per-kind sequences.
- New `ContentMediaCapabilities.cs` forwards `canPlayType` queries to the actual host decoder rather than inheriting unconditional true. Pending decode/capture/capability requests complete on disconnect. Query concurrency is bounded; decode waiting observes cancellation.

New tests: `ContentHostPolicyTests`, `ContentCaptureTransportTests`, `ContentMediaCapabilitiesTests`, Windows `IframeHostDispatchTests`.

### Native devices and continuous capture (#533/#535/#552)

New `WindowsMediaCaptureProvider.cs`, `MediaFoundationCameraCapture.cs`, `WinMmMicrophoneCapture.cs`:

- Opt-in Windows adapters; constructors do not open devices. Device workers own native resources; camera uses asynchronous MF callbacks and microphone bounded waveIn buffers.
- Partial-open rollback, independent `StopAudio`/`StopVideo`, copied snapshots, cancellation/revocation/disposal seams and injected fake-native tests.
- `IMediaCaptureTrackControl` is an additive optional interface in FolioOptions. Existing providers remain source-compatible.
- Capture request identity is now reference-based; subscriptions are detached; revocation returns to the document owner loop. Invalid frame dimensions fail validation without overflow.
- New `CaptureAudioBuffer.cs` has bounded history, independent consumer cursors, sequence-first deduplication, drop/skip behavior and shared media-budget accounting. Canvas2D/DomContext plumbing adds delayed/continuous microphone playback, disabled-track silence/black video and resolution budget checks.

Known limits: hardware formats/unplug/privacy revocation remain unqualified. A stalled native driver may retain driver-owned buffers; the adapter must not free memory still owned by a driver. Native synchronous decoder reads are not forcibly interruptible. Shared audio-output lifecycle/mixing between consumers is unfinished.

### Bounded combined A/V decoding (#533/#535)

`MediaFoundationAudioDecoder.cs` now drains both selected audio/video streams to EOS, negotiates mono/stereo, shares an output budget, builds a timestamped audio timeline and preserves offsets/gaps/preroll. It rejects budget/time truncation rather than reporting partial video with full duration. Includes bounded MP4 edit-list parsing because Windows MF did not preserve the offset fixture's empty edit automatically. Optional WebM/Ogg codec claims were removed from the MIME hint list.

Three synthetic fixtures and their reproducible development-only FFmpeg commands are under `tests/Folio.Scripting.Tests/Native/Fixtures/combined-av.md`. FFmpeg was already installed; it is not a production or test-runtime dependency.

Remaining: cancellation of blocked synchronous MF calls, total peak memory beyond retained-output budgeting, broader MP4 edit/multiple-track support, physical output-clock synchronization and playback-rate behavior. Do not describe this as a universal streaming video player.

### Workers and service workers (#539/#541/#556/#559)

- WorkerState retires memory-limit failures, returns slots/reservations, preserves the first terminal error and avoids reporting shutdown interruption as a second memory fault. New tests exercise explicit termination, timer/normal allocation failures, page/sibling survival and replacement workers, including restricted-process paths.
- IsolatedServiceWorkerRuntime/ServiceWorkerBindings add cancellation and bounded task waits, disposal cleanup, lifecycle promise tracking, separate candidates, waiting/activation sequencing, skipWaiting and claim behavior, plus rollback handling.
- Some existing service-worker test fixtures now explicitly claim clients during activation rather than relying on registration automatically taking control.

Remaining: the two service-worker failures below, true independent scheduling, and host loaders that block synchronously before returning their Task. A bounded wait on a returned task is not proof that arbitrary host code can be interrupted.

### GPU worker foundation (#531) — NOT integrated

Four new `GpuWorker*` files and an early `--gpu-worker` entry mode in ContentHost provide a bounded binary protocol, restricted parent-launched worker broker, surface proxy and worker loop. Fake tests cover deadlines/cancellation/malformed replies/budgets without using a GPU.

This is only a surface clear/readback slice. **Production WebGL D3D still runs in the document process.** Parent↔DOM IPC attachment, proxy factory injection, full WebGL draw forwarding, context-lost/restoration behavior, shared memory and native-worker runtime acceptance remain. See `docs/gpu-worker-checkpoint.md`; its early “not run” notes are superseded only by the focused results below, not by real hardware qualification.

### Folio-owned WebRTC foundation — NOT a remote stack

New `Rtc/StunMessage.cs` provides bounded STUN framing, classic short-term HMAC-SHA1 verification, fingerprint checking, XOR-mapped IPv4/IPv6 address decoding and authenticated encoding. `StunMessageTests` checks RFC 5769 numeric interoperability vectors and malformed/tampered inputs. It opens no sockets.

**Remote ICE connectivity, TURN, DTLS, SRTP, SCTP/data channels, real SDP negotiation and browser interoperability remain unimplemented.** Existing WebRtcBindings still use in-page loopback. Do not claim full transport or enable its production route.

## Latest verification

All source owners were frozen for the final build/test wave. Source changes by the worker, decoder and GPU owners precede the final compiled test DLL timestamps. No code was changed after the final source freeze except this handoff documentation.

| Check | Result |
| --- | --- |
| Release solution build, `FolioOracleHost=false`, `FolioWasmOracle=false` | Passed; zero errors, two xUnit1051 cancellation-token warnings in GPU tests |
| Focused scripting wave 3 | **220 total: 216 passed, 4 failed, no skips** |
| Windows capture-format/provider/UI dispatch wave 3 | **29/29 passed**; fake devices, no capture hardware |
| Native presentation benchmark, 120 frames | Median 0.66 ms, p95 1.06 ms versus 16.67 ms; synthetic decoded frames, not codec/device latency |
| Final full solution/conformance/fuzz/published NativeAOT/package-consumer/CI run | **Not performed for this checkpoint** |

The new media benchmark now uses Folio's native engine/software renderer, checks changing frames and exits nonzero above its printed p95 threshold. CI has a corresponding presentation gate. This is not real-device playback evidence.

### Four known failing tests to fix first

1. `ServiceWorkerSafetyLifecycleTests.FailedActivationPersistenceDoesNotRestoreARetiredWorker(native:false)` — storage IOException escapes Jint's host boundary instead of becoming a catchable lifecycle rejection/rollback.
2. `ServiceWorkerSafetyLifecycleTests.DisposingRetainedRuntimeDisposesEachEngineExactlyOnceAndReleasesHandles` — disposal counts pass but weak engine reference remains alive. Native host-object caching is suspected; root cause is unverified.
3. `CapturePlaybackTests.DisabledAndStoppedVideoIsOpaqueBlackAndMissingRealFrameHasNoGradient(native:false)` — Jint MemoryLimitExceededException during host-side property access; reported allocation 568628328 against 67108864. Investigate constraint scope/baseline and host getters, not merely raise the limit. Native case passes.
4. `ContentCaptureBridgeTests.ContentProcessForwardsCaptureFramesAndRevocation` — after provider revocation the next child query reads `live` instead of `ended`. Investigate send-pump/current-read revocation ordering; do not add arbitrary sleeps to hide it.

Previous wave's timer-memory, corrupt-MP4 and invalid-GPU-budget failures pass in wave 3.

## Reproduce and continue

```powershell
git clone --branch fix/folio-feature-completion https://github.com/Yuuzulight/Folio.git
cd Folio
dotnet build Folio.slnx -c Release -p:FolioOracleHost=false -p:FolioWasmOracle=false
```

The checkout's global.json requires the patched .NET SDK (10.0.403 baseline). Windows system codecs are required for native media fixture tests; Visual Studio C++/Windows SDK tools are needed to publish the NativeAOT host.

Focused scripting command (exclude the existing real-output-device test):

```powershell
dotnet tests/Folio.Scripting.Tests/bin/Release/net10.0/Folio.Scripting.Tests.dll -class '*NativeMediaTests' -class '*CaptureDeviceLifetimeTests' -class '*CaptureCapabilityTests' -class '*CaptureAudioBufferTests' -class '*CapturePlaybackTests' -class '*ContentCaptureTransportTests' -class '*HostCaptureAuthorityTests' -class '*ContentCaptureBridgeTests' -class '*WorkerIsolationAcceptanceTests' -class '*ContentHostPolicyTests' -class '*ContentMediaCapabilitiesTests' -class '*ServiceWorkerSafetyLifecycleTests' -class '*ServiceWorkerCapabilityTests' -class '*ServiceWorkerReentrancyTests' -class '*GpuWorkerIsolationTests' -class '*StunMessageTests' -method- '*NativeDeviceAcceptsSilentPcmAndLifecycleCommands' -parallelMode none
dotnet tests/Folio.WinForms.Tests/bin/Release/net10.0-windows/Folio.WinForms.Tests.dll -class '*WindowsMediaCaptureProviderTests' -class '*WindowsNativeCaptureFormatTests' -class '*IframeHostDispatchTests' -parallelMode none
dotnet tests/Folio.Benchmarks/bin/Release/net10.0/Folio.Benchmarks.dll media 120
```

Local logs remain under ignored `artifacts/feature-*` and are not required to reproduce. The source tests/fixtures are pushed. Fix the four known failures before broadening verification; then run the full suite, genuine native conformance, fuzzing, fresh package consumers, published-host tests and CI. Obtain user permission before real camera/microphone/speaker or deliberate runaway-GPU testing. Complete GPU isolation and the remote WebRTC stack, qualify original acceptance, and only then promote each feature's classifier flag individually.

**Classifier is deliberately unchanged: among M6 features, only WebAssembly is production-enabled. This checkpoint must not be merged or described as completed feature work.**
