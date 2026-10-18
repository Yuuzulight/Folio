# Milestone 6 acceptance audit — 2026-10-18

Milestone 6 is **not complete against its original issue scope**. Earlier closure and completion claims were premature. This audit retains the original requirements rather than defining completion as the existing bounded API surface.

`Partial` means implemented behavior exists but does not prove the full acceptance; `Unverified` means the evidence is not strong enough to claim resolution. Only a later requirement-specific runtime check can change a row to verified. The table is a source/acceptance audit, not a claim that three complete review passes have already been performed for every issue.

## Repairs verified during this audit

- Media Foundation: correct GUIDs, HRESULT/COM layout, inherited method slots, apartment/startup balancing, actual audio metadata, EOS handling, RGB conversion/stride/duration, budget rejection and cleanup.

- Wave-out: pointer-sized WAVEHDR fields, bounded queue, paused startup, sample conversion and retaining driver-owned buffers.

- NativeMediaTests runs actual WAV/MP3/H.264 decoding on Windows; fake-driver tests exercise failures and queueing on every OS. A silent real-device test runs only when Windows has an output device.

- Jint compatibility engine: 4.17.0 replaces the 4.1 blocking-await workaround. Evaluations, calls and checkpoints use one owner rather than competing evaluation/pump threads. This is separate from the native engine requirements.

- Worker lifecycle, iframe isolated scripting/retirement and real conformance reopen now have direct regressions. None of these repairs by itself proves completion of the original umbrella issues.

## Original issue acceptance and current evidence

### #169 — WebGL (after milestone 5)

**Incomplete.** Evidence: `WebGL findings #528–#531`.

Requires a GPU/GLSL backend, not only canvas path rasterization and resource state.

### #170 — Audio and video playback (after milestone 5)

**Incomplete.** Evidence: `Media findings #532–#537`.

Native decoding and device output are being repaired; full timed video, sound policy, budgets and IPC remain incomplete.

### #171 — Web workers (after milestone 5)

**Incomplete.** Evidence: `Worker findings #539–#541`.

Threaded engine startup is implemented, but original worker-global/event-loop and zero-copy acceptance remains incomplete.

### #172 — WebAssembly (after milestone 5)

**Incomplete.** Evidence: `WebAssembly findings #543–#545`.

Native-engine execution/caching/routing requirement is unmet.

### #173 — Iframes and cross-document messaging (after milestone 5)

**Unverified.** Evidence: `Iframe findings #547–#550`.

Permitted child scripts now run in isolated contexts; original full messaging, sandbox forms, shared nested limits, input-boundary and process-watchdog acceptance is not proven.

### #174 — WebRTC (after milestone 5)

**Incomplete.** Evidence: `WebRTC findings #552–#554`.

Synthetic capture and local object loopback do not satisfy real permitted capture/transport requirements.

### #175 — Service workers (after milestone 5)

**Incomplete.** Evidence: `Service-worker findings #555–#559`.

Worker isolation, subresource interception and a real offline reopen test remain incomplete.

### #523 — Hosting: feature capabilities the host grants

**Partial.** Evidence: `FolioOptions.cs, ContentMessages.cs, DomBindings.UpdateCapabilities`.

Capability serialization exists. Sound/camera/microphone are not separate grants; per-feature diagnostic deduplication still needs verification.

Original acceptance:

- Unit tests: the default set is empty; a granted set reaches the content process in the load message; a run-time change reaches it as its own message.

- Using an ungranted capability produces one diagnostic per feature, not one per call.

- The IPC fuzz harness covers the new messages.

### #524 — ArtifactClassifier: recognise the stage-6 features and route them by capability

**Incomplete.** Evidence: `ArtifactClassifier.cs: WebAssembly and WebGl capability checks`.

Routing checks grants without proving a native WASM engine or a GPU/GLSL backend is available.

Original acceptance:

- Unit tests per feature, both ways: not built or not granted gives `NeedsBrowser`; built and granted (simulated by a test switch until a feature lands) lets the artifact through.

- The reason names the feature, so the host can tell the user why an artifact opened in the browser.

### #525 — Rendering: a clear fallback when a feature is off

**Unverified.** Evidence: `DisplayListBuilder.cs; AccessibilityTreeTests.cs`.

Fallback rendering exists. Each required failure reason, both colour schemes, layout invariance and runtime failure paths require dedicated evidence.

Original acceptance:

- Goldens for the placeholder per reason, light and dark.

- A reftest that the placeholder keeps the element's used size and does not change layout.

- Accessibility tree test: the placeholder has a name that says what is missing.

