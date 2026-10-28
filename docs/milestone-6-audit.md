# Milestone 6 acceptance audit — 2026-10-28

## Canvas animation performance (#519)

The frame harness supports both the compatibility engine and an explicit
Folio-native mode through `FOLIO_CONFORMANCE_ENGINE=native`, and reports
per-artifact script/update, display-list, host-paint and total p95 timings.
A fresh Release run (30 frames per artifact) shows the native engine below the
16.7 ms target for every D5/D7 artifact; the worst case is
`D7/canvas-snake-game` at 11.55 ms. The compatibility-engine run still misses
the target for `D5/canvas-animated-bars` at 46.44 ms and is borderline for
`D7/canvas-snake-game` at 16.92 ms; the other library-card artifacts remain
below target. Native-mode numbers are now acceptance
evidence for the Folio-owned execution path; the issue remains open for the
compatibility-engine misses rather than being hidden by routing.

Milestone 6 is **not complete against its original issue scope**. Earlier closure and completion claims were premature. This audit retains the original requirements rather than defining completion as the existing bounded API surface.

`Partial` means implemented behavior exists but does not prove the full acceptance; `Unverified` means the evidence is not strong enough to claim resolution. Only a later requirement-specific runtime check can change a row to verified. The table is a source/acceptance audit, not a claim that three complete review passes have already been performed for every issue.

## Verification refresh — 2026-10-30

Focused runtime checks now pass WebRTC/capture 31/31, Worker/Iframe 196/196,
media playback/native decoding 26/26, media budget/host-switch 37/37, native WebGL texture/framebuffer 74/74,
and WebGL fallback/loss/limits 123/123. These strengthen the deterministic
implementation evidence, but the corresponding issues remain open where their
original acceptance requires real device, process-teardown, runaway-shader or
optional-format behavior beyond the local harness.

## Verification refresh — 2026-10-28

The current audit branch was rechecked after the shaping changes: the Release
render suite passes 758/758 and the Release scripting suite passes 1,401/1,407
with six documented software-backend skips. The complete CI run
`40599053194` passed on Ubuntu and Windows, including all capability-matrix
rows. These checks confirm that the audited fixes did not regress the covered
surfaces; they do not promote any of the explicitly partial rows below to
complete.

The full serialized Release solution runner then passed 4,211 tests with zero
failures and three documented skips (4,214 discovered), including benchmarks,
core tests, render tests, scripting tests, WinForms tests and fuzz tests.

The native conformance runner independently passes D8 6/6, D9 5/5, D10 4/4,
D11 4/4, D12 4/4, D13 4/4 and D14 3/3, with the document-named-property
scope now wired for native inline handlers.

- The latest complete Release solution passes 4,211 tests with 0 failures and 3 documented environment/software skips (4,214 discovered). The run includes the worker, WebAssembly, WebRTC, iframe and service-worker sandbox regressions added during this audit, plus the latest render and event-loop diagnostics coverage. This verifies the covered subsets only; the remaining native backend, interruption and routing requirements remain open.
- Native Folio D8 conformance passes 6/6 with no crashes, hangs, limits or expected failures.
- Compatibility and native D9 both pass their current 5/5 corpus. A fresh native sweep passes D1 2/2 and D8–D14 at 30/30 (32/32 combined). These category results are current runtime evidence, not a claim that the broader native replacement gate is closed.
- Compatibility and native D2–D7 both pass 60/60. The final `D2/pantry-react-jsx` interaction now passes after the native evaluator was corrected to propagate labeled breaks out of switches; issue #645 is resolved. The broader Folio-owned engine replacement gate remains open.
- The native WebGL page, float/depth, GPU-surface and ANGLE backend checks pass 93/93 on this host.
- Focused media, worker, WebAssembly, iframe, WebRTC and service-worker compatibility checks pass 266/266.
- The focused native WebAssembly capability set passes 46/46, including the current
  Wasmtime-backed native bridge, limits, imports/exports and lifecycle checks. This
  is transition-path evidence only; it does not satisfy the required Folio-owned
  Wasm replacement or dependency-removal gate.
- The documentation acceptance suites for Milestones 1 through 6 also pass in the
  direct Release runner; these suites validate the documented wiring and evidence
  indexes, not the unresolved backend or replacement gates.
- Tracker reconciliation on 2026-10-28 reopened the nine child issues whose
  acceptance rows remain partial or incomplete: #527, #530, #531, #533, #535,
  #537, #545, #554 and #556. Their original scope and acceptance text is retained
  on GitHub with the audit status appended; verified-slice issues remain closed.
- These results verify the current compatibility paths and regressions only. They do not close the original M6 gates: complete WebGL 2/integer/volume conformance, continuous device-level media pacing and output, native-engine worker/Wasm execution, real permitted WebRTC capture/transport, isolated service-worker offline reopening, or equivalent non-Windows containment.

## Repairs verified during this audit

- Media Foundation: correct GUIDs, HRESULT/COM layout, inherited method slots, apartment/startup balancing, actual audio metadata, EOS handling, RGB conversion/stride/duration, budget rejection and cleanup.

- Wave-out: pointer-sized WAVEHDR fields, bounded queue, paused startup, sample conversion and retaining driver-owned buffers.

- NativeMediaTests runs actual WAV/MP3/H.264 decoding on Windows; fake-driver tests exercise failures and queueing on every OS. A silent real-device test runs only when Windows has an output device.

- Jint compatibility engine: 4.17.0 replaces the 4.1 blocking-await workaround. Evaluations, calls and checkpoints use one owner rather than competing evaluation/pump threads. This is separate from the native engine requirements.

- Worker lifecycle, iframe isolated scripting/retirement and real conformance reopen now have direct regressions. None of these repairs by itself proves completion of the original umbrella issues.

- Native `MessageChannel` delivery now has a direct regression (`NativeLibraryProbeTests.ProbeNativeMessageChannelDelivery`): a posted message reaches its handler after a native `HtmlEventLoop` turn. This narrows the remaining native React/JSX failure to reconciliation/shared-internals behavior rather than the basic scheduler transport.

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

**Acceptance verified; follow-up remains.** Evidence: `Iframe findings #547–#550`, `IframeState.cs`, `IframeLifecycleTests.cs`, iframe input/accessibility regressions, and native D12 passing 4/4.

Permitted child scripts now run in isolated contexts, and parent/child windows exchange structured-cloned messages through an origin-checked bridge. Original sandbox-form coverage, shared nested limits, input-boundary and process-watchdog acceptance is not proven.

### #174 — WebRTC (after milestone 5)

**Incomplete.** Evidence: `WebRTC findings #552–#554`.

Synthetic capture and local object loopback do not satisfy real permitted capture/transport requirements.

### #175 — Service workers (after milestone 5)

**Incomplete.** Evidence: `Service-worker findings #555–#559`.

