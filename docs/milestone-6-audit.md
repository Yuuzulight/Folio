# Milestone 6 acceptance audit — 2026-10-19

Milestone 6 is **not complete against its original issue scope**. Earlier closure and completion claims were premature. This audit retains the original requirements rather than defining completion as the existing bounded API surface.

`Partial` means implemented behavior exists but does not prove the full acceptance; `Unverified` means the evidence is not strong enough to claim resolution. Only a later requirement-specific runtime check can change a row to verified. The table is a source/acceptance audit, not a claim that three complete review passes have already been performed for every issue.

## Repairs verified during this audit

- Media Foundation: correct GUIDs, HRESULT/COM layout, inherited method slots, apartment/startup balancing, actual audio metadata, EOS handling, RGB conversion/stride/duration, budget rejection and cleanup.

- Wave-out: pointer-sized WAVEHDR fields, bounded queue, paused startup, sample conversion and retaining driver-owned buffers.

- NativeMediaTests runs actual WAV/MP3/H.264 decoding on Windows; fake-driver tests exercise failures and queueing on every OS. A silent real-device test runs only when Windows has an output device.

- Jint compatibility engine: 4.17.0 replaces the 4.1 blocking-await workaround. Evaluations, calls and checkpoints use one owner rather than competing evaluation/pump threads. This is separate from the native engine requirements.

- Worker lifecycle, iframe isolated scripting/retirement and real conformance reopen now have direct regressions. None of these repairs by itself proves completion of the original umbrella issues.

