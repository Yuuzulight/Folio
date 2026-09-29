# Layout: grid

## What the spec requires

- [CSS Grid Layout 2](https://www.w3.org/TR/css-grid-2/) (Level 1 plus subgrid): explicit grid from `grid-template-rows/columns/areas`, line names, `repeat()` including `auto-fill`/`auto-fit`, `minmax()`, `fit-content()`, `fr`; implicit grid from `grid-auto-rows/columns`; placement (`grid-row/column/area`, spans, negative lines, named lines); [auto-placement](https://www.w3.org/TR/css-grid-2/#auto-placement-algo) (`grid-auto-flow: row/column [dense]`); [track sizing algorithm](https://www.w3.org/TR/css-grid-2/#algo-track-sizing); alignment via [Box Alignment 3](https://www.w3.org/TR/css-align-3/); `gap`.

## Hard parts and pitfalls

- **Track sizing** is the core: initialise base sizes and growth limits → resolve intrinsic track sizes (items spanning one track first, then by increasing span; intrinsic minimums, then content-based minimums, then max-content minimums; then growth limits) → maximise tracks → expand flexible tracks (`fr`, with the "find the size of an fr" loop) → stretch `auto` tracks.
- **Column and row sizing depend on each other**: row sizes need item heights, which depend on column widths. The spec's order is columns, then rows, then (if any item's min-content contribution changed) columns and rows once more.
- **`fr` in indefinite containers** (a grid inside a shrink-to-fit parent) uses a different rule (the max-content contributions divided by flex factors).
- **`auto-fill`/`auto-fit`** need a definite container size (or max size); otherwise one repetition. `auto-fit` collapses empty tracks, including their gaps.
- **Percentage tracks and percentage gaps** against an indefinite size behave as `auto` during intrinsic sizing and then resolve.
- **Automatic minimum size of grid items** (like flex): `min-width: auto` → content-based minimum only for items spanning at least one `auto` track and no flexible track. `1fr` is `minmax(auto, 1fr)`, which is why `grid-template-columns: 1fr 1fr` with a long word overflows; AI CSS often writes `minmax(0, 1fr)` to avoid it.
- **Named areas** must form rectangles; invalid templates make the declaration invalid.

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Full spec track sizing algorithm, both axes, with the one re-run | Correct for the dashboards and card grids AI artifacts are full of | The largest single layout algorithm in the engine |
| B. Only fixed and `fr` tracks; intrinsic tracks approximated as `auto` = equal share | Very small | Visibly wrong on `auto`/`max-content`/`minmax(200px, auto)` columns, which are common in generated tables-as-grids |

## Decision

**Option A**, as numbered spec steps, like [flexbox](07-layout-flexbox.md).

- **Grid data**: tracks as struct arrays per axis (base size, growth limit, flags); items as a struct array with resolved line numbers. Placement uses an occupancy bitmap per row that grows as the implicit grid grows. Cap the implicit grid at 1000 tracks per axis (the spec allows engines to clamp; a diagnostic is reported).
- **Measuring items** goes through the shared `IntrinsicSizes`/`Layout` entry points with the item's grid area as the containing block; the layout cache keeps the re-run cheap.
- **Order of work**: placement → column sizing → row sizing → re-run once if any item's min-content contribution changed → alignment (`justify-items/self`, `align-items/self`, `justify/align-content`, `place-*`) → lay out items in their areas.
- **M1 scope**: everything in Grid Level 1, `grid-template-areas` and named lines, `dense`, `auto-fill`/`auto-fit`, intrinsic keywords, `gap`, absolutely positioned grid children using grid areas as containing blocks.
- **Deferred**: `subgrid` (M2; rare in artifacts), baseline alignment in grid (falls back to `start`), masonry (not a standard yet).