Worker isolation, nested-frame budgeting and a real offline reopen test remain incomplete. Parent/child iframe windows now have a structured-clone message bridge with direct round-trip regression coverage.

### #523 — Hosting: feature capabilities the host grants

**Capability acceptance verified locally.** Evidence: `FolioOptions.cs`, `ContentMessages.cs`, `DomBindings.UpdateCapabilities`, the capability regression suites and actual content-process tests.

All ten grants are independent and default to denied, including sound, camera and microphone. Native permission checks and retained-handle retirement cover the implemented compatibility surfaces. Ten cases verify one denial diagnostic per feature despite repeated calls. Real-process tests cover load grants, dedicated runtime updates, retained WebAssembly state, revoke/regrant, malformed updates and fresh-document isolation. IPC parsing rejects duplicate capability fields and wrappers; the fuzz target parses capability payloads. The WinForms host orders loads and updates and rejects stale artifact updates. This verifies the hosting capability contract; the unfinished feature backends and the full process sandbox matrix remain separate requirements.

Original acceptance:

- Unit tests: the default set is empty; a granted set reaches the content process in the load message; a run-time change reaches it as its own message.

- Using an ungranted capability produces one diagnostic per feature, not one per call.

- The IPC fuzz harness covers the new messages.

### #524 — ArtifactClassifier: recognise the stage-6 features and route them by capability

**Routing acceptance verified.** Evidence: `ArtifactClassifier.cs`, `StageSixRoutingTests.cs`, `ArtifactClassifierTests.cs`.

Production availability is independent of host permission and remains disabled for all seven incomplete feature backends. Per-call internal test availability verifies every built/granted combination without a mutable global switch or public bypass. Reasons identify both incomplete support and missing permission independently. Event handlers, JavaScript links, optional calls and literal-property access share the feature scan; SharedWorker now follows the dedicated-worker capability and has a shared runtime/MessagePort regression. Eleven production regressions failed before the repair; the final change adds 88 cases, including every availability/grant combination and independent feature routing. Three fresh code reviews completed; the earlier focused Release verification passed 3,195 tests with one local symbolic-link privilege skip. The current complete Release totals are recorded above. This verifies routing, not the backend requirements in the feature issues.

Original acceptance:

- Unit tests per feature, both ways: not built or not granted gives `NeedsBrowser`; built and granted (simulated by a test switch until a feature lands) lets the artifact through.

- The reason names the feature, so the host can tell the user why an artifact opened in the browser.

### #525 — Rendering: a clear fallback when a feature is off

**Locally verified.** Evidence: `FeatureFallback.cs; DisplayListBuilder.cs; FeatureFallbackTests.cs; FeatureFallbackRenderTests.cs; FeatureFallbackPipelineTests.cs; FeatureFallbackLifecycleTests.cs; FeatureFallbackContentProcessTests.cs`.

Painting and accessibility share typed failure state. Thirty-six reviewed exact-pixel goldens (18 each for Windows and Linux) cover all nine reasons in light and dark. Bounds checks and a pixel reftest cover layout preservation; accessibility tests cover the missing feature/reason and authored names. PNG/SVG poster tests, native binding recovery and a real content-process pixel round trip exercise failure and recovery. Normal empty canvases/frames and allowed no-source media players remain valid. Three restarting reviews completed after the final correction; the Release solution passed 3,478 tests with one local symbolic-link privilege skip, and an isolated one-CPU run passed all 867 scripting cases with unchanged deadlines. See [feature failure presentation](feature-fallbacks.md). This verifies #525's presentation contract, not the unfinished native backends or #537's poster/capture memory accounting.

Original acceptance:

- Goldens for the placeholder per reason, light and dark.

- A reftest that the placeholder keeps the element's used size and does not change layout.

- Accessibility tree test: the placeholder has a name that says what is missing.

### #526 — Tests: sandbox, memory-cap and watchdog suites with each capability on

**Base acceptance verified.** Evidence: `WindowsContentLauncher.cs; WindowsJobObject.cs; WindowsSandboxTests.cs; ContentProcessWatchdogTests.cs; PreHandleContentLifecycleTests.cs; ContentProcessOwnershipTests.cs; ContentProcessClientCleanupTests.cs; .github/workflows/ci.yml`. The previously reported Release pre-handle lifecycle timeout now passes in three consecutive fresh test-process runs (2.6–2.7 seconds each); no code change was required, so #644 was an environment-only transient and is closed.

All twelve Windows rows passed locally: denied, each of the ten independent grants and all granted. Every row boots the actual shared content runtime inside an AppContainer and checks the native token and job configuration, host-file read/write denial, blocked sockets against a known reachable listener, child-process denial, hard memory termination, watchdog termination with a retained frame and rejection of a forged frame path. Each row also runs actual ContentHost/WinForms checks for applied capabilities, retained pixels and a visible stopped notice, pre-handle delivery, process replacement and static-mode retirement. A separate real-process regression proves cleanup despite a faulting host callback. Three restarting reviews completed after the final source correction with unchanged hashes. See [the sandbox contract](content-process-sandbox.md).

The original issue explicitly allows the base suite until a feature's closing issue adds its extra cases. Native GPU/shader, real media/capture/transport, native WebAssembly and isolated service-worker acceptance remain in those open issues. Sound output through process IPC is not implemented or claimed by this matrix. The non-Windows launch path does not yet provide equivalent OS containment.

Original acceptance:

- The matrix runs in CI with one row per capability and one with all granted.

- A failing row says which capability it ran with.

### #527 — Conformance: a WebGL category

**Partial.** Evidence: `tests/conformance/D8: 6 manifests`.

Artifact count and interaction evidence are now met: all six D8 manifests assert context/framebuffer/scene setup, and the particle/shader controls are exercised; D8 passes 6/6. The context-loss artifact now performs a real `WEBGL_lose_context` loss/restore cycle and asserts the restored callback in the native run. Full scene/GLSL goldens and animation-performance evidence remain separate acceptance work in #529–#531.

Original acceptance:

- At least six artifacts with manifests, references and interaction scripts.

- The category shows up in the pass-rate report, all expected-to-fail.

### #528 — WebGL: GPU surface in the content process, frames to the host, and the GPU budget

**Windows acceptance verified.** All fourteen CI jobs passed at `af77d65e4efec03b154c3f1265a6dfb0dcb0ed5c`; #528 is closed. Evidence: `AngleGpuBackend.cs`, `GpuDocumentBridge.cs`, `GpuSurfaceTests.cs`, `GpuProcessBenchmark.cs`.

The trusted clear-colour path creates a native ANGLE D3D11 device inside the Windows AppContainer, publishes pixels through the host-owned frame store and composes at the canvas box in display-list order. Tests check surrounding content, an overlapping sibling, aggregate reservations, refusal before raster allocation, actual missing-library fallback, revocation and retained-handle disposal. The benchmark records DXGI local/nonlocal content-process usage separately from resource reservations. Windows CI runs the benchmark; non-Windows native graphics remains unavailable. The WebGL scripting API and shader/draw requirements remain separate in #529–#531.

