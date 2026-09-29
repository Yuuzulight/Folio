# Box tree generation

## What the spec requires

- [CSS Display 3](https://www.w3.org/TR/css-display-3/): `display` has an outer role (block/inline/run-in) and an inner model (flow, flow-root, flex, grid, table, ruby); `none` generates nothing; `contents` removes the element's own box but keeps its children; `list-item` adds a marker.
- [CSS 2.2 §9.2](https://www.w3.org/TR/CSS22/visuren.html#box-gen): anonymous block boxes wrap inline content that sits next to block siblings; a block inside an inline splits the inline.
- [CSS Tables 3 §3](https://www.w3.org/TR/css-tables-3/#fixup-algorithm): missing table wrappers/rows/cells are generated (fix-up).
- Flex and grid items are "blockified" ([Display 3 §2.7](https://www.w3.org/TR/css-display-3/#transformations)); runs of text inside a flex/grid container become anonymous items; whitespace-only runs are dropped.
- Pseudo-elements `::before`/`::after` (with `content`), `::marker` ([Lists 3](https://www.w3.org/TR/css-lists-3/)), counters.
- Replaced elements (`img`, `svg`, `video`, `canvas`, form controls) are atomic boxes with intrinsic sizes.
- `position: absolute/fixed` and floats are taken out of flow (blockified).

## Hard parts and pitfalls

- **Block inside inline** (`<a><div>…</div></a>` is common in AI cards): the inline must be split into parts around an anonymous block, and each part keeps the inline's borders/padding only on its outer edges.
- **Whitespace**: collapsible whitespace between blocks must not create boxes; inside inline content it must collapse across element boundaries (`<b>a </b> b` → one space).
- **Table fix-up** is fiddly but mechanical; AI tables are almost always well formed, but `display: table` on divs needs the full fix-up.
- **Keeping DOM ↔ box mapping** for hit testing, selection and incremental rebuilds.
- `display: contents` children must be treated as children of the grandparent box.

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Lay out the DOM directly (style + geometry fields on elements, anonymous boxes as special nodes) | No second tree | Anonymous boxes, pseudo-elements, `contents` and continuations don't map to DOM nodes; layout code fills with special cases |
| B. Separate box tree built from DOM + computed styles; layout reads boxes and writes an immutable **fragment tree** | Clean inputs for layout; anonymous/pseudo boxes are just boxes; partial rebuild per subtree | Second tree to keep in sync |
| C. Box tree without fragments (layout writes positions into boxes) | Less memory | Hard to cache layout results or keep a previous layout while computing a new one; fragmentation (multi-line inlines) needs special structures anyway |

## Decision

**Option B: DOM + styles → box tree → layout → fragment tree.**

- `Box` classes: `BlockContainerBox` (holds either block children or one inline formatting context), `InlineBox` (non-atomic inline, e.g. `<span>`), `TextRun` (a DOM text node's content inside an inline formatting context), `AtomicInlineBox` wrapper, `ReplacedBox`, `FlexContainerBox`, `GridContainerBox`, `TableWrapperBox`/`TableBox`/`TableRowGroupBox`/`TableRowBox`/`TableCellBox`/`TableColumnBox`/`TableCaptionBox`, `MarkerBox`, `SvgRootBox` (see [SVG](13-svg.md)).
- Each box holds: its `ComputedStyle`, a reference back to its DOM node (or the node that generated it, with a pseudo-element tag), children, and a layout cache slot.
- **Inline content representation**: an inline formatting context is stored as a flat list of *inline items* (text, open-inline-box, close-inline-box, atomic inline, forced break, float, out-of-flow placeholder) plus one concatenated text buffer for the whole paragraph. This is what line breaking, bidi and shaping want (they work on paragraph text, not on a tree). The inline box tree is kept for styles and hit testing.
- **Block-in-inline**: the paragraph ends at the block, the block becomes an anonymous-block sibling, and the inline box's items continue in the next paragraph with a "continuation" flag so borders/padding render only on the first/last fragments.
- **Whitespace processing** (CSS Text phase I: collapsing, segment-break transformation) happens once while building the inline item list, per `white-space`/`white-space-collapse`.
- **Generated content**: `::before`/`::after` with `content` strings, `attr()`, `counter()`/`counters()`, `open-quote`/`close-quote` (simple quote pairs). Counters are computed during box building in tree order.
- **List markers**: `disc`, `circle`, `square`, `decimal`, `decimal-leading-zero`, `lower/upper-alpha`, `lower/upper-roman`, `lower-greek`, `none`, string markers; `list-style-position` inside/outside; `list-style-image` via the image loader.
- **Rebuild granularity**: a style change whose damage says "rebuild boxes" rebuilds only the box subtree of the nearest ancestor that is a block container (or formatting-context root); see [invalidation](14-invalidation.md).
- **Replaced elements**: `img` (with `alt` text fallback when the image is missing or blocked), inline `svg` (an empty box of the right size in M1, rendered from M2), `canvas` (blank until canvas 2D arrives in M5), `video`/`audio`/`iframe`/`object`/`embed` render as a neutral placeholder box with their specified size (never loaded). Form controls (`input`, `button`, `select`, `textarea`, `progress`, `meter`) render with Folio's own static appearance in M1; interaction and text input arrive in M3.
