# Layout: flexbox

## What the spec requires

- [CSS Flexible Box Layout 1](https://www.w3.org/TR/css-flexbox-1/), especially [§9 Flex Layout Algorithm](https://www.w3.org/TR/css-flexbox-1/#layout-algorithm) (the numbered steps), [§4.5 automatic minimum size](https://www.w3.org/TR/css-flexbox-1/#min-size-auto), [§9.9 intrinsic sizes](https://www.w3.org/TR/css-flexbox-1/#intrinsic-sizes).
- [CSS Box Alignment 3](https://www.w3.org/TR/css-align-3/): `justify-content`, `align-items`, `align-self`, `align-content`, `gap`, `safe`/`unsafe`, baseline alignment.
- Properties: `display: flex/inline-flex`, `flex-direction`, `flex-wrap`, `flex-flow`, `flex-grow`, `flex-shrink`, `flex-basis`, `flex`, `order`, `gap`/`row-gap`/`column-gap`, alignment properties above.

## Hard parts and pitfalls

- **`min-width: auto`** for flex items is *not* zero: it is the content-based minimum size, which is why long words or wide children refuse to shrink. Getting this wrong is the most visible flexbox bug. `min-width: 0` / `overflow: hidden` turns it off.
- **`flex-basis: auto` vs `content` vs `0%`**: `flex: 1` means `1 1 0%`, which behaves differently from `flex: auto` when content sizes differ.
- **Resolving flexible lengths** (§9.7) is an iterative freeze loop with min/max violations; shortcuts break when several items hit their min or max at once.
- **Column flex containers with indefinite height**: items are laid out at their content height; percentages inside resolve differently.
- **Cross-size stretch** changes the item's definite size, which requires a second layout of the item.
- **Nested flex performance**: each level measures children (intrinsic or hypothetical main size) and then lays them out; without caching, depth *d* costs 2^*d* layouts.
- **Baselines**: `align-items: baseline` and the container's own baseline (first item's first line).
- `order` changes layout order and paint order, but not tab/selection order.
- Absolutely positioned children do not participate but their static position depends on `justify-content`/`align-items`.

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Implement §9 step by step, as the spec is written, on top of the block layout's `Layout(box, constraints)` and `IntrinsicSizes(box)` | Directly checkable against the spec; spec clarifications map to code | Must be careful about repeated child layout (solved by the layout cache) |
| B. A simplified "grow/shrink proportionally once" algorithm | Short | Wrong as soon as min/max constraints or mixed bases appear, which is common in AI layouts (`flex: 1` next to fixed sidebars with `min-width`) |

## Decision

**Option A.** Folio implements the spec algorithm as numbered steps, with each step a separate method named after the spec section, so reviewing against the spec is mechanical.

- Items are measured through the shared `IntrinsicSizes` / `Layout` entry points with appropriate constraint spaces (definite cross size when stretched, indefinite main size when measuring content). The layout cache prevents repeated work.
- **Intrinsic size of the container** (§9.9): Folio uses the spec's rule that the max-content main size is the sum of items' max-content contributions (plus gaps) for single-line containers and the largest item contribution for multi-line containers. The more exact §9.9.1 algorithm (which accounts for flex factors) is deferred until a real artifact needs it; the difference only shows with unusual flex factors inside shrink-to-fit containers.
- **Automatic minimum size** implemented exactly: content size suggestion, specified size suggestion, transferred size suggestion (aspect ratio), clamped by max sizes.
- **Alignment**: all `justify-content`/`align-*` values including `space-evenly`, `stretch`, `safe`/`unsafe`, `first/last baseline`, `start`/`end`/`self-start`/`self-end`/`flex-start`/`flex-end`/`left`/`right`.
- **Direction and wrap**: `row`, `row-reverse`, `column`, `column-reverse`; `wrap`, `wrap-reverse`; logical mapping with `direction: rtl`.
- **Out of scope**: fragmentation across pages/columns (Folio never paginates), `visibility: collapse` on flex items (treated as `hidden`).
