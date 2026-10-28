# Folio-owned replacement gate

This is a hard, non-optional release gate for Folio. Dependency-backed
implementations are allowed as intermediate delivery steps, but they do not
count as Folio completion. No milestone, release, or completion report may
waive, defer, or reclassify this gate as optional.

Folio is complete only when every replacement below is selected by production,
has passed differential and failure-behavior verification, and its superseded
production dependency has been removed:

1. **Text shaping** — Folio-owned shaping covers every script in the supported
   corpus and matches the current oracle for glyph selection and positioning.
2. **Rasterisation** — Folio-owned rasterisation covers paths, text, images,
   effects, clipping and compositing, with the existing golden, reftest,
   performance and fuzz bars preserved.
3. **Image codecs** — Folio-owned GIF and WebP decoders are production
   selected, fuzzed and conformance-equivalent to the existing decoder path.
   This gate is complete: GIF and WebP are wired through `ImageLoader`, with
   checked-in differential fixtures, malformed/limit tests, and fuzz targets.
   The shared `Folio.Imaging.ImageDecoder` entry point is also used by canvas
   still-media fallback, so video/canvas decode no longer has a narrower
   PNG/JPEG-only format matrix.
4. **JavaScript** — Folio's parser, VM, DOM host layer and hot-path execution
   replace Jint in production and pass the complete scripting and conformance
   suites, including sandbox and resource-limit behavior.
5. **WebAssembly** — Folio-owned validation, compilation and execution replace
   Wasmtime and the compatibility interpreter, including imports, linear memory,
   tables, globals, streaming, workers, traps, limits and teardown behavior.

## Evidence required for each replacement

- Production wiring selects the Folio-owned implementation by default.
- Differential tests compare the old backend and the Folio implementation over
  the complete relevant corpus.
- Reftests, goldens, fuzz targets and performance measurements meet the prior
  acceptance bars; failures are fixed rather than hidden behind routing.
- Failure, cancellation, limits, disposal and capability-revocation behavior
  remain equivalent.
- The dependency is absent from the production project graph and published
  artifacts. It may remain only in test infrastructure as an oracle until the
  final audit is complete.

The status table in [`docs/dependencies.md`](dependencies.md) and the milestone
roadmap must be updated with links to this evidence. A dependency-backed pass
or a partial Folio implementation is progress, never completion.

## Verification refresh — 2026-10-28

The replacement gate remains open. The latest complete Release verification
passes 4,185 tests with 0 failures and 7 documented skips (4,192 discovered).
The Folio-owned GIF/WebP/BMP fuzz targets completed 1,167,395, 60,239 and
4,066,722 mutations respectively with zero failures. These are progress and
safety evidence, not completion: HybridShaper still routes complex scripts to
HarfBuzz, rasterisation still uses SkiaSharp, Jint remains the production
JavaScript engine, and Wasmtime remains part of the native WebAssembly path.
The complete differential corpus, production selection, parity bars and
dependency removal are still required for those tracks.

The latest native D2–D7 probe passes 60/60: all library-card and canvas
artifacts pass, including `D2/pantry-react-jsx`, which now mounts and reaches
the click handler. Native `var` declarations are hoisted across conditional
scheduler branches (commit `c08da8bb`), fixing the previous dispatcher-binding
corruption. The prior React `NotFoundError` diagnosis was resolved by
labeled-break propagation in commit `b37a1f44`.
The canvas frame benchmark now has an
explicit native-engine mode. In the current compatibility-engine 30-frame run,
every library-card artifact and the snake game are below 16.7 ms p95, but
`D5/canvas-animated-bars` is 31.65 ms, dominated by script/update time, under
#519. The native 30-frame run puts every D5/D7 artifact below the target (worst
case `D7/canvas-snake-game`: 11.06 ms), so native-mode numbers are now
acceptance evidence; native D2–D7 parity remains tracked separately.

Latest focused refresh: the Folio-owned GIF/WebP decoder suite passes 26/26,
the Folio-owned raster differential/conformance/unit suite passes 12/12, and
the native D2–D7 matrix now passes 60/60 (D2–D7 each 10/10; D5 remains 10/10,
D6 10/10, D7 10/10). These results strengthen the verified slices but do not
close the JavaScript, raster-performance, shaping, or WebAssembly replacement
gates.

The shaping replacement now also has a checked-in differential probe covering
Latin, digits, Greek, Cyrillic, Latin-extended and Hebrew-base runs, plus a
deliberately perturbed kerning value that the report detects. Two explicit
Arabic/CJK cases also prove complex runs remain on the HarfBuzz oracle until
their gates are met (9 focused tests total). This is still only the proven
simple-run corpus for #561; the complete per-script corpus and complex-script
routing work remain open.

