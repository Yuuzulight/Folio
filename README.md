<p align="center">
  <img src="docs/assets/folio-logo.png" alt="Folio" width="300">
</p>

<p align="center">
  A lightweight HTML/CSS rendering engine for .NET, written from scratch in C#.
</p>

<p align="center">
  <a href="https://github.com/Yuuzulight/Folio/actions/workflows/ci.yml"><img src="https://github.com/Yuuzulight/Folio/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/Yuuzulight/Folio/releases/latest"><img src="https://img.shields.io/github/v/release/Yuuzulight/Folio" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/licence-Apache--2.0-blue" alt="Apache-2.0"></a>
</p>

<p align="center">
  <a href="docs/releases/1.0.0.md">Release notes</a> &middot;
  <a href="docs/architecture.md">Documentation</a> &middot;
  <a href="#what-folio-supports">Supported features</a> &middot;
  <a href="https://github.com/Yuuzulight/Folio/milestone/9">1.1 plans</a>
</p>

## What Folio is

Folio renders and runs HTML pages (reports, tables, charts, small interactive pages) inside your application's own window, or straight to an image, without embedding a web view. It started as the rendering engine for [Mana](https://github.com/Yuuzulight/Mana) and is a standalone library that any .NET application can use.

- **Embeddable.** A WinForms control with hover, clicks, scrolling, text selection and relayout on resize, and a headless renderer that produces PNGs.
- **Not a general-purpose browser.** There is no address bar, profile, extension system or page navigation. Advanced web APIs are narrower than in a browser, and some are not available; see [what Folio supports](#what-folio-supports).
- **Safe by default.** Scripts run in a separate sandboxed process with strict limits, and a page has no network access unless the host grants it.
- **Light.** Typical scripted pages start in about 25 ms using about 60 MiB; see [footprint](#footprint).
- **Platforms.** Packages target .NET 10. The WinForms control and the script host need Windows; static rendering also runs on Linux.

## Install

Folio is released as GitHub release assets and is not on NuGet.org. Download the packages into a folder, add the folder as a NuGet source, and reference the package:

```
gh release download v1.0.0 --repo Yuuzulight/Folio --pattern "*.nupkg" --pattern SHA256SUMS.txt --dir folio-feed
dotnet nuget add source ./folio-feed --name folio
dotnet add package Folio --version 1.0.0
```

The packages are `Folio` (portable static rendering), `Folio.Diagrams`, `Folio.Scripting` and `Folio.WinForms` (the Windows control). Check the files against `SHA256SUMS.txt` before use. Running page scripts also needs the standalone host, `Folio.ContentHost-1.0.0-win-x64.zip`, from the same release; deploy the whole extracted directory, including `fonts`.

## Quick start

Render HTML to a PNG, with no external rendering libraries:

```csharp
using Folio.Rendering;

byte[] png = HeadlessRenderer.RenderPng("<h1>Quarterly report</h1><p>Revenue rose <strong>12%</strong> on the year.</p>", width: 800);
File.WriteAllBytes("report.png", png);
```

`RenderPng` is a static renderer and does not run page scripts. It supplies bundled fonts when none are specified and applies image size limits.

Show a page in a Windows application by adding a `Folio.WinForms.FolioView` to a form and calling `LoadHtml(html)`. The control includes bundled fonts. Dispose it when its window closes.

Run page scripts by pointing the control at the standalone host:

```csharp
view.Options = new FolioOptions
{
    Scripting = ScriptingPolicy.ContentProcess,
    ContentProcessPath = Path.Combine(AppContext.BaseDirectory, "Folio", "Folio.ContentHost.exe")
};
view.LoadHtml(html);
```

Network, media, camera and microphone access must be granted deliberately by the host; see [feature capabilities](docs/feature-capabilities.md). Do not treat a loaded page as proof that all of its requested features worked.

## What Folio supports

Folio renders and runs a tested subset of the web platform. It does not claim general browser compatibility.

| Area | Status | Notes |
|---|---|---|
| HTML and CSS layout, text, bundled fonts | Supported | Bundled fonts cover a finite licensed set; families load on demand. |
| SVG and images | Supported | |
| Headless render to PNG | Supported | Windows and Linux. |
| Interactive control (hover, clicks, scroll, selection, resize) | Supported | Windows. |
| Diagrams (`Folio.Diagrams`) | Supported | |
| Scripting | Supported | Folio's own JavaScript engine in a sandboxed process. React 18.3.1 with Babel, Twind, D3 7.9.0 and Phaser 3.90.0 are tested. Chart.js and Mermaid are not run as libraries: pages that load them are drawn by Folio's built-in chart bridge and diagram renderer, which cover a subset ([#821](https://github.com/Yuuzulight/Folio/issues/821), [#822](https://github.com/Yuuzulight/Folio/issues/822)). |
| Canvas 2D | Supported | |
| WebRTC | Supported, with limits | Data channels, Opus, VP8 and H.264 receive, through a host-mediated transport. Interop was checked with Chrome only; sent video is VP8 key frames only. See the [release notes](docs/releases/1.0.0.md#known-limits). |
| WebAssembly, WebGL, workers, media, camera and microphone | Partial | Available when the host grants them; coverage is the tested corpus. Capture needs a host-supplied provider. |
| Iframes | Partial | Implemented, but not yet promoted to the production feature set; artifacts that use them fall back. |
| Service workers | Not supported | Denied. |
| Navigation, address bar, extensions, profiles | Not supported | Out of scope. |

The [consumer readiness audit](docs/consumer-readiness-audit.md) and [feature capabilities](docs/feature-capabilities.md) have the detail, and the [release notes](docs/releases/1.0.0.md#known-limits) list the known limits.

## Footprint

Measured on an AMD Ryzen 7 5800X, Windows 11, .NET SDK 10.0.403, JIT, on a quiet machine. Figures are medians; the [measurements record](docs/releases/1.0.0-measurements.md) has the method, the ranges and every row.

| What | Measured | Budget |
|---|---|---|
| Script start-up, ordinary libraries (React, Twind, diagrams) | 23 to 25 ms, at most 61 MiB | 50 ms, 80 MiB |
| Script start-up, charts (D3, chart bridge) | 30 to 33 ms, at most 63 MiB | 60 ms, 85 MiB |
| Script start-up, Phaser | 98 ms, at most 86 MiB | 180 ms, 130 MiB |
| Script host cold start | 178 ms | 400 ms |
| First paint, headless | 37 ms | 50 ms |
| First paint, visible control | **53.5 ms: over budget** | 50 ms |
| Animation frame, typical dashboard | 6.2 ms | 8 ms |
| Scroll frame (retained scroll) | 1.6 ms | 8 ms |

The visible first paint misses its budget in JIT mode. ReadyToRun or NativeAOT packaging removes most of the cause, and that choice is the application's.

## Documentation

- [Architecture](docs/architecture.md), [roadmap](docs/roadmap.md) and [interactive artifacts](docs/interactive-artifacts.md)
- [Content process sandbox](docs/content-process-sandbox.md) and [dependencies](docs/dependencies.md)
- [Release notes for 1.0.0](docs/releases/1.0.0.md)

## Contributing

Building, running the tests, fuzzing, benchmarks and packing are described in [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

Apache-2.0. Third-party notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
