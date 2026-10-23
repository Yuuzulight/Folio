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
| Simple-script shaper (Latin, Greek, Cyrillic, common punctuation and digits) | **Folio builds it** | Kerning and ligatures from `kern`/`GPOS`/`GSUB` |
| Complex-script shaping | **Dependency for now**: HarfBuzzSharp | Behind `ITextShaper` |
| PNG decoder | **Folio builds it** | On `System.IO.Compression.ZLibStream`; all colour types, bit depths, interlacing, `tRNS`, gamma ignored except sRGB; APNG shows the first frame |
| JPEG decoder | **Folio builds it** | Baseline and progressive, Huffman, 4:4:4/4:2:2/4:2:0, restart markers, EXIF orientation, DCT-domain downscaling; CMYK/arithmetic coding refused with a diagnostic |
| GIF and WebP decoders | **In-house** (`Folio.Imaging`), each equal to SkiaSharp's codec on the test images | `ImageLoader` |
| BMP and ICO decoders | Not decoded yet | `ImageLoader` |
| Display list, paint order, border/gradient/shadow geometry | **Folio builds it** | [study](study/12-painting.md) |
| 2D rasterisation | **Dependency for now**: SkiaSharp; an in-house backend (`SoftwareRasterBackend`) is written and checked against it, not yet the default | Behind `IRasterBackend`/`ICanvas` |
| Native GPU surface and GLSL translation | **Dependency for now**: ANGLE | Windows surface pipe uses D3D11 hardware or explicitly identified WARP; native WebGL shader/draw integration remains unfinished |
| Glyph rasterisation (outlines, hinting, colour glyph formats) | **Dependency for now**: SkiaSharp; glyf and CFF outlines and CBDT bitmaps are read in-house (`GlyphOutlines`), without hinting, CFF2 or COLR | Glyphs drawn by id through `ICanvas` |
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

`ShaperRouter` asks Folio's `SimpleShaper` first and falls back to `HarfBuzzShaper` (in `Folio.Skia`). A configuration switch forces all text through one shaper for differential testing.

### ANGLE — native GPU surfaces and shader translation

For #528, the selected Windows dependency is `Avalonia.Angle.Windows.Natives` **2.1.27548.20260419**, a native-only ANGLE distribution with no managed framework dependencies. Its `av_libglesv2.dll` assets cover Windows x64, ARM64 and x86. The [package metadata](https://www.nuget.org/packages/Avalonia.Angle.Windows.Natives/2.1.27548.20260419) identifies the Avalonia-maintained [ANGLE fork](https://github.com/AvaloniaUI/angle) and its BSD-style license; the package includes `LICENSE`. The restored package SHA-256 is `76d67901097e9173d155efc4c2b7d2c8e3122f2c2e0415028eb0c88478a386f9`.

Folio owns the device/surface/resource interfaces, permission and lifetime checks, resource reservations, canvas composition and process transport. ANGLE supplies native EGL/GLES and the shader translator. The first backend requests D3D11 hardware, then explicitly identified WARP when hardware creation fails. WARP is a native software driver; it is not hardware performance evidence. Failed native creation produces the graphics fallback rather than using Folio's existing CPU triangle approximation. The non-Windows path currently reports native GPU unavailability.

Resource reservations and peak reservations are distinct from native OS process usage. Windows metrics use [`IDXGIAdapter3.QueryVideoMemoryInfo`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo), with local/nonlocal bytes and measurement coverage reported separately; unavailable counters remain unknown. The benchmark's `gpu` command samples the sandboxed content process. The [native surface pipe](native-gpu-surfaces.md) has local sandbox/readback/budget evidence; this does not establish the full WebGL API, GLSL/draw conformance, loss/restoration or performance requirements in #529–#531.

This follows the dependency-first plan. Replacing ANGLE later would require a Folio-owned shader translator and native graphics backend behind the same interfaces; it is not part of this implementation.

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

**Why**: animated GIF, WebP (lossy VP8 and lossless) and the long tail of BMP/ICO variants are rare in artifacts; Folio's own decoders cover PNG and JPEG, which nearly all artifacts use.

**Swap-out interface**:

```csharp
public interface IImageDecoder
{
    bool CanDecode(ReadOnlySpan<byte> header);                    // signature sniffing
    ImageInfo ReadInfo(ReadOnlySpan<byte> data);                  // size checked against limits before any allocation
    DecodedImage Decode(ReadOnlySpan<byte> data, SizeI? targetSize);
}
```

`ImageDecoderRegistry` tries Folio's `PngDecoder` and `JpegDecoder` first, then `SkiaCodecDecoder` (in `Folio.Skia`).

### System services

`IFontSource` (list families, open face data, match a character for last-resort fallback), `IClipboard`, and the IME/accessibility hooks are implemented by the host layer (`Folio.Skia` for fonts, `Folio.WinForms` for the rest). Tests use a bundled-fonts `IFontSource`, which is also what makes the engine and its tests run on Linux CI.

## Towards all-Folio

The intended delivery strategy is dependency-first, replacement-last: finish the
product on the mature backends, keep every backend behind a Folio-owned interface,
then replace dependencies incrementally with differential tests and corpus
benchmarks. This keeps product work moving while the replacements are built. A
replacement is complete only when it matches the existing backend on conformance,
goldens, fuzzing, performance, and failure behavior.