### #526 — Tests: sandbox, memory-cap and watchdog suites with each capability on

**Incomplete.** Evidence: `CapabilityMatrixTests.cs; .github/workflows/ci.yml`.

Rows exist, but base matrix assertions instantiate supervisors with tiny limits. They do not launch the sandbox and exercise network/filesystem/process denial per capability.

Original acceptance:

- The matrix runs in CI with one row per capability and one with all granted.

- A failing row says which capability it ran with.

### #527 — Conformance: a WebGL category

**Partial.** Evidence: `tests/conformance/D8: 6 manifests`.

Artifact count is met. Scene/GLSL coverage, interaction scripts and animation performance evidence do not establish the original acceptance.

Original acceptance:

- At least six artifacts with manifests, references and interaction scripts.

- The category shows up in the pass-rate report, all expected-to-fail.

### #528 — WebGL: GPU surface in the content process, frames to the host, and the GPU budget

**Incomplete.** Evidence: `Canvas2D.cs: WebGlContextState.DrawTriangles`.

Draws paths through ICanvas; no GPU device/translation-layer backend is wired. GPU process-memory benchmark acceptance is not established.

Original acceptance:

- Headless test: a cleared surface reaches the host and paints at the element's box, in paint order with the content around it.

- An allocation beyond the budget is refused and reported.

- No usable GPU gives the fallback from #525.

- The benchmark harness records the content process's GPU memory.

### #529 — WebGL: contexts, buffers, shaders and draw calls

**Incomplete.** Evidence: `DomNodeTemplates.cs: compileShader; Canvas2D.cs: DrawTriangles`.

Shader compilation checks for the words void/main. Drawing uses attribute zero and uniform zero directly, without executing GLSL.

Original acceptance:

- Unit tests on the binding surface: WebIDL conversions, errors, state queries.

- A triangle, an indexed mesh and an instanced draw match goldens.

- A malformed shader gives a diagnostic and an info log, never a crash; shader sources are fuzzed through validation.

### #530 — WebGL: textures, framebuffers and readPixels

**Incomplete.** Evidence: `Canvas2D.cs: DrawTriangles, texture dictionaries`.

Resource dictionaries and uploads exist; DrawTriangles does not sample bound textures or execute shader texture operations.

Original acceptance:

- Goldens: a textured quad, render-to-texture, mipmapped minification.

- `readPixels` unit tests for formats and flipping.

- Uploading past the budget is refused and reported.

### #531 — WebGL: context loss, the host switch and the limits tests

**Incomplete.** Evidence: `WebGlContextState and CapabilityMatrixTests.cs`.

Loss/restore state exists, but no executing shader backend means the runaway shader/device reset acceptance is unproven.

Original acceptance:

- Tests for the loss and restore cycle through the host switch and through the budget.

- The WebGL row of the sandbox matrix passes with WebGL-specific cases (GPU memory counted, a runaway shader).

- Classifier tests both ways; the WebGL conformance category passes.

### #532 — Conformance: an audio and video category

**Incomplete.** Evidence: `tests/conformance/D9: 5 manifests and HTML files`.

Count is met; player/timeline fixtures have no playable source. Fixed-time moving clip and seek image evidence is missing.

Original acceptance:

- At least five artifacts with manifests, references and interaction scripts.

- The category shows up in the pass-rate report, all expected-to-fail.

### #533 — Media: decoding through the operating system's media stack

**Partial.** Evidence: `MediaFoundationAudioDecoder.cs; NativeMediaTests.cs; ContentHost/Program.cs`.

This audit repairs ABI and adds actual WAV/MP3/H.264 thumbnail checks. Content process has no decoder adapter; canPlayType is hard-coded. A thumbnail is not a playback session.

Original acceptance:

- Unit tests through the fake decoder: metadata, frames, end of stream, errors.

- On the Windows runner, the files in the media category decode.

- A corrupt or truncated file gives a diagnostic, never a crash; Folio's own container probing is fuzzed.

### #534 — Media: the media element API, playback clock and autoplay policy

**Incomplete.** Evidence: `Canvas2D.cs: Play, SubmitAudioThrough; DomNodeTemplates.cs: currentTime`.

Clock/state exists. MediaPlaybackTests now covers mute/volume, script seeking, replay, loop tails and binding teardown. Remaining full state/event, source-selection and sound-grant semantics are unproven.

Original acceptance:

- Unit tests with the fake decoder for the state machine and event order.

- Autoplay policy tests both ways.

- Deterministic under the virtual clock.

### #535 — Media: video frames in the display list and audio output

