# Folio

A lightweight HTML/CSS rendering engine for .NET, written from scratch in C#.

Folio is being built for [Mana](https://github.com/Yuuzulight/Mana), a personal AI companion whose native Windows launcher is designed to stay light on memory. Mana writes HTML "artifacts" (reports, tables, charts, small interactive pages), and Folio shows them inside Mana's own window. It's a standalone project so other .NET apps can use it too.

**Status:** The Windows production content host uses Folio-owned rendering, shaping, scripting and WebAssembly implementations. Optional Skia, HarfBuzz, Jint and Wasmtime paths are compatibility or differential-test tools, not requirements of the production package graph. Folio is not a general-purpose browser. Repository test results do not establish that every originally planned web-platform feature is complete: see the [consumer readiness audit](docs/consumer-readiness-audit.md) for integration evidence and remaining feature blockers.

## Goal
Render and run AI-written HTML artifacts as well as a modern browser does: pages that use modern CSS, web fonts, SVG charts and diagrams, and, in later milestones, the scripts, UI libraries and canvas graphics those artifacts rely on.

## Principles
- **Native and light:** static content uses Folio's own renderer inside the host app; scripts run in a separately constrained content process.
- **Interactive:** a real on-screen control with hover, clicks, scrolling, text selection and relayout on resize. A WinForms control comes first, with a headless render-to-image API alongside it.
- **Safe by default:** no network access unless the host allows it; scripts run sandboxed with strict limits.
- **Measured:** progress is tracked against a conformance suite of real-world-style artifacts.

## Scope

Folio provides artifact rendering and interaction rather than address bars, browser profiles, extensions or general web navigation. Static HTML/CSS, text, SVG, images and the tested scripting/canvas corpus have dedicated implementation and regression coverage. Advanced APIs have narrower support: their existence in the DOM is not a promise of browser-equivalent networking, device integration or isolation.

The [milestone-6 acceptance audit](docs/milestone-6-audit.md) and [replacement gate](docs/replacement-gate.md) document the original requirements. Closed GitHub milestones and passing feature slices should not be used to erase unmet requirements. The production classifier deliberately retains browser routing for advanced features that have not been promoted; hosts must handle that result.

## Using it

Reference `Folio` for portable static rendering, or `Folio.WinForms` for the Windows control. The complete production feed contains `Folio`, `Folio.Diagrams`, `Folio.Scripting` and `Folio.WinForms`; NuGet resolves their dependencies. Render HTML to a PNG without external rendering libraries:

```csharp
using Folio.Rendering;

byte[] png = HeadlessRenderer.RenderPng("<h1>Quarterly report</h1><p>Revenue rose <strong>12%</strong> on the year.</p>", width: 800);
File.WriteAllBytes("report.png", png);
```

`RenderPng` is a static renderer: it does not execute page scripts. It supplies bundled fonts when none are specified, preserves explicit host font/resource policies, and applies image size limits. For explicit layout, use `Document.Parse` with `Fonts = BundledFontSource.Settings`, then `HeadlessRenderer.Render`; dispose both the document and returned image.

On Windows, add a `Folio.WinForms.FolioView` to a form and call `LoadHtml(html)`. The default control includes bundled fonts. Replacing its options does not remove those defaults unless an explicit font source is supplied. Font families load on demand; the shipped corpus is not universal Unicode coverage. Packages copy fonts and license notices into consumer output and publish directories. A host that supplies all fonts itself may set the MSBuild property `FolioBundledFonts=false`.

For scripts, publish the standalone Windows host separately, deploy the **whole** output directory (including `fonts`), and configure:

```csharp
view.Options = new FolioOptions
{
    Scripting = ScriptingPolicy.ContentProcess,
    ContentProcessPath = Path.Combine(AppContext.BaseDirectory, "Folio", "Folio.ContentHost.exe")
};
view.LoadHtml(html);
```

Dispose the control when its window closes. The production host is a separate download/build artifact, not an executable hidden inside the NuGet packages. Portable static rendering does not require it. Low-level integrations can use `ContentProcessClient.StartWithOptions` to forward host resource, storage and media services, then send the document and its capability grants through the protocol. The raw `Start` overload remains available for explicitly configured integrations.

Network, media, camera and microphone permissions must be supplied deliberately by the host. Camera/microphone capture requires both the corresponding capability grant and an available provider; an optional host permission callback can deny requests. The WinForms control invokes that callback on its UI thread. Disposing, reloading or revoking a grant releases owned capture sessions. Advanced API support and denied capabilities must remain visible to the user; do not treat a loaded page as proof that all of its requested features worked.

The explicitly selected `Folio.Skia` adapter remains available from source for compatibility/oracle builds; it is not part of the default production feed.

## Building
Needs .NET SDK 10.0.403 or newer (including the patched .NET 10.0.14 runtime). The engine and its tests run on Windows and Linux; the WinForms control runs on Windows.

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

The solution uses Microsoft.Testing.Platform. Run the complete suite with
`dotnet test --solution Folio.slnx -c Release --max-parallel-test-modules 1`, or use the direct commands above
for individual projects.

The Windows production content host is a standalone NativeAOT executable. Publishing it requires the patched SDK
above and Visual Studio C++ build tools with a Windows SDK (plus ARM64 tools when targeting ARM64):

```powershell
pwsh -File tools/publish-content-host.ps1
```

The script validates the native image, patched compiler packages, font hashes and license notices before replacing
an existing publish. It writes `native-build.json` with the build provenance. Published-host integration tests use
`FOLIO_CONTENT_HOST` to select that exact executable; ordinary developer builds remain available for engine oracles.

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
pwsh -File tools/pack.ps1 [-Output <folder>] [-Version <release-version>]
```

By default the version is `0.1.0-m1.N`, where N is the number of commits in the packed commit's history, so each commit on main packs as its own version. The script refuses to pack uncommitted changes or a shallow clone. An explicit `-Version` lets a clean tagged commit produce its release package version. A host pins both a commit and package version, adds the feed as a package source, and builds the standalone host from that same commit. Building packages does not publish a GitHub release. Mana automatic updates accept published stable releases only, excluding drafts and prereleases.

## Licence
Apache-2.0, same as Mana.
