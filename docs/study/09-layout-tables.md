# Layout: tables

Tables are not in the original subsystem list but AI-written reports are full of them, so they get their own decision record.

## What the spec requires

- [CSS 2.2 §17 Tables](https://www.w3.org/TR/CSS22/tables.html) is normative; [CSS Tables 3 (Editor's Draft)](https://drafts.csswg.org/css-tables-3/) documents the details engines actually converge on: anonymous box fix-up, `table-layout: auto` width distribution, row height distribution, `border-collapse` conflict resolution, `border-spacing`, `caption-side`, `empty-cells`, `vertical-align` in cells, `colspan`/`rowspan`.
- HTML's presentational attributes map to CSS through the [Rendering section](https://html.spec.whatwg.org/multipage/rendering.html#tables-2) (`width`, `cellpadding`, `cellspacing`, `border`, `align`, `valign`, `bgcolor`).

## Hard parts and pitfalls

- **Auto layout width distribution** is only loosely specified in CSS 2.2; the Tables 3 draft gives the practical algorithm (min/max per column from cells, spanning cells distributed over their columns, then distribute the table width across columns by type: percent, fixed, auto).
- **`border-collapse: collapse`**: per-edge conflict resolution by width, style priority, then origin (cell > row > row group > column > table); the table's own width then includes half of the outer borders.
- **Row spans** affect row heights after the fact.
- **`width: 100%` tables with `white-space: nowrap` cells** overflow their container, which is correct.
- Wide tables in narrow windows: authors expect horizontal scrolling on an `overflow-x: auto` wrapper, which needs [overflow](10-layout-positioning-overflow-stacking.md).

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Implement the Tables 3 draft algorithms | Matches what authors see today | Draft text still moves in places |
| B. Map tables onto grid layout | Reuses grid | Different width distribution and border model; visibly different results |

## Decision

**Option A.** Folio follows the Tables 3 draft (fix-up, auto and fixed layout, collapsed borders) and falls back to CSS 2.2 wording where the draft is silent. M1 includes: `table-layout: auto/fixed`, `border-collapse`, `border-spacing`, `caption-side: top/bottom`, `empty-cells`, `colspan`/`rowspan`, `<col>`/`<colgroup>` widths and backgrounds, `vertical-align: top/middle/bottom/baseline` in cells, and HTML presentational attributes. `visibility: collapse` on rows/columns is treated as `hidden`.