Original acceptance:

- Headless test: a cleared surface reaches the host and paints at the element's box, in paint order with the content around it.

- An allocation beyond the budget is refused and reported.

- No usable GPU gives the fallback from #525.

- The benchmark harness records the content process's GPU memory.

### #529 — WebGL: contexts, buffers, shaders and draw calls

**Acceptance verified for the native ANGLE path.** Evidence: `AngleNativeWebGlContext.Shaders.cs`, `AngleNativeWebGlContext.Buffers.cs`, `NativeWebGlPageTests.cs`, and `NativeWebGlShaderFuzz.cs`.

Native shader compilation/linking and execution now run through ANGLE. The page suite covers WebGL/WebGL2 triangles, indexed meshes, instanced draws and uniform-buffer bindings against independent goldens; malformed shaders preserve an info log and host diagnostic; and the native shader fuzz target exercises compile/status/log paths without allowing native failures to escape. The compatibility fallback remains available when native WebGL is unavailable, but it is no longer the implementation used by the native acceptance path.

Original acceptance:

- Unit tests on the binding surface: WebIDL conversions, errors, state queries.

- A triangle, an indexed mesh and an instanced draw match goldens.

- A malformed shader gives a diagnostic and an info log, never a crash; shader sources are fuzzed through validation.

### #530 — WebGL: textures, framebuffers and readPixels

**Incomplete.** Evidence: `AngleNativeWebGlContext.Textures.cs`, `WebGlBindingAdapter.cs`, and `NativeWebGlPageTests.cs`.

Native typed texture/framebuffer/renderbuffer objects now allocate through ANGLE, including immutable `texStorage2D` and `texStorage3D`, multisample renderbuffers, core WebGL2 renderbuffer formats, all WebGL2 clear-buffer overloads (`clearBufferfv`/`clearBufferiv`/`clearBufferuiv`/`clearBufferfi`), upload, framebuffer-copy, framebuffer-blit and sub-update RGB/RGBA byte, depth, integer, half-float and RGBA floating-point images (including 2D, cube-map and 3D/array subimages), expose tracked texture parameters and level dimensions, attach textures, cube faces, 3D/array layers or renderbuffers, report framebuffer status, feed native `readPixels` for RGBA/RGB byte rows, RGBA float output and packed RGB565/RGBA4444/RGBA5551 output with pack alignment, row length and skip offsets, honor `UNPACK_FLIP_Y_WEBGL` for 2D uploads, participate in 2D, cube-map, 3D and 2D-array shader sampling including `sampler2DShadow` and `usampler2D`, and generate 2D/3D mipmaps. Generated mip levels now update Folio's tracked dimensions and budget reservations, with direct success and budget-refusal regressions. Texture image uploads and immutable 3D storage now commit reservations and level metadata only after ANGLE succeeds, preventing native failures from leaking budget or leaving stale dimensions. Renderbuffer budget reservations are transactional: a native format failure releases the provisional reservation and preserves the prior allocation, with a direct WARP regression. Packed RGB565/RGBA4444/RGBA5551 texture uploads and sub-updates are now expanded to a canonical native RGBA path, with a direct native regression. Native page regressions also prove oversized texture uploads are refused with `OUT_OF_MEMORY` under the document GPU budget, explicit level-1 sampling after native mipmap generation, actual minification through `LINEAR_MIPMAP_NEAREST`, and invalid-format/invalid-length upload errors. Native extension discovery now exposes `EXT_color_buffer_float` and `EXT_float_blend` only when ANGLE reports the corresponding driver extension, preserving WebGL extension identity. A direct WARP probe confirms `RGBA16F` renderbuffer attachment remains `FRAMEBUFFER_UNSUPPORTED`; no texture-backed downgrade is claimed. Optional float renderbuffer formats, reviewed mipmap goldens and complete error/packing coverage remain unfinished.

Original acceptance:

- Goldens: a textured quad, render-to-texture, mipmapped minification.

- `readPixels` unit tests for formats and flipping.

- Uploading past the budget is refused and reported.

### #531 — WebGL: context loss, the host switch and the limits tests

**Partial.** Evidence: `CanvasElementState.cs`, `WebGlBindingAdapter.cs`, `WebGlObjects.cs`, `NativeWebGlPageTests.cs`, and `WebGlContextState` tests.

Native ANGLE loss and restoration now retire the old registry, invalidate old objects, recreate the native context on a capability regrant, and dispatch `webglcontextlost` / `webglcontextrestored` handlers. The native page suite verifies the transition and object retirement, including a host GPU/device-reset hook exposed through `DomBindings.NotifyGpuDeviceLost/Restored`. The real content-process regression `FeatureFallbackContentProcessTests.PermissionFailuresAndRecoveryPublishRealProcessPixels` also drives a live capability revoke/regrant through IPC and verifies both the fallback pixels and the restored native draw. Ordinary GPU-budget exhaustion correctly remains an `OUT_OF_MEMORY` API error without implicitly losing the context; runaway-shader behavior and the complete sandbox matrix remain unproven.

Original acceptance:

- Tests for the loss and restore cycle through the host switch and through the budget.

- The WebGL row of the sandbox matrix passes with WebGL-specific cases (GPU memory counted, a runaway shader).

- Classifier tests both ways; the WebGL conformance category passes.

### #637 — WebGL 2 shading and transform feedback

**Acceptance verified locally.** Cube maps, arbitrary-expression derivatives (including `fwidth`, `dFdx`, and `dFdy` of a computed expression), unsigned/integer uniforms and samplers, signed and unsigned integer 2D/3D/2D-array texture uploads and sampling (including immutable storage plus signed `texSubImage2D` and an explicit-layer unsigned `sampler2DArray` fetch), 3D and 2D-array textures, uniform-buffer bindings and native readback are covered by the current ANGLE path. The WebGL2 IDL now exposes the uniform-reflection enum constants, and a WARP regression verifies a block index, uniform index, std140 offset, block membership and `vec4` type as well as updating the bound block between draws. Transform feedback is wired end to end: typed opaque objects have context ownership and retirement, native create/bind/delete and pause/resume/begin/end calls are routed through WebIDL, varying declarations and reflection reach ANGLE, transform-feedback buffer targets participate in allocation and readback, and a WARP regression captures three vertex values into a buffer, unbinds the feedback target, and feeds that buffer into the next draw. The IDL now includes the signed integer attribute type constants, with direct WARP regressions for unsigned and signed vertex-buffer input. The dedicated native WebGL suite now passes 75/75 on this host, covering every currently implemented acceptance feature in #637; compatibility fallback and unsupported-device behavior remain tracked under #530/#531.

### #532 — Conformance: an audio and video category

