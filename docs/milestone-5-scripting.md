# Milestone 5 scripting results

This document records the current M5 implementation and verification baseline.

## Conformance

The content-process scripting path, restricted Fetch, raster-backed Canvas 2D, pinned library cache, and JSX hand-off are merged. The public D2–D7 corpus now contains ten reviewed, library-backed artifacts per category (60 total); the current conformance run passes all 60, including their interaction scripts.

## Implemented surface

- MutationObserver, CSSOM rule mutation, restricted `fetch`, integrity checks and allowlisted script handling.
- Canvas 2D state, paths, gradients, transforms, compositing, functional colors, image data mutation, and raster-backed content-host rendering.
- Native diagram parsing/layout/SVG writing for flowchart, sequence, class, state, entity-relationship, Gantt, pie, and timeline forms. Sequence supports participants, aliases, message kinds, activation markers, notes, blocks, and autonumbering; class/state/entity forms expose members, relations, labels, notes, and attributes. Configured Mermaid-library routing uses the Folio stand-in with unsupported-syntax fallback.
- Content-process shutdown and watchdog handling, including closed-pipe cleanup during termination.

## Verification baseline

- `dotnet build Folio.slnx --no-restore`: passes.
- `dotnet test tests/Folio.Tests/Folio.Tests.csproj --no-restore`: 1,809 passed, 5 environment-dependent Unicode and symlink tests skipped.
- `dotnet test tests/Folio.Scripting.Tests/Folio.Scripting.Tests.csproj --no-restore`: 458/458 passed.
- `dotnet run --project tests/Folio.RenderTests -- conformance D2 D3 D4 D5 D6 D7`: 60/60 passed.
- `dotnet test tests/Folio.RenderTests/Folio.RenderTests.csproj --no-restore`: 454/454 passed.

## Milestone status

Milestone 5 is in progress using the dependency-backed Jint engine. Remaining work includes complete diagram-library artifact coverage, the native-engine implementation and dual-engine tests, final classifier routing, and the closing tracking work. The Folio-owned engine described in [`native-engine-plan.md`](native-engine-plan.md) remains a later replacement project.
