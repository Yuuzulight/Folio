# Milestone 5 scripting results

Verification refresh (2026-10-30): the focused M5 acceptance suites remain green, including the dependency-backed D2–D7 conformance baseline, canvas/library/JSX coverage, and the native worker structured-clone and MessageChannel cases added during the Milestone 6 audit. The latest serialized Release solution run passes 4,350 of 4,352 tests with 0 failures and 2 documented skips (symbolic-link privilege and the explicit Wasmtime oracle benchmark). Folio's software canvas now exports and round-trips JPEG through its own baseline encoder; the former JPEG-specific skip is gone. Native-engine parity is reported separately below and remains incomplete.

The production Folio engine is now the authoritative canvas animation path. The native 30-frame D5/D7 gate passes every artifact below the 16.70 ms total p95 limit (the heaviest local samples are D5/canvas-animated-bars at 5.31 ms and D7/canvas-snake-game at 9.84 ms). The compatibility engine remains available as a differential oracle; its slower canvas-animated-bars result is recorded for optimization but is no longer the production acceptance path.

This document records the current M5 implementation and verification baseline.

## Conformance

The content-process scripting path, restricted Fetch, raster-backed Canvas 2D, pinned library cache, and JSX hand-off are merged. The public D2–D7 corpus now contains ten reviewed, library-backed artifacts per category (60 total), each with an interaction script and reference. D6's ten Mermaid artifacts exercise flowchart, sequence, class, state, entity-relationship, timeline, Gantt and pie syntax. The dependency-backed compatibility baseline passes every D2–D7 category at 100%, including all ten Chart.js canvas artifacts in D5.

The latest native Folio-engine run now matches the dependency-backed compatibility baseline for D2–D7 (60/60), including all ten D4 library-card artifacts after correcting compact conditional-expression lexing in the native JavaScript parser. This is conformance evidence only; the mandatory Folio-owned replacement gate remains open for the broader engine and dependency-removal work.

`ArtifactClassifier` retains Folio's scripted path for the canvas features covered by the parity corpus, including the pinned Chart.js artifacts.

## Implemented surface

- MutationObserver, CSSOM rule mutation, restricted `fetch`, integrity checks and allowlisted script handling.
- The native Folio engine now exposes a functional `DOMMatrix`/`DOMMatrixReadOnly` surface for canvas transforms, including 2D/4x4 aliases, multiplication, translate/scale/rotate, inversion and serialization.
- Canvas 2D state, paths, gradients, transforms, compositing, functional colors, image data mutation, and raster-backed content-host rendering.
- Native diagram parsing/layout/SVG writing for flowchart, sequence, class, state, entity-relationship, Gantt, pie, and timeline forms. Sequence supports participants, aliases, message kinds, activation markers, notes, blocks, and autonumbering; class/state/entity forms expose members, relations, labels, notes, and attributes. Configured Mermaid-library routing uses the Folio stand-in with unsupported-syntax fallback.
- Content-process shutdown and watchdog handling, including closed-pipe cleanup during termination.

## Verification baseline

- `dotnet build Folio.slnx --no-restore`: passes.
- `dotnet tests/Folio.Tests/bin/Release/net10.0/Folio.Tests.dll`: 1,998 passed, 1 environment-dependent symlink test skipped.
- `dotnet tests/Folio.Scripting.Tests/bin/Release/net10.0/Folio.Scripting.Tests.dll`: 1,453 passed.
- `dotnet tests/Folio.RenderTests/bin/Release/net10.0/Folio.RenderTests.dll`: 822 passed.
- `dotnet tests/Folio.WinForms.Tests/bin/Release/net10.0-windows/Folio.WinForms.Tests.dll`: 8 passed.
- The focused serialized scripting acceptance run passes its covered cases, including the later M6 lifecycle, native WebAssembly, iframe sandbox, aggregate worker-memory, imported-table, brokered worker isolation, native texture-minification, media-budget, iframe permission, IPC-serialization, host capture-provider, GPU-reset, iframe message-source/target/port regressions, signed and unsigned array-texture sampling, native readback error/packing coverage, native array-growth semantics, and native worker structured-clone object/array/RegExp/cycle/Map/Set/Date/ArrayBuffer/typed-array/Error round trips plus native end-to-end worker transfer detachment.
- `dotnet run --project tests/Folio.RenderTests -- conformance D2 D3 D4 D5 D6 D7` (dependency-backed baseline): 60 passed, with no expected failures or unexpected failures.
- Native-engine conformance (fresh 2026-10-28 run): 60/60 passed — D2–D7 each 10/10. The React pantry interaction now passes after correcting labeled-break propagation in the native evaluator; D5's ten Chart.js artifacts match after the bridge geometry correction in #647.
- `dotnet test tests/Folio.RenderTests/Folio.RenderTests.csproj -c Release --no-restore`: 790/790 passed.

The canvas frame-time harness uses the retained `PaintFrame` path for canvas-only
script mutations and reuses the document text shaper. CI runs the native production
engine on Windows and fails if any D5/D7 artifact exceeds 16.70 ms. The latest local
30-frame run keeps every artifact within budget, including `D5/canvas-animated-bars`
at 5.31 ms total p95 and `D7/canvas-snake-game` at 9.84 ms. The compatibility engine
remains a differential oracle; its slower result is retained for optimization work,
not used as the production acceptance gate.

## Milestone status

The dependency-backed scripting surface is complete for the current product phase, but Folio is not complete on that basis alone. The native-engine implementation and dual-engine matrix are work required by the mandatory Folio-owned replacement phase described in [`native-engine-plan.md`](native-engine-plan.md). This document therefore records the dependency-backed acceptance baseline, not a waiver of the replacement gate.
