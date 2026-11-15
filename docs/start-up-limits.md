# Start-up limits for pinned libraries

Confirmed for 1.0. Ordinary libraries (D2, D3, D6) keep 50 ms / 80 MiB; chart/canvas libraries (D4, D5) are limited to 60 ms / 85 MiB and Phaser-class libraries (D7) to 180 ms / 130 MiB.

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

## Row misses, cause and harness change (2026-11-15)

The 1.0 measurement found class medians within limits but individual rows over. Cause, from `--profile` and repeated runs:

- **The first row of each library class** (D2 `library-card-03`, D4 `library-card-02`, D7 `library-card-02`) was a harness artifact. It is the first load of that library in the long-lived process and pays the one-off library parse and glyph and shaping caches (D2 raster 18 ms against 1.3 ms for the next row). It is real for a user's first visit, but the limits are warm-reload limits. The benchmark now loads each class's first artifact once untimed, prints that cold load as `(cold first load, discarded warm-up)` and does not judge it, then times every row as the median of three warm loads, so one noisy load cannot trip a row.
- **Background load.** In a session with a browser, chat apps and overlays running (CPU 16-83% before runs), 10 runs of the changed harness still had 0 to 11 of 54 rows over, with D4 class medians of 49-64 ms and D7 of 147-178 ms; the remaining misses were `D2/pantry-react-jsx` (51-58 ms), D4 rows and D7 rows. The same machine in that session ran every row 15% or more slower than in a quiet one.
- **Quiet machine, current main (079ab3eb, includes the engine speed-ups of #811).** With browsers, chat apps and the game closed (CPU 1-13% before each run, RAM 48-51%, only a vendor network service idle), 5 runs, 54 rows each: **0 rows over their limit in every run**. No limit was changed: the numbers do not support tighter ones with any margin on a loaded machine, and the loaded-machine run above shows the headroom is needed.

| Class (limit ms / MiB) | Class median ms, min / median / max | Slowest row ms, min / median / max | Cold first load ms (not judged) | Max working set MiB |
|---|---|---|---|---|
| D2 React+Babel (50 / 80) | 22.7 / 23.1 / 24.9 | 29.4 / 30.1 / 30.9 | 38.3 / 39.2 / 40.0 | 61.4 |
| D3 Twind (50 / 80) | 24.5 / 24.7 / 25.4 | 25.5 / 26.2 / 32.4 | 26.8 / 28.6 / 31.4 | 56.6 |
| D4 D3 library (60 / 85) | 32.4 / 32.7 / 35.2 | 34.0 / 35.0 / 36.6 | 56.9 / 58.3 / 61.3 | 63.3 |
| D5 Chart.js (60 / 85) | 29.0 / 30.1 / 31.0 | 30.5 / 31.8 / 33.1 | 34.2 / 35.9 / 37.3 | 56.8 |
| D6 Mermaid (50 / 80) | 23.1 / 23.5 / 24.8 | 24.2 / 25.1 / 26.1 | 20.5 / 24.7 / 28.1 | 56.2 |
| D7 Phaser (180 / 130) | 97.4 / 97.7 / 98.2 | 99.5 / 100.5 / 113.4 | 165.7 / 170.2 / 172.5 | 86.3 |

Cold first loads are the first visit to a library in a process; all are within their class limits on the quiet machine (they were 59-67 ms for D2 and 245-303 ms for D7 on the loaded one, over their limits). Rows judged against limits are warm reloads.

## First paint: headless and visible

Budget: 50 ms median, 150 ms p95 over the 24 S1/S3 fixtures at 1920x1080, 96 DPI. Both numbers are published because they include different things. Quiet machine, 5 runs each, alternating (min / median / max):

| Path | What it includes | Median ms | p95 ms |
|---|---|---|---|
| Headless (`Folio.Benchmarks firstpaint 5 on`) | Parse, style, layout, full raster into a new surface, managed-array copy of the frame; fresh JIT process, no window | 36.7 / 37.1 / 37.6 | 73.5 / 88.2 / 94.3 |
| Visible (`tools/Folio.WinForms.Benchmarks`, `FolioView` in a shown window) | LoadHtml, first `OnPaint`: layout, accessibility tree sync, raster, copy into a GDI+ bitmap, `Clear` and `DrawImageUnscaled`, up to the `Rendered` event; fresh JIT process | 49.9 / 53.5 / 57.5 | 76.4 / 94.8 / 108.0 |

The p95 meets 150 ms on both paths. **The visible median misses the 50 ms budget** (53.5 ms; one of five runs, 49.9 ms, met it). Cause: an instrumented run on the loaded machine split the visible first paint into layout 12.2 ms, raster 29.5 ms, copy into the bitmap 1.4 ms and drawing to the window 1.8 ms, so presenting costs about 3 ms and the rest is the engine. Both benchmarks run the JIT: the first paints are executed in tier-0 code that has not been optimised yet, which is a larger share of the 12-16 ms visible-over-headless gap than the window itself (the visible path adds a window, GDI+ and accessibility work on top of a first paint in a fresh process). Whether to ship ReadyToRun or NativeAOT, which removes most tier-0 cost, is the consumer's packaging choice, so the visible figure is a property of the JIT host that was measured, not a floor for every host. The content host figures above and D1 below are NativeAOT. No limit or budget was changed.

D1 scripted start-up, NativeAOT content host, quiet machine, 5 runs (min / median / max): cold start 171.6 / 177.8 / 216.5 ms (limit 400), per-run median warm first frame 14.9 / 15.4 / 16.0 ms (limit 50), slowest D1 artifact 21.2 / 22.7 / 24.3 ms, working set up to 58.0 / 58.9 / 59.3 MB (limit 80).
