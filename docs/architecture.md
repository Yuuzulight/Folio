# Folio architecture

Folio turns an HTML string into pixels in a .NET window, and later runs the artifact's scripts safely. This document is the overall design; each subsystem's reasoning is in [`docs/study/`](study/), dependencies in [`dependencies.md`](dependencies.md), and the milestone plan in [`roadmap.md`](roadmap.md).

**Target**: AI-written single-file HTML artifacts, reaching parity with how a modern browser shows and runs them, in milestones. Static rendering comes first; scripting comes last and runs out of process.

## Pipeline

```
 HTML text ──► Tokenizer ──► Tree builder ──► DOM ◄───────────── (M4) scripts via bindings
                                               │
 CSS (UA, <style>, <link>, style="") ──► Stylesheets ──► Selector matching + cascade
                                               │
                                               ▼
                                         Computed styles (shared style groups)
                                               │
                                               ▼
                                          Box tree  (anonymous boxes, pseudo-elements, inline items)
                                               │
                                               ▼  Layout(box, ConstraintSpace) → Fragment
                                         Fragment tree (immutable, positioned)
                                               │
                                               ▼
                                         Stacking tree ──► Hit testing, selection
                                               │
                                               ▼
                                         Display list (flat, backend-independent)
                                               │  replay with scroll offsets, culling
                                               ▼
                                     ICanvas ──► SkiaSharp surface ──► WinForms control / PNG
```

One entry point drives it: `Document.Update()` runs only the stages that are dirty ([invalidation](study/14-invalidation.md)). Resource loads (images, fonts, stylesheets) complete asynchronously and mark the document dirty.

| Stage | Input → output | Study |
|---|---|---|
| Parse HTML | text or bytes → DOM | [01](study/01-html-parsing.md), [02](study/02-dom.md) |
| Parse CSS | text → rules, bucketed selectors | [03](study/03-css-parsing-and-selectors.md) |
| Style | DOM + rules → `ComputedStyle` per element | [04](study/04-cascade-and-computed-values.md) |
| Box tree | DOM + styles → boxes | [05](study/05-box-tree.md) |
| Layout | boxes + viewport → fragments | [06](study/06-layout-block-and-inline.md), [07](study/07-layout-flexbox.md), [08](study/08-layout-grid.md), [09](study/09-layout-tables.md), [10](study/10-layout-positioning-overflow-stacking.md), [11](study/11-text.md), [13](study/13-svg.md) |
| Paint | fragments → stacking tree → display list → pixels | [12](study/12-painting.md) |
| Interaction | input → hit test → default actions / DOM events | [15](study/15-interaction.md) |
| Resources and security | URLs → bytes, through the host's loader | [16](study/16-resources-and-security.md) |
| Scripting (M4+) | content process, event loop, bindings | [17](study/17-scripting.md) |
| Budgets and testing | | [18](study/18-memory-and-performance.md), [19](study/19-testing.md) |

## Key decisions

1. **Everything that decides layout and appearance is Folio's own code**; SkiaSharp (raster), HarfBuzzSharp (complex shaping) and later a JavaScript engine are dependencies behind Folio interfaces ([dependencies](dependencies.md)).
2. **Separate trees with one-way flow**: DOM → styles → box tree → immutable fragment tree → stacking tree → display list. Each stage can be tested by dumping its output as text.
3. **Layout as functions with caches**: `Layout(box, ConstraintSpace) → Fragment` and `IntrinsicSizes(box)`, cached per box. Flex, grid and tables measure children freely without exponential cost.
4. **Spec algorithms implemented step by step**, with method names that follow spec section names, so review against the spec is mechanical.
5. **`float` CSS pixels** in layout; pixel snapping only at paint time.
6. **Shared, immutable style groups** plus a style-sharing cache keep style memory small.
7. **Flat display list with scroll frames**: scrolling and sticky positioning never rebuild layout or the list.
8. **Deny-by-default resources**: all loads go through the host's `IResourceLoader`; the default allows `data:` URLs only.
9. **Static documents render in-process; scripted documents run in a sandboxed content process** that owns DOM, style, layout and the script engine and sends display lists to the host.
10. **Content never throws**: bad HTML/CSS is recovered per spec; limits stop work gracefully; everything is reported as diagnostics that a host (Mana) can feed back to the model that wrote the artifact.
11. **Scripts**: Jint through M4, a native JIT-compiling engine from M5, both behind `IScriptEngine` so the switch is a backend swap. JSX is compiled by the host before content reaches Folio; diagram-description languages are rendered by Folio's own diagram renderer instead of a diagram library.
12. **.NET 10 only**: `net10.0` for the engine (runs on Windows and Linux, which the tests use), `net10.0-windows` for the WinForms control and the content process.

