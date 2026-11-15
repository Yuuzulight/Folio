# Contributing to Folio

## References

- Folio's design is its own. Code, comments, docs, commit messages and pull requests do not credit, link to, describe or say they were "inspired by" other rendering engines, browsers or similar projects.
- Specifications are the reference: link the spec section an algorithm implements.
- Data a specification requires is part of implementing it and is kept as the spec gives it, even when it names old products (for example the legacy DOCTYPE public identifiers that select quirks mode).
- Folio's own dependencies (SkiaSharp, HarfBuzzSharp, test libraries) may be named where they are used.

## Naming

- The public hosting API is in the `Folio` namespace (`Folio.Document`, `FolioOptions`, ...). The internal DOM document class is `Folio.Dom.DocumentNode`.
- Avoid a namespace and a type with the same name (`Folio.Painting` is the home of the planned `Paint` type, `Folio.Typography` sits beside the DOM's `Text`).

## Building and tests
Needs .NET SDK 10.0.403 or newer (including the patched .NET 10.0.14 runtime). The engine and its tests run on Windows and Linux; the WinForms control runs on Windows.

```
dotnet build Folio.slnx
# The test projects use direct xUnit v3 in-process runners.
dotnet tests/Folio.Tests/bin/Debug/net10.0/Folio.Tests.dll
dotnet tests/Folio.RenderTests/bin/Debug/net10.0/Folio.RenderTests.dll
dotnet tests/Folio.Scripting.Tests/bin/Debug/net10.0/Folio.Scripting.Tests.dll
dotnet tests/Folio.Fuzz/bin/Debug/net10.0/Folio.Fuzz.dll
dotnet tests/Folio.Benchmarks/bin/Debug/net10.0/Folio.Benchmarks.dll
dotnet tests/Folio.WinForms.Tests/bin/Debug/net10.0-windows/Folio.WinForms.Tests.dll
```

The solution uses Microsoft.Testing.Platform. Run the complete suite with
`dotnet test --solution Folio.slnx -c Release --max-parallel-test-modules 1`, or use the direct commands above
for individual projects.

The Windows production content host is a standalone NativeAOT executable. Publishing it requires the patched SDK
above and Visual Studio C++ build tools with a Windows SDK (plus ARM64 tools when targeting ARM64):

```powershell
pwsh -File tools/publish-content-host.ps1
```

The script validates the native image, patched compiler packages, font hashes and license notices before replacing
an existing publish. It writes `native-build.json` with the build provenance. Published-host integration tests use
`FOLIO_CONTENT_HOST` to select that exact executable; ordinary developer builds remain available for engine oracles.

The render tests include an equivalence check: after random style changes to a page, an incremental update must draw exactly what a fresh render draws.

Image tests that fail or change write their actual, expected and diff images to `tests/render-output/`. Golden images change only through the approve command, after reviewing those images:

```
dotnet run --project tests/Folio.RenderTests -- approve <area|area/name>
```

The conformance report renders the corpus and prints pass rates per category:

```
dotnet run --project tests/Folio.RenderTests -- conformance
```

The fuzzer mutates the parser and decoder test inputs for the given number of seconds per target. Inputs it fails on are written to `tests/fuzz-output/`; once fixed, they go in `tests/Folio.Fuzz/Regressions/<target>/`, which the test run replays:

```
dotnet run --project tests/Folio.Fuzz -c Release -- fuzz <seconds> [html|css|png|jpeg|font|svg]...
```

The benchmark harness times each pipeline stage and measures memory for generated documents and the conformance corpus, against the targets in `docs/study/18-memory-and-performance.md`:

```
dotnet run --project tests/Folio.Benchmarks -c Release -- bench [iterations] [frames]
```

`iterations` controls parse/layout measurements; `frames` independently fixes the number of animation,
interaction, and raster-replay samples so runs can be compared across machines (for example, `bench 20 80`).

`gc` reports the layout stage's time, GC pause and collection counts, and a whole render through the public API, for the large generated documents:

```
dotnet run --project tests/Folio.Benchmarks -c Release -- gc 9
```

The pack script writes the packages to a folder that works as a local package source (by default `artifacts/packages`). It runs in PowerShell 5.1 (`powershell`) or 7 (`pwsh`):

```
pwsh -File tools/pack.ps1 [-Output <folder>] [-Version <release-version>]
```

By default the version is `0.1.0-m1.N`, where N is the number of commits in the packed commit's history, so each commit on main packs as its own version. The script refuses to pack uncommitted changes or a shallow clone. An explicit `-Version` lets a clean tagged commit produce its release package version. A host pins both a commit and package version, adds the feed as a package source, and builds the standalone host from that same commit. Building packages does not publish a GitHub release. Mana automatic updates accept published stable releases only, excluding drafts and prereleases.

Golden images change only through the approve command, and the new images are reviewed in the pull request.