1. **Extend the in-house shaper** script by script: Hebrew (marks), then Arabic (joining forms, mark positioning, contextual lookups), then Indic and South-East Asian scripts (reordering). *Good enough*: for each added script, glyph ids and positions identical to HarfBuzzSharp's output over the text corpus for the bundled and default system fonts; then that script stops routing to HarfBuzzSharp.
   - *Status (audited 2026-10-23)*: `Folio.Typography.ComplexShaper` maps Arabic letters to their presentation forms, gives Hebrew marks zero advance, reorders only the Devanagari pre-base vowel sign (U+093F) and gives Thai marks zero advance. It reads no GSUB or GPOS table, so conjuncts, reph, half forms, contextual lookups and mark attachment are not done, and its output is not yet compared with HarfBuzz per script. It is reachable only through `SoftwareRasterBackend.TextShaper` (canvas text), not from HTML layout. This item is open.
2. **Folio's own rasteriser** behind IRasterBackend: scanline coverage rasteriser with SIMD (Vector256), stroker, gradients, blur, compositing and blend modes, clip masks, image resampling, and glyph rasterisation from glyf/CFF/CFF2 outlines plus colour formats (COLR, bitmap glyphs through Folio's PNG decoder). *Good enough*: all reftests and goldens pass within their existing tolerances, text is judged equal in side-by-side review at 100% and 150% scale, and scroll and animation frame targets still hold at 1920×1080.
   - *Status (2026-10-23)*: `Folio.Painting.SoftwareRasterBackend` (code in `Folio.Painting.Software`) implements the whole `IRasterBackend`/`ICanvas` surface: antialiased fills (non-zero and even-odd, 16 vertical samples with exact horizontal coverage, edge ends snapped to a quarter pixel as Skia's analytic scan converter does), strokes with joins, caps and dashes, clips as coverage masks, all 26 blend modes, solid, linear, radial, focal and conic gradients, image shaders with mipmaps, Gaussian blur (box blurs from sigma 2), layers with their own bounds, every filter primitive (including turbulence, displacement, lighting and convolution) with Skia's rounding between primitives, backdrop filters, projective transforms, text from glyph outlines (`GlyphOutlines`: TrueType glyf with composites, CFF Type 2) and CBDT bitmap glyphs. Measured on Windows against the Skia backend: reftests 69 of 69, the 22 golden pages all within their tolerances, 14 differential scenes, and the conformance corpus 150 of 164 against Skia's 155. The 5 pages it fails and Skia passes (`D12/iframe-01`, `D12/iframe-02`, `S6/bilingual-train-timetable`, `S6/chinese-tech-news-article`, `S6/japanese-recipe-card`) are text pages 0.5% to 1% over the limit: DirectWrite snaps the horizontal stems of glyphs to whole pixels and Folio's outlines are not hinted. Unit tests check coverage against areas worked out by hand, every blend mode against the Compositing specification's formulas and image sampling at 0.5, 1 and 3 times, and the canvas 2D tests run on it too (127 of 130 pass; the rest need a JPEG encoder and the shaper's mark positioning). It is slow: replaying the typical dashboard's display list takes about 39 ms at 800×600 and 99 ms at 1920×1080 against Skia's 5 and 11 ms (7.6× and 9×; the benchmark harness prints it), so the 8 ms frame target is far off. Not done: CFF2 outlines, COLR colour glyphs, text hinting, a JPEG encoder, SIMD and any speed work. `FolioView` and the content host still use the Skia backend. This item is open.
3. **In-house GIF and WebP decoders**. *Good enough*: fuzzed, conformance images unchanged.
   - *Status (2026-10-23)*: `Folio.Imaging.GifDecoder` (GIF87a/GIF89a, LZW, interlacing, transparency; the first frame) and `Folio.Imaging.WebpDecoder` (lossless `VP8L` in `WebpLossless`, lossy `VP8` key frames in `Vp8Decoder` with `ALPH` alpha planes in `WebpAlpha`, an animated file's first frame) are used by `ImageLoader`; the SkiaSharp codecs are not used for either. Each is checked against SkiaSharp's decode on checked-in images (`DecoderReferenceTests`: zero tolerance for GIF and lossless WebP, one step for lossy WebP; equal today), against hostile input (truncation, bit flips, size limits) and by a fuzz target in CI. Done: #576, #577, #578.
4. **JavaScript engine**: planned removal of the temporary Jint dependency after
   the Folio-owned engine passes the full scripting and conformance suites.
   - *Status (audited 2026-10-23)*: `Folio.Scripting.Native` has a tokenizer, parser, bytecode compiler and stack VM for a small subset of the language: functions, closures, objects, arrays, loops, switch, simple try/catch, basic Promise and a few built-ins. It lacks classes, destructuring, spread, template-literal interpolation, regular expressions, `for...of`/`for...in`, default parameters, async functions, generators, `Map`/`Set`, `JSON`, accessors and more; postfix `++` on members and per-iteration `let` bindings are wrong. On a 40-case battery of ordinary scripts it passes 4 and the Jint backend passes all 40. It is not wired into the content host, and no suite runs the DOM bindings or the conformance corpus on it. Jint stays the engine.
