# Colour font mutation seeds

These small fonts are generated from Folio's own box-font source (the repository licence applies).
They cover COLR v0 layering, COLR v1 offset-addressed transforms and gradients, nonzero CPAL palette offsets,
and sbix PNG strikes. Each remains below the mutation harness's 64 KiB input ceiling so edits can reach the
colour tables instead of immediately truncating a large text font.

Regenerate deterministically with:

```sh
dotnet run --project tools/Folio.Gen -c Release -- colour-font-seeds
```

The generator uses only project-owned box outlines and the existing tiny PNG fixture. These are fuzz seeds,
not production fonts. The independent binary-format and rendering regressions are in `ColourFontFormatTests`.
