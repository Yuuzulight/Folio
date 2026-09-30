# Text: shaping, font selection and fallback, emoji, line breaking

## What the specs require

- [CSS Fonts 4](https://www.w3.org/TR/css-fonts-4/): `font-family` lists and generic families (`serif`, `sans-serif`, `monospace`, `system-ui`, `ui-*`, `emoji`, …), the [font matching algorithm §5](https://www.w3.org/TR/css-fonts-4/#font-matching-algorithm) (stretch → style → weight, per character with fallback), `@font-face` with `src`, `unicode-range`, `font-display`, descriptors; `font-feature-settings`, `font-variant-*` (notably `font-variant-numeric: tabular-nums`), `font-variation-settings`, `font-synthesis`, `font-size-adjust`.
- [CSS Text 3](https://www.w3.org/TR/css-text-3/): white-space processing, line breaking and word boundaries ("UAX #14 as the baseline, tailored"), `text-transform` (Unicode case mapping, full-width), letter/word spacing rules (never inside a cluster).
- Unicode: [UAX #14 Line Breaking](https://www.unicode.org/reports/tr14/), [UAX #9 Bidi](https://www.unicode.org/reports/tr9/), [UAX #29 Text Segmentation](https://www.unicode.org/reports/tr29/) (graphemes for cursor/selection, words for double-click selection), [UAX #24 Script property](https://www.unicode.org/reports/tr24/) (run segmentation for shaping), [UAX #11 East Asian Width](https://www.unicode.org/reports/tr11/) (used by UAX #14), [UTS #51 Emoji](https://www.unicode.org/reports/tr51/) (presentation, sequences).
- [OpenType specification](https://learn.microsoft.com/en-us/typography/opentype/spec/): tables `cmap`, `head`, `hhea`, `hmtx`, `maxp`, `name`, `OS/2`, `post`, `kern`, `GSUB`, `GPOS`, `GDEF`, `fvar`; colour tables `COLR`/`CPAL`, `CBDT`/`CBLC`, `sbix`. Web font wrappers: [WOFF 1](https://www.w3.org/TR/WOFF/), [WOFF 2](https://www.w3.org/TR/WOFF2/).

## Hard parts and pitfalls

- **Complex scripts** (Arabic joining, Indic reordering, Thai, Khmer, Myanmar, Hangul jamo, mark positioning) need a full OpenType shaping engine; getting even one right is a large project.
- **Fallback per cluster, not per character**: a base letter plus combining mark, or an emoji ZWJ sequence, must come from one font; otherwise glyphs detach.
- **Emoji**: presentation depends on `Emoji_Presentation`, VS15/VS16, ZWJ sequences, skin-tone modifiers, keycaps, regional-indicator flags. The emoji font must be chosen for the whole sequence. Colour glyph formats vary.
- **Metrics**: line height `normal` uses the font's ascent/descent/line gap (from `hhea` or `OS/2` typo metrics when `USE_TYPO_METRICS` is set); mixing fallback fonts in one line changes line height, which is correct but surprising.
- **Shaping across style boundaries**: `<b>of</b>fice` must not form an "ffi" ligature across the boundary with different fonts, but kerning across same-font boundaries is expected.
- **Performance**: shaping is the most expensive per-character step; re-shaping on every relayout (resize!) is wasteful since text and fonts don't change.
- **.NET coverage**: the BCL gives `Rune`, `CharUnicodeInfo.GetUnicodeCategory`, `char.IsSurrogate` and friends, and `StringInfo`/`GetNextTextElementLength` (extended grapheme clusters, UAX #29). It has **no** line breaking, bidi, word segmentation, script property, East Asian width or emoji properties. `TextInfo.ToUpper` covers simple case mapping only (no `ß` → `SS`).

## Options

**Shaping**

| Option | Pros | Cons |
|---|---|---|
| A. HarfBuzzSharp for everything | Correct for every script from day one | Native dependency on every text run; no control over caching granularity |
| B. Folio's own simple shaper for simple scripts, HarfBuzzSharp for complex scripts, behind one `ITextShaper` interface | Common case (Latin, Greek, Cyrillic, digits, punctuation) runs fully in Folio code; complex scripts stay correct; the interface makes the native part swappable | Two shapers must agree on simple text (tested differentially) |
| C. Folio shaper for everything | No native shaping dependency | Complex scripts would be wrong for a long time |

**Unicode algorithms**

| Option | Pros | Cons |
|---|---|---|
| A. Take a third-party Unicode library | Less work | Another dependency for something that is data + small algorithms |
| B. Folio implements UAX #14, #9, #29 (words), #24, #11, UTS #51 properties from the Unicode Character Database files, with generated compact lookup tables; uses the BCL's grapheme segmentation | Full control, small, testable against Unicode's own test files (`LineBreakTest.txt`, `BidiCharacterTest.txt`, `WordBreakTest.txt`) | Must keep tables in sync with the Unicode version |

## Decision

- **Shaping: option B.** `ITextShaper.Shape(text span, font face, size, script, direction, language, features) → ShapedRun` (glyph ids, cluster indices, advances, offsets, unsafe-to-break flags).
  - `SimpleShaper` (Folio): `cmap` lookup, `hmtx` advances, kerning from `kern` and `GPOS` pair adjustment (lookup type 2), `GSUB` single substitution (type 1, for `tnum`, `smcp`, etc.) and ligatures (type 4, `liga`/`clig`), `GDEF` classes. Used for runs whose script is Latin/Greek/Cyrillic/Common/Inherited, that contain no combining marks needing mark positioning, and whose requested features are all in the supported set.
  - `HarfBuzzShaper` (in `Folio.Skia`, wraps HarfBuzzSharp): everything else.
  - The HarfBuzzSharp path lands first and serves as the reference. The simple shaper becomes the default for simple runs once a differential test over the text corpus shows identical glyph ids and advances.
- **Unicode: option B.** A build-time tool (`tools/Folio.UnicodeGen`) reads UCD files and writes two-stage lookup tables into C# source (committed). Folio pins its tables to the same Unicode version as the .NET runtime's grapheme segmentation; a unit test checks the pin. Unicode's own conformance test files run in CI.
- **Font parsing**: Folio's own OpenType reader (bounds-checked `ReadOnlySpan<byte>` readers, never trusting offsets) for `cmap` (formats 4 and 12, plus 14 for variation selectors), metrics tables, `name`, `OS/2`, `post`, `kern`, `GSUB`/`GPOS`/`GDEF` basics, `fvar` (to list axes). WOFF 1 via `ZLibStream`, WOFF 2 via `BrotliDecoder` plus Folio's `glyf`/`loca` reconstruction. Outlines and colour glyphs are **not** parsed by Folio: the raster backend draws glyphs by id (see [painting](12-painting.md)).
- **Font discovery**: `IFontSource` lists installed families and opens face data; the Windows implementation asks the system (DirectWrite via the SkiaSharp font manager). Web fonts from `@font-face` (M2) come through the [resource loader](16-resources-and-security.md), are unwrapped from WOFF (`ZLibStream`) or WOFF 2 (`BrotliDecoder` plus the `glyf`/`loca`/`hmtx` reconstruction, capped at 32 MiB decoded) and are validated by Folio's reader before use. They load once, synchronously, before the first layout, so `font-display` has no swap to do; an `@font-face` family shadows an installed family of the same name, `unicode-range` restricts which clusters a face serves, and a family none of whose sources loads falls through to the next family in the list. `data:` URLs and `local()` work now; other origins wait for the host's allowlist.
- **Font matching**: Folio implements CSS Fonts 4 §5 itself over the faces of each family. Generic families map to a host-configurable table (defaults: `sans-serif`/`system-ui` → Segoe UI, `serif` → Times New Roman, `monospace` → Cascadia Mono then Consolas, `emoji` → Segoe UI Emoji).
- **Fallback, per grapheme cluster**: (1) each family in `font-family` in order, first face whose `cmap` covers the whole cluster; (2) Folio's per-script fallback list (host-configurable; defaults cover CJK, Arabic, Hebrew, Indic, Thai, symbols); (3) system fallback (`IFontSource.MatchCharacter`); (4) the `.notdef` box, with a diagnostic. Results are cached per (font list, style, script, code point).
- **Emoji**: runs are segmented with UTS #51 rules (emoji presentation, VS15/VS16, ZWJ sequences, modifiers, keycaps, tags). Emoji-presentation clusters go to the emoji generic family first. Colour glyphs (COLR v0/v1, CBDT, sbix) are drawn by the backend. Folio shows exactly what the system emoji font contains (for example, the Windows emoji font has no country-flag glyphs, so flags appear as letter pairs).
- **Font synthesis**: synthetic bold and oblique when a family lacks the face and `font-synthesis` allows; variable fonts use named instances first, the `wght`/`wdth`/`slnt`/`ital` axes when the backend supports variation coordinates.
- **Caching**: shaped runs are cached per (face, size, features, script, direction, text) in a bounded LRU keyed by word-sized segments, so resize relayout re-uses shaping entirely.
- **Line breaking**: UAX #14 with CSS tailorings (`word-break: normal/break-all/keep-all/break-word`, `overflow-wrap`, `line-break` treated as `normal`, no-break inside `white-space: nowrap/pre`). Break opportunities are computed once per paragraph and stored as a bit array.
- **Bidi**: UAX #9 in full (including bracket pairs, isolates); paragraph-level from `direction`/`dir`.
- **Case mapping**: `text-transform` uses Folio's full case mapping (`SpecialCasing.txt`) for upper/lower/capitalize; `full-width` via a generated table.
- **Not in M1**: vertical text, `text-combine-upright`, automatic hyphenation dictionaries, `font-size-adjust` (parsed, ignored).
