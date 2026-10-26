# Milestone 5 scripting results

Verification refresh (2026-10-26): the serialized scripting suite now passes 1,161/1,161, including signed and unsigned integer 2D-array sampling and native `readPixels` error handling coverage added during the Milestone 6 audit.

This document records the current M5 implementation and verification baseline.

## Conformance

The content-process scripting path, restricted Fetch, raster-backed Canvas 2D, pinned library cache, and JSX hand-off are merged. The public D2–D7 corpus now contains ten reviewed, library-backed artifacts per category (60 total), each with an interaction script and reference. D6's ten Mermaid artifacts exercise flowchart, sequence, class, state, entity-relationship, timeline, Gantt and pie syntax. The current run passes every D2–D7 category at 100%, including all ten Chart.js canvas artifacts in D5.

`ArtifactClassifier` retains Folio's scripted path for the canvas features covered by the parity corpus, including the pinned Chart.js artifacts.

## Implemented surface

- MutationObserver, CSSOM rule mutation, restricted `fetch`, integrity checks and allowlisted script handling.
- Canvas 2D state, paths, gradients, transforms, compositing, functional colors, image data mutation, and raster-backed content-host rendering.
- Native diagram parsing/layout/SVG writing for flowchart, sequence, class, state, entity-relationship, Gantt, pie, and timeline forms. Sequence supports participants, aliases, message kinds, activation markers, notes, blocks, and autonumbering; class/state/entity forms expose members, relations, labels, notes, and attributes. Configured Mermaid-library routing uses the Folio stand-in with unsupported-syntax fallback.
- Content-process shutdown and watchdog handling, including closed-pipe cleanup during termination.

## Verification baseline

- `dotnet build Folio.slnx --no-restore`: passes.
- `dotnet tests/Folio.Tests/bin/Release/net10.0/Folio.Tests.dll`: 1,947 passed, 1 environment-dependent symlink test skipped.
- `dotnet tests/Folio.Scripting.Tests/bin/Release/net10.0/Folio.Scripting.Tests.dll`: 1,159/1,159 passed in serialized mode (including the later M6 lifecycle, native WebAssembly, iframe sandbox, aggregate worker-memory, imported-table, brokered worker isolation, native texture-minification, media-budget, iframe permission, IPC-serialization, host capture-provider, GPU-reset, iframe message-source/target/port regressions, and unsigned array-texture sampling).
- `dotnet run --project tests/Folio.RenderTests -- conformance D2 D3 D4 D5 D6 D7`: 60 passed, with no expected failures or unexpected failures.
- `dotnet tests/Folio.RenderTests/bin/Release/net10.0/Folio.RenderTests.dll`: 658/658 passed.

## Milestone status

The dependency-backed scripting surface is complete for the current product phase, but Folio is not complete on that basis alone. The native-engine implementation and dual-engine matrix are work required by the mandatory Folio-owned replacement phase described in [`native-engine-plan.md`](native-engine-plan.md). This document therefore records the dependency-backed acceptance baseline, not a waiver of the replacement gate.
