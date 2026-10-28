# Shaping differential report

This is the checked-in evidence for the mandatory shaping replacement track
([#561](https://github.com/Yuuzulight/Folio/issues/561)). The oracle is
`HarfBuzzShaper`; the Folio paths are `SimpleShaper` and `ComplexShaper`.

## Current green corpus

`ShapingDifferentialTests` passes 23/23 on the bundled fonts:

- Latin and punctuation (`Source Sans 3`)
- digits and kerning (`Folio Box`)
- Greek and Cyrillic
- Latin extended characters
- Hebrew base text
- Hebrew niqqud words (`שלום`, `בראשית`, `מלכה`)
- Arabic promotion-switch routing through `ComplexShaper`
- Arabic extended corpus parity (`العربية لغة جميلة`, `مَرْحَبًا بِكُمْ`,
  `اللغة العربية`, `فلسطين`), with advances exact and offsets within 0.001
- Hebrew promotion-switch routing through `ComplexShaper`
- mixed-script safety (a promoted script cannot capture an entire mixed run)
- deliberate kerning perturbation detection

`HarfBuzzShaperTests` passes 19/19, including representative Hebrew niqqud
and Arabic `سلام`, `لا`, and `لم` cases. The direct Arabic corpus now also
covers `مُحَمَّد`; glyph, cluster, advance and multi-mark offset parity pass
for all four cases. The broader Arabic corpus remains open below.

## Explicitly open parity

The promotion switch does not claim parity. It is a controlled harness switch
for a script under review; production routing remains unchanged until the
script's corpus is identical in glyphs, clusters, advances, and offsets.

The broader Arabic probe still requires additional ordinary-word, cursive-
attachment and mark corpus coverage beyond these promoted samples. Those
remaining differences are recorded on [#563](https://github.com/Yuuzulight/Folio/issues/563),
so Arabic remains on the HarfBuzz oracle in production.

## Gate interpretation

This report is evidence of the harness and the proven slices only. It does not
close #561 or any script issue: the mandatory replacement gate still requires
complete per-script corpus parity and removal of HarfBuzz from production.
