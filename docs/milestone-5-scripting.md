# Milestone 5 scripting results

This document records the current M5 implementation and verification baseline.

## Conformance

The public D2–D7 corpus is run with the content-process scripting path and a 100 ms default virtual animation window. The latest run reports all six categories at 100% under their documented tolerances. D5 differs by 2.0% of pixels and D7 by 1.8%; both are within the scripted-category review ceiling documented in their manifests.

## Implemented surface

- MutationObserver, CSSOM rule mutation, restricted `fetch`, integrity checks and allowlisted script handling.
- Canvas 2D state, paths, gradients, transforms, compositing, functional colors, image data mutation, shared frame publication and content-host rendering.
- Native diagram rendering through `Folio.Diagrams` for the supported flowchart, sequence, class/state and pie forms.
- Content-process shutdown and watchdog handling, including closed-pipe cleanup during termination.

## Verification baseline

- `dotnet build Folio.slnx --no-restore`: passes.
- `dotnet test tests/Folio.Tests/Folio.Tests.csproj --no-restore`: passes (environment-dependent Unicode and symlink tests may skip).
- `dotnet test tests/Folio.Scripting.Tests/Folio.Scripting.Tests.csproj --no-restore`: 216/216 passed.
- D5 and D7 paint-only performance have a dedicated p95 frame measurement against the 16.7 ms M5 target.

The reproducible `canvas-perf` command (`dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj -- canvas-perf 3`) reports full conformance wall-time diagnostics. The script-driven frame command (`dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj -- canvas-frames 30`) warms each document, advances Jint's virtual clock in 16.7 ms ticks, reuses the raster surface, consumes each canvas command history after display-list emission, and measures display-list replay after frame preparation. The current paint-only p95 baseline is 2.50 ms for D5 and 4.75 ms for D7, below the 16.7 ms target.

## Milestone status

Milestone 5 is complete using the dependency-backed Jint engine by design. The
Folio-owned engine described in [`native-engine-plan.md`](native-engine-plan.md)
is a later replacement project and does not block scripted parity or product
completion.