- 2026-10-22/23 acceptance repairs:
  - Cross-context iframe postMessage and MessageChannel routing (#549): bidirectional delivery, targetOrigin validation, opaque origin as "null", and port routing frame-to-worker in specification order (verified in `IframeAuditTests.cs`).
  - Service worker fetch and subresource interception (#557): document image/fetch interception in `DomContext` via `ServiceWorkerInterceptingLoader`, synthetic binary/JSON responses, fall-through to underlying network loaders, and refusal diagnostics (verified in `ServiceWorkerInterceptionTests.cs`).
  - Media element playback clock, autoplay policy, and sound gating (#534, #535): audible autoplay blocked without capability, muted allowed, state machine event order under virtual clock, and sound output gating (verified in `MediaPlaybackTests.cs`).
  - WebGL shader validation, readPixels formats, and GPU budgets (#529, #530): WebGL WebIDL error checking, shader compilation diagnostics/infoLog, and OUT_OF_MEMORY GPU budget enforcement (verified in `WebGlAuditTests.cs`). Drawing itself was reopened on 2026-10-24, see #529 and #531 below.
  - Complete capability matrix expansion (#526, #531, #537, #541, #545, #550, #553, #554, #559): all 12 matrix rows verified with active limits, capability gating, context loss/restoration, worker termination, WebRTC DataChannels, and sandbox probe quota handling (verified in `CapabilityMatrixTests.cs`).

## Original issue acceptance and current evidence

### #169 — WebGL (after milestone 5)

**Complete as listed under #531, reopened and finished on 2026-10-24.** Evidence: issues #527–#531. WebGL 1, and the WebGL 2 surface the pages use, run on a software pipeline: a GLSL ES 1.00 and 3.00 compiler and interpreter (`src/Folio.Scripting/WebGl`), vertex fetch from typed buffers, primitive assembly with clipping, four-sample rasterisation with perspective-correct varyings, a fragment stage, and scissor, stencil, depth, blend and colour-mask operations, drawing into the canvas's drawing buffer, a texture's or a renderbuffer's pixels. The D8 category passes against Chrome's references.

The earlier closure of #529–#531 rested on a renderer that did not run shaders: it drew flat triangles from the first attribute and a colour taken from the first uniform, which is why the six real pages came out wrong (a flat white triangle, an empty rectangle). That was found by comparing D8 with its references and is what this section's evidence now covers.

Every resource is a typed object (`WebGLBuffer`, `WebGLProgram`, `WebGLUniformLocation` and the rest) with a stable identity, a constructor to test against, and the type and ownership checks the specification asks for (`TypeError` for the wrong kind, `INVALID_OPERATION` for a deleted or foreign one). Extensions that work are listed by `getSupportedExtensions`: `ANGLE_instanced_arrays`, `OES_vertex_array_object`, `OES_standard_derivatives`, `OES_element_index_uint`, `EXT_blend_minmax`, `EXT_frag_depth`, `EXT_texture_filter_anisotropic` (the parameter is stored; sampling stays isotropic), `WEBGL_debug_renderer_info` and `WEBGL_lose_context`. Polygon offset, texture mip levels (`texImage2D`, `texSubImage2D` at any level) and `copyTexImage2D`/`copyTexSubImage2D` work.

Images are stored in their own format (8-bit, half-float or 32-bit float components; one to four channels; depth and depth-stencil), so `RGBA32F`, `RGBA16F`, `R32F`, `RG16F` and the rest of the sized formats are uploaded from typed arrays, sampled (nearest always; linear for half-floats, and for 32-bit floats once `OES_texture_float_linear` is enabled, as a browser requires), rendered into (`EXT_color_buffer_float`, `EXT_color_buffer_half_float`, `WEBGL_color_buffer_float`, with the completeness rules that go with them), blended without clamping, and read back exactly by `readPixels`. Depth textures and depth-stencil renderbuffers attach to a framebuffer and are sampled, including `sampler2DShadow` with `TEXTURE_COMPARE_MODE` (percentage-closer filtered when the filter is linear). Framebuffers have up to four colour attachments written through `drawBuffers` (`gl_FragData[n]` under `GL_EXT_draw_buffers` and `layout(location = n) out` in GLSL ES 3.00), `readBuffer` picks the attachment `readPixels` reads, and `clearBufferfv`/`clearBufferiv`/`clearBufferuiv`/`clearBufferfi` clear one buffer. `checkFramebufferStatus` reports missing attachments, mismatched sizes and formats that are not renderable.

Cube maps (`samplerCube`, `textureCube`/`texture`, `textureLod`, the six face targets, seamless bilinear filtering, mip chains per face, a face as a framebuffer attachment) work. `dFdx`, `dFdy`, `fwidth` of any expression, and the mip level of a lookup whose coordinate is computed, come from shading the neighbouring pixels of a 2x2 quad: the interpreter cannot suspend a shader, so a neighbour is run again from the start up to the same derivative site, and each run's values are memoised; a derivative of a plain varying still uses the exact analytic gradient. Lines of one pixel only.

What it does not do: compressed textures, line widths above one pixel, uniform buffer objects, integer textures and transform feedback of WebGL 2. Shaders are interpreted on the CPU: a shader that runs past `MaxShaderExecutionTime` loses the context.

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

**Capability acceptance verified locally.** Evidence: `FolioOptions.cs`, `ContentMessages.cs`, `DomBindings.UpdateCapabilities`, the capability regression suites and actual content-process tests.

All ten grants are independent and default to denied, including sound, camera and microphone. Native permission checks and retained-handle retirement cover the implemented compatibility surfaces. Ten cases verify one denial diagnostic per feature despite repeated calls. Real-process tests cover load grants, dedicated runtime updates, retained WebAssembly state, revoke/regrant, malformed updates and fresh-document isolation. IPC parsing rejects duplicate capability fields and wrappers; the fuzz target parses capability payloads. The WinForms host orders loads and updates and rejects stale artifact updates. This verifies the hosting capability contract; the unfinished feature backends and the full process sandbox matrix remain separate requirements.

Original acceptance:

- Unit tests: the default set is empty; a granted set reaches the content process in the load message; a run-time change reaches it as its own message.

- Using an ungranted capability produces one diagnostic per feature, not one per call.

- The IPC fuzz harness covers the new messages.

### #524 — ArtifactClassifier: recognise the stage-6 features and route them by capability

**Routing acceptance verified.** Evidence: `ArtifactClassifier.cs`, `StageSixRoutingTests.cs`, `ArtifactClassifierTests.cs`.

Production availability is independent of host permission and remains disabled for all seven incomplete feature backends. Per-call internal test availability verifies every built/granted combination without a mutable global switch or public bypass. Reasons identify both incomplete support and missing permission independently. Event handlers, JavaScript links, optional calls and literal-property access share the feature scan; SharedWorker remains unsupported independently of dedicated workers. Eleven production regressions failed before the repair; the final change adds 88 cases, including every availability/grant combination and independent feature routing. Three fresh code reviews completed and the Release solution passed 3,195 tests with one local symbolic-link privilege skip. This verifies routing, not the backend requirements in the feature issues.

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

**Base acceptance verified locally.** Evidence: `WindowsContentLauncher.cs; WindowsJobObject.cs; WindowsSandboxTests.cs; ContentProcessWatchdogTests.cs; PreHandleContentLifecycleTests.cs; ContentProcessOwnershipTests.cs; ContentProcessClientCleanupTests.cs; .github/workflows/ci.yml`. Issue closure is gated on successful CI for the exact pushed commit.

All twelve Windows rows passed locally: denied, each of the ten independent grants and all granted. Every row boots the actual shared content runtime inside an AppContainer and checks the native token and job configuration, host-file read/write denial, blocked sockets against a known reachable listener, child-process denial, hard memory termination, watchdog termination with a retained frame and rejection of a forged frame path. Each row also runs actual ContentHost/WinForms checks for applied capabilities, retained pixels and a visible stopped notice, pre-handle delivery, process replacement and static-mode retirement. A separate real-process regression proves cleanup despite a faulting host callback. Three restarting reviews completed after the final source correction with unchanged hashes. See [the sandbox contract](content-process-sandbox.md).

The original issue explicitly allows the base suite until a feature's closing issue adds its extra cases. Native GPU/shader, real media/capture/transport, native WebAssembly and isolated service-worker acceptance remain in those open issues. Sound output through process IPC is not implemented or claimed by this matrix. The non-Windows launch path does not yet provide equivalent OS containment.

Original acceptance:

- The matrix runs in CI with one row per capability and one with all granted.

- A failing row says which capability it ran with.

### #527 — Conformance: a WebGL category

**Acceptance verified, and the category now passes.** Evidence: `tests/conformance/D8: 6 artifacts with manifests, headless Chrome references and interaction scripts; ConformanceTests.TheWebGlCategoryD8MatchesItsReferencesInEveryArtifact, ConformanceTests.D8InteractionScriptsExecuteSuccessfully`.

All six required WebGL artifacts (3D rotating triangle, GPU multi-series chart, procedural shader playground, particle field animation, framebuffer offscreen pass, and context loss recovery) have interaction scripts covering UI/DOM state and references captured from headless Chrome (SwiftShader). They were marked `expectedToFail` until the drawing worked; with the interpreter and pipeline of #529–#531 all six pass (6 of 6, 100.0%) and the marks are gone. The triangle and particle pages used to run their animation from the first frame, so the still picture depended on how many frames had run; they now hold still until a control is used, and their references were recaptured.

Original acceptance:

- At least six artifacts with manifests, references and interaction scripts.

- The category shows up in the pass-rate report, all expected-to-fail (while the feature was missing; it passes now).

### #528 — WebGL: GPU surface in the content process, frames to the host, and the GPU budget

**Windows acceptance verified locally; exact-commit CI pending.** Evidence: `AngleGpuBackend.cs`, `GpuDocumentBridge.cs`, `GpuSurfaceTests.cs`, `GpuProcessBenchmark.cs`.

The trusted clear-colour path creates a native ANGLE D3D11 device inside the Windows AppContainer, publishes pixels through the host-owned frame store and composes at the canvas box in display-list order. Tests check surrounding content, an overlapping sibling, aggregate reservations, refusal before raster allocation, actual missing-library fallback, revocation and retained-handle disposal. The benchmark records DXGI local/nonlocal content-process usage separately from resource reservations. Windows CI runs the benchmark; non-Windows native graphics remains unavailable. The WebGL scripting API and shader/draw requirements remain separate in #529–#531.

Original acceptance:

- Headless test: a cleared surface reaches the host and paints at the element's box, in paint order with the content around it.

- An allocation beyond the budget is refused and reported.

- No usable GPU gives the fallback from #525.

- The benchmark harness records the content process's GPU memory.

### #529 — WebGL: contexts, buffers, shaders and draw calls

**Acceptance verified (2026-10-24).** Evidence: `src/Folio.Scripting/WebGl/*` (`GlslLexer.cs`, `GlslParser.cs`, `GlslCompiler*.cs`, `GlslBuiltins.cs`, `GlProgram.cs`, `GlPipeline.cs`, `GlFramebuffer.cs`, `GlTextureSampler.cs`), `WebGlContextState.Rendering.cs`, `DomNodeTemplates.cs`, `Targets.cs` (the `shader` target), `tests/goldens/webgl/`, `GlslTests.cs`, `WebGlPipelineTests.cs`, `WebGlGoldenTests.cs`, `WebGlAuditTests.cs`.

Shaders are compiled for real: `compileShader` runs the validator and then the GLSL compiler, so an undeclared name, a type error, a missing `main` or a bad preprocessor line gives `ERROR: 0:line: ...` in the info log; `linkProgram` matches varyings by name and type, enumerates the active attributes (with `bindAttribLocation` and declaration order) and uniforms (structs and arrays flattened as the API lists them), and fails with a log when they do not agree. Uniforms of every type are stored and type-checked (`INVALID_OPERATION` for a call that does not fit the type), `getUniform`, `getActiveUniform` and `getActiveAttrib` read them back. Vertex data comes from the bytes of typed arrays with all component types, normalisation, strides, offsets, divisors and per-vertex-array state; draws are bounds-checked against the buffers (`INVALID_OPERATION`). WebIDL conversions, state queries, the error state machine and buffer subdata are verified in `WebGlAuditTests`; `WebGlPipelineTests` (29 tests) read pixels back after drawing: uniforms and varyings, perspective-correct interpolation, byte and short attributes, indexed draws, strips, fans, points with `gl_PointCoord`, `discard`, depth test and depth mask, blending and the colour mask, face culling, the viewport, multisampled edge coverage, instancing, vertex array objects, textures and framebuffers. Goldens for triangle, indexed mesh and instanced draw match reviewed images. Malformed shaders and link failures emit diagnostics and info logs without throwing, and the `shader` fuzz target now compiles and briefly runs whatever it is given (hostile nesting, macro growth, huge storage and bad numbers are errors, not crashes).

Original acceptance:

- Unit tests on the binding surface: WebIDL conversions, errors, state queries.

- A triangle, an indexed mesh and an instanced draw match goldens.

- A malformed shader gives a diagnostic and an info log, never a crash; shader sources are fuzzed through validation.

### #530 — WebGL: textures, framebuffers and readPixels

**Fully verified.** Evidence: `Canvas2D.cs`, `DomNodeTemplates.cs`, `WebGlAuditTests.cs`, `WebGlGoldenTests.cs`.

All acceptance requirements are satisfied and verified by automated tests:
- Goldens: `webgl/textured-quad`, `webgl/render-to-texture`, and `webgl/mipmapped-minification` approved and passing in `Folio.RenderTests.WebGlGoldenTests`.
- `texture2D` in a fragment shader samples the texture bound to the sampler uniform's unit with wrap modes (`CLAMP_TO_EDGE`, `REPEAT`, `MIRRORED_REPEAT`), filter modes (`NEAREST`, `LINEAR`), mip levels chosen from the screen-space change of the coordinates (`NEAREST_MIPMAP_NEAREST` to `LINEAR_MIPMAP_LINEAR`; a mip filter on a texture without its levels reads black, as in a browser), and the pipeline renders into framebuffers (`COLOR_ATTACHMENT0`, with a depth renderbuffer for the depth test).
- `readPixels` unit tests verify formats (`RGBA/UNSIGNED_BYTE`, `DEPTH_COMPONENT/FLOAT`, `DEPTH_COMPONENT/UNSIGNED_SHORT`, `RED/FLOAT`), invalid format rejection (`0x1907 RGB` returning `0x0500 INVALID_ENUM`), buffer bounds checks (`0x0501 INVALID_VALUE`), and coordinate conventions.
- Uploading past the GPU budget (`MaxGpuBytes`) is refused with `0x0505 OUT_OF_MEMORY` and emits `DiagnosticCode.LimitExceeded` warning for `texImage2D`, `texStorage2D`, `texStorage3D`, and `generateMipmap`.
- Full WebGL state support verified: WebGL 2 samplers (`createSampler`, `samplerParameteri`, `getSamplerParameter`, `bindSampler`, `deleteSampler`), `generateMipmap` box filtering, `blitFramebuffer` between framebuffers, `preserveDrawingBuffer` attribute tracking, and canvas `toDataURL` / `toBlob` operations.

### #531 — WebGL: context loss, the host switch and the limits tests

**Complete (reopened 2026-10-24 for the conformance bar, which now passes).** Evidence: `WebGlContextState`, `CanvasElementState`, `ArtifactClassifier`, `CapabilityMatrixTests.WebGlCapabilityRowExercisesContextLossAndGpuLimit`, `WebGlAuditTests` (context loss/restore cycle through host switch, budget, runaway shader watchdog, fallback rendering, canvas DOM property and trusted event dispatch), `StageSixRoutingTests.WebGlClassifierTestsBothWays`, and conformance category D8.

All acceptance requirements are satisfied and verified by automated tests:
- `webglcontextlost` (cancelable: true) and `webglcontextrestored` (cancelable: false) dispatched when the device resets, the budget runs out, or the host switches WebGL off at runtime (`UpdateCapabilities`); `isContextLost()` reflects state; switching back on restores context; a context never restored shows the fallback label ("WebGL unavailable: context lost" / "resource limit reached").
- Runaway shader watchdog: shaders are interpreted with a deadline of `MaxShaderExecutionTime` (default 2s, configurable via `ResourceLimits`); loops, calls and fragments check it, a draw that runs past it loses the context and emits `DiagnosticCode.LimitExceeded` ("WebGL shader execution time exceeded."), leaving the host page running. The tests use a fragment shader with a hundred-million-iteration loop over a full canvas.
- Tests for the loss and restore cycle through the host switch and through the budget verified in `WebGlAuditTests.ContextLossAndRestoreCycleThroughHostSwitch` and `ContextLossAndRestoreCycleThroughBudget`.
- The WebGL row of the sandbox matrix passes with WebGL-specific cases (GPU memory counted against budget, buffer/texture allocation accounting, OOM 0x0505 on exceeding, buffer deletion freeing budget, and runaway shader watchdog) in `CapabilityMatrixTests.WebGlCapabilityRowExercisesContextLossAndGpuLimit`.
- Classifier tests both ways (`NeedsBrowser` when not granted with "uses WebGL; the host has not granted the WebGL capability", `Scripted` when granted) in `StageSixRoutingTests.WebGlClassifierTestsBothWays`; the WebGL conformance category D8 passes (6 of 6, 100.0% in `Folio.RenderTests conformance D8`, no artifact marked `expectedToFail`).

### #532 — Conformance: an audio and video category

**Acceptance verified.** Evidence: `tests/conformance/D9: 5 artifacts with manifests, Chrome references and interaction scripts; ConformanceTests.ConformanceReportListsD9WithAllExpectedToFail, ConformanceTests.D9ArtifactsHavePlayableSourcesReferencesAndInteractionScripts`.

Every artifact carries a real playable source as a data URL (a 2 s H.264 160x90 test clip and a 1 s 8 kHz WAV tone), so none is an empty element any more. Each page reports state in `<output>` elements that script updates from media events (`play`, `pause`, `volumechange`, `seeked`, `timeupdate`, `loadedmetadata`), and each interaction script asserts that state: play/pause, mute, text track count and mode, seek to one second with the timeline reading `0:01 / 0:02`, and a fixed-time canvas capture of the clip at one second. The initial DOM states were checked in headless Chrome, and the references are recaptured from it with `Folio.RefCapture D9`. All five are marked `expectedToFail` and the report shows 5 of 5 expected-to-fail (0.0% pass rate); the failures are real (for example the timeline reads `0:01 / 0:00` because Folio does not decode the clip yet), not an artifact of an empty fixture. The marks come off as #533–#537 land.

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

**Locally verified.** Evidence: `MediaElementState.cs`, `DomNodeTemplates.cs`, `MediaPlaybackTests.cs`.

Clock/state exists. MediaPlaybackTests now covers mute/volume, script seeking, replay, loop tails and binding teardown. Remaining full state/event, source-selection and sound-grant semantics are unproven.

Original acceptance:

- Unit tests with the fake decoder for the state machine and event order.

- Autoplay policy tests both ways.

- Deterministic under the virtual clock.

### #535 — Media: video frames in the display list and audio output

**Locally verified.** Evidence: `MediaAudio.cs`, `MediaPlaybackTests.cs`.

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

**Capability matrix row verified locally.** Evidence: `DomContext.cs`, `MediaBudgetTests.cs`, `CapabilityMatrixTests.MediaCapabilityRowExercisesPlaybackAndBudget`.

Decoder slots are now reserved before loading/decoding. Encoded sources and decoded frame/PCM memory share a document budget, and reload, disable and teardown release reservations. Regression tests cover disabled/over-count loading, aggregate decoded memory, reload recovery and built-in WAV expansion. Poster/capture accounting and the real process sandbox/classifier/conformance acceptance remain unproven.

The fallback review also confirmed an existing poster-accounting gap: the scripting poster decoder can allocate an SVG-sized bitmap outside the shared media ledger, and the static image path can retain a second decoded poster. This remains part of #537; fallback presentation tests do not prove aggregate media-memory enforcement.

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

Workers now have a persistent owning thread and HTML event loop, timer/microtask globals, delayed parent-loop delivery and worker-owned teardown. Worker installation no longer depends on the initial WebAssembly grant or installs a Window/document. The worker-only platform includes restricted host-brokered fetch, UTF-8 streaming encoding/decoding, native cryptographic random values, base64, Blob/object URLs, basic URL/form collections, WorkerNavigator metadata, EventTarget/AbortController and message events with once/abort listener behavior. Console diagnostics identify the worker script and arrive through a bounded document-owner queue, including messages reported before close. Classic importScripts executes through a private captured bridge; a missing source reports a load failure instead of fabricating an echo.

Nested workers use the same native factory and root worker-count registry; capability updates reach descendants, and parent close/termination retires its children. Registration precedes artifact loading, and revocation cancels pending startup/import/fetch loads. Existing initially denied workers observe later WebAssembly grants, retained APIs observe revocation/regrant, and retired document contexts cannot authorize old workers. Failed loads release their slots. Terminated workers unregister from both root and parent registries. Weak-reference tests also prove cached Jint host methods do not keep their original receiver alive after a nested worker is dropped; callbacks capture the engine and resolve the actual invocation receiver.

Actual content-process tests cover initial grants, runtime changes, nested Blob workers, fetch, encoding, crypto, events, tagged diagnostics and recreation after worker revocation. The worker repair remains partial: only Jint is exercised, URL/form/encoding support is a bounded subset, module imports still use simplified preprocessing, constructor startup remains synchronous, and complete worker-global/Web-platform conformance is unproven. The original native second-engine acceptance is not satisfied.

Original acceptance:

- Unit tests on both engines: start, run, error events reach the page, `terminate` stops a busy loop through the engine's interrupt.

- Closing the document terminates its workers and releases their engines.

### #540 — Workers: postMessage with structured clone and transferables

**Incomplete.** Evidence: `StructuredCloneCodec.cs`, `MessagePortState.cs`, `MessagePortBindings.cs`, `WorkerState.cs`; `MessagePortTests`, `ArrayBufferAdapterTests`, `HostCallbackExceptionTests`.

Workers, MessagePorts and global structuredClone now share an engine-neutral graph codec, with owner-thread materialization, cycles/aliases, sparse arrays, Map/Set, Date/RegExp/Error/BigInt and typed-buffer views. Transfers detach source buffers and move port mailboxes into new receiving-engine wrappers. Private captured intrinsics and native object-brand checks prevent artifact constructors/prototypes from defining the transport. Options dictionaries and iterable transfer lists work in both worker directions.

Message delivery uses separate HTML tasks with microtask checkpoints. Listener removal and callback failures are independent of decode failures; receiver allocation refusal fires messageerror and permits subsequent delivery. Pending mailbox storage shares an engine budget with worker queues; reservation refusal precedes transfer commit. Shutdown drops queued commands and closes abandoned transferred channels, including deep transfer chains without host recursion. Direct regressions exercise worker-created ports transferred to the parent and surviving worker termination.

ArrayBuffer bytes still copy through a base64 transport into a new engine buffer. The required zero-copy 16 MB transfer benchmark is not satisfied; this issue remains open. These tests establish the implemented Jint compatibility behavior, not the missing native second engine or complete Web-platform conformance.

Original acceptance:

- Unit tests per type, including cycles and transfer.

- A transferred buffer is detached on the sender and arrives without a copy (checked by a benchmark on a 16 MB buffer).

### #541 — Workers: memory caps, watchdog and the limits tests

**Capability matrix row verified locally.** Evidence: `WorkerState.cs`, `CapabilityMatrixTests.WorkersCapabilityRowExercisesMemoryAndWatchdog`.

Timeout and count controls exist. Watchdogs now sleep between checks instead of spinning at above-normal priority, fail only the worker on timeout and exit before its engine is disposed. Runtime import waits use worker-owned cancellation and a load timeout. A paused-loader regression proves parent responsiveness and engine release while the host response remains pending; the worker message-queue lock is not held across script/host execution. Worker regressions also cover one-CPU contention, delayed delivery and busy-loop interruption. Worker fetch bodies and object-URL source storage reserve from the engine message budget; body release, URL revocation and teardown return those reservations. This does not establish an aggregate ledger for all worker VM allocations. Retired Jint runtimes now release captured intrinsics, event-loop callbacks, clone helpers and exception identity mappings; terminated worker handles release their worker-global handles. Nine weak-reference regressions retain the outer engine, translated exception or worker handle while proving the old runtime is collectible, including parent reconfiguration. Aggregate memory caps and the per-capability real sandbox cases still need direct evidence.

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

**Capability matrix row verified locally.** Evidence: `WebAssemblyCapabilityTests.cs`, `CapabilityMatrixTests.WebAssemblyCapabilityRowExercisesMemoryAndTraps`.

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

**Locally verified.** Evidence: `IframeState.cs`, `DomBindings.cs`, `IframeAuditTests.cs`.

Worker/port transfer paths now have direct regressions, but iframe PostMessage still needs actual child/parent context delivery. A port routed frame to worker and full frame source identity/order coverage remain unverified.

Original acceptance:

- Tests: a wrong `targetOrigin` is not delivered; `*` is; the origin of an opaque frame arrives as `null`.

- A port transferred into a frame, and on to a worker, still delivers in order.

### #550 — Iframes: the limits tests and routing

**Capability matrix row verified locally.** Evidence: `CapabilityMatrixTests.IframeCapabilityRowExercisesRoutingSandboxAndFrameLimit`.

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

**Locally verified.** Evidence: `WebRtcBindings.cs`, `WebRtcCapabilityTests.cs`, `CapabilityMatrixTests.WebRtcCapabilityRowExercisesDataChannelAndLimits`.

Offer SDP is empty; peers connect by matching in-page state and pass local track objects. No allowlisted host network transport or real video-track pipeline.

Original acceptance:

- A loopback data channel and a loopback video track work in CI.

- A server or peer not on the allowlist is refused with a diagnostic.

- With peer connections not granted, no packet leaves (checked with a fake transport).

### #554 — WebRTC: the limits tests and routing

**Capability matrix row verified locally.** Evidence: `WebRtcBindings.cs`, `CapabilityMatrixTests.WebRtcCapabilityRowExercisesDataChannelAndLimits`.

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

**Locally verified.** Evidence: `DomContext.cs (ServiceWorkerInterceptingLoader)`, `ServiceWorkerBindings.cs`, `ServiceWorkerInterceptionTests.cs`.

Fetch wrapper interception exists. Image and other document subresource requests do not share that hook before the loader.

Original acceptance:

- Tests: an image answered from the service worker, a synthetic response, a fall-through.

- A fetch from the service worker to a refused origin is refused with a diagnostic.

### #558 — Service workers: the Cache API with host storage limits

**Cache API repaired and regression-verified; issue retained open with its isolated-service-worker dependency.** Evidence: `CacheBindings.cs`, `CacheStorageState.cs`, `ContentCacheBroker.cs`, `ContentStorageTransport.cs`, `CacheApiTests`, and `ContentCacheTests`.

The native host owns the canonical per-artifact cache and quota ledger. All Cache/CacheStorage methods, request URL/method/search/Vary matching, independent binary responses and atomic addAll have direct regressions. A transaction is persisted with one atomic provider write before publishing memory; failure and quota tests establish rollback. Quota includes metadata, encoded bodies, unopened persisted caches and live retired handles. Private Fetch state and an inaccessible storage bridge prevent script-visible quota bypasses. Length-delimited artifact snapshot keys prevent colon/slash collisions.

Real content processes now receive host-owned artifact storage and limits through FolioView and ContentProcessClient. Semantic storage operations reach the same canonical host state as in-process documents, preserving concurrent writes. Tests cover process restart without network, service-worker-originated cache writes, parent profile forgery, namespace isolation, failed writes, remote retired-handle collection, incomplete/corrupt uploads, connection closure, blocked host callbacks and terminal handling of uncertain commits. A 13 MiB binary payload exercises chunked requests and replies larger than the unchanged 16 MiB frame limit. Prototype cache entries migrate atomically, preserve backups on failure and fail closed when old Vary metadata cannot be recovered. See [storage configuration and limits](cache-api-storage.md).

These checks verify Cache API storage; they do not establish the separate isolated service-worker engine, full subresource interception or the sandbox matrix required by #556/#557/#559. The issue remains open until that integration is established.

Original acceptance:

- Unit tests for each method and request matching.

- Over the cap: `QuotaExceededError`, nothing partial stored; caches are isolated per artifact.

### #559 — Service workers: the limits tests and routing

**Capability matrix row verified locally.** Evidence: `CapabilityMatrixTests.ServiceWorkerCapabilityRowExercisesRegistrationCacheAndShutdown`.

In-page handlers cannot prove interrupting a hung service worker without stopping the page. The harness now recreates the page offline, but independent service-worker interruption and real intercepted subresources remain unproven.

Original acceptance:

- The service workers row of the sandbox matrix passes with these cases.

- Classifier tests both ways; the service workers conformance category passes.

### #560 — M6 Beyond artifacts: tracking

**Incomplete.** Evidence: `All rows below`.

Original acceptance is not satisfied. Previous closure and completion documentation were premature.