## Solution layout

```
Folio.slnx
src/
  Folio/                     net10.0          The engine. No native dependencies.
    Dom/                     Nodes (DocumentNode, Element, ...), atoms, ranges
    Html/                    Tokenizer, tree builder, entity table
    Xml/                     Small XML parser for standalone SVG
    Css/                     Tokenizer, parser, property table, selectors, media queries
    Style/                   Cascade, computed values, style groups, sharing cache
    Typography/              Unicode algorithms, OpenType reader, WOFF/WOFF2, font matching,
                             fallback, SimpleShaper, ShaperRouter, shaping cache
                             Interfaces: ITextShaper, IFontSource
    Imaging/                 PngDecoder, JpegDecoder, ImageDecoderRegistry
                             Interface: IImageDecoder
    Layout/                  Box tree, block/inline/flex/grid/table/positioned layout, fragments
    Svg/                     SVG render tree, path data, paint servers
    Painting/                Stacking tree, display list builder, RecordingCanvas
                             Interfaces: IRasterBackend, ICanvas
    Interaction/             Hit testing, selection, InputRouter, form controls, focus
    Resources/               ResourceRequest/Response, DataUri, LocalFolderLoader,
                             AllowlistHttpLoader, cache, integrity
                             Interface: IResourceLoader
    Hosting/                 Document, FolioOptions, Update(), diagnostics, limits,
                             ArtifactClassifier
  Folio.Skia/                net10.0          SkiaRasterBackend (ICanvas), HarfBuzzShaper (ITextShaper),
                                              SkiaCodecDecoder (IImageDecoder), SystemFontSource (IFontSource),
                                              HeadlessRenderer
  Folio.WinForms/            net10.0-windows  FolioView control: input, IME, clipboard, accessibility,
                                              cursors, tooltips, popups
  Folio.Scripting/           net10.0          (M4) Event loop, generated DOM/CSSOM bindings, canvas 2D API,
                                              IPC protocol. Interface: IScriptEngine
  Folio.Scripting.Jint/      net10.0          (M4) IScriptEngine on Jint
  Folio.Scripting.Jit/       net10.0          (M5) IScriptEngine on a native JIT-compiling engine
  Folio.Diagrams/            net10.0          (M5) Diagram-description languages → SVG in the DOM
  Folio.ContentHost/         net10.0-windows  (M4) Sandboxed content process executable
tools/
  Folio.RefCapture/          Captures reference images from the installed reference browsers
  Folio.Gen/                 Generates entity table, property table, Unicode tables, bindings,
                             the test box font
tests/
  Folio.Tests/               net10.0          Unit tests, parser/layout/display-list dumps, Unicode conformance
  Folio.RenderTests/         net10.0          Reftest runner + golden-image runner (headless via Folio.Skia);
                                              `folio-test approve` command; writes actual/expected/diff images
  Folio.WinForms.Tests/      net10.0-windows  Control behaviour (input, scrolling, selection)
  Folio.Fuzz/                net10.0          Fuzz harnesses (HTML, CSS, fonts, images, SVG/XML, IPC)
  Folio.Benchmarks/          net10.0          Time and memory harness over the conformance corpus
  fonts/                     Bundled test fonts (box font + openly licensed text and emoji fonts)
  reftests/<spec-module>/    test.html + ref.html pairs, manifest of expected failures
  goldens/<area>/            Feature golden images with tolerance settings
  conformance/<category>/<artifact>/
                             index.html, manifest (features, viewports, tolerance, interaction script),
                             reference.png (parity target), reference-secondary.png (tie-breaker),
                             approved.png (regression guard)
  conformance-private/       Gitignored: the user's own artifacts (or FOLIO_PRIVATE_CORPUS), local only
```

