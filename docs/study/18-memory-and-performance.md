# Memory budget and performance targets

## Where memory goes

For a typical artifact (50–300 KB of HTML/CSS, 1–5k elements) the big consumers are not the DOM:

| Consumer | Typical size | How Folio bounds it |
|---|---|---|
| Raster backing surface | 8 MB at 1920×1080, 33 MB at 3840×2160 (4 bytes/pixel) | One surface the size of the control's client area; no full-page raster; tiles only if measurements demand it |
| Decoded images | unbounded without care | Decode at the size actually painted when smaller (JPEG DCT scaling, PNG downsample after decode), 64 MB per-document cap with eviction |
| Glyph cache (backend) | grows with sizes × glyphs | Backend font cache limit set explicitly (default 8 MB) |
| Font files | 1–20 MB each | System fonts are memory-mapped by the backend (shared, reclaimable pages); web fonts held once per process, deduplicated by hash |
| Shaping cache | grows with text | Bounded LRU (default 2 MB) |
| DOM + styles + boxes + fragments | ~1–3 KB per element with sharing | Interned names, shared style groups, style sharing cache, struct arrays for inline items and glyph runs |
| Display list | ~50–100 bytes per item | Flat struct array, reused between frames |
| .NET runtime and code | shared with the host | Folio adds its assemblies only; no second runtime in-process |

## Targets

Measured as the **increase in the host's working set** when a document is opened in the WinForms control at 1920×1080, 100% scale, after the first paint, with a warm process:

| Scenario | Target |
|---|---|
| Typical artifact (corpus median) | ≤ 30 MB |
| Large artifact (corpus 95th percentile, e.g. a 5,000-row table) | ≤ 60 MB |
| After closing the document and a GC | returns to within 2 MB of the baseline |
| Scripted document (M4+): content process working set, typical | ≤ 80 MB (the job's hard cap is separate and larger) |

Time targets on a mid-range desktop CPU, warm process (JIT done), typical artifact:

| Operation | Target |
|---|---|
| Parse + style + layout + first paint | ≤ 50 ms (≤ 150 ms at the corpus 95th percentile) |
| First document in a cold process (includes JIT) | ≤ 400 ms; ReadyToRun compilation is used to get there |
| Relayout + repaint on window resize | ≤ 16 ms per step |
| Scroll frame (replay + raster of the viewport) | ≤ 8 ms |
| Hover restyle + repaint (M3) | ≤ 4 ms |
| Paint-only animation frame (M2) | ≤ 8 ms |
| Canvas 2D animation (M5, conformance category) | p95 frame ≤ 16.7 ms |

## Hard parts and pitfalls

- **.NET allocation churn** in hot loops (tokenizers, selector matching, line breaking) causes GC pauses that show as scroll jank.
- **Measuring working set on Windows** is noisy (the runtime reserves and trims); targets need a repeatable harness.
- **Caches that never shrink** turn a long-running host (Mana runs all day) into a slow leak.

## Decisions

- **Allocation discipline in hot paths**: `Span<T>`-based tokenizers, pooled buffers (`ArrayPool<T>`), struct-of-arrays for inline items, glyph runs, grid tracks and display items; no LINQ in hot paths; no per-glyph or per-token objects.
- **Every cache is bounded** and keyed so that closing a document can drop its entries; a `Document.Dispose()` releases images, web fonts and caches owned by it.
- **Benchmark harness in the repo** (from M1): renders the conformance corpus headless and in the control, records time per pipeline stage and working-set deltas, and fails CI on a regression beyond a set tolerance against the stored baseline.
- **Trimming and AOT**: the engine assembly avoids reflection-based code so the headless API is trimming- and NativeAOT-compatible (useful for the content process and for small tools). The WinForms control follows WinForms' own support.
- **CPU raster first**: no GPU context memory; GPU rendering is only considered if CPU raster misses the scroll or animation targets.
