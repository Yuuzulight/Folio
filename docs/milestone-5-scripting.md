# Milestone 5 scripting results

This document records the current M5 implementation and verification baseline.

## Conformance

The content-process scripting path, restricted Fetch, raster-backed Canvas 2D, pinned library cache, and JSX hand-off are merged. The public D2–D7 corpus now contains ten reviewed, library-backed artifacts per category (60 total), each with an interaction script and reference. D6's ten Mermaid artifacts exercise flowchart, sequence, class, state, entity-relationship, timeline, Gantt and pie syntax. The current run passes D2, D3, D4, D6 and D7 at 100%; D5 has one pass and nine explicit expected failures for Chart.js canvas painting, which remains routed to the browser.

`ArtifactClassifier` now gives Chart.js canvas pages the same explicit browser routing reason as the conformance report, while retaining Folio's scripted path for the canvas features that meet the current parity bar.

## Implemented surface

- MutationObserver, CSSOM rule mutation, restricted `fetch`, integrity checks and allowlisted script handling.
- Canvas 2D state, paths, gradients, transforms, compositing, functional colors, image data mutation, and raster-backed content-host rendering.
- Native diagram parsing/layout/SVG writing for flowchart, sequence, class, state, entity-relationship, Gantt, pie, and timeline forms. Sequence supports participants, aliases, message kinds, activation markers, notes, blocks, and autonumbering; class/state/entity forms expose members, relations, labels, notes, and attributes. Configured Mermaid-library routing uses the Folio stand-in with unsupported-syntax fallback.
- Content-process shutdown and watchdog handling, including closed-pipe cleanup during termination.

## Verification baseline

- `dotnet build Folio.slnx --no-restore`: passes.
- `dotnet test tests/Folio.Tests/Folio.Tests.csproj --no-restore`: 1,815 passed, 5 environment-dependent Unicode and symlink tests skipped.
- `dotnet test tests/Folio.Scripting.Tests/Folio.Scripting.Tests.csproj --no-restore`: 461/461 passed.
- `dotnet run --project tests/Folio.RenderTests -- conformance D2 D3 D4 D5 D6 D7`: 51 passed and 9 explicit expected failures, with no unexpected failures.
- `dotnet test tests/Folio.RenderTests/Folio.RenderTests.csproj --no-restore`: 454/454 passed.

## Milestone status

Milestone 5 is complete on the dependency-backed Jint engine. The native-engine implementation and dual-engine matrix are deliberately deferred to the Folio-owned replacement project described in [`native-engine-plan.md`](native-engine-plan.md); the M5 tracker records those issues as deferred rather than presenting the dependency-backed path as native.
