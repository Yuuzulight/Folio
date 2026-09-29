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
| GIF, WebP, BMP, ICO decoders | **Dependency for now**: SkiaSharp codecs | Behind `IImageDecoder` |
| Display list, paint order, border/gradient/shadow geometry | **Folio builds it** | [study](study/12-painting.md) |
| 2D rasterisation | **Dependency for now**: SkiaSharp | Behind `IRasterBackend`/`ICanvas` |
| Glyph rasterisation (outlines, hinting, colour glyph formats) | **Dependency for now**: SkiaSharp | Glyphs drawn by id through `ICanvas` |
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
| JavaScript engine | **Dependency**: Jint through M4; a native JIT-compiling engine from M5 | Behind `IScriptEngine`; the switch is a backend swap |
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

### JavaScript engine — Jint through M4, a native JIT-compiling engine from M5

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

Both engines run only inside the sandboxed content process ([study](study/17-scripting.md)). M4 ships `Folio.Scripting.Jint`; M5 adds `Folio.Scripting.Jit` on a native JIT-compiling engine (chosen at the start of M5) as a second implementation of the same interface, so the switch is a backend swap and does not change the security model.

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

Optional future work, done only if a dependency becomes a problem (size, licensing, platform reach, or a quality issue Folio cannot fix upstream). None of it blocks the milestones in the [roadmap](roadmap.md).

1. **Extend the in-house shaper** script by script: Hebrew (marks), then Arabic (joining forms, mark positioning, contextual lookups), then Indic and South-East Asian scripts (reordering). *Good enough*: for each added script, glyph ids and positions identical to HarfBuzzSharp's output over the text corpus for the bundled and default system fonts; then that script stops routing to HarfBuzzSharp.
2. **Folio's own rasteriser** behind `IRasterBackend`: scanline coverage rasteriser with SIMD (`Vector256`), stroker, gradients, blur, compositing and blend modes, clip masks, image resampling, and glyph rasterisation from `glyf`/`CFF`/`CFF2` outlines plus colour formats (`COLR`, bitmap glyphs through Folio's PNG decoder). *Good enough*: all reftests and goldens pass within their existing tolerances, text is judged equal in side-by-side review at 100% and 150% scale, and scroll and animation frame targets still hold at 1920×1080.
3. **In-house GIF and WebP decoders**. *Good enough*: fuzzed, conformance images unchanged.
4. **JavaScript engine**: not planned; it stays a dependency.