**Incomplete.** Evidence: `MediaAudio.cs; Canvas2D.cs; ContentProtocol.cs; ContentHost/Program.cs`.

Decoder returns one image. No timed video-frame session or PCM IPC wiring; no synchronized audio/video or pacing evidence.

Original acceptance:

- Goldens for a frame at a fixed time with each `object-fit` value, and for the poster.

- Frame pacing test: p95 frame time within the performance bar while a video plays.

- With sound not granted, nothing reaches the audio output (checked with a fake output).

### #536 — Media: controls, captions and text tracks

**Partial.** Evidence: `WebVttParserTests.cs; native media controls in painting/input/accessibility`.

Parser and UI tests exist. Complete light/dark/cue goldens and keyboard/accessibility acceptance still need an evidence mapping.

Original acceptance:

- Goldens for the controls, light and dark, and for rendered cues.

- Keyboard operation tests; accessibility tree names and values for the controls.

- WebVTT parser unit tests; the parser is fuzzed.

### #537 — Media: budgets, the host switch and the limits tests

**Incomplete.** Evidence: `DomContext.GetMedia; MediaElementState constructor`.

Decoder slots are now reserved before loading/decoding. Encoded sources and decoded frame/PCM memory share a document budget, and reload, disable and teardown release reservations. Regression tests cover disabled/over-count loading, aggregate decoded memory, reload recovery and built-in WAV expansion. Poster/capture accounting and the real process sandbox/classifier/conformance acceptance remain unproven.

Original acceptance:

- Tests: going over a budget stops the extra decoder with a diagnostic, other media keep playing; the switch pauses and shows the fallback.

- The media row of the sandbox matrix passes with media-specific cases.

- Classifier tests both ways; the media conformance category passes.

### #538 — Conformance: a workers category

**Partial.** Evidence: `tests/conformance/D10: 4 manifests`.

Artifact count is met, but all D10 manifests lack interactions. Image-filter/module-worker workloads also remain unproven.

Original acceptance:

- At least four artifacts with manifests, references and interaction scripts.

- The category shows up in the pass-rate report, all expected-to-fail.

### #539 — Workers: dedicated worker threads with their own engine instance

**Partial.** Evidence: `WorkerState.cs`.

Workers now have a persistent owning thread and HTML event loop, timer/microtask globals, delayed parent-loop delivery and worker-owned teardown. Regressions exercise timer ordering, delayed messages, interruption and closure. Native second-engine validation and the fetch/crypto worker subset remain unproven.

Original acceptance:

- Unit tests on both engines: start, run, error events reach the page, `terminate` stops a busy loop through the engine's interrupt.

- Closing the document terminates its workers and releases their engines.

### #540 — Workers: postMessage with structured clone and transferables

**Incomplete.** Evidence: `WorkerState.CloneIntoWorker/CloneIntoParent`.

ArrayBuffers are copied into a new engine buffer; nested values serialize through JSON helpers. The required zero-copy 16 MB transfer benchmark is not satisfied.

Original acceptance:

- Unit tests per type, including cycles and transfer.

- A transferred buffer is detached on the sender and arrives without a copy (checked by a benchmark on a 16 MB buffer).

### #541 — Workers: memory caps, watchdog and the limits tests

**Unverified.** Evidence: `WorkerState.ExecuteWithWatchdog; engine limits tests`.

Timeout and count controls exist. Watchdogs now sleep between checks instead of spinning at above-normal priority, and exit before their engine is disposed. Eleven worker regressions pass with the test process restricted to one CPU core, including idle-worker contention, delayed delivery, busy-loop interruption and parent responsiveness. Aggregate memory caps and the per-capability real sandbox cases still need direct evidence.

Original acceptance:

- Tests: the worker limit is enforced; a worker over its memory limit fails alone; an endless loop in a worker is interrupted and the page stays responsive.

- The workers row of the sandbox matrix passes.

- Classifier tests both ways; the workers conformance category passes.

### #542 — Conformance: a WebAssembly category

**Partial.** Evidence: `tests/conformance/D11: 4 manifests`.

Count is met. Native compiled game/image-filter workloads and animation performance are not established by these fixtures.

Original acceptance:

- At least four artifacts with manifests, references and interaction scripts.

- The category shows up in the pass-rate report, all expected-to-fail.

### #543 — WebAssembly: the JavaScript API on the native engine, with linear memory capped

**Incomplete.** Evidence: `DomBindings.cs: __folio_wasm_functions and WebAssembly constructors`.

