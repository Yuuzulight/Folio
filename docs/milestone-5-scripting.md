# Milestone 5 scripting results

Verification refresh (2026-10-27): the focused M5 acceptance suites remain green, including the dependency-backed D2–D7 conformance baseline, canvas/library/JSX coverage, and the native worker structured-clone and MessageChannel cases added during the Milestone 6 audit. The latest complete Release solution run passes 4,094 tests with 0 failures and 7 documented skips (4,101 discovered). Native-engine parity is reported separately below and remains incomplete.

This document records the current M5 implementation and verification baseline.

## Conformance

The content-process scripting path, restricted Fetch, raster-backed Canvas 2D, pinned library cache, and JSX hand-off are merged. The public D2–D7 corpus now contains ten reviewed, library-backed artifacts per category (60 total), each with an interaction script and reference. D6's ten Mermaid artifacts exercise flowchart, sequence, class, state, entity-relationship, timeline, Gantt and pie syntax. The dependency-backed compatibility baseline passes every D2–D7 category at 100%, including all ten Chart.js canvas artifacts in D5.

The native Folio-engine run is not yet equivalent: the latest fresh result is 49/60 (D2 8/10, D3 10/10, D4 10/10, D5 1/10, D6 10/10, D7 10/10). The remaining failures are tracked by the mandatory replacement work, including the React reconciliation blocker in [#645](https://github.com/Yuuzulight/Folio/issues/645). The 60/60 compatibility result must not be used as evidence that native replacement is complete.

`ArtifactClassifier` retains Folio's scripted path for the canvas features covered by the parity corpus, including the pinned Chart.js artifacts.

## Implemented surface

- MutationObserver, CSSOM rule mutation, restricted `fetch`, integrity checks and allowlisted script handling.
- The native Folio engine now exposes a functional `DOMMatrix`/`DOMMatrixReadOnly` surface for canvas transforms, including 2D/4x4 aliases, multiplication, translate/scale/rotate, inversion and serialization.
- Canvas 2D state, paths, gradients, transforms, compositing, functional colors, image data mutation, and raster-backed content-host rendering.
- Native diagram parsing/layout/SVG writing for flowchart, sequence, class, state, entity-relationship, Gantt, pie, and timeline forms. Sequence supports participants, aliases, message kinds, activation markers, notes, blocks, and autonumbering; class/state/entity forms expose members, relations, labels, notes, and attributes. Configured Mermaid-library routing uses the Folio stand-in with unsupported-syntax fallback.
- Content-process shutdown and watchdog handling, including closed-pipe cleanup during termination.

## Verification baseline

- `dotnet build Folio.slnx --no-restore`: passes.
- `dotnet tests/Folio.Tests/bin/Release/net10.0/Folio.Tests.dll`: 1,961 passed, 1 environment-dependent symlink test skipped.
- The focused serialized scripting acceptance run passes its covered cases, including the later M6 lifecycle, native WebAssembly, iframe sandbox, aggregate worker-memory, imported-table, brokered worker isolation, native texture-minification, media-budget, iframe permission, IPC-serialization, host capture-provider, GPU-reset, iframe message-source/target/port regressions, signed and unsigned array-texture sampling, native readback error/packing coverage, native array-growth semantics, and native worker structured-clone object/array/RegExp/cycle/Map/Set/Date/ArrayBuffer/typed-array/Error round trips plus native end-to-end worker transfer detachment.
- `dotnet run --project tests/Folio.RenderTests -- conformance D2 D3 D4 D5 D6 D7` (dependency-backed baseline): 60 passed, with no expected failures or unexpected failures.
- Native-engine conformance (fresh 2026-10-27 run): 49/60 passed — D2 8/10, D3 10/10, D4 10/10, D5 1/10, D6 10/10, D7 10/10. D6's text flowchart now passes after wiring mutable native array results and regex-based string splitting.
- `dotnet tests/Folio.RenderTests/bin/Release/net10.0/Folio.RenderTests.dll`: 744/744 passed.

## Milestone status

The dependency-backed scripting surface is complete for the current product phase, but Folio is not complete on that basis alone. The native-engine implementation and dual-engine matrix are work required by the mandatory Folio-owned replacement phase described in [`native-engine-plan.md`](native-engine-plan.md). This document therefore records the dependency-backed acceptance baseline, not a waiver of the replacement gate.
