# Folio

A lightweight HTML/CSS rendering engine for .NET, written from scratch in C#.

Folio is being built for [Mana](https://github.com/Yuuzulight/Mana), a personal AI companion whose native Windows launcher is designed to stay light on memory. Mana writes HTML "artifacts" (reports, tables, charts, small interactive pages), and Folio shows them inside Mana's own window. It's a standalone project so other .NET apps can use it too.

**Status:** static rendering (milestone 1) is complete and in use in Mana. Static parity (milestone 2) has 5 open issues left. Interaction (milestone 3) is in progress: 6 issues closed, 24 open.

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
| 1. Static rendering: parsing, cascade, block, inline, flex and grid layout, text, images and painting. Ships as a WinForms control and a render-to-image API. | Done, in use in Mana | 0 |
| 2. Static parity: gradients, shadows, transforms, transitions and animations, web fonts, and SVG. | Nearly done | 5 |
| 3. Interaction: scrolling, hover, links, text selection, focus, forms and text input, incremental relayout and repaint. | In progress | 24 |
| 4. Scripting foundations: a sandboxed JavaScript engine, a broad DOM and CSS object model subset, events, timers, animation frames and storage. | Not started | 40 |
| 5. Scripted parity: canvas 2D at 60 fps, mutation observers, diagrams, and the UI and charting libraries artifacts load from a host-approved list of CDNs. | Not started | 36 |
| 6. Beyond artifacts: WebGL, audio and video, workers, WebAssembly, iframes, service workers and WebRTC. | Not started | 45 |
| Optional: towards all-Folio: an in-house shaper, rasteriser and image decoders. Never a blocker for the milestones above. | Not started | 19 |

Until a milestone lands, anything Folio can't handle yet opens in the system browser.

## Using it

Hosts use Folio as NuGet packages: `Folio`, `Folio.Skia` and `Folio.WinForms`. Render HTML to a PNG:

```csharp
using Folio.Skia;

byte[] png = HeadlessRenderer.RenderPng("<h1>Quarterly report</h1><p>Revenue rose <strong>12%</strong> on the year.</p>", width: 800);
File.WriteAllBytes("report.png", png);
```

On Windows, `FolioView` (in `Folio.WinForms`) shows a document in a window: call `LoadHtml(html)` on it.

## Building
Needs the .NET 10 SDK. The engine and its tests run on Windows and Linux; the WinForms control runs on Windows.

```
dotnet build Folio.slnx
dotnet test --solution Folio.slnx
```

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
dotnet run --project tests/Folio.Benchmarks -c Release -- bench [iterations]
```

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
