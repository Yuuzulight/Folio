# Invalidation, incremental relayout and repaint

## What the specs require

Nothing directly: specs define results, not how to reach them cheaply. They do constrain it: the result after an incremental update must equal a full re-render of the same state. [CSS Containment 2](https://www.w3.org/TR/css-contain-2/) (`contain`, `content-visibility`) lets authors promise that a subtree's layout or paint is independent, which an engine may exploit.

## Triggers, by milestone

| Trigger | M1–M2 (static) | M3 onwards |
|---|---|---|
| Viewport resize | relayout (restyle only if a media query or viewport unit changed) | same |
| Scroll | replay the display list with the new offset | same |
| Image / web font finished loading | relayout | relayout of affected boxes only |
| Animation frame (M2) | paint-only for opacity/transform/filter/colour; relayout otherwise | same, per layer |
| Hover / active / focus | — | restyle only elements that can be affected |
| Text selection change | — | repaint selection rectangles |
| Form input, DOM mutation, script | — | full incremental path |

## Hard parts and pitfalls

- **Knowing what a style change affects**: `color` needs repaint only; `width` needs relayout of the box and possibly its ancestors; `display` needs a box-tree rebuild.
- **Relayout propagation**: a child's size change may change its parent (auto height, shrink-to-fit), up to the root, unless an ancestor has a fixed size and establishes a formatting context.
- **Selector invalidation**: toggling `:hover` on one element can change siblings (`:hover + .x`), descendants (`.card:hover .title`) and, with `:has()`, ancestors.
- **Correctness**: incremental bugs show up as stale rendering that disappears on resize; they are hard to reproduce.

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Full re-render (style + layout + paint) on any change, relying on caches (style sharing, shaping cache, layout cache) | Always correct; trivial | Scales with document size; too slow for large documents, typing in forms, or script-driven updates at 60 fps |
| B. Dirty bits + damage classification: per element `NeedsStyle`/`DescendantNeedsStyle`, per box `NeedsLayout`/`DescendantNeedsLayout`; diffing old vs new computed style yields damage `{None, Repaint, Relayout, RebuildBoxes}` | Standard; work proportional to the change | Must be implemented carefully and tested differentially |
| C. Fine-grained dependency tracking (every computed value knows its dependents) | Minimal work | Heavy memory and complexity |

## Decision

**Option B's plumbing from M1; used coarsely in M1–M2 and fully from M3.**

- **Dirty bits** exist from day one: the DOM mutation hook ([DOM](02-dom.md)) sets them. The pipeline entry point is `Update()`: restyle dirty elements → rebuild dirty box subtrees → relayout from dirty boxes up → rebuild the display list → invalidate the damaged rectangle.
- **Damage classification** comes from the property table: each property is tagged `Paint`, `Layout` or `Boxes`. Old and new `ComputedStyle` are compared group by group (reference first, then value); the maximum damage wins.
- **M1–M2**: a resize or resource load marks the root dirty and re-runs the pipeline; caches keep it fast. M2 animations use the damage tags: paint-only animations skip style and layout for everything but the animated elements and rebuild only the affected stacking context's display items.
- **M3** (interaction) adds:
  - State pseudo-class invalidation: at stylesheet load, record where each state pseudo-class appears (subject, ancestor compound, sibling compound). A hover change marks only the elements that could be affected; if no selector mentions `:hover`, a hover change costs nothing.
  - Invalidation sets per class/id/attribute name, so toggling one class restyles only what can change.
  - `:has()` upward invalidation.
  - Relayout from the dirty box up to the nearest ancestor whose size cannot depend on its content; the layout cache makes clean siblings free.
  - `contain: layout/paint/size` and `content-visibility: auto` stop propagation and skip work.
  - Partial display-list rebuild per stacking context, and partial re-raster of the damaged rectangle.
- **M4–M5** (scripting): DOM mutations batch until the next frame; layout-reading APIs (`getComputedStyle`, `offsetWidth`, `getBoundingClientRect`) force a synchronous `Update()` of just what is dirty.
- **Safety net**: a test mode runs a full re-render after every incremental update and compares fragment trees and display lists; any difference fails. Fuzzers mutate documents under this check (see [testing](19-testing.md)).