**Compatibility and native category acceptance verified.** Evidence:
`tests/conformance/D9: 5 manifests and HTML files`; both compatibility and native
reports pass 5/5. The native event-handler scope now enumerates document-named
elements, so inline handlers such as `video.play()` and `frame.getContext()` use
the same host wrappers as direct DOM lookups. Broader media device/pacing and
replacement work remains tracked in #533/#535 and #579.

Count, references, interaction scripts and visual parity are met. The five bounded data-URI fixtures (WAV, PNG video and VTT captions) pass without crashes, script failures or expected-failure markers. Poster presentation, non-rendering audio, captioned playback, seek interactions and canvas capture are covered by the passing run.

Original acceptance:

- At least five artifacts with manifests, references and interaction scripts.

- The category shows up in the pass-rate report with all five artifacts passing.

### #533 — Media: decoding through the operating system's media stack

**Partial.** Evidence: `MediaFoundationAudioDecoder.cs; NativeMediaTests.cs; MediaPlaybackTests.cs; ContentHost/Program.cs`.

This audit repairs ABI and adds actual WAV/MP3/H.264 checks. Host/data-URL MIME metadata now reaches the configured decoder and is regression-tested. The Windows decoder now retains a bounded, timestamped sequence of H.264 frames (up to the shared output budget) rather than returning only a thumbnail; the native fixture test verifies multiple frames, monotonic timestamps and dimensions. Bounded content-process media decode and audio-output broker paths are implemented, and an isolated content-process regression proves host decoding plus audio start/submit delivery. Both broker directions now reject malformed media payloads before they reach the document or host device: invalid rate/channel counts, partial interleaved frames, empty payloads, non-finite samples, invalid timestamps and malformed frame images are covered. Retained video plus audio is charged against one aggregate decode budget, with direct IPC regressions. `canPlayType` now consults the configured decoder capability while retaining Folio's built-in format list. Continuous playback pacing and device-level acceptance remain open.

Original acceptance:

- Unit tests through the fake decoder: metadata, frames, end of stream, errors.

- On the Windows runner, the files in the media category decode.

- A corrupt or truncated file gives a diagnostic, never a crash; Folio's own container probing is fuzzed.

### #534 — Media: the media element API, playback clock and autoplay policy

The rendering-clock path now has a direct regression proving that a playing element with `playbackRate = 2` advances by 0.5 seconds over a 250 ms rendering interval. This strengthens clock/rate evidence; continuous device-level pacing and hardware acceptance remain open.

**Acceptance verified.** Evidence: `Canvas2D.cs: Play, AdvancePlayback, Seek`, `DomNodeTemplates.cs`, `MediaPlaybackTests`, `SoundCapabilityTests`, and `DomStyleDeclarationTests.MediaElementsExposeCapabilityGatedPlaybackState`.

The virtual-clock state machine covers mute/volume, script seeking, replay, loop tails, binding teardown, child-`source` selection and `currentSrc`, idempotent `play()`/`pause()` lifecycle dispatch, decoder-aware `canPlayType`, sound re-grant while playback is already running, timeupdate/pause/ended ordering, media handler properties, retryable autoplay rejection after muting, and distinct decode errors. The acceptance tests exercise both autoplay policy directions and deterministic playback under the virtual clock.

Original acceptance:

- Unit tests with the fake decoder for the state machine and event order.

- Autoplay policy tests both ways.

- Deterministic under the virtual clock.

### #535 — Media: video frames in the display list and audio output

**Partial.** Evidence: `MediaAudio.cs; Canvas2D.cs; MediaPlaybackTests.TimedVideoFramesFollowThePlaybackClock; ContentProtocol.cs; ContentHost/Program.cs`.

The decoder contract now accepts bounded timestamped video frames, and playback selects the active frame from the virtual media clock while retaining the full sequence under the shared budget. Focused protocol tests cover bounded PCM/video decode responses and audio submit/start/pause/flush/stop commands, a deterministic regression proves audio sample position and video-frame selection advance from the same virtual clock, and a production content-process test covers host decode plus audio start/submit delivery. Video frames and posters now use the same `object-fit`/`object-position` placement path as other replaced images, with contain/cover clipping regressions. Standalone goldens now cover every `object-fit` value plus the paused poster state. `Folio.Benchmarks media 120` measures 120 clock-driven display-list/raster frames (current local run: 0.49 ms median, 0.90 ms p95 against a 16.67 ms frame budget); device-level audio/video synchronization remains incomplete.

Original acceptance:

- Goldens for a frame at a fixed time with each `object-fit` value, and for the poster.

- Frame pacing test: p95 frame time within the performance bar while a video plays.

- With sound not granted, nothing reaches the audio output (checked with a fake output).

### #536 — Media: controls, captions and text tracks

**Acceptance verified.** Evidence: `WebVttParserTests.cs; MediaPlaybackTests.cs; AccessibilityTreeTests.cs; MediaRenderTests.cs; tests/goldens/media/runtime-controls-and-cues-{light,dark}.png; native media controls in painting/input/accessibility`.

The WebVTT parser, keyboard operation, accessibility names/values and light/dark control-plus-active-cue rendering are directly covered. The reviewed runtime goldens exercise controls and a visible cue in both color schemes; parser fuzz coverage is included in the fuzz target set.

Original acceptance:

- Goldens for the controls, light and dark, and for rendered cues.

- Keyboard operation tests; accessibility tree names and values for the controls.

- WebVTT parser unit tests; the parser is fuzzed.

### #537 — Media: budgets, the host switch and the limits tests

**Partial.** Evidence: `DomContext.GetMedia; MediaElementState constructor; MediaResourceBudget; MediaPlaybackTests.PosterDecodedPixelsAreCountedByTheSharedMediaBudget`.

Decoder slots are now reserved before loading/decoding. Encoded sources and decoded frame/PCM memory share a document budget, and reload, disable and teardown release reservations. The complete retained timestamped video sequence is now charged to that budget rather than only the currently selected frame, with a two-element aggregate-budget regression. Regression tests cover disabled/over-count loading, aggregate decoded memory, reload recovery and built-in WAV expansion. Poster encoded bytes and decoded pixels use a byte-only lease in the shared media ledger; identical static source/poster sharing, external poster image-kind routing, and synthetic `srcObject` capture-frame allocation all have direct budget regressions. A real content-process regression now revokes and re-grants media on a live element, verifies the fallback state, reloads the decoder and restores `readyState`. The real Windows sandbox baseline has also been executed with the `media` capability row (file, network, child-process, token and job limits all pass), but media-specific sandbox cases and the complete classifier/conformance acceptance remain open.

Original acceptance:

- Tests: going over a budget stops the extra decoder with a diagnostic, other media keep playing; the switch pauses and shows the fallback.

- The media row of the sandbox matrix passes with media-specific cases.

- Classifier tests both ways; the media conformance category passes.