The same bundled-font evidence is reproducible without the test runner:
`dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj -c Release
-- shaping-differential` reports six proven runs plus a deliberate kerning
perturbation check. This is the checked-in harness evidence for #561; system
font coverage and the remaining complex-script corpus are still required
before any additional script switch can be turned off.
On Windows, `dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj
-c Release -- shaping-differential-system` compares Arial, Times New Roman and
Segoe UI against HarfBuzzSharp; the current host reports all three PASS. The
system corpus remains a local evidence run because installed fonts vary by host.

The artifact classifier now shares the image-format contract with `ImageLoader`:
data URLs for GIF, WebP, BMP, ICO and SVG remain in Folio alongside PNG/JPEG,
while unsupported formats such as AVIF still route to the browser. The supported
format matrix is covered by `ArtifactClassifierTests.BuiltInImageFormatsStayInFolio`.

The native-engine differential conformance probe makes the remaining JavaScript
gate concrete: D1 passes 2/2 and the latest native D2–D7 run passes 60/60.
The native D5 chart
bridge now renders all ten Chart.js artifacts within the reference tolerance;
the React hooks interaction artifact now passes. These residual results are not
accepted as full replacement parity.
The native matrix now clears the aggregate M5 category bar, but it cannot select
the native engine as Folio's production engine until the React artifact passes.

The earlier reproducible library bootstrap and host-commit failures are resolved:
the staged parser executes the cached minified React/ReactDOM sources, labeled
control flow is implemented, DOM event bubbling works across host wrappers, and
`createRoot(...).render(...)` produces the expected DOM tree. Function-scoped
`var` hoisting fixes the shared-internals scheduler binding; the remaining
blocker is broader Folio-owned replacement, not React host-parent selection. The
focused MessageChannel, placement and bubbling regressions pass.

The script runner now forwards uncaught timer, animation-frame and promise-job
exceptions into document diagnostics. This makes the React host-commit failure
actionable (`NotFoundError` is reported as an event-loop script error) without
weakening DOM mutation validity.

The UMD repair landed in `a0d2877`: native non-strict calls without an explicit
receiver bind `this` to the global object. Subsequent native engine work also
corrected array enumeration, relative slicing, null/undefined join fields and
`Array.from` strings/array-likes. The rebuilt full scripting suite passes all 1,390
executable tests with six documented skips. These fixes do not yet establish React
DOM mounting, the D2–D7 parity bar, or production replacement of Jint.

## Verification refresh — 2026-10-27

A fresh probe using the real Folio document, DOM bindings and event loop still
reproduces the next native-engine blocker: `ReactDOM.render(React.createElement('b',
null, 'ready'), root)` reaches DOM reconciliation but throws Folio's spec-correct
`NotFoundError` from `removeChild` because the reconciler supplies `body` as the
host parent while the child `<p>` is still attached to a nested `<div>`; the root
is left empty. The DOM validity check is intentionally unchanged.
This is tracked in [#645](https://github.com/Yuuzulight/Folio/issues/645) and remains
required before the native engine can pass the React/JSX interaction artifact or
the mandatory replacement gate.

The Folio-owned software rasterizer also remains differential-only: the focused
blur/filter and glyph suite passes 19/19. After the SIMD blur pass, the latest
repository benchmark measured 77.1 ms versus Skia's 6.2 ms at 800x600 (12.5x
slower), and 173.7 ms versus 14.8 ms at 1920x1080 (11.7x slower). The
production performance gate and backend cutover therefore remain open under
issue #575.

Arabic GSUB progress (2026-10-28, commits `c3d9f83f` and `7c152852`): the Folio-owned shaper now
selects the Arabic script's required `rlig` feature and applies format-3
contextual substitutions, reverse-chain single substitutions and SingleSubst
format-2 replacement records.
Representative `سلام`, `لا` and `لم` forms match the HarfBuzz oracle; the focused
shaping suite passes 19/19 and the full Release suite passes 4,172 tests with no
failures. This narrows #563 but does not close it: marks, cursive attachment,
Indic/SE Asian shaping and broad corpus coverage remain required for the mandatory
all-Folio replacement track.

The post-change Release verification remains green across the repository suites:
Folio.Tests 1,961 with one documented symlink skip, RenderTests 744/744, and
Scripting.Tests 1,390 executable tests succeeded, with six documented software skips. These results verify
that the receiver-aware and stable-identity host fixes do not regress existing
coverage; they do not close the React reconciliation or replacement gate.
