# Dependencies and what Folio builds itself

Folio builds the engine itself: everything that decides *what* a page looks like and how it behaves. It relies on the operating system for services the OS owns, and on a small number of libraries for work where the quality and speed bar is very high and not specific to HTML. Every library sits behind a Folio-defined interface so it can be replaced without touching the engine.

Target framework: **.NET 10** (`net10.0` for the engine and headless rendering, `net10.0-windows` for the WinForms control and the content process). No other runtimes are targeted.

## Overview

| Piece | Who | Notes |
|---|---|---|
| HTML tokenizer and tree builder | **Folio builds it** | [study](study/01-html-parsing.md) |
| DOM | **Folio builds it** | [study](study/02-dom.md) |
| XML parser (standalone SVG files) | **Folio builds it** | Namespaces, no DTD processing ([study](study/13-svg.md)) |
| CSS tokenizer, parser, selector matching | **Folio builds it** | [study](study/03-css-parsing-and-selectors.md) |
| Cascade, inheritance, computed values, `var()`, `calc()`, colour spaces | **Folio builds it** | [study](study/04-cascade-and-computed-values.md) |
| Box tree, layout (block, inline, flex, grid, tables, positioning, overflow) | **Folio builds it** | [studies 05–10](study/) |
| SVG styling, geometry, path parsing, arc conversion | **Folio builds it** | [study](study/13-svg.md) |
| Unicode text algorithms | **Folio builds it**, BCL where it covers the need | See [Unicode](#unicode-what-the-bcl-covers-and-what-folio-implements) below |
| Font file parsing (OpenType tables, WOFF, WOFF2) | **Folio builds it** | `cmap`, `head`, `hhea`, `hmtx`, `maxp`, `name`, `OS/2`, `post`, `kern`, `GSUB`/`GPOS`/`GDEF` basics, `fvar`; WOFF on `ZLibStream`, WOFF2 on `BrotliDecoder` plus Folio's `glyf` reconstruction |
| Font matching and fallback logic | **Folio builds it** | CSS Fonts 4 matching, per-cluster fallback chain ([study](study/11-text.md)) |
| Simple-script shaper (Latin, Greek, Cyrillic, Hebrew, common punctuation and digits) | **Folio builds it** | Kerning and ligatures from `kern`/`GPOS`/`GSUB`; Hebrew base, niqqud words, length-changing ccmp/rlig cases and conformance-style RTL paragraphs are differential-proven, while broader complex-script runs remain on the oracle |
| Complex-script shaping | **Dependency for now**: HarfBuzzSharp | Behind `ITextShaper` |
| PNG decoder | **Folio builds it** | On `System.IO.Compression.ZLibStream`; all colour types, bit depths, interlacing, `tRNS`, gamma ignored except sRGB; APNG shows the first frame |
| JPEG codec | **Folio builds it** | Decoder: baseline and progressive, Huffman, 4:4:4/4:2:2/4:2:0, restart markers, EXIF orientation, DCT-domain downscaling; CMYK/arithmetic coding refused with a diagnostic. Encoder: baseline 4:4:4 export for software-canvas `toDataURL("image/jpeg")`, with quality scaling, alpha compositing and Folio decoder round-trip coverage. |
| GIF decoder | **Folio builds it** | First-frame GIF87a/GIF89a decoding with palettes, interlace, transparency and bounded LZW |
| BMP decoder | **Folio builds it** | Bounded uncompressed 8/16/24/32-bit decoding, including top-down images and 16/32-bit BI_BITFIELDS masks; broader differential corpus expansion remains part of the replacement gate |
| WebP decoder | **Folio builds it** | RIFF/VP8L lossless and VP8 lossy key frames, ALPH alpha, extended/animated first-frame handling, bounded parsing and fuzz coverage |
| ICO decoder | **Folio builds it** | Bounded selection of the largest 16/24/32-bit DIB or embedded PNG entry, with alpha-mask handling; broader differential corpus remains open |
| Display list, paint order, border/gradient/shadow geometry | **Folio builds it** | [study](study/12-painting.md) |
| 2D rasterisation | **Dependency for now**: SkiaSharp | Behind `IRasterBackend`/`ICanvas` |
| Native GPU surface and GLSL translation | **Temporary mandatory dependency**: ANGLE (`Avalonia.Angle.Windows.Natives`) | Windows surface pipe uses D3D11 hardware or explicitly identified WARP; replacement is tracked by M7 issue #682 and is required before Folio completion |
| Glyph rasterisation (outlines, hinting, colour glyph formats) | **Folio-owned for glyf/loca, CFF Type 2, CFF2, CBDT/CBLC, PNG sbix and COLR v0 plus bounded COLR v1 solid/linear/radial/sweep-gradient/composite paint graphs and variable-wrapper base formats**; Skia remains for variation outlines and unsupported COLR v1 paint formats | SoftwareCanvas draws Folio-parsed outlines and bounded COLR v1 PaintSolid/PaintGlyph/PaintColrLayers/PaintColrGlyph/PaintLinearGradient/PaintRadialGradient/PaintSweepGradient/PaintComposite graphs, including PaintTranslate/PaintVarTranslate, PaintTransform/PaintVarTransform, PaintScale/PaintVarScale variants, PaintRotate/PaintVarRotate variants, and PaintSkew/PaintVarSkew variants; synthetic COLR v1 pixel coverage, translation/transform/scale/rotate/skew, variable-wrapper default-instance, linear/radial/sweep gradient, composite, CBDT/CBLC, sbix and CFF2 differential/malformed-limit checks pass (SoftwareRasterTests 39/39, including the cache-cap stress test); PNG bitmap strikes and layered palette glyphs use Folio parsers; parsed outlines and flattened paths share an 8 MiB per-font LRU budget; non-default variation deltas, unsupported composite modes and broader malformed/limit corpus remain required |
| Font discovery and last-resort system fallback | **System-provided**: Windows (DirectWrite font collection and fallback) | Reached through `IFontSource`; the first implementation uses SkiaSharp's font manager, which calls DirectWrite on Windows |
| Hit testing, selection, input routing, form editing | **Folio builds it** | [study](study/15-interaction.md) |
| Clipboard | **System-provided** | WinForms `Clipboard` |
| IME (text composition) | **System-provided** | Windows IME messages handled by the control |
| Accessibility | **System-provided** | UI Automation through WinForms accessibility objects |
| Cursors, tooltips, DPI, colour scheme, reduced motion, refresh rate | **System-provided** | Read through WinForms and Windows settings |
| Resource loaders, cache, integrity checks | **Folio builds it** | On `HttpClient` and `System.Security.Cryptography` ([study](study/16-resources-and-security.md)) |
| WinForms control | **Folio builds it** | On .NET WinForms |
| Content process, IPC protocol, event loop, DOM bindings, canvas 2D API | **Folio builds it** (M4–M5) | [study](study/17-scripting.md) |
| Process sandbox (Job Objects, AppContainer / low-integrity token) | **System-provided** | Set up by Folio through Windows APIs |
| JavaScript engine | **Temporary compatibility dependency**: Jint | Behind `IScriptEngine`; the production engine is planned as Folio-owned code |
| WebRTC signalling and loopback transport | **Folio builds it** | Capability-gated offer/answer, ICE state, tracks and data channels; external ICE/peer transport remains a host-owned seam |
| Diagram renderer (diagram-description languages → SVG) | **Folio builds it** (M5) | `Folio.Diagrams`, instead of running a diagram library in the sandbox |
| JSX compilation | **Host** (Mana) | Done before content reaches Folio |
| Per-artifact script storage | **Host** | Behind `IArtifactStorage`: local, isolated per artifact |
| Code generators (entities, property table, Unicode tables, bindings), test tools | **Folio builds it** | Build-time only, in `tools/` |

## Unicode: what the BCL covers and what Folio implements

| Need | Source |
|---|---|
| UTF-8/UTF-16 decoding, `Rune` iteration | BCL (`System.Text`) |
| General category | BCL (`Rune.GetUnicodeCategory`, `CharUnicodeInfo`) |
| Extended grapheme clusters (UAX #29) | BCL (`StringInfo`, `StringInfo.GetNextTextElementLength`) |
| Simple case mapping, normalisation | BCL (`TextInfo`, `string.Normalize`) |
| Full case mapping (`SpecialCasing.txt`) for `text-transform` | Folio, generated table |
| Line breaking (UAX #14) | Folio, from `LineBreak.txt` and `EastAsianWidth.txt` |
| Bidirectional algorithm (UAX #9) | Folio, from `DerivedBidiClass.txt`, `BidiBrackets.txt`, `BidiMirroring.txt` |
| Word boundaries (UAX #29) for double-click selection and `text-transform: capitalize` | Folio, from `WordBreakProperty.txt` |
| Script property (UAX #24) for run segmentation | Folio, from `Scripts.txt`, `ScriptExtensions.txt` |
| Emoji properties (UTS #51) | Folio, from `emoji-data.txt` |

Tables are generated by `tools/Folio.Gen` into committed C# source (two-stage lookup tables) and pinned to the same Unicode version as the runtime's grapheme segmentation. Unicode's own conformance test files run in CI.

## Why each dependency is a dependency, and how it can be swapped

### SkiaSharp — 2D rasterisation, glyph rasterisation, extra image codecs

**Why it is hard to build**: antialiased path filling that is both exact (no seams between adjacent shapes, correct coverage on thin lines) and fast needs SIMD-tuned scanline code; stroking with joins, caps and dashes; gradients without banding; Gaussian blur fast enough for shadows at 60 fps; Porter-Duff compositing and blend modes on premultiplied pixels; offscreen layers; antialiased clip masks; high-quality image resampling with mipmaps; glyph rasterisation with hinting and every colour-glyph format (`COLR` v0/v1, `CBDT`, `sbix`). Each of these is mature in the library, and text quality at 96 DPI is where users notice differences first.

**Swap-out interface**:

```csharp
public interface IRasterBackend
{
    IRasterSurface CreateSurface(int width, int height);          // premultiplied BGRA8
    IFontHandle LoadFont(ReadOnlyMemory<byte> data, int faceIndex, FontVariation variation);
    IImageHandle CreateImage(DecodedImage pixels);
}

public interface ICanvas                                          // the display list replays onto this
{
    void Save(); void Restore();
    void Translate(float dx, float dy); void Concat(in Matrix3x2 m);
    void ClipRect(in RectF r, bool antialias); void ClipRoundedRect(in RoundedRect r); void ClipPath(PathData p, FillRule rule);
    void FillRect(in RectF r, in Paint paint); void FillRoundedRect(in RoundedRect r, in Paint paint);
    void FillPath(PathData p, FillRule rule, in Paint paint); void StrokePath(PathData p, in Stroke stroke, in Paint paint);
    void DrawGlyphs(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<PointF> positions, in Paint paint);
    void DrawImage(IImageHandle image, in RectF src, in RectF dst, ImageSampling sampling);
    void DrawBoxShadow(in RoundedRect box, in BoxShadow shadow);
    void PushLayer(in LayerOptions options);                      // opacity, blend mode, filter chain, backdrop filter, mask
    void PopLayer();
    ReadOnlySpan<float> GetGlyphIntercepts(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs,
                                           ReadOnlySpan<PointF> positions, float top, float bottom); // underline skip-ink
}
```

`Paint` is Folio's own description (solid colour, linear/radial/conic gradient with stops, image pattern). Implementations: `SkiaRasterBackend` (in `Folio.Skia`) and `RecordingCanvas` (in the engine, for tests).

### HarfBuzzSharp — complex-script shaping

**Why it is hard to build**: correct shaping of Arabic, Indic, South-East Asian and other complex scripts requires script-specific reordering and feature application in exactly the order fonts are designed for, plus all `GSUB`/`GPOS` lookup types including contextual and chaining lookups and mark attachment. Errors produce visibly broken text for whole languages.

**Swap-out interface**:

```csharp
public interface ITextShaper
{
    bool CanShape(in ShapingRequest request);                     // script, font, features, text
    void Shape(in ShapingRequest request, ShapedRunBuilder output); // glyph ids, clusters, advances, offsets, unsafe-to-break
}
```

`HybridShaper` (the production `Folio.Skia` shaper) asks Folio's `SimpleShaper` first and falls back to `HarfBuzzShaper` for complex runs. The differential harness now has an explicit per-script promotion switch with conservative mixed-script handling: a script can be moved to Folio's path only while its corpus is being proven, without changing the default safe routing. Folio's Arabic path now consumes bounded GSUB single/multiple substitutions, applies the computed isol/init/medi/fina form at its shaping position, and applies pair advances, mark attachments and Folio-owned GPOS cursive entry/exit attachment; representative joining, marked-vocabulary and S6 paragraph cases are green, while broader Arabic corpus parity is still open. The differential tests compare both paths on bundled Latin, Hebrew and Arabic fonts, including Hebrew base runs, niqqud mark-to-base positioning and RTL paragraphs. Multiple-mark attachment and complex scripts remain on HarfBuzz until each replacement track meets the criteria below. The bundled corpus can also be rerun as a standalone gate with `dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj -c Release -- shaping-differential`; the current checked-in case list passes and the deliberate kerning perturbation is detected. System-font coverage and the complete complex-script corpus remain mandatory before promotion.

The local system-font side is reproducible with `dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj -c Release -- shaping-differential-system`; the current Windows host compares Arial, Times New Roman and Segoe UI simple runs plus expanded Hebrew and Arabic complex runs, including marked and paragraph cases, and reports all ten PASS. Installed-font results remain host-local evidence. The bundled command reports the complete checked-in case list plus its deliberate kerning perturbation check.

### ANGLE — native GPU surfaces and shader translation

For #528, the selected Windows dependency is `Avalonia.Angle.Windows.Natives` **2.1.27548.20260419**, a native-only ANGLE distribution with no managed framework dependencies. Its `av_libglesv2.dll` assets cover Windows x64, ARM64 and x86. The [package metadata](https://www.nuget.org/packages/Avalonia.Angle.Windows.Natives/2.1.27548.20260419) identifies the Avalonia-maintained [ANGLE fork](https://github.com/AvaloniaUI/angle) and its BSD-style license; the package includes `LICENSE`. The restored package SHA-256 is `76d67901097e9173d155efc4c2b7d2c8e3122f2c2e0415028eb0c88478a386f9`.

Folio owns the device/surface/resource interfaces, permission and lifetime checks, resource reservations, canvas composition and process transport. ANGLE supplies native EGL/GLES and the shader translator. The first backend requests D3D11 hardware, then explicitly identified WARP when hardware creation fails. WARP is a native software driver; it is not hardware performance evidence. Failed native creation produces the graphics fallback rather than using Folio's existing CPU triangle approximation. The non-Windows path currently reports native GPU unavailability.

Resource reservations and peak reservations are distinct from native OS process usage. Windows metrics use [`IDXGIAdapter3.QueryVideoMemoryInfo`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo), with local/nonlocal bytes and measurement coverage reported separately; unavailable counters remain unknown. The benchmark's `gpu` command samples the sandboxed content process. The [native surface pipe](native-gpu-surfaces.md) has local sandbox/readback/budget evidence; this does not establish the full WebGL API, GLSL/draw conformance, loss/restoration or performance requirements in #529–#531.

This follows the dependency-first plan. Replacing ANGLE later would require a Folio-owned shader translator and native graphics backend behind the same interfaces; it is not part of this implementation.

### WebAssembly execution — Wasmtime

**M6 native-path repair (2026-10-26):** `Wasmtime` 48.0.2 is now wired behind the WebAssembly binding seam. Granted numeric modules, including JavaScript function imports, execute in the native engine with a per-store linear-memory cap and epoch-based execution deadline; validated modules are reused through a bounded compiled-module cache, local/imported memories synchronize through the native boundary, numeric local/imported globals use type-safe native accessors, and instances plus the engine are retired with the document. Basic local funcref table exports are exposed with null-reference mutation, and native-origin or typed ordinary JavaScript numeric funcref entries can cross imported tables through signature-checked callbacks. Reference-valued signatures and other unsupported exports continue through the compatibility path while their native bridge is completed. Wasmtime is a dependency-backed transition step, not the final Folio-owned implementation; removing it remains mandatory under Milestone 7 and is explicitly listed in the [replacement gate](replacement-gate.md).

### JavaScript engine — Jint

**M6 audit repair (2026-10-19):** upgraded the compatibility backend to Jint 4.17.0. Jint 4.1 blocked during pending `await`, and Folio's background evaluation/host-pump workaround could run continuations concurrently and corrupt lexical scope. The wrapper now evaluates and calls functions directly on the event-loop owner, with promise jobs drained at checkpoints. Awaited timer/fetch/microtask and worker lifecycle regressions cover this path. Folio-owned installer scripts use a bounded prepared-code cache through an internal host-only path; artifact URLs cannot select it. Jint's native stack-overflow guard is enabled, while artifact execution limits remain unchanged. This repair does not satisfy M6's separate native-engine/WebAssembly requirements.


**M5 decision (2026-10-16):** keep Jint as the dependency-backed engine behind `IScriptEngine` while scripted parity is completed. We are not selecting a native JIT binding for M5: it would add architecture-specific binaries and a second binding surface before the corpus demonstrates that Jint misses a required bar. The Folio-owned parser, VM, and optimizer remain the planned replacement after the dependency-backed path is complete.

The decision was made against the options below. The current warm-process
`folio-bench bench 3` run is the rendering baseline; an apples-to-apples engine
startup and working-set comparison is intentionally deferred until the Folio-owned
engine exists. The other rows are design options, not claims that an unbuilt engine
has already been benchmarked.

| Option | Current evidence | Decision |
|---|---|---|
| Jint 4.17 behind `IScriptEngine` | Async setup returns promises; the host executes evaluations and promise jobs on one owning thread | Use for M5 parity and corpus growth |
| Native JIT binding (V8 or equivalent) | Would require architecture-specific binaries, a second binding surface and a new sandbox audit before it could replace Jint | Do not add as an M5 dependency |
| Folio-owned parser, VM and hot-path compiler | No implementation or measurements yet; the staged plan is documented in [`native-engine-plan.md`](native-engine-plan.md) | Build after the dependency-backed path, then measure against this baseline |
| `IScriptEngine` compatibility seam | DOM bindings, limits, interrupts and microtask checkpoints are already independent of Jint | Keep as the cutover contract |

**Why it is hard to build**: a standards-conforming ECMAScript engine (the whole language, its built-in library, regular expressions, garbage-collected object model, and enough speed for library bundles) is a project of its own, unrelated to rendering.

**Swap-out interface**:

```csharp
public interface IScriptEngine : IDisposable
{
    void Configure(ScriptLimits limits);                          // time, statements, recursion, memory
    ScriptValue Evaluate(string source, string url);
    ScriptValue Call(ScriptValue function, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args);
    ScriptValue CreateHostObject(IHostObjectTemplate template, object target); // DOM wrappers from generated bindings
    void PerformMicrotaskCheckpoint();
    void Interrupt();                                             // watchdog
}
```

The engine runs only inside the sandboxed content process ([study](study/17-scripting.md)).
`Folio.Scripting.Jint` remains a temporary compatibility backend while Folio's
own parser, VM, and native hot-path compiler are built. The production target is
zero external JavaScript-runtime dependencies; see
[`native-engine-plan.md`](native-engine-plan.md).

### WebRTC — Folio API and loopback transport

The scripting layer owns the WebRTC object model and a deterministic in-process
loopback transport used by tests and offline artifacts. `FolioOptions.WebRtcAllowedOrigins`
limits ICE server URLs, and the capability is disabled by default. Real network
transport is intentionally host-owned: a future transport adapter can route
packets through the host the same way resource loads are routed, without giving
the content process direct network access.

### Image codecs not built in-house

**Why**: the image replacement track is now Folio-owned for GIF and WebP. The remaining codec work is corpus expansion for long-tail BMP/ICO variants, not a production WebP fallback.

**Swap-out interface**:

```csharp
public interface IImageDecoder
{
    bool CanDecode(ReadOnlySpan<byte> header);                    // signature sniffing
    ImageInfo ReadInfo(ReadOnlySpan<byte> data);                  // size checked against limits before any allocation
    DecodedImage Decode(ReadOnlySpan<byte> data, SizeI? targetSize);
}
```

The current `ImageLoader.Decode` path tries Folio's `PngDecoder`, `JpegDecoder`,
first-frame `GifDecoder`, bounded `WebpDecoder` (VP8L/VP8 plus ALPH), bounded
`BmpDecoder` and bounded `IcoDecoder`. WebP lossless and lossy fixtures are
checked against SkiaSharp, and both GIF and WebP are fuzz targets. The decoder
returns null for malformed or over-limit data rather than exposing partial
pixels.

### System services

`IFontSource` (list families, open face data, match a character for last-resort fallback), `IClipboard`, and the IME/accessibility hooks are implemented by the host layer (`Folio.Skia` for fonts, `Folio.WinForms` for the rest). Tests use a bundled-fonts `IFontSource`, which is also what makes the engine and its tests run on Linux CI.

## Required Folio-owned replacement track (mandatory completion gate)

The authoritative release rule is [`replacement-gate.md`](replacement-gate.md).
This track is non-optional and non-waivable: dependency-first delivery is only
the order of work, never a substitute for replacing the production backends.

The intended delivery strategy is dependency-first, replacement-last: finish the
product on the mature backends, keep every backend behind a Folio-owned interface,
then replace dependencies incrementally with differential tests and corpus
benchmarks. The replacements are mandatory for Folio completion; dependency-first
only defines the delivery order. A replacement is complete only when it matches
the existing backend on conformance, goldens, fuzzing, performance, and failure
behavior, and the dependency can be removed from the production build.

This is a hard release gate: no milestone, feature-complete claim, or Folio
completion report may waive, defer, or mark this track optional. The product may
ship intermediate dependency-backed implementations while the replacements are
being built, but Folio itself is complete only after all five replacement tracks below
are selected by production and their superseded dependencies are removable.

**Current status (2026-10-29):** the latest Release build and direct M1–M6
acceptance suites pass. Native D2–D7 now passes 60/60, including the React host
reconciliation interaction fixed in #645; the mandatory replacement gate remains
open. The production Skia path now selects
`HybridShaper`, which routes provably simple runs through Folio's own shaper and
keeps HarfBuzz as the complex-script oracle. Folio's GPOS mark-to-base/mark-to-mark
parser has direct differential coverage; the Folio complex shaper now matches the
bundled Hebrew base, isolated-mark and selected RTL multi-mark cases, while production
broader complex Hebrew runs remain on HarfBuzz until corpus-wide ordering,
GSUB and kerning parity is proven. Arabic now has Folio-owned joining plus
script-selected `locl`/`rlig`/`calt`/`liga` contextual substitutions for the
representative corpus, but broad Arabic mark/corpus parity, Indic and South-East
Asian scripts, and the differential corpus remain outstanding. The
rasteriser and JavaScript engine are still dependency-backed. The
latest local system-font shaping differential report passes 10/10 cases; this
is host-specific evidence only, and the complete per-script corpus and
production cutover requirements remain open. The
Folio-owned JavaScript replacement has now started with a bounded lexer and
source-location/error tests plus bounded expression, statement, explicit
host-function, array-literal, indexed-access, object-literal, property-read,
and object/array mutation, bounded while/for-loops with break/continue,
throw/catch/finally control flow,
prefix/postfix updates, conditional expressions, `typeof`/`void`, signed-Int32
bitwise and shift operators, and exponentiation, plus
captured function-expression AST evaluation with live outer-binding assignment,
recursive nested closure capture, multi-character/logical operators, function
declarations, single- and multi-parameter arrow functions, bounded `Array.isArray`,
`Object.keys`, `Math`, coercion/parsing, console, bounded array/string member
methods, callback-oriented collection methods, mutable script arrays with `push`/`pop`/`shift`/`unshift`/`splice`, JSON parse/stringify,
`queueMicrotask`, basic Promise checkpoint scheduling, bounded `new` syntax,
scoped `with` blocks and a bounded dynamic `Function` constructor,
isolated-engine limit propagation, and staged
`IHostObjectTemplate` property/method/indexed access plus buffer-source
ownership/copy/detachment operations and script-visible typed-buffer
properties/indexed mutation in `Folio.Scripting.Native`;
it is available to the content host through the explicit
`FOLIO_SCRIPT_ENGINE=native` probe, but is not selected by default while
content-process parity is incomplete; selecting the native engine does not by
itself complete the dependency-removal gate. The
Folio-owned GIF decoder is production-selected, differentially checked against
SkiaSharp, bounded to the 12-bit GIF LZW table, and included in the image fuzz
target (a five-second run exercised 4,067,165 mutations with no failures). Four
generated differential fixtures now cover interlaced and transparent frames in
addition to the one-pixel corpus case. Its full conformance corpus is still to
be assembled. The production ICO path now has an embedded-PNG differential
fixture against Skia as well. BMP is also production
selected through a bounded Folio decoder for uncompressed palette, 24-bit and
32-bit images; its broader differential corpus still remains to be assembled.
Generated bottom-up and top-down BMP cases are also pixel-compared with
SkiaSharp in the render-test suite.

1. **Extend the in-house shaper** script by script: bounded Hebrew base and isolated-mark runs now meet the differential bar; multiple-mark Hebrew, then Arabic (joining forms, mark positioning, contextual lookups), Indic and South-East Asian scripts (reordering) follow. *Good enough*: for each added script, glyph ids and positions identical to HarfBuzzSharp's output over the text corpus for the bundled and default system fonts; then that script stops routing to HarfBuzzSharp.
2. **Folio's own rasteriser** behind `IRasterBackend`: scanline coverage rasteriser with SIMD (`Vector256`), stroker, gradients, blur, compositing and blend modes, clip masks, image resampling, and glyph rasterisation from `glyf`/`CFF`/`CFF2` outlines plus colour formats (`COLR`, bitmap glyphs through Folio's PNG decoder). Default-instance COLR v1 variable solid/gradient paint wrappers now retain their base paint values; non-default variation deltas remain part of the open gate. *Good enough*: all reftests and goldens pass within their existing tolerances, text is judged equal in side-by-side review at 100% and 150% scale, and scroll and animation frame targets still hold at 1920×1080.
3. **In-house GIF and WebP decoders**. Both decoders are production-selected and bounded. GIF covers first-frame GIF87a/GIF89a with interlace/transparency; WebP covers VP8L lossless, VP8 key-frame lossy, ALPH alpha, extended and animated first-frame files. Differential corpus and fuzz evidence are checked in; this image-codec replacement gate is complete.
4. **JavaScript engine**: planned removal of the temporary Jint dependency after
   the Folio-owned engine passes the full scripting and conformance suites.
5. **WebAssembly runtime**: replace Wasmtime and the compatibility interpreter
   with Folio-owned validation, compilation and execution covering imports,
   linear memory, tables, globals, streaming, workers, traps, limits and
   teardown. The native Wasmtime bridge is an intermediate M6 backend, never a
   completion substitute.