**Boundaries**:

- `Folio` references no native library. Namespaces are layered: `Resources` → `Dom`/`Html`/`Xml` → `Css`/`Style` → `Typography`/`Imaging` → `Layout`/`Svg` → `Painting` → `Interaction` → `Hosting`; lower layers never reference higher ones (`Resources` sits at the bottom and knows nothing of the DOM). A unit test reads the assembly's metadata and fails on a layering violation.
- Swap-out interfaces live in the engine; implementations that use a dependency live in `Folio.Skia`, `Folio.Scripting.Jint` or `Folio.Scripting.Jit`. Replacing a dependency means adding one assembly.
- `Folio.WinForms` contains no rendering logic; it translates Windows messages to Folio input and blits the surface.

## Public API sketch

```csharp
namespace Folio;

public sealed class FolioOptions
{
    public IResourceLoader ResourceLoader { get; init; } = ResourceLoaders.DataUrisOnly;
    public Uri? BaseUri { get; init; }
    public ColorScheme ColorScheme { get; init; } = ColorScheme.Light;
    public bool ReducedMotion { get; init; }
    public string? UserStyleSheet { get; init; }
    public FontSettings Fonts { get; init; } = FontSettings.Default;   // generic families, fallback lists
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;
    public ScriptingPolicy Scripting { get; init; } = ScriptingPolicy.Disabled; // M4: ContentProcess
    public string? ArtifactId { get; init; }                           // M4: key for per-artifact storage
    public IArtifactStorage? Storage { get; init; }                    // M4: localStorage backing; null = memory only
    public bool CollectDiagnostics { get; init; } = true;
}

public sealed class Document : IDisposable
{
    public static Document Parse(string html, FolioOptions? options = null);
    public static Document Parse(Stream bytes, FolioOptions? options = null);   // encoding sniffing
    public string Title { get; }
    public Element? DocumentElement { get; }
    public Element? GetElementById(string id);
    public Element? QuerySelector(string selectors);
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public Task ResourcesSettled { get; }            // all allowed loads finished or failed
}

public static class ArtifactClassifier
{
    // Static: Folio renders it fully. Scripted: needs scripting (M4+). NeedsBrowser: hand to the system browser.
    public static ArtifactKind Classify(string html, FolioOptions options);
}

public sealed record Diagnostic(DiagnosticCode Code, Severity Severity, string Message,
                                SourceLocation? Location, string? Feature);
```

Headless rendering (in `Folio.Skia`):

```csharp
public static class HeadlessRenderer
{
    public static RenderResult Render(Document document, RenderRequest request);
    public static byte[] RenderPng(string html, int width, FolioOptions? options = null); // convenience
}

public sealed record RenderRequest(
    int ViewportWidth,
    int? ViewportHeight = null,          // null: full document height
    float DeviceScale = 1f,
    TimeSpan? ResourceWait = null,       // how long to wait for allowed loads
    TextAntialiasing Text = TextAntialiasing.Greyscale); // Subpixel on request; the control follows the system setting

public sealed class RenderResult : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public ReadOnlySpan<byte> Pixels { get; }   // premultiplied BGRA8, row-major
    public void SavePng(Stream destination);
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
```

WinForms control (in `Folio.WinForms`):

```csharp
public class FolioView : Control
{
    public FolioOptions Options { get; set; }
    public Document? Document { get; }
    public void LoadHtml(string html, Uri? baseUri = null);
    public float ZoomFactor { get; set; } = 1f;

    public string SelectedText { get; }                                  // M3
    public void SelectAll();                                              // M3
    public void CopySelection();                                          // M3
    public void ScrollToFragment(string id);                              // M3

    public event EventHandler? Rendered;
    public event EventHandler<DiagnosticsEventArgs>? DiagnosticsChanged;
    public event EventHandler<RenderFailedEventArgs>? RenderFailed;
    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;     // M1: host decides; if unhandled, http/https/mailto open in the system browser
    public event EventHandler<FormSubmittedEventArgs>? FormSubmitted;     // M3
}
```

## Threading model