### #538 — Conformance: a workers category

**Acceptance verified.** Evidence: `tests/conformance/D10: 4 manifests`; native D10 passes 4/4.

Artifact count is met and all four D10 manifests now include wait/text interaction scripts plus checked-in worker sources. The report executes all four interactions and they pass after the fixtures use explicit DOM lookups (the harness intentionally does not synthesize implicit element globals). Dedicated and module worker message delivery is therefore wired through the conformance event loop. The original expected-to-fail marker is intentionally absent now that the category passes; keeping a stale marker would turn the green result into an unexpected-pass failure.

Original acceptance:

- At least four artifacts with manifests, references and interaction scripts.

- The category shows up in the pass-rate report, all expected-to-fail.

### #539 — Workers: dedicated worker threads with their own engine instance

**Acceptance verified; broader Web-platform parity remains follow-up.** Evidence: `WorkerState.cs`, `WorkerCapabilityTests`, `WorkerEventLoopTests`, `NestedWorkerCapabilityTests`.

Workers now have a persistent owning thread and HTML event loop, timer/microtask globals, delayed parent-loop delivery and worker-owned teardown. Dedicated and shared workers use the same isolated runtime; SharedWorker instances are keyed by URL/name and connect through transferred MessagePorts. Worker installation no longer depends on the initial WebAssembly grant or installs a Window/document. The worker-only platform includes restricted host-brokered fetch, UTF-8 streaming encoding/decoding, native cryptographic random values, base64, Blob/object URLs, basic URL/form collections, WorkerNavigator metadata, EventTarget/AbortController and message events with once/abort listener behavior. Console diagnostics identify the worker script and arrive through a bounded document-owner queue, including messages reported before close. Classic importScripts executes through a private captured bridge; a missing source reports a load failure instead of fabricating an echo.

Nested workers use the same native factory and root worker-count registry; capability updates reach descendants, and parent close/termination retires its children. Registration precedes artifact loading, and revocation cancels pending startup/import/fetch loads. Existing initially denied workers observe later WebAssembly grants, retained APIs observe revocation/regrant, and retired document contexts cannot authorize old workers. Failed loads release their slots. Terminated workers unregister from both root and parent registries. Weak-reference tests also prove cached Jint host methods do not keep their original receiver alive after a nested worker is dropped; callbacks capture the engine and resolve the actual invocation receiver.

Actual content-process tests cover initial grants, runtime changes, nested Blob workers, fetch, encoding, crypto, events, tagged diagnostics and recreation after worker revocation. A native-engine worker now has a separate owning runtime and passes real parent-to-worker-to-parent message round trips for primitives, objects, arrays, cycles, Map/Set, aliases and transferred ArrayBuffers; native workers use the shared graph codec through the bounded worker-platform/event-target path. Both engines cover startup, execution, worker errors, busy-loop interruption and document-close teardown. URL/form/encoding support remains a bounded subset, module imports still use simplified preprocessing, constructor startup remains synchronous, and complete worker-global/Web-platform conformance is unproven; these are follow-ups beyond the issue's acceptance. D10 and the Windows sandbox worker row now verify the feature through the conformance and content-process paths.

Original acceptance:

- Unit tests on both engines: start, run, error events reach the page, `terminate` stops a busy loop through the engine's interrupt.

- Closing the document terminates its workers and releases their engines.

### #540 — Workers: postMessage with structured clone and transferables

**Acceptance verified; follow-up remains.** Evidence: `StructuredCloneCodec.cs`, `MessagePortState.cs`, `MessagePortBindings.cs`, `WorkerState.cs`; 90 worker/ArrayBuffer tests and the 7-test benchmark suite pass, including the 16 MiB transfer benchmark.

Workers, MessagePorts and global structuredClone now share an engine-neutral graph codec, with owner-thread materialization, cycles/aliases, sparse arrays, Map/Set, Date/RegExp/Error/BigInt and typed-buffer views. Transfers detach source buffers and move port mailboxes into new receiving-engine wrappers. Private captured intrinsics and native object-brand checks prevent artifact constructors/prototypes from defining the transport. Options dictionaries and iterable transfer lists work in both worker directions.

Message delivery uses separate HTML tasks with microtask checkpoints. Listener removal and callback failures are independent of decode failures; receiver allocation refusal fires messageerror and permits subsequent delivery. Pending mailbox storage shares an engine budget with worker queues; reservation refusal precedes transfer commit. Shutdown drops queued commands and closes abandoned transferred channels, including deep transfer chains without host recursion. Direct regressions exercise worker-created ports transferred to the parent and surviving worker termination.

Explicitly transferred ArrayBuffer bytes now travel as host-owned transfer payloads with opaque clone tokens, avoiding base64 expansion; ordinary structured clones retain the encoded graph path. The Jint adapter now takes the sender's backing array through its transfer-only ownership seam and the receiving ArrayBuffer adopts that array. `TransferBenchmark` measures a 16 MiB cross-engine transfer and asserts backing-array identity and sentinel bytes. Native-engine worker regressions cover the same graph codec, cycles, Map/Set, aliases and transferred buffers end to end. The issue's stated acceptance is met; complete Web-platform conformance remains a separate follow-up.

Original acceptance:

- Unit tests per type, including cycles and transfer.

- A transferred buffer is detached on the sender and arrives without a copy (checked by a benchmark on a 16 MB buffer).

### #541 — Workers: memory caps, watchdog and the limits tests

**Acceptance verified; conservative accounting remains documented.** Evidence: `WorkerState.ExecuteWithWatchdog; WorkerCapabilityTests; WindowsSandboxTests.NativeSandboxProbeDeniesFileNetworkAndChildProcessForEveryCapabilityRow` (workers and all rows).

Timeout and count controls exist. Watchdogs now sleep between checks instead of spinning at above-normal priority, fail only the worker on timeout and exit before its engine is disposed. Runtime import waits use worker-owned cancellation and a load timeout. A paused-loader regression proves parent responsiveness and engine release while the host response remains pending; the worker message-queue lock is not held across script/host execution. Worker regressions also cover one-CPU contention, delayed delivery and busy-loop interruption, including native-engine busy-loop and native WebAssembly watchdogs that leave the page responsive. Worker fetch bodies and object-URL source storage reserve from the engine message budget; body release, URL revocation and teardown return those reservations. A document-wide aggregate worker-memory ledger now reserves each worker's configured VM ceiling before startup, rejects excess dedicated/shared/nested workers with `QuotaExceededError`, and releases the reservation on termination (direct regression coverage included). Jint still does not expose live heap usage, so this is a conservative reservation gate rather than measured allocation accounting. Retired Jint runtimes now release captured intrinsics, event-loop callbacks, clone helpers and exception identity mappings; terminated worker handles release their worker-global handles. Nine weak-reference regressions retain the outer engine, translated exception or worker handle while proving the old runtime is collectible, including parent reconfiguration. The Windows sandbox matrix now runs a worker in the real content process for the `workers` and `all` rows and verifies the parent receives the worker result while the process remains under the same token/job restrictions.

