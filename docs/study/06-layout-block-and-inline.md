# Layout: block and inline formatting

## What the spec requires

- [CSS 2.2 §9 Visual formatting model](https://www.w3.org/TR/CSS22/visuren.html) and [§10 Visual formatting model details](https://www.w3.org/TR/CSS22/visudet.html): block formatting contexts, width/height/margin equations, margin collapsing, floats and clearance, line boxes, `vertical-align`, `line-height`.
- [CSS Box Model 3](https://www.w3.org/TR/css-box-3/), [CSS Sizing 3](https://www.w3.org/TR/css-sizing-3/) (`box-sizing`, `min-content`/`max-content`/`fit-content`, intrinsic size contributions, `aspect-ratio` from [Sizing 4](https://www.w3.org/TR/css-sizing-4/#aspect-ratio)).
- [CSS Inline 3](https://www.w3.org/TR/css-inline-3/) (line box height, baselines), [CSS Text 3](https://www.w3.org/TR/css-text-3/) (white-space, line breaking rules, `word-break`, `overflow-wrap`, `text-align`, `text-align-last`, `text-indent`, `letter-spacing`, `word-spacing`, `text-transform`, justification), [CSS Text Decoration 3](https://www.w3.org/TR/css-text-decor-3/).
- [CSS Writing Modes 4 §2](https://www.w3.org/TR/css-writing-modes-4/#text-direction): `direction`, `unicode-bidi`, and the [Unicode Bidirectional Algorithm (UAX #9)](https://www.unicode.org/reports/tr9/).
- [CSS Overflow 3 `text-overflow`](https://www.w3.org/TR/css-overflow-3/#text-overflow), [`line-clamp`](https://www.w3.org/TR/css-overflow-4/#line-clamp) (including the legacy prefixed alias the spec defines for compatibility).

## Hard parts and pitfalls

- **Margin collapsing**: parent/first-child, last-child/parent, empty blocks collapsing through, negative margins, interaction with clearance and with `min-height`. Most "why is this 20px off" bugs live here.
- **Floats**: the float placement rules plus line boxes shortening beside floats. AI artifacts rarely use floats for layout, but images and old-style layouts do.
- **Percentages and indefinite sizes**: `height: 100%` against an `auto`-height parent is `auto`.
- **Intrinsic sizes** are needed by flex, grid, tables, `fit-content`, `inline-block` and absolutely positioned boxes. Computing them by running full layout is slow; not caching them is exponential in nested flex/grid.
- **Line breaking**: break opportunities come from UAX #14 **and** CSS rules (`white-space`, `word-break: break-all/keep-all`, `overflow-wrap: anywhere/break-word`, `hyphens` — manual only), and shaping must happen *before* measuring but can change *at* the break (ligatures/kerning across the break). Re-shaping every candidate line is slow.
- **Inline box geometry**: `vertical-align: middle/top/bottom/text-top/sub/super/<length>`, line-height strut, fonts with different ascent/descent in one line, atomic inlines with baselines (an `inline-block`'s baseline is its last line box).
- **Bidi**: resolve embedding levels over the whole paragraph, then reorder each line separately; inline boxes spanning bidi runs are split into several fragments.
- **Justification** must distribute space at word separators (and between CJK characters), never inside a cluster.
- **Floating-point drift**: summing many fractional widths.

## Options

**Layout algorithm structure**

| Option | Pros | Cons |
|---|---|---|
| A. One recursive `Layout()` per box writing into the box | Short | Hard to cache, hard to lay the same box out twice with different constraints (needed by flex/grid) |
| B. Pure functions per formatting context: `Layout(box, constraints) → Fragment`, plus `IntrinsicSizes(box) → (min, max)`, both cached on the box keyed by the inputs | Flex/grid can "measure" children freely; caching is explicit; old fragments stay valid while new layout runs | More types (constraint space, fragments) |

**Units**

| Option | Pros | Cons |
|---|---|---|
| A. `float` CSS px | Simple, fast, matches SkiaSharp | Drift over long sums; equality tests need epsilon |
| B. Fixed-point (e.g. 1/64 px in an `int`) | Exact, deterministic sums | Conversions everywhere; overflow on huge documents |

**Line breaking**

| Option | Pros | Cons |
|---|---|---|
| A. Greedy first-fit per line | What authors expect on screen; fast; stable while resizing | Ragged justified text |
| B. Total-fit optimal paragraph breaking | Better justified text | Slower; lines change when later text changes; not what screens normally do |

## Decision

- **Structure: option B.** A `ConstraintSpace` carries available inline/block size (definite or indefinite), percentage-resolution sizes, whether the box is a new formatting-context root, the float exclusion space (for block formatting contexts), and margin-collapsing input state. The output `Fragment` (box fragment or line fragment or text fragment) holds size, children with offsets, baselines, and the collapsed-through margin state. Fragments are immutable.
- **Caching**: each box keeps its last `(ConstraintSpace, Fragment)` and its intrinsic sizes. A dirty flag clears both; otherwise an equal constraint space returns the cached fragment. Flex and grid often lay out a child with the same constraints twice, so this single-entry cache removes most repeated work; a second "measure" entry is added only if profiling shows the need.
- **Units: option A (`float` px)**, with pixel snapping only at paint time: box edges are snapped by rounding each edge separately (so adjacent boxes never gap or overlap), text baselines snapped vertically only.
- **Block layout**: full CSS 2.2 margin collapsing, floats (left/right, clear), BFC roots (`flow-root`, `overflow` other than visible, floats, abspos, inline-blocks, flex/grid items, table cells) avoid floats and contain margins.
- **Inline layout**, per paragraph (one inline formatting context):
  1. Items and text buffer come from the [box tree](05-box-tree.md).
  2. Bidi levels for the paragraph (UAX #9 with `unicode-bidi` isolation/embedding from inline boxes), paragraph direction from `direction` (or `dir=auto` rules).
  3. Segment text into runs by (bidi level, font after fallback, script, style) and shape each run once (see [text](11-text.md)).
  4. Break opportunities from UAX #14 tailored by CSS (`word-break`, `line-break: auto/strict` treated as default, `overflow-wrap`), computed once per paragraph.
  5. **Greedy line filling** (option A) over shaped glyph advances. Only the glyphs next to a chosen break are re-shaped if the shaper reports that the break is "unsafe to break" there.
  6. Per line: reorder bidi runs visually (UAX #9 L2), apply `text-align`/`text-align-last`/justification, then compute the line box from the strut and inline box metrics per CSS Inline 3 and place atomic inlines by `vertical-align`.
  7. `text-overflow: ellipsis` and `line-clamp` truncate the relevant line and append an ellipsis run.
- **`inline-block` / atomic inlines** use shrink-to-fit width: `min(max(min-content, available), max-content)`.
- **Not in M1** (revisited in M2 if the conformance corpus needs them): vertical writing modes, `::first-line`/`::first-letter`, `initial-letter`, ruby layout (ruby text shown inline), automatic hyphenation (only `&shy;` and `hyphens: manual`), multi-column layout (`columns` renders as a single column — a diagnostic is reported).