- **A `Document` is single-threaded.** It belongs to one thread at a time; debug builds assert this. In `FolioView` that is the UI thread; in headless use, whichever thread calls `Render`.
- **Loading**: `FolioView.LoadHtml` parses on a worker thread; the finished `Document` is handed to the UI thread before anyone else can see it. Style, layout and display-list building run on the UI thread (small documents fit the frame budget; see [targets](study/18-memory-and-performance.md)).
- **Raster**: on the UI thread in M1. The display list is immutable once built, so rasterisation can move to a worker thread later without changing the engine.
- **Resource decoding** (images, fonts, stylesheets) runs on the thread pool; results are posted back to the owning thread through a dispatcher (the control's `SynchronizationContext`; headless rendering pumps its own queue while waiting).
- **Process-wide caches** (atom table, UA stylesheet, font faces, shaping cache, decoded-font data) are thread-safe, so several headless documents can render in parallel on different threads.
- **Content process (M4)**: one engine thread running the HTML event loop, plus one IPC thread. The host side stays single-threaded per document.

## Core data structures

| Structure | Shape |
|---|---|
| `Atom` | 4-byte interned name; tags, attributes, classes, ids, property names |
| DOM | `Node` objects with parent/first/last child and sibling links; attribute arrays; three engine fields per element (flags, style, box) |
| Stylesheet index | Rules bucketed by id, class, tag and universal; ancestor Bloom filter |
| `ComputedStyle` | ~10 references to immutable, shared style groups; custom properties in a persistent map |
| Box tree | Box objects per formatting role; inline formatting contexts as a flat item array plus one text buffer |
| `ShapedRun` | Struct arrays: glyph ids, clusters, advances, offsets, flags |
| Fragment tree | Immutable fragments with offsets, sizes, baselines; cached per box by constraint space |
| `StackingTree` | Stacking contexts with seven sorted layer lists; shared by paint and hit testing |
| Display list | Struct array of items plus side arrays (paths, glyph runs, paints); push/pop for clips, transforms, layers, scroll frames |
| `ScrollState` | Scroll offsets by box, outside the fragment tree so relayout keeps positions |

## Error handling

| Situation | Behaviour |
|---|---|
| Malformed HTML/CSS/SVG | Recovered as the specs define; optional diagnostic |
| Unsupported feature | Ignored as the specs require for unknown features; diagnostic with a feature tag |
| Blocked or failed load | Rendered without it (`alt` text, fallback fonts); diagnostic |
| Limit exceeded | Work stops for that part, the rest renders; diagnostic |
| Host API misuse (wrong thread, disposed document, bad arguments) | Standard .NET exceptions |
| Internal bug during `Update()` | The control keeps the last good frame and raises `RenderFailed`; headless `Render` throws `FolioException` with the diagnostics collected so far |
| Content process crash or hang (M4) | Process killed, last frame kept with a notice, `RenderFailed` raised |

## Milestone 1 in detail: static rendering

The full milestone list is in the [roadmap](roadmap.md). M1 is specified here because it fixes the core architecture.

**Scope**: HTML and CSS parsing, the cascade, block, inline, flex, grid and table layout, positioning and overflow, text with font fallback and emoji, PNG/JPEG images, painting with SkiaSharp, the WinForms control (display, resize, root-page scrolling with wheel and scrollbar, clickable links) and the headless API.

**HTML elements rendered**: all flow and phrasing content with the UA stylesheet (headings, paragraphs, lists including nested and `start`/`reversed`, `dl`, `blockquote`, `pre`, `code`, `kbd`, `hr`, `br`, `wbr`, `a`, `strong`/`em`/`b`/`i`/`u`/`s`/`mark`/`small`/`sub`/`sup`, `abbr`, `time`, `figure`/`figcaption`, `details`/`summary` in their initial state, `section`/`article`/`header`/`footer`/`nav`/`aside`/`main`), tables with all parts, `img` (with `srcset` density selection and `alt` fallback), `picture`/`source`, form controls in static appearance, `progress`, `meter`. `svg`, `canvas`, `video`, `audio`, `iframe`, `object`, `embed` render as boxes of their specified size.

**CSS features M1 must support** (chosen from what AI-written artifacts use):

- **Syntax and cascade**: full tokenizer and error recovery, nesting, `@media` (features listed in [study 03](study/03-css-parsing-and-selectors.md)), `@supports`, `@layer`, `@import`, `!important`, `initial`/`inherit`/`unset`/`revert`/`revert-layer`, the M1 selector set from study 03.
- **Values**: `px em rem % vw vh vmin vmax dvh svh lvh ch ex pt`, `calc() min() max() clamp()`, `var()` with fallbacks, `@property` initial values, colours: named, hex (3/4/6/8), `rgb() hsl() hwb() lab() lch() oklab() oklch()`, `color-mix()`, `currentcolor`, `transparent`, `color-scheme`, `light-dark()`.
- **Display and box model**: `display` (`block inline inline-block flow-root flex inline-flex grid inline-grid table*` family, `list-item`, `contents`, `none`), `box-sizing`, `width/height/min-*/max-*` including `min-content max-content fit-content`, `aspect-ratio`, `margin` (with `auto` centring), `padding`, `border-width/style/color` (all styles), `border-radius`, `outline`, `outline-offset`, `visibility`, `float`, `clear`.
- **Flexbox**: every property and value in [study 07](study/07-layout-flexbox.md), `gap`.
- **Grid**: the M1 scope in [study 08](study/08-layout-grid.md).
- **Tables**: the M1 scope in [study 09](study/09-layout-tables.md).
- **Positioning and overflow**: `position` (all five), `inset` and sides, `z-index`, `overflow` (all values, per axis), `text-overflow`, `line-clamp` (and its legacy prefixed alias), `scrollbar-gutter`, `scrollbar-width`, `scrollbar-color`, `isolation`.
- **Text**: `font-family` with generic families, `font-size` (keywords and relative), `font-weight` (1–1000), `font-style`, `font-stretch`, `font` shorthand, `font-variant-numeric`, `font-feature-settings`, `line-height`, `letter-spacing`, `word-spacing`, `text-align` (incl. `justify`, `start`/`end`), `text-align-last`, `text-indent`, `text-transform`, `white-space` and its longhands, `word-break`, `overflow-wrap`, `hyphens: manual`, `tab-size`, `vertical-align`, `direction`, `unicode-bidi`, `text-decoration` (line, style, colour, thickness, `text-underline-offset`, skip-ink), `list-style-*`, `counter-reset`/`counter-increment`/`counter-set`, `content` with strings/`attr()`/counters/quotes.
- **Painting**: `background-color`, `background-image` with `url()`, `background-size/position/repeat/origin/clip`, multiple layers, `opacity`, `object-fit`, `object-position`, `image-rendering`, `cursor` (recorded for M3), `accent-color` (form control appearance).
- **Animations**: resolved to their end state as described in [study 04](study/04-cascade-and-computed-values.md).

Everything else parses (so `@supports` and fallbacks behave correctly) and is reported as unsupported.

**M1 acceptance criteria**

1. **CI is in place and green**: GitHub Actions on Linux and Windows run unit tests, parser/layout/display-list dump tests, Unicode conformance tests, reftests and golden-image tests on every push and PR. Golden tests have per-test tolerances, the `folio-test approve` workflow exists, and CI uploads actual/expected/diff images for every failing or changed golden.
2. **Spec coverage**: every feature in the list above has at least one reftest, dump test or golden test; the expected-failure manifest is empty for M1 features.
3. **Conformance**: in the simple static categories, S1 (documents) and S3 (data tables), at least 95% of artifacts pass against the reference images. Every artifact in the corpus, including those needing M2+ features and scripts, renders without an exception, hang or limit hit.
4. **Budgets**: the time and memory targets in [study 18](study/18-memory-and-performance.md) hold for S1 and S3 in the benchmark harness.
5. **Safety**: with the default options, a test loader proves no request other than `data:` is ever made; fuzz harnesses for HTML, CSS, PNG, JPEG and fonts run on schedule in CI with no open crash bugs.
6. **Integration**: Mana's "Open in Mana" window can show artifacts with `FolioView`, handles `LinkActivated`, using `ArtifactClassifier` to send anything classified `Scripted` or `NeedsBrowser` to the system browser.