Original acceptance:

- Tests: the worker limit is enforced; a worker over its memory limit fails alone; an endless loop in a worker is interrupted and the page stays responsive.

- The workers row of the sandbox matrix passes.

- Classifier tests both ways; the workers conformance category passes.

### #542 — Conformance: a WebAssembly category

**Acceptance verified; follow-up remains.** Evidence: `tests/conformance/D11: 4 manifests`; native D11 passes 4/4.

Count and interaction evidence are now met: all four D11 manifests wait for and assert a distinct WebAssembly result, and native D11 passes 4/4. Native compiled game/image-filter workloads, worker-side module coverage and animation performance are follow-up coverage beyond this issue's stated acceptance.

Original acceptance:

- At least four artifacts with manifests, references and interaction scripts.

- The category shows up in the pass-rate report, all expected-to-fail.

### #543 — WebAssembly: the JavaScript API on the native engine, with linear memory capped

**Acceptance verified; follow-up remains.** Evidence: `WebAssemblyBindings.cs`, `DomBindings.cs`, 62 native/WebAssembly regression tests passing, and the native worker watchdog/module coverage.

The compatibility implementation still preserves linear-memory contents across `Memory.grow()` and enforces its byte/page limits. A Wasmtime-backed native execution path is now wired for granted numeric modules, including JavaScript function imports; it applies a per-store memory limit and retires with the document. Native epoch interruption now traps runaway native execution at the production deadline without sharing the execution lock, and instance/store teardown precedes cached-module and engine disposal. Known Wasmtime fail-fast trap encodings are routed through Folio's compatibility trap path so page errors remain catchable and cannot terminate the host. Local and imported native memory exports now expose synchronized `buffer`/`grow()` behavior, numeric local and imported globals use type-safe native accessors, and dedicated workers install and retire their own native bridge. The JavaScript binding now passes typed-array module bytes into the native bridge, and direct page plus worker regressions prove native selection and bounded runaway execution, including a native worker module that is interrupted by the execution deadline. Commit `19d6220` adds a real native evaluator regression for `WebAssembly.Instance(...).exports.run()` and fixes the Proxy export lookup and constructor identity needed by that path. `NativeWebAssemblyTests` directly exercises native execution, JS import callbacks, externref identity preservation, local and imported memory synchronization, numeric globals, over-budget rejection, cache reuse and disposal, local and imported funcref tables with null-reference handling, native-origin callbacks, typed ordinary JavaScript callbacks, and the runaway-execution deadline. The issue's stated acceptance is met; reference-valued tables beyond the supported externref bridge, streaming timing evidence, worker second-engine accounting and full sandbox acceptance remain follow-up work.

Original acceptance:

- Unit tests with small modules checked in as bytes: instantiate, imports and exports, traps as exceptions.

- Growing past the limit fails inside the page; the process survives.

- The same module runs in a worker.

### #544 — WebAssembly: streaming compilation and the compiled-module cache

**Acceptance verified; follow-up remains.** Evidence: `DomBindings.cs: __folio_wasm_compile_cache`, `NativeWebAssemblyRuntime.cs`, streaming MIME/cache tests, host-refusal diagnostics, and the WebAssembly benchmark.

The native path now uses a bounded, shared Wasmtime compiled-module cache keyed by validated module bytes; module construction and streaming compilation warm that cache, and direct tests prove repeated instantiation reuses one compiled module within the configured byte cap. `NativeWebAssemblyTests.StreamingCompilationWarmsAndUsesTheNativeModuleCache` and the invalid-MIME test cover the streaming API; the restricted-fetch suite proves refused host URLs reject with `TypeError` and `ResourceNotLoaded` diagnostics. Fetch `Request.integrity` now propagates into the host `ResourceRequest` for both document and worker fetches, so allowlisted streaming sources can enforce SRI before caching. `Folio.Benchmarks wasm 5` records cold versus warm native instantiation (the current Windows run measured 30.6 ms cold and 0.0 ms warm median with one cached module). The issue's stated acceptance is met; streaming responses still materialize their bytes before validation, and host-persistent cache timing remains follow-up work.

Original acceptance:

- Tests: a wrong MIME type rejects with `TypeError`; a module the host does not allow is refused with a diagnostic.

- A second load hits the cache and starts faster (measured in the benchmark harness); the cache stays under its cap.

### #545 — WebAssembly: the limits tests and routing

**Partial; sandbox evidence verified, routing remains open.** Evidence: `ArtifactClassifier.cs; CapabilityMatrixTests.cs; WindowsSandboxTests.NativeSandboxProbeDeniesFileNetworkAndChildProcessForEveryCapabilityRow` (WebAssembly and all rows).

Classifier admits the compatibility WebAssembly surface with a capability. The native path now has direct import-free execution, JavaScript function imports, imported funcref tables with native-origin and typed page numeric callbacks, store-limit and native epoch-deadline tests, and native worker regressions prove both module construction/execution and bounded runaway execution under the worker execution limit. The streaming integration regression also proves `instantiateStreaming` reaches the native cache, and invalid MIME rejection is covered directly. The Windows content-process matrix now executes a WebAssembly module for the `webassembly` and `all` rows while retaining the same token/job restrictions. Native routing policy, reference-value tables and streaming-to-native timing evidence remain unmet.

Original acceptance:

- The WebAssembly row of the sandbox matrix passes with these cases.

- Classifier tests: granted on the native engine lets it through; Jint or not granted routes to the browser.

- The WebAssembly conformance category passes.

### #546 — Conformance: an iframes category

**Acceptance verified.** Evidence: `tests/conformance/D12: 4 manifests; Conformance.cs frame selector branch; D12 category 4/4`.

Live-child frame-selector support now works. The four D12 pages now have distinct child scripts and frame-scoped assertions for click delivery, sandbox startup, a nested overflow container and a form-bearing child; the category passes 4/4. The original category acceptance is met; richer visual and cross-document interaction coverage remains follow-up work.

Original acceptance:

- At least four artifacts with manifests, references and interaction scripts.

- The runner resolves selectors inside frames; the category shows up in the pass-rate report, all expected-to-fail.

### #547 — Iframes: nested documents from srcdoc

**Acceptance verified; process-watchdog follow-up remains.** Evidence: `IframeState.cs; IframeLifecycleTests.cs; DomStyleDeclarationTests iframe layout/input/accessibility coverage`.

Nested rendering now has isolated child script engines/event loops/runners, with navigation and parent-binding teardown. Both Jint and the Folio-owned native engine enforce bounded script execution in the child context. Regressions cover the sized/scrolled reftest path, hit testing, focus order, wheel scrolling, selection, accessibility, script execution/isolation, relative script locations, sandbox gating, capability revocation, child host-media cleanup and a shared aggregate nested-frame budget. The separate OS process-watchdog mapping remains follow-up work beyond the issue's acceptance.

