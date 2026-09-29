# Cascade, inheritance and computed values

## What the spec requires

- [CSS Cascade 5](https://www.w3.org/TR/css-cascade-5/): cascade order = origin and importance (UA, user, author; `!important` reverses), cascade layers, specificity, then order of appearance. `style=""` sits above author rules. Keywords `initial`, `inherit`, `unset`, `revert`, `revert-layer`.
- Value stages: declared → cascaded → specified → **computed** → used → actual. Inheritance passes the *computed* value down.
- [CSS Variables 1](https://www.w3.org/TR/css-variables-1/): custom properties inherit, are substituted at computed-value time, cycles make all involved properties invalid, invalid substitution makes the property "invalid at computed-value time" (behaves as `unset`).
- [CSS Values 4](https://www.w3.org/TR/css-values-4/): lengths (`px em rem ex ch vw vh vmin vmax` plus `dvh`/`svh`/`lvh` = `vh` in a fixed viewport, `pt pc in cm mm Q`), `calc()`, `min()`, `max()`, `clamp()`, `round()`, `mod()`, `rem()`, percentages resolved against properties' own bases.
- [CSS Color 4](https://www.w3.org/TR/css-color-4/) and [Color 5 `color-mix()`](https://www.w3.org/TR/css-color-5/#color-mix): hex, named, `rgb()`/`hsl()`/`hwb()`, `lab()`/`lch()`/`oklab()`/`oklch()`, `currentcolor`, `transparent`, gamut mapping to sRGB.
- [CSS Transitions 1](https://www.w3.org/TR/css-transitions-1/), [Animations 1](https://www.w3.org/TR/css-animations-1/): animated values sit in their own cascade origins.

## Hard parts and pitfalls

- **Percentages and `calc()` mixing lengths and percentages** (`calc(100% - 2rem)`) cannot be resolved at computed time; the computed value must stay as a small expression until layout gives the basis.
- **`em` in `font-size`** refers to the parent's font size; `em` elsewhere refers to the element's own. `rem` refers to the root. `ch`/`ex` need font metrics at style time.
- **Custom properties are strings until used**: `--x: 10px` then `width: calc(var(--x) * 2)`. Long chains of variables (utility CSS defines dozens per element, mostly inherited) make naive "copy a dictionary per element" very expensive.
- **Memory**: a flat computed style with ~150 properties per element is ~1–2 KB; 10k elements → 10–20 MB. Must share.
- **`currentcolor`** must stay symbolic in computed values for inheritance to work correctly.
- **Animations that end visible**: AI artifacts often write `.card { opacity: 0; animation: fadeIn .5s forwards; }`. An engine that ignores animations shows an empty page.

## Options

**Computed style storage**

| Option | Pros | Cons |
|---|---|---|
| A. One flat class per element with every property | Simple access | 1–2 KB per element; poor sharing |
| B. Grouped immutable structs ("style groups": font/text inherited group, box group, margin/padding/border group, background group, flex/grid group, visual effects group, …) referenced from a small `ComputedStyle` object; groups shared by reference between elements and copied only on write | Most elements share most groups with their parent or siblings; memory drops by an order of magnitude; equality checks between old and new style are cheap reference compares per group | More code to define groups |
| C. Sparse dictionary of non-default values | Tiny for simple documents | Slow lookups in layout's hot paths |

**Custom properties**

| Option | Pros | Cons |
|---|---|---|
| A. Copy a dictionary per element | Simple | Quadratic-ish memory with utility CSS |
| B. Persistent (immutable, structurally shared) map: inherit by reference, and only elements that declare custom properties create a new map layered on the parent's | Cheap inheritance; memory proportional to declarations | Slightly slower lookups (layered chain, capped depth then flattened) |

## Decision

- **Cascade**: per element, the matched declarations (from [selector matching](03-css-parsing-and-selectors.md)) are applied in cascade order into a property builder, highest priority last. Layers get an integer order at stylesheet load time so the sort key is `(origin+importance, layer, specificity, source order)`.
- **Storage: option B**, with ~10 style groups. Groups are generated from the same property table as the parser, so adding a property is one table row plus its computed type.
- **Style sharing cache**: before cascading an element, look up a sibling/cousin with the same parent style, tag, id (none), classes, attributes that appear in selectors, and state flags; reuse its `ComputedStyle` outright. Big win for tables and lists.
- **Custom properties: option B**. Substitution happens once per element when a declaration's value has `var()`; the result is parsed with the longhand's parser. Cycle detection per spec (a small DFS over the element's own custom properties).
- **Computed value types**: lengths resolve to px (`float`) where possible; `LengthPercentage` keeps `{px, percent}` or a calc expression tree when both are present; colours computed to a `Color` (premultiplied-ready float RGBA in sRGB after gamut mapping) or `CurrentColor`; keywords as enums. Font-relative units resolve using the element's primary font metrics from [text](11-text.md).
- **`calc()`**: parsed into a tiny expression tree, simplified at parse time (constant folding, like-unit merging per spec), leftovers resolved at use.
- **Colour spaces**: all specified colours convert to sRGB at computed time with CSS Color 4 gamut mapping (chroma reduction in OKLCh). `color-mix()` and gradient interpolation honour the requested space. Wide-gamut output is out of scope.
- **UA stylesheet**: Folio's own, written from the HTML Rendering section, compiled once per process and shared.
- **Transitions and animations**:
  - M1 (static rendering): animations are resolved to their **end state** when `animation-fill-mode` is `forwards`/`both` (otherwise ignored), transitions are ignored. Pages never render "stuck at frame 0".
  - M2 (static parity): a real animation timeline, transitions included (they fire from M3 on, when hover and focus can change styles). Each animated property is interpolated per frame into an animation origin that sits in the cascade. Opacity, transform, filter and colour animations only mark paint damage (no relayout); others go through normal [invalidation](14-invalidation.md). Frame rate follows the host's refresh; animations pause when the control is hidden. `prefers-reduced-motion` comes from the OS setting.
- **Errors**: every invalid-at-computed-value-time fallback can be logged as a diagnostic.
