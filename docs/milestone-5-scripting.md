# Milestone 5 scripting results

This document records the current M5 implementation and verification baseline.

## Conformance

The public D2–D7 corpus is run with the content-process scripting path and a 100 ms default virtual animation window. The latest run reports D2, D3, D4 and D6 as exact passes. D5 differs by 2.0% of pixels and D7 by 1.8%; both are below the roadmap's 10% category mismatch ceiling, but remain outside the renderer's strict 1% image-comparison tolerance.

## Implemented surface

- MutationObserver, CSSOM rule mutation, restricted `fetch`, integrity checks and allowlisted script handling.
- Canvas 2D state, paths, gradients, transforms, compositing, functional colors, image data mutation, shared frame publication and content-host rendering.
- Native diagram rendering through `Folio.Diagrams` for the supported flowchart, sequence, class/state and pie forms.
- Content-process shutdown and watchdog handling, including closed-pipe cleanup during termination.

## Verification baseline

- `dotnet build Folio.slnx --no-restore`: passes.
- `dotnet test tests/Folio.Tests/Folio.Tests.csproj --no-restore`: passes (environment-dependent Unicode and symlink tests may skip).
- `dotnet test tests/Folio.Scripting.Tests/Folio.Scripting.Tests.csproj --no-restore`: 216/216 passed.
- D5 and D7 paint-only performance still require a dedicated p95 frame measurement against the 16.7 ms M5 target.

The reproducible `canvas-perf` command (`dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj -- canvas-perf 3`) reports full conformance wall-time diagnostics. The script-driven frame command (`dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj -- canvas-frames 30`) warms each document, advances Jint's virtual clock in 16.7 ms ticks, reuses the raster surface, consumes each canvas command history after display-list emission, and measures a display-list frame. The current p95 baseline is 23.4 ms for D5 and 23.4 ms for D7, above the 16.7 ms target; the performance gate is therefore still open.

## Remaining closing gates

The native JIT backend and its two-engine CI matrix are not yet present. The final M5 closeout therefore remains open until the engine choice, p95 canvas measurements, artifact routing audit and corresponding baseline are completed.
