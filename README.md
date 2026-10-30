# Folio

A lightweight HTML/CSS rendering engine for .NET, written from scratch in C#.

Folio is being built for [Mana](https://github.com/Yuuzulight/Mana), a personal AI companion whose native Windows launcher is designed to stay light on memory. Mana writes HTML "artifacts" (reports, tables, charts, small interactive pages), and Folio shows them inside Mana's own window. It's a standalone project so other .NET apps can use it too.

**Status:** Folio has dependency-backed rendering through milestone 6 while the native JavaScript engine is now the production content-host default. The compatibility D2–D7 corpus passes, including Chart.js canvas parity, and the latest native D1–D14 sweep passes 102/102 artifacts. Jint remains only as an explicitly built differential oracle; SkiaSharp, HarfBuzz, Wasmtime and ANGLE replacement gates are unfinished. Milestone 6 is in progress; the [acceptance audit](docs/milestone-6-audit.md) records the remaining native backend, media, transport, isolation and sandbox requirements. Transactional Cache API storage now works through the host in both in-process and separate-process documents; see the [storage guide](docs/cache-api-storage.md). Native raster work now bounds each font's parsed-outline cache to 8 MiB and covers bounded COLR v1 solid, linear/radial/sweep gradients, composites and non-variable transform wrappers; CFF2 variation, broader color-font corpus and raster performance gates remain open. Milestone 7 is a **mandatory, non-waivable completion gate**: Folio is not finished until the dependency-backed shaper, rasteriser, image codecs, JavaScript engine, WebAssembly runtime and ANGLE GPU backend have all been replaced, differentially verified and removed from production. See the [replacement gate](docs/replacement-gate.md).

Latest recorded hosted verification: run `40820835338` completed green across all 26 jobs. That run predates the current audit-branch tip; no newer hosted result is claimed here. The mandatory replacement gate remains open.

## Goal
Render and run AI-written HTML artifacts as well as a modern browser does: pages that use modern CSS, web fonts, SVG charts and diagrams, and, in later milestones, the scripts, UI libraries and canvas graphics those artifacts rely on.

## Principles
- **Native and light:** draws with SkiaSharp inside the host app; tens of MB, not a separate browser process.
- **Interactive:** a real on-screen control with hover, clicks, scrolling, text selection and relayout on resize. A WinForms control comes first, with a headless render-to-image API alongside it.
- **Safe by default:** no network access unless the host allows it; scripts run sandboxed with strict limits.
- **Measured:** progress is tracked against a conformance suite of real-world-style artifacts.

## Non-goals
- Browsing the web: no address bar, tabs, logins or extensions.
- The parts of the web platform artifacts don't use (video calls, 3D, streaming media, offline apps).

## Milestones

Each milestone is a [GitHub milestone](https://github.com/Yuuzulight/Folio/milestones). Open-issue counts include each milestone's tracking issue.

| Milestone | Status | Open |
|---|---|---|
| 0. Study and architecture: how each part of an engine works, written up as Folio's own design in `docs/study`. | Done | 0 |
| 1. Static rendering: parsing, cascade, block, inline, flex and grid layout, text, images and painting. Ships as a WinForms control and a render-to-image API. | Done, in use in Mana; CI fuzz job still times out after a zero-failure run | 0 |
| 2. Static parity: gradients, shadows, transforms, transitions and animations, web fonts, and SVG. | Done | 0 |
| 3. Interaction: scrolling, hover, links, text selection, focus, forms and text input, incremental relayout and repaint. | Done; performance target remains an advisory measurement | 0 |
| 4. Scripting foundations: a sandboxed JavaScript engine, a broad DOM and CSS object model subset, events, timers, animation frames and storage. | In progress: D1 compatibility/native conformance is 12/12 with zero mismatches; scripted startup and working-set targets remain open under #484/#677 | 2 |
| 5. Scripted parity: canvas 2D, mutation observers, diagrams, and the UI and charting library artifacts load from a host-approved list of CDNs. | In progress: dependency-backed path and native canvas frame-time gate are verified; mandatory Folio-owned replacement remains | 1 |
| 6. Beyond artifacts: WebGL, audio and video, workers, WebAssembly, iframes, service workers and WebRTC. | In progress: capability slices pass, but backend, device, transport and isolation acceptance remains open | 15 |
| 7. Folio-owned replacements: replace the dependency-backed shaper, rasteriser, image codecs, script engine, WebAssembly runtime and ANGLE GPU backend with Folio-owned implementations. **Mandatory, non-waivable completion gate after Milestone 6.** | In progress, incomplete (mandatory) | 12 |

Until a milestone lands, anything Folio can't handle yet opens in the system browser.

## Using it

Hosts use Folio as NuGet packages: `Folio`, `Folio.Skia` and `Folio.WinForms`. Render HTML to a PNG:

```csharp
using Folio.Skia;

byte[] png = HeadlessRenderer.RenderPng("<h1>Quarterly report</h1><p>Revenue rose <strong>12%</strong> on the year.</p>", width: 800);
File.WriteAllBytes("report.png", png);
```

On Windows, `FolioView` (in `Folio.WinForms`) shows a document in a window: call `LoadHtml(html)` on it. Scripted documents can use `Folio.ContentHost` through `ContentProcessClient`; configure `FolioOptions.Scripting` as `ContentProcess` and provide `ContentProcessPath`.

## Building
Needs the .NET 10 SDK. The engine and its tests run on Windows and Linux; the WinForms control runs on Windows.

```
dotnet build Folio.slnx
# The test projects use direct xUnit v3 in-process runners.
dotnet tests/Folio.Tests/bin/Debug/net10.0/Folio.Tests.dll
dotnet tests/Folio.RenderTests/bin/Debug/net10.0/Folio.RenderTests.dll
dotnet tests/Folio.Scripting.Tests/bin/Debug/net10.0/Folio.Scripting.Tests.dll
dotnet tests/Folio.Fuzz/bin/Debug/net10.0/Folio.Fuzz.dll
dotnet tests/Folio.Benchmarks/bin/Debug/net10.0/Folio.Benchmarks.dll
dotnet tests/Folio.WinForms.Tests/bin/Debug/net10.0-windows/Folio.WinForms.Tests.dll
```

The solution-level `dotnet test` wrapper does not discover these in-process
runners; use the direct commands above (or the equivalent `Release` paths after
a Release build) so the tests actually execute.

The render tests include an equivalence check: after random style changes to a page, an incremental update must draw exactly what a fresh render draws.

Image tests that fail or change write their actual, expected and diff images to `tests/render-output/`. Golden images change only through the approve command, after reviewing those images:

```
dotnet run --project tests/Folio.RenderTests -- approve <area|area/name>
```

The conformance report renders the corpus and prints pass rates per category:

```
dotnet run --project tests/Folio.RenderTests -- conformance
```

The fuzzer mutates the parser and decoder test inputs for the given number of seconds per target. Inputs it fails on are written to `tests/fuzz-output/`; once fixed, they go in `tests/Folio.Fuzz/Regressions/<target>/`, which the test run replays:

```
dotnet run --project tests/Folio.Fuzz -c Release -- fuzz <seconds> [html|css|png|jpeg|font|svg]...
```

The benchmark harness times each pipeline stage and measures memory for generated documents and the conformance corpus, against the targets in `docs/study/18-memory-and-performance.md`:

```
dotnet run --project tests/Folio.Benchmarks -c Release -- bench [iterations] [frames]
```

`iterations` controls parse/layout measurements; `frames` independently fixes the number of animation,
interaction, and raster-replay samples so runs can be compared across machines (for example, `bench 20 80`).

`gc` reports the layout stage's time, GC pause and collection counts, and a whole render through the public API, for the large generated documents:

```
dotnet run --project tests/Folio.Benchmarks -c Release -- gc 9
```

The pack script writes the packages to a folder that works as a local package source (by default `artifacts/packages`). It runs in PowerShell 5.1 (`powershell`) or 7 (`pwsh`):

```
pwsh -File tools/pack.ps1 [-Output <folder>]
```

The version is `0.1.0-m1.N`, where N is the number of commits in the packed commit's history, so each commit on main packs as its own version. The script refuses to pack uncommitted changes or a shallow clone. A host pins a version, adds the folder as a package source, and can rebuild the same packages from the pinned commit.

## Licence
Apache-2.0, same as Mana.
