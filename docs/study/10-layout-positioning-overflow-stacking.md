# Layout: positioning, overflow and stacking contexts

## What the spec requires

- [CSS Positioned Layout 3](https://www.w3.org/TR/css-position-3/): `position: static/relative/absolute/fixed/sticky`, `inset` properties, containing blocks, static position, the width/height equations for absolutely positioned boxes (`auto` offsets, shrink-to-fit), sticky positioning relative to the nearest scrollport.
- [CSS 2.2 §10.1 containing blocks](https://www.w3.org/TR/CSS22/visudet.html#containing-block-details); [Transforms 1 §2](https://www.w3.org/TR/css-transforms-1/#containing-block-for-all-descendants): a transformed (or `filter`ed, or `contain: paint`) ancestor becomes the containing block for fixed descendants.
- [CSS Overflow 3](https://www.w3.org/TR/css-overflow-3/): `overflow-x/y` (`visible`, `hidden`, `clip`, `scroll`, `auto`), scroll containers, scrollable overflow area, `overflow-clip-margin`, `scrollbar-gutter`; [CSS Scrollbars 1](https://www.w3.org/TR/css-scrollbars-1/): `scrollbar-width`, `scrollbar-color`.
- [CSS 2.2 Appendix E](https://www.w3.org/TR/CSS22/zindex.html): painting order within a stacking context. Stacking context creation: root, positioned with `z-index` ≠ `auto`, flex/grid items with `z-index` ≠ `auto`, `opacity` < 1, `transform`/`filter`/`backdrop-filter`/`clip-path`/`mask`, `mix-blend-mode` ≠ normal, `isolation: isolate`, `position: fixed/sticky`, `will-change` of those, `contain: paint/layout`.

## Hard parts and pitfalls

- **Static position** of an abspos box with `auto` offsets depends on where it would have been in flow (including inside an inline formatting context, or inside flex/grid alignment).
- **Clipping follows the containing-block chain, not the DOM tree**: an absolutely positioned child of an `overflow: hidden` box escapes the clip if its containing block is further up. Painting by DOM ancestry gets this wrong.
- **Scrollable overflow** includes descendants' borders and positioned descendants whose containing block is inside the scroller, but not transforms' visual overflow in all cases; the spec text is precise about it.
- **`overflow: hidden` still scrolls programmatically**, `clip` never does; `visible` combined with non-`visible` on the other axis computes to `auto`.
- **Sticky** depends on the scroll offset, so it must not force relayout on scroll.
- **Paint order** has seven layers per stacking context (backgrounds of the root, negative z-index, block backgrounds in flow, floats, inline content, z-index 0/auto positioned, positive z-index), and positioned descendants with `z-index: auto` paint as if they created a stacking context without actually being one.
- **Fixed position** in an embedded control: the viewport is the control's client area.

## Options

**Where abspos layout happens**

| Option | Pros | Cons |
|---|---|---|
| A. Lay out abspos boxes when their containing block finishes layout (propagate "pending out-of-flow" descendants upward in the fragment result) | One pass; containing block size is known | Static position must be recorded and carried up |
| B. Separate pass after the whole tree is laid out | Simple to reason about | Abspos boxes can affect scrollable overflow of ancestors, which then needs another pass |

**Stacking/paint order structure**

| Option | Pros | Cons |
|---|---|---|
| A. Build an explicit stacking-context tree from fragments, sorted once, used by both painting and hit testing | One source of truth for order; hit testing is exactly the reverse of paint order | One more tree (small: only stacking contexts and positioned boxes) |
| B. Compute order while walking fragments in each consumer | No extra structure | Painting and hit testing drift apart; subtle bugs |

## Decision

- **Abspos: option A.** A fragment result carries a list of out-of-flow descendants with their static positions; the first ancestor that is their containing block lays them out after its own children. `fixed` boxes bubble to the viewport unless a transform/filter ancestor catches them.
- **Relative and sticky**: relative offsets are applied as a fragment offset after layout. Sticky boxes get their in-flow position from layout and a **sticky constraint** (nearest scrollport, containing block rectangle, insets); the actual offset is computed at paint/hit-test time from the current scroll offset, so scrolling never relayouts.
- **Overflow**: every scroll container fragment records its scrollport rectangle and scrollable overflow rectangle. Scroll offsets live in a `ScrollState` table keyed by box, outside the fragment tree, so a new layout keeps the user's scroll position (clamped).
- **Scrollbars**: Folio draws its own thin overlay scrollbars (they don't take layout space by default, like OS overlay scrollbars); `scrollbar-gutter: stable` and `scrollbar-width` reserve space when authors ask. `scrollbar-color` is honoured.
- **Stacking: option A.** After layout, a `StackingTree` is built: each node is a stacking context with its seven layer lists (z-order lists sorted with a stable sort by `z-index`, then tree order). The [display list](12-painting.md) builder and the [hit tester](15-interaction.md) both walk this tree.
- **Clip chains**: each paintable fragment knows its clip chain, computed from its containing-block ancestry (not DOM ancestry), and stored as a reference to a shared clip node.
- **M1 includes** all five `position` values, `inset`, `z-index`, all `overflow` values including `clip`, `text-overflow`, `scrollbar-gutter`, `scrollbar-width`, `scrollbar-color`, `isolation`. `contain` is parsed and only its stacking/containing-block effects are applied; its optimisation effects arrive with [incremental layout](14-invalidation.md) in M3.
