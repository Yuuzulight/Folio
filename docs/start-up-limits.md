# Start-up limits for pinned libraries

Proposed for 1.0, pending owner confirmation. Ordinary libraries keep 50 ms / 80 MiB; chart/canvas and Phaser-class libraries get their own limits.

## Measured (NativeAOT content host, median of 3 runs x 27 pages per category)

| Category | Class | First frame ms (before -> after) | Working set MiB (before -> after) |
|---|---|---|---|
| D2 React+Babel | ordinary | 29.6 -> 26.5 | 58.6 -> 58.2 |
| D3 Twind | ordinary | 34.1 -> 28.6 | 60.4 -> 60.0 |
| D4 D3 library | chart/canvas | 52.7 -> 46.3 | 68.0 -> 67.7 |
| D5 Chart.js | chart/canvas | 42.1 -> 35.9 | 63.0 -> 62.5 |
| D6 Mermaid | ordinary | 36.7 -> 31.2 | 62.1 -> 61.7 |
| D7 Phaser 3.90 | Phaser-class | 223.0 -> 150.4 | 96.0 -> 107.0 |

## Limits

| Class | First frame | Working set | Rationale |
|---|---|---|---|
| Ordinary (D2, D3, D6) | 50 ms | 80 MiB | Unchanged; best medians 26-31 ms / 58-62 MiB. |
| Chart/canvas (D4, D5) | 60 ms | 85 MiB | About 30% over the slowest class median (D4 46.3 ms); working set about 25% over 67.7 MiB. |
| Phaser-class (D7) | 180 ms | 130 MiB | About 20% over the median (150 ms) and 107 MiB |

Limits apply per page. Each row is the median of three warm loads, so one noisy load cannot trip it (see below); the class median is the figure the limits are sized from.

## Method

Build the host with `tools/publish-content-host.ps1 -Destination artifacts/content-host`, then run
`FOLIO_CONTENT_HOST=<path>\Folio.ContentHost.exe dotnet run -c Release --project tests/Folio.Benchmarks -- scripted --libraries [--profile]`.
First frame is timed warm in one long-lived content process, so repeat loads of the same script hit the parse cache: these are warm-reload figures, not a user's first visit to a library. Worst-case retained parse cache is about 35 MiB of syntax trees plus keys (2.5M source characters), held for the life of the process. The cheaper retire collection is not measured here (the working-set read never retires a document). Each row is judged on a single sample while limits are sized from medians, so a noisy first load can trip a row.

 working set is read from a fresh process that loaded only that page. Each row is checked against its category's limit (`LibraryLimits` in `ScriptedProcessBenchmark.cs`).

## Row misses, cause and harness change (re-measured 2026-11-15)

The 1.0 measurement found class medians within limits but individual rows over. Cause, from `--profile` and 15 runs:

- **First row of each library class (D2 `library-card-03`, D4 `library-card-02`, D7 `library-card-02`)** was a harness artifact. It is the first load of that library in the long-lived process: it pays the one-off library parse and glyph and shaping caches (D2 raster 18 ms against 1.3 ms for the next row; D7 275 ms against 150-185 ms). It is real for a user's first visit, but the limits are warm-reload limits. The benchmark now loads each class's first artifact once untimed, prints that cold load as `(cold first load, discarded warm-up)` and does not judge it, then times every row as the median of three warm loads. Cold first loads measured: D2 59-67 ms, D3 34-48 ms, D4 76-138 ms, D5 42-49 ms, D6 33-56 ms, D7 245-303 ms. D4 and D7 cold loads exceed their limits; that cost is real and is not hidden, only not judged against a warm limit.
- **Single noisy loads** (D5, D6 and D7 rows at 80-220 ms in some runs, while the machine was also running a browser and chat apps) are removed by the median of three.
- **Real cost, not fixed.** `D2/pantry-react-jsx` (a compiled JSX app, not a library card) is 51-58 ms in most runs (33 ms of script, 11 ms raster): over 50 ms by 2-8 ms. D4 (the D3 library) has a median of 49-64 ms against 60: script evaluation dominates (about 65 ms in profile) and the median moves with machine state, so the 60 ms limit has no headroom on a loaded machine. D7 rows sit at 150-187 ms against 180. None was reduced in this change and no limit was raised.

Re-measurement with the changed harness, 10 runs (min / median / max of the per-run class median, ms): D2 28.9 / 29.9 / 37.3, D3 30.9 / 35.0 / 38.7, D4 49.1 / 60.8 / 64.4, D5 38.7 / 43.1 / 47.3, D6 30.6 / 36.0 / 39.4, D7 146.8 / 171.9 / 177.8. Rows over limit per run: 7, 9, 0, 0, 10, 9, 9, 6, 8, 11 of 54; only two runs (both with the quietest machine) had none. With the previous harness (5 runs): 7, 8, 8, 8, 5. The remaining misses are pantry-react-jsx, D4 rows and D7 rows near the limit.

## First paint: headless and visible

Budget: 50 ms median, 150 ms p95 over the 24 S1/S3 fixtures at 1920x1080, 96 DPI. Both numbers are published because they include different things.

| Path | What it includes | Median ms (min / median / max, 5 runs) | p95 ms (min / median / max) |
|---|---|---|---|
| Headless (`Folio.Benchmarks firstpaint 5 on`) | Parse, style, layout, full raster into a new surface, managed-array copy of the frame; fresh process, no window | 37.8 / 46.4 / 51.9 | 127.9 / 143.0 / 154.0 |
| Visible (`tools/Folio.WinForms.Benchmarks`, `FolioView` in a shown 1920x1080 window) | LoadHtml, first `OnPaint`: layout, accessibility tree sync, raster, copy into a GDI+ bitmap, `Clear` and `DrawImageUnscaled`, up to the `Rendered` event | 56.7 / 59.7 / 66.4 | 105.1 / 109.3 / 128.9 |

The visible path qualifies the budget; the headless one is a pipeline comparison. In these runs the visible median misses 50 ms (p95 meets). An instrumented run of the visible path gave per first paint, median over 123 paints: layout 12.2 ms, raster 29.5 ms, copy into the bitmap 1.4 ms, draw to the window 1.8 ms. So presenting costs about 3 ms and the engine (layout and raster) is almost all of it; there is no window-specific cost to remove. The earlier 1.0 run measured the same visible path at 43 ms median; the machine ran noticeably slower in this session (the same headless row moved 40 to 46 ms, and D3 library rows 29-32 to 36 ms), so the visible figure is machine-state sensitive and the miss is not attributable to a change in the engine. This was not reduced here.