Production engine is Jint; WebAssembly is a custom JavaScript interpreter. The issue explicitly requires the native engine API and no WebAssembly global on Jint.

Original acceptance:

- Unit tests with small modules checked in as bytes: instantiate, imports and exports, traps as exceptions.

- Growing past the limit fails inside the page; the process survives.

- The same module runs in a worker.

### #544 — WebAssembly: streaming compilation and the compiled-module cache

**Incomplete.** Evidence: `DomBindings.cs: __folio_wasm_compile_cache`.

Cache is an in-page Map rather than native compiled code in the host cache folder; restart/cache timing benchmark is absent.

Original acceptance:

- Tests: a wrong MIME type rejects with `TypeError`; a module the host does not allow is refused with a diagnostic.

- A second load hits the cache and starts faster (measured in the benchmark harness); the cache stays under its cap.

### #545 — WebAssembly: the limits tests and routing

**Incomplete.** Evidence: `ArtifactClassifier.cs; CapabilityMatrixTests.cs`.

Classifier admits Jint WASM with a capability. Native-engine requirement and native-code watchdog coverage remain unmet.

Original acceptance:

- The WebAssembly row of the sandbox matrix passes with these cases.

- Classifier tests: granted on the native engine lets it through; Jint or not granted routes to the browser.

- The WebAssembly conformance category passes.

### #546 — Conformance: an iframes category

**Incomplete.** Evidence: `tests/conformance/D12: 4 manifests; Conformance.cs frame selector branch`.

Live-child frame-selector support now works, but all four D12 pages contain the same inert child paragraph. Required sandbox-message, scrolling-preview and form workloads are absent.

Original acceptance:

- At least four artifacts with manifests, references and interaction scripts.

- The runner resolves selectors inside frames; the category shows up in the pass-rate report, all expected-to-fail.

### #547 — Iframes: nested documents from srcdoc

**Partial.** Evidence: `IframeState.cs; static nested-document layout/input/accessibility tests`.

Nested rendering now has isolated child script engines/event loops/runners, with navigation and parent-binding teardown. Regressions cover script execution and isolation, relative script locations, sandbox gating, capability revocation and child host-media cleanup. Full focus/selection and process-watchdog acceptance still need a complete mapping.

Original acceptance:

- Reftests: a frame against the same content in a sized, scrolling `div`.

- Tests for hit testing, focus order and wheel scrolling into and out of a frame.

- The accessibility tree includes the frame's content.

### #548 — Iframes: sandbox flags, origins and src loads

**Partial.** Evidence: `IframeState.cs; DomContext.GetIframe`.

Empty sandbox flags block scripts and create an opaque origin; data-document origins are opaque. Child options inherit the host capabilities, resource loader and media adapters, and runtime revocation retires the child. Depth and local counts are bounded, but the aggregate nested-frame count is not shared. Child grants intersect the current host capabilities with explicit allow directives, including none/self/origin rejection tests. Form restrictions and the full Permissions Policy grammar remain unproven.

Original acceptance:

- Unit tests: flag parsing; scripts and forms blocked without their flags; opaque origin reported as `null`.

- A refused `src` shows the fallback; capability inheritance tests both ways.

- Nesting and count limits stop extra frames with a diagnostic.

### #549 — Iframes: postMessage and MessageChannel

**Partial.** Evidence: `IframeState.cs; WorkerState.cs; message port bindings`.

Origin/port paths exist. A transferred port routed frame to worker and full source identity/order coverage remain unverified.

Original acceptance:

- Tests: a wrong `targetOrigin` is not delivered; `*` is; the origin of an opaque frame arrives as `null`.

- A port transferred into a frame, and on to a worker, still delivers in order.

### #550 — Iframes: the limits tests and routing

**Unverified.** Evidence: `CapabilityMatrixTests.IframeCapabilityRowExercisesRoutingSandboxAndFrameLimit`.

A count check exists. A hung child context under the actual process watchdog is not established.

Original acceptance:

- The frames row of the sandbox matrix passes with these cases.

- Classifier tests both ways; the iframes conformance category passes.

### #551 — Conformance: a WebRTC category

**Incomplete.** Evidence: `tests/conformance/D13: 4 manifests`.

All four D13 pages only construct one RTCPeerConnection and append a ready label. Paired peers, data-channel exchange, fake capture/level-meter devices and video loopback workloads are absent.

Original acceptance:

- At least three artifacts with manifests, references and interaction scripts, using fake devices.

- The category shows up in the pass-rate report, all expected-to-fail.

### #552 — WebRTC: camera and microphone capture with host permission