Original acceptance:

- Reftests: a frame against the same content in a sized, scrolling `div`.

- Tests for hit testing, focus order and wheel scrolling into and out of a frame.

- The accessibility tree includes the frame's content.

### #548 — Iframes: sandbox flags, origins and src loads

**Acceptance verified; follow-up remains.** Evidence: `IframeState.cs; DomContext.GetIframe; 34 iframe tests passing, including native sandbox/origin, refused-src fallback, capability inheritance, forms blocking, and nesting/count limits.

Empty sandbox flags block scripts and create an opaque origin; data-document origins are opaque. Child options inherit the host capabilities, resource loader and media adapters, and runtime revocation retires the child. Depth and local counts are bounded, and nested contexts now share the root document's aggregate iframe budget. Child grants intersect the current host capabilities with explicit allow directives, including wildcard, none, self, src, quoted-origin and HTML-whitespace forms; the iframe lifecycle suite covers these cases (27/27 passing), including native-engine sandboxed script/origin behavior. `FormsAllowed` now propagates into child options and form `submit`/`requestSubmit` throw `SecurityError` when forms are not granted. The broader process/watchdog acceptance remains unproven.

Original acceptance:

- Unit tests: flag parsing; scripts and forms blocked without their flags; opaque origin reported as `null`.

- A refused `src` shows the fallback; capability inheritance tests both ways.

- Nesting and count limits stop extra frames with a diagnostic.

### #549 — Iframes: postMessage and MessageChannel

**Acceptance verified.** Evidence: `IframeState.cs`, `MessagePortState.cs`, and `IframeLifecycleTests.WindowMessagesEnforceTargetOriginAndExposeOpaqueOrigin`, `IframeLifecycleTests.WindowMessagesExposeCorrectOriginSourceAndTarget`, and `IframeLifecycleTests.TransferredPortCanCrossAnIframeIntoAWorkerInOrder`.

Wrong target origins are rejected, wildcard delivery works, opaque sandboxed frames report `origin === "null"`, and both directions expose the correct source and target identities. A transferred `MessagePort` crosses into a frame, is forwarded to a dedicated worker, and returns its result in order; the test drives the child event loop through the normal rendering path rather than calling private internals.

The native staged engine now also preserves constructor receivers for `MessageChannel`, exposes callable native port methods, and passes direct structured-clone object/array and iframe window-message round trips after `MessagePort.start()`/`addEventListener('message', ...)`. This strengthens native wiring evidence but does not close the broader iframe/process isolation requirements.

Original acceptance:

- Tests: a wrong `targetOrigin` is not delivered; `*` is; the origin of an opaque frame arrives as `null`.

- A port transferred into a frame, and on to a worker, still delivers in order.

### #550 — Iframes: the limits tests and routing

**Acceptance verified; follow-up remains.** Evidence: iframe capability-matrix row passes, `IframeLifecycleTests.NestedFramesShareTheAggregateIframeLimit`, 87 StageSixRouting tests, and native D12 passing 4/4.

A shared aggregate count check now exists alongside the routing matrix. A hung child context under the actual process watchdog is not established.

Original acceptance:

- The frames row of the sandbox matrix passes with these cases.

- Classifier tests both ways; the iframes conformance category passes.

### #551 — Conformance: a WebRTC category

**Acceptance verified; follow-up remains.** Evidence: `tests/conformance/D13: 4 manifests; native category 4/4.

The four D13 pages now exercise distinct offer negotiation, paired in-page data-channel setup, synthetic camera capture and synthetic microphone capture, each with an interaction assertion; D13 passes 4/4. The implementations are still synthetic/in-process: host device adapters, real transport denial and process teardown evidence remain open.

Original acceptance:

- At least three artifacts with manifests, references and interaction scripts, using fake devices.

- The category shows up in the pass-rate report, all expected-to-fail.

### #552 — WebRTC: camera and microphone capture with host permission

**Acceptance verified; follow-up remains.** Evidence: `CaptureBindings.cs`, `FolioOptions.MediaCaptureProvider`, `CaptureCapabilityTests`, `ContentCaptureBridgeTests`, and activity/revocation regressions.

The API now has an explicit host-owned `IMediaCaptureProvider`/`IMediaCaptureSession` seam: approved requests open a session, host RGBA frames reach a `video` element, activity is reported while tracks are live, and the session is disposed when the final track stops or the document retires. The separate content process receives the provider through a bounded capture IPC bridge and can poll or receive host-owned RGBA/PCM samples and revocation events, with the WinForms launcher forwarding the provider into that bridge. `ContentCaptureBridgeTests` proves a real content-host process opens the provider, receives a pushed frame, observes revocation, and disposes the session. The issue's stated fake-device acceptance is met; real OS camera/microphone adapters and device-level conformance remain outstanding.

Original acceptance:

- Denied by default: `NotAllowedError`, and no device is opened.

- Granted with a fake device: frames reach a `video` element; the indicator shows while live and goes when stopped.

- Revoking mid-stream ends the tracks; closing the document releases the devices.

### #553 — WebRTC: peer connections and data channels to host-allowed peers

**Acceptance verified; follow-up remains.** Evidence: `WebRtcBindings.cs`, `WebRtcCapabilityTests`, the in-page data/video loopback regression, allowlist refusal tests, and capability-denial tests.

Offers and answers now contain deterministic session descriptions with data/media sections, and loopback regressions cover negotiation, data channels and video tracks on the compatibility and Folio-owned native engines (`WebRtcCapabilityTests.NativeEngineLoopbackPeerNegotiationDeliversData`). Allowlist refusal and denied-capability tests prove blocked peers do not acquire or deliver. The issue's stated in-process acceptance is met; peers still connect by matching in-page state and passing local track objects, with no allowlisted host network transport or real encoded video-track pipeline yet.

Original acceptance:

- A loopback data channel and a loopback video track work in CI.

- A server or peer not on the allowlist is refused with a diagnostic.

- With peer connections not granted, no packet leaves (checked with a fake transport).

### #554 — WebRTC: the limits tests and routing

**Incomplete; sandbox row execution is now directly covered, but process/device/network acceptance remains open.** Evidence: `CapabilityMatrixTests.cs; WindowsSandboxTests.NativeSandboxProbeDeniesFileNetworkAndChildProcessForEveryCapabilityRow`; content-process capture indicator tests.

The Windows content-process matrix now creates and closes a data channel for the `webrtc` and `all` rows under the real sandbox token/job restrictions. Real capture/transport is still absent, so device release after process death, bandwidth limits and network denial are not yet proven; in-process bookkeeping cannot satisfy those parts of the acceptance.

Original acceptance:

- The WebRTC row of the sandbox matrix passes with these cases.

