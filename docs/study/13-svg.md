# SVG rendering

Charts, icons and diagrams in AI-written artifacts are very often SVG: inline `<svg>` in the page, SVG files as `<img>`/`background-image` (usually `data:image/svg+xml`), and SVG produced at run time by charting and diagram scripts (M4–M5). Static parity (M2) is impossible without it.

## What the specs require

- [SVG 2](https://www.w3.org/TR/SVG2/) (with [SVG 1.1 Second Edition](https://www.w3.org/TR/SVG11/) where SVG 2 defers): document structure (`svg`, `g`, `defs`, `symbol`, `use`), basic shapes (`rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`), [`path` data grammar](https://www.w3.org/TR/SVG2/paths.html#PathDataBNF) including elliptical arcs, text (`text`, `tspan`, `textPath`), paint servers (`linearGradient`, `radialGradient`, `pattern`), `clipPath`, `mask`, `marker`, `image`, `foreignObject`, coordinate systems (`viewBox`, `preserveAspectRatio`, nested viewports), the `transform` attribute grammar, stroking (`stroke-dasharray`, `-linecap`, `-linejoin`, `-miterlimit`), `fill-rule`, `paint-order`, `vector-effect: non-scaling-stroke`.
- [SVG 2 §6 Styling](https://www.w3.org/TR/SVG2/styling.html): presentation attributes are CSS declarations with author-level origin and specificity 0; CSS selectors from the HTML page apply to inline SVG.
- [HTML foreign content parsing](https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-inforeign) for inline SVG; [XML 1.0](https://www.w3.org/TR/xml/) and [Namespaces in XML](https://www.w3.org/TR/xml-names/) for standalone SVG files.
- [SVG integration](https://www.w3.org/TR/svg-integration/): SVG used as an image runs in "secure static mode" (no script, no external resources, no interaction).

## Hard parts and pitfalls

- **Sizing of `<svg>` in CSS layout**: intrinsic size from `width`/`height` attributes, aspect ratio from `viewBox`, the 300×150 default, and CSS `width`/`height` overriding them. Icons without width/height inside flex items are a classic "icon is huge" bug.
- **`currentColor` and inheritance** across the HTML/SVG boundary (icons coloured by the surrounding link colour).
- **Arcs**: endpoint → centre parameterisation with radius correction ([SVG 2 Appendix B.2](https://www.w3.org/TR/SVG2/implnote.html#ArcImplementationNotes)).
- **`use`** clones a subtree with its own style inheritance; recursive `use` must be detected.
- **Text in SVG**: absolute positioning per glyph (`x`/`y`/`dx`/`dy`/`rotate` lists), `text-anchor`, `dominant-baseline` (charts rely on `middle`/`central` for axis labels), no line wrapping.
- **`foreignObject`**: some diagram generators emit HTML labels inside `foreignObject`, which needs full HTML/CSS layout inside an SVG coordinate system.
- **XML parser security**: entity expansion bombs, external entities.
- **Markers** (arrowheads in diagrams): orientation `auto`/`auto-start-reverse`, `markerUnits`.

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Folio's own SVG subsystem: SVG elements live in Folio's DOM, are styled by Folio's cascade, and paint into the same display list | Page CSS and `currentColor` work naturally; one styling and painting path; `foreignObject` can reuse Folio's HTML layout; scripts (M4) mutate SVG through the same DOM | Folio must implement SVG geometry and paint servers |
| B. A separate SVG rendering library as a dependency | Less code at first | Separate style system: page CSS doesn't reach inline SVG; scripted charts can't mutate it; a second DOM in memory |
| C. Rasterise SVG once to a bitmap | Simple | Blurry on zoom/HiDPI; no interaction; still needs a renderer |

## Decision

**Option A.**

- **Parsing**: inline SVG through the HTML parser's foreign-content rules. SVG files through Folio's own small XML parser: UTF-8/UTF-16, namespaces, character and predefined entity references only; any `<!DOCTYPE>` internal subset is ignored (no custom entity expansion, no external entities).
- **Styling**: presentation attributes are converted to declarations at the start of the author origin with specificity 0; SVG-only properties (`fill`, `stroke`, `stroke-*`, `stop-color`, `stop-opacity`, `marker-*`, `text-anchor`, `dominant-baseline`, `paint-order`, `vector-effect`, `clip-rule`, `fill-rule`, `shape-rendering`) are ordinary rows in the property table.
- **Layout**: the outer `<svg>` is a replaced box in CSS layout (intrinsic size/aspect ratio per the rules above). Inside it, an `SvgRenderTree` computes geometry in user units: viewport transforms, shape geometry into Folio's own `PathData` (move/line/quad/cubic/close; arcs converted to cubics), text positioned per glyph using the [text](11-text.md) shaper.
- **Painting**: into the same display list via `FillPath`/`StrokePath`, gradient/pattern paints, `PushClipPath`, `PushMaskLayer`, opacity/filter layers. Stroking is done by the backend.
- **SVG as an image** (`<img>`, CSS `url()`): parsed into a separate, inert document (secure static mode), rendered at the destination size, cached per (document, size, device scale).
- **M2 core scope** (lands first within M2): structure elements, all basic shapes, `path` (full grammar), `viewBox`/`preserveAspectRatio`, `transform`, fill/stroke with all stroke properties, `linearGradient`/`radialGradient` (with `gradientUnits`, `gradientTransform`, `spreadMethod`, `href` inheritance), `clipPath`, `use`/`symbol`, `text`/`tspan` with `text-anchor` and `dominant-baseline`, `opacity`/`fill-opacity`/`stroke-opacity`, `marker`, `image` (data: URIs and allowed loads), `title` (tooltip in the control).
- **Rest of M2**: `mask`, `pattern`, `foreignObject` (HTML layout inside SVG), `textPath`, SVG `filter` primitives commonly used by charts (`feGaussianBlur`, `feOffset`, `feFlood`, `feComposite`, `feMerge`, `feColorMatrix`, `feDropShadow`).
- **Not planned**: SMIL animation elements (a diagnostic is reported; CSS animations on SVG elements work once animations run), SVG fonts.
