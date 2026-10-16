# Milestone 5 scripting results

This document records the current M5 implementation and verification baseline.

## Conformance

The content-process scripting path, restricted Fetch, raster-backed Canvas 2D, pinned library cache, and JSX hand-off are merged. The public D2–D7 corpus still needs to grow to at least ten artifacts per category before pass-rate claims are meaningful; it currently has one artifact in each category.

## Implemented surface

- MutationObserver, CSSOM rule mutation, restricted `fetch`, integrity checks and allowlisted script handling.
- Canvas 2D state, paths, gradients, transforms, compositing, functional colors, image data mutation, and raster-backed content-host rendering.
- Native diagram parsing/layout/SVG writing for flowchart, sequence, class, state, entity-relationship, Gantt, pie, and timeline forms, with configured Mermaid-library routing through the Folio stand-in and unsupported syntax fallback.
- Content-process shutdown and watchdog handling, including closed-pipe cleanup during termination.

## Verification baseline

- `dotnet build Folio.slnx --no-restore`: passes.
- `dotnet test tests/Folio.Tests/Folio.Tests.csproj --no-restore`: passes (environment-dependent Unicode and symlink tests may skip).
- `dotnet test tests/Folio.Scripting.Tests/Folio.Scripting.Tests.csproj --no-restore`: 438/438 passed after the Fetch and Canvas 2D merges.
- `dotnet test tests/Folio.RenderTests/Folio.RenderTests.csproj --no-restore`: 454/454 passed after the conformance-cache and JSX merge.

## Milestone status

Milestone 5 is in progress using the dependency-backed Jint engine. Remaining work includes the diagram and Mermaid issues, Canvas shared-memory frames, corpus expansion, the engine/custom-elements investigations, and the closing tracking and documentation issues. The frame-time requirement is covered by the D5/D7 benchmark and is tracked separately from the remaining implementation work. The Folio-owned engine described in [`native-engine-plan.md`](native-engine-plan.md) remains a later replacement project.
