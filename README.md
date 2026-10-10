# Folio Artifacts

A lightweight HTML/CSS rendering engine for .NET, written from scratch in C#.

Folio is being built for [Mana](https://github.com/Yuuzulight/Mana), a personal AI companion whose native Windows launcher is designed to stay light on memory. Mana writes HTML "artifacts" (reports, tables, charts, small interactive pages), and Folio shows them inside Mana's own window. It's a standalone project so other .NET apps can use it too.

**Status:** static rendering (milestone 1) is complete and already in use in Mana. Static parity (milestone 2) is nearly done, and interaction (milestone 3) has started.

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
0. **Study and architecture:** how each part of an engine works, written up as Folio's own design in `docs/`.
1. **Static rendering:** HTML and CSS parsing, the cascade, block, inline, flex and grid layout, text and font fallback, images, painting with SkiaSharp. Ships as a WinForms control (page scrolling and clickable links) and a render-to-image API.
2. **Static parity:** gradients, shadows, transforms, transitions and animations, web fonts, and SVG. Most artifacts look identical to how a browser shows them.
3. **Interaction:** scrolling, hover, links, text selection, focus, forms and text input, incremental relayout and repaint.
4. **Scripting foundations:** a sandboxed JavaScript engine, a broad DOM and CSS object model subset, events, timers, animation frames and storage.
5. **Scripted parity:** canvas 2D at 60 fps, mutation observers, and the UI and charting libraries artifacts load from a host-approved list of CDNs. Scripted artifacts behave as they do in a browser.

Until a milestone lands, anything Folio can't handle yet opens in the system browser.

## Building
Needs the .NET 10 SDK. The engine and its tests run on Windows and Linux; the WinForms control runs on Windows.

```
dotnet build Folio.slnx
dotnet test --solution Folio.slnx
```

Image tests that fail or change write their actual, expected and diff images to `tests/render-output/`. Golden images change only through the approve command, after reviewing those images:

```
dotnet run --project tests/Folio.RenderTests -- approve <area|area/name>
```

The fuzzer mutates the parser and decoder test inputs for the given number of seconds per target. Inputs it fails on are written to `tests/fuzz-output/`; once fixed, they go in `tests/Folio.Fuzz/Regressions/<target>/`, which the test run replays:

```
dotnet run --project tests/Folio.Fuzz -c Release -- fuzz <seconds> [html|css|png|jpeg|font|svg]...
```

The benchmark harness times each pipeline stage and measures memory for generated documents and the conformance corpus (plus the private corpus when present), against the targets in `docs/study/18-memory-and-performance.md`:

```
dotnet run --project tests/Folio.Benchmarks -c Release -- bench [iterations]
```

Hosts use Folio as NuGet packages: `Folio`, `Folio.Skia` and `Folio.WinForms`. The pack script writes them to a folder that works as a local package source (by default `artifacts/packages`). It runs in PowerShell 5.1 (`powershell`) or 7 (`pwsh`):

```
pwsh -File tools/pack.ps1 [-Output <folder>]
```

The version is `0.1.0-m1.N`, where N is the number of commits in the packed commit's history, so each commit on main packs as its own version. The script refuses to pack uncommitted changes or a shallow clone. A host pins a version, adds the folder as a package source, and can rebuild the same packages from the pinned commit.

## Licence
Apache-2.0, same as Mana.