- Killing the content process mid-call clears the in-use indicator and releases the devices.

- Classifier tests both ways; the WebRTC conformance category passes.

### #555 — Conformance: a service workers category

**Acceptance verified; follow-up remains.** Evidence: `Conformance.cs: reopen command; tests/conformance/D14: 3 manifests and checked-in sw.js sources`; native and Jint D14 both pass 3/3.

Reopen now disposes the old bindings, runner, event loop, engine and document, then creates a fresh load retaining only artifact storage and an offline artifact/pinned-cache loader. Focused lifecycle tests prove different document/engine identities, invalidation of the old engine, storage survival and restoration of a persisted registration. The native conformance runner now reports D14 3/3 after the reopened-page response bridge was fixed: native dispatch uses a synchronous seam and reconstructs a page-owned `Response` instead of passing an isolated-engine prototype through the promise bridge. The broader native backend, interruption and sandbox requirements remain open.

Original acceptance:

- At least three artifacts with manifests, references and interaction scripts, including an offline reopen.

- The category shows up in the pass-rate report, all expected-to-fail.

### #556 — Service workers: registration and lifecycle scoped to the artifact

**Incomplete.** Evidence: `ServiceWorkerBindings.cs: service-worker validator and workerSelf`.

Source validation now fails closed (`TimeoutError` for bounded-execution failure and `SyntaxError` for ordinary script errors), and handler execution uses an explicit worker scope that hides page-only globals and keeps worker state out of the page global. Native-engine documents now run service-worker scripts in a separate native engine and retire/recreate it across revocation and regrant, with direct lifecycle, isolation, interception, page-message reply and `clients.claim()` controller regressions. Broker-backed content processes now use the same isolated worker runtime for lifecycle, fetch and message dispatch while cache operations continue through the broker-safe backend; a real content-process regression proves page-only globals remain unavailable and the cache persistence suite remains green. A complete independent runtime/sandbox matrix across every backend is not yet satisfied.

Original acceptance:

- Lifecycle unit tests in spec order.

- Closing the artifact leaves no worker thread or engine behind; reopening reuses the registration.

- Without the capability, `navigator.serviceWorker` is absent.

### #557 — Service workers: fetch interception

**Acceptance verified; lifecycle/sandbox follow-up remains separate.** Evidence: `DomBindings.cs` service-worker fetch hooks, `Folio/Resources/Loading.cs`, `DomContext.cs` service-worker resource interceptor, `ServiceWorkerCapabilityTests` (Jint/native fall-through and refused-origin regressions), `ContentCacheTests`, and native/Jint D14 3/3.

Fetch wrapper interception restores persisted registrations before a reopened document's first fetch on the compatibility path. Restoration is deliberately deferred until Cache/Fetch bindings are installed, so an install handler using `caches.open()` is valid during process reopen; `ContentCacheTests.PersistedServiceWorkerInterceptsTheFirstFetchAfterProcessReopen` proves the first offline fetch is intercepted in a fresh content process. Artifact-prefixed storage tests prove registrations do not cross artifacts, and a capability-revocation-on-reopen test keeps the persisted registration hidden when service workers are not granted. Image, stylesheet, font, document and media requests share the controller hook, and focused tests cover content types, binary bodies and refused origins. The native isolated runtime dispatches worker code and native D14 now passes 3/3 after the response bridge fix. Jint/native regressions prove same-origin fall-through, synthetic interception and cross-origin refusal diagnostics, while content-process cache tests cover brokered persistence and worker-originated writes. Independent service-worker interruption and the sandbox matrix remain tracked by #556/#559.

Original acceptance:

- Tests: an image answered from the service worker, a synthetic response, a fall-through.

- A fetch from the service worker to a refused origin is refused with a diagnostic.

### #558 — Service workers: the Cache API with host storage limits

**Acceptance verified; service-worker isolation remains tracked separately.** Evidence: `CacheBindings.cs`, `CacheStorageState.cs`, `ContentCacheBroker.cs`, `ContentStorageTransport.cs`, `CacheApiTests`, and `ContentCacheTests`.

The native host owns the canonical per-artifact cache and quota ledger. All Cache/CacheStorage methods, request URL/method/search/Vary matching, independent binary responses and atomic addAll have direct regressions. A transaction is persisted with one atomic provider write before publishing memory; failure and quota tests establish rollback. Quota includes metadata, encoded bodies, unopened persisted caches and live retired handles. Private Fetch state and an inaccessible storage bridge prevent script-visible quota bypasses. Length-delimited artifact snapshot keys prevent colon/slash collisions.

Real content processes now receive host-owned artifact storage and limits through FolioView and ContentProcessClient. Semantic storage operations reach the same canonical host state as in-process documents, preserving concurrent writes. Tests cover process restart without network, service-worker-originated cache writes, parent profile forgery, namespace isolation, failed writes, remote retired-handle collection, incomplete/corrupt uploads, connection closure, blocked host callbacks and terminal handling of uncertain commits. A 13 MiB binary payload exercises chunked requests and replies larger than the unchanged 16 MiB frame limit. Prototype cache entries migrate atomically, preserve backups on failure and fail closed when old Vary metadata cannot be recovered. See [storage configuration and limits](cache-api-storage.md).

These checks verify the Cache API contract, including service-worker-originated writes, host quotas, rollback and artifact isolation. They do not establish the separate isolated service-worker engine, full subresource interception or the sandbox matrix required by #556/#557/#559; those remain separate issues.

Original acceptance:

- Unit tests for each method and request matching.

- Over the cap: `QuotaExceededError`, nothing partial stored; caches are isolated per artifact.

### #559 — Service workers: the limits tests and routing

**Acceptance verified; broader lifecycle follow-up remains separate.** Evidence: `CapabilityMatrixTests` service-worker row, `WindowsSandboxTests.ServiceWorkerCapabilityRowRunsThroughTheRealContentProcess`, `ServiceWorkerCapabilityTests.NativeServiceWorkerBusyFetchIsInterruptedWithoutStoppingThePage`, `DomBindings` service-worker handlers, classifier routing tests, and D14 3/3 on native/Jint.

The service-worker capability row runs registration, host-loaded worker code and an intercepted fetch inside the real content process for both the `service-workers` and `all` matrix rows, with artifact storage and capability grants applied. The native isolated runtime receives the document execution timeout, and a busy fetch is interrupted while the page remains responsive. Storage caps, disposal/revocation, classifier routing and the conformance category are covered by their focused suites. Process-death cleanup and richer host transport behavior remain follow-up work under the lifecycle issue.

Original acceptance:

- The service workers row of the sandbox matrix passes with these cases.

- Classifier tests both ways; the service workers conformance category passes.

### #560 — M6 Beyond artifacts: tracking

**Incomplete.** Evidence: `All rows below`.

Original acceptance is not satisfied. Previous closure and completion documentation were premature.