**Incomplete.** Evidence: `DomBindings.cs: navigator.mediaDevices; Canvas2D.CreateCaptureFrame`.

Devices and frames are synthetic, with no host capture-device adapter. Revocation is polled on property access rather than immediate termination.

Original acceptance:

- Denied by default: `NotAllowedError`, and no device is opened.

- Granted with a fake device: frames reach a `video` element; the indicator shows while live and goes when stopped.

- Revoking mid-stream ends the tracks; closing the document releases the devices.

### #553 — WebRTC: peer connections and data channels to host-allowed peers

**Incomplete.** Evidence: `DomBindings.cs: RTCPeerConnection.createOffer/setRemoteDescription`.

Offer SDP is empty; peers connect by matching in-page state and pass local track objects. No allowlisted host network transport or real video-track pipeline.

Original acceptance:

- A loopback data channel and a loopback video track work in CI.

- A server or peer not on the allowlist is refused with a diagnostic.

- With peer connections not granted, no packet leaves (checked with a fake transport).

### #554 — WebRTC: the limits tests and routing

**Incomplete.** Evidence: `CapabilityMatrixTests.cs; content-process capture indicator`.

No real capture/transport to validate device release, bandwidth limits or network denial. In-process bookkeeping cannot prove this acceptance.

Original acceptance:

- The WebRTC row of the sandbox matrix passes with these cases.

- Killing the content process mid-call clears the in-use indicator and releases the devices.

- Classifier tests both ways; the WebRTC conformance category passes.

### #555 — Conformance: a service workers category

**Incomplete.** Evidence: `Conformance.cs: reopen command; tests/conformance/D14: 3 manifests`.

Reopen now disposes the old bindings, runner, event loop, engine and document, then creates a fresh load retaining only artifact storage and an offline artifact/pinned-cache loader. A regression proves different document/engine identities, invalidation of the old engine and storage survival. Frame interactions use the live rendered child. D14 still needs assertions proving real persisted service-worker interception after reopen; serving assets from the offline artifact loader alone is not that proof.

Original acceptance:

- At least three artifacts with manifests, references and interaction scripts, including an offline reopen.

- The category shows up in the pass-rate report, all expected-to-fail.

### #556 — Service workers: registration and lifecycle scoped to the artifact

**Incomplete.** Evidence: `DomBindings.cs: service-worker validator and workerSelf`.

Source is checked in an isolated engine, then handlers execute via new Function in the page engine. Required worker isolation and independent shutdown are not satisfied.

Original acceptance:

- Lifecycle unit tests in spec order.

- Closing the artifact leaves no worker thread or engine behind; reopening reuses the registration.

- Without the capability, `navigator.serviceWorker` is absent.

### #557 — Service workers: fetch interception

**Incomplete.** Evidence: `DomBindings.cs service-worker fetch hooks; Folio/Resources/Loading.cs`.

Fetch wrapper interception exists. Image and other document subresource requests do not share that hook before the loader.

Original acceptance:

- Tests: an image answered from the service worker, a synthetic response, a fall-through.

- A fetch from the service worker to a refused origin is refused with a diagnostic.

### #558 — Service workers: the Cache API with host storage limits

**Partial.** Evidence: `DomBindings.cs: Cache API; host ArtifactStorage`.

Cache.addAll delegates to Promise.all of individual add/put operations, so a later quota/fetch failure can leave earlier entries stored, violating the explicit atomicity acceptance. Request matching and quota accounting across persisted stores also remain unproven.

The content-process load path does not forward artifact storage. Its resource broker refuses denied requests but drops allowed requests without invoking a host loader. In-process storage uses ambiguous colon-separated artifact keys and exposes a raw storage bridge to page scripts, bypassing Cache API quota checks. These integration and isolation gaps must be repaired before closure.

Original acceptance:

- Unit tests for each method and request matching.

- Over the cap: `QuotaExceededError`, nothing partial stored; caches are isolated per artifact.

### #559 — Service workers: the limits tests and routing

**Incomplete.** Evidence: `CapabilityMatrixTests service-worker row; DomBindings service-worker handlers`.

In-page handlers cannot prove interrupting a hung service worker without stopping the page. The harness now recreates the page offline, but independent service-worker interruption and real intercepted subresources remain unproven.

Original acceptance:

- The service workers row of the sandbox matrix passes with these cases.

- Classifier tests both ways; the service workers conformance category passes.

### #560 — M6 Beyond artifacts: tracking

**Incomplete.** Evidence: `All rows below`.

Original acceptance is not satisfied. Previous closure and completion documentation were premature.
