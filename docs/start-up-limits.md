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

Limits apply per page, so a single noisy first load can exceed one (see the benchmark's Over budget rows); the median is the figure the limits are sized from.

## Method

Build the host with `tools/publish-content-host.ps1 -Destination artifacts/content-host`, then run
`FOLIO_CONTENT_HOST=<path>\Folio.ContentHost.exe dotnet run -c Release --project tests/Folio.Benchmarks -- scripted --libraries [--profile]`.
First frame is timed warm in one long-lived content process, so repeat loads of the same script hit the parse cache: these are warm-reload figures, not a user's first visit to a library. Worst-case retained parse cache is about 35 MiB of syntax trees plus keys (2.5M source characters), held for the life of the process. The cheaper retire collection is not measured here (the working-set read never retires a document). Each row is judged on a single sample while limits are sized from medians, so a noisy first load can trip a row.

 working set is read from a fresh process that loaded only that page. Each row is checked against its category's limit (`LibraryLimits` in `ScriptedProcessBenchmark.cs`).
