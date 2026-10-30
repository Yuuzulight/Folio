# Folio-owned replacement gate

This is a hard, non-optional release gate for Folio. Dependency-backed
implementations are allowed as intermediate delivery steps, but they do not
count as Folio completion. No milestone, release, or completion report may
waive, defer, or reclassify this gate as optional.

## Current audit head — 2026-10-30

The native worker, nested-worker, iframe, and WebRTC capability slices are
green (54/54, 7/7, 27/27, and 11/11 respectively), and the latest full native
scripting runner is 1,449/1,449 on the current checkout. Jint is now excluded from the shipped content
host and retained only in the explicitly built differential/oracle host.
SkiaSharp, HarfBuzzSharp, Wasmtime, and ANGLE
(`Avalonia.Angle.Windows.Natives`) remain in the production graph until
the complete replacement gates below are met. The WARP float-renderbuffer
probe also confirms that the current WebGL fallback is RGBA8 emulation, so it
cannot be counted as a Folio-owned float backend.

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
4. **JavaScript** — [#685](https://github.com/Yuuzulight/Folio/issues/685): Folio's
   parser, VM, DOM host layer and hot-path execution replace Jint in production
   and pass the complete scripting and conformance suites, including sandbox and
   resource-limit behavior.
5. **WebAssembly** — [#686](https://github.com/Yuuzulight/Folio/issues/686): Folio-owned
   validation, compilation and execution replace Wasmtime and the compatibility
   interpreter, including imports, linear memory, tables, globals, streaming,
   workers, traps, limits and teardown behavior.
6. **GPU backend and shader translation** — Folio-owned WebGL/GPU backend and
   shader translation replace ANGLE, including texture/framebuffer formats,
   float/integer paths, device loss/recovery, budgets, sandboxing and host-frame
   transport. The dependency must be absent from production projects and
   published artifacts after differential verification.

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

## Verification refresh — 2026-10-30

Current PR #658 head (`ec7a5d56`) adds the native glyph-cache cap check: parsed
outlines and flattened paths share an 8 MiB per-font budget, and the stress
test covers every Source Sans glyph at three tolerances. `SoftwareRasterTests`
passes 39/39 and the full `Folio.RenderTests` Release suite passes 822/822.
The same head's broader project-level Release checks pass `Folio.Tests`
1997/1997 executable tests (one documented symlink skip), `Folio.WinForms.Tests`
8/8, and `Folio.Scripting.Tests` 1411/1411.
The font fuzz target also completed 178,885 mutated inputs from six seeds with
zero failures in a five-second Release run.
The current audit rerun extends that evidence to 606,558 mutated inputs in
15 seconds, still with zero failures, including the bundled CFF2 OTF and WOFF2
seeds.
The decoder fuzz targets also remain clean on this head: JPEG 181,368 inputs,
WebP 73,735 inputs, and GIF 1,307,221 inputs, all with zero failures.
This closes the cache-cap evidence portion of #573; CFF2 non-default variation
deltas, the broader glyph corpus and the default-backend performance gate remain
open.

The latest audit head (`fa0849fc`) extends the opaque fast path to fully
covered solid spans, while preserving the 37/37 `SoftwareRasterTests` result.
A short 20-frame Release run measured software replay at 73.4 ms versus 9.2
ms for Skia at 800×600 and 49.3 ms versus 16.6 ms at 1920×1080. The spread is
still noisy and the default-backend performance gate remains open.

Commit `fd8e8e4d` removes a second identity-transform glyph polygon copy during
software replay. `SoftwareRasterTests` remains 39/39; a short Release run then
measured approximately 55 ms at 800×600. The 1920×1080 replay remained above
the target, so this is an optimization result, not completion of #575.

The latest audit head (`9f417579`) bulk-fills integral opaque RGBA rectangles
in the Folio-owned software canvas. `SoftwareRasterTests` remains green at
37/37. A short Release benchmark measured typical software replay at 66.0 ms
at 800×600 and 49.2 ms at 1920×1080; these noisy measurements are evidence of
progress only and do not satisfy the default-backend performance gate.

The current audit head (`fc431f5b`) adds an exact opaque normal-source-over
fast path to the Folio-owned compositor. `SoftwareRasterTests` remains green
at 37/37. A fresh Release benchmark measured software replay at approximately
69.0 ms versus Skia 8.1 ms at 800×600, and 134.0 ms versus 18.0 ms at
1920×1080. The optimization is measurable, but the software backend is still
well above the production performance bar, so #575 and the default-backend
replacement gate remain open.

PR #658's latest head (`3f3d2c40`) passed CI run
[`40748439916`](https://github.com/Yuuzulight/Folio/actions/runs/40748439916)
on both Linux and Windows, including the serialized Release suite, conformance,
capability matrices, GPU/WebGL checks, benchmarks and fuzz stages. The current
native conformance sweep remains 94/94 across D1–D14, and the focused
Folio-owned JavaScript/parser/evaluator/program/library probes remain 131/131.
These are verified behavior slices; they do not by themselves select the
native engine for production or remove its dependency-backed transition path.

The current Release benchmark records paint-only animation at 4.8 ms median /
7.2 ms p95 for the typical dashboard and 17.4 ms median / 26.7 ms p95 for the
busy dashboard. Software raster replay remains informational at 83.7 ms versus
Skia's 5.7 ms at 800×600 and 163.0 ms versus 15.5 ms at 1920×1080. Therefore
the mandatory production raster-performance gate (#575), full complex-script
shaping gate, Jint and Wasmtime removal, and the remaining native M6 backend
acceptance work remain open.

The follow-up software-raster optimizations in PR #658 (`355831eb`, `348209b1`)
remove a delegate allocation and bypass path construction for plain rectangles.
A fresh local Release run measured typical software replay at 64.6 ms at
800×600 and 155.7 ms at 1920×1080, down from 81.2 ms and 165.0 ms in the
preceding run; typical paint-only animation remained 6.8 ms p95. This is
measurable progress, but full-scene software replay is still above the Skia
baseline, so it does not satisfy the default-backend switch gate.

The serialized local Release suite after these changes passes 4,248 tests with
zero failures and three documented skips (4,251 discovered).

An 80-frame benchmark after the unclipped-solid-paint fast path measured 69.8
ms at 800×600 and 84.9 ms at 1920×1080 for software replay, with typical
paint-only p95 at 8.6 ms. The spread between runs confirms that the benchmark
needs stabilization before the default-backend decision; these numbers are
evidence, not a gate pass.

## Verification refresh — 2026-10-30

An additional Release benchmark (`Folio.Benchmarks bench 3 5`) measured the
typical dashboard at 5.1 ms paint-only p95 and the busy dashboard at 18.8 ms
p95. Raster replay remained 60.2 ms versus Skia's 5.9 ms at 800x600 and
109.2 ms versus 11.6 ms at 1920x1080. The typical animation is within its
frame target, but the busy case and software-backend gap keep #575 open.

Fresh Release benchmark evidence (`Folio.Benchmarks bench 3 20`) still fails
the production raster gate: the Folio-owned software backend replays the
typical dashboard at 63.7 ms median versus Skia's 7.2 ms at 800x600, and 46.0
ms versus 15.3 ms at 1920x1080. Busy-dashboard paint-only p95 is 24.2 ms
against the 8.0 ms typical target. #575 therefore remains open.

Latest local rerun of the same benchmark (2026-10-30) measured typical
paint-only p95 at 5.5 ms and scroll p95 at 7.4 ms, while the busy dashboard
paint-only p95 was 20.7 ms. Software replay measured 62.5 ms at 800x600 and
53.8 ms at 1920x1080 versus Skia's 6.5 ms and 12.2 ms. The typical and scroll
checks pass, but the busy case and software-backend gap still keep #575 open.

Commit `d8ff8db5` adds a direct-copy fast path for fully opaque normal-source-over
software layers; the focused software raster suite remains 46/46. Follow-up
30-frame benchmark samples measured 61.8 ms at 800x600 and 39.3 ms at
1920x1080, still far above the replacement bar and subject to host noise.

Commit `e089a7d7` adds an exact-pixel fast path for integral axis-aligned
`clearRect` calls. A fresh 20-replay sample measured 57.8 ms versus Skia's
7.0 ms at 800x600 and 43.7 versus 14.0 ms at 1920x1080; the optimization is
useful but does not satisfy the production performance gate.

The replacement gate remains open. The latest complete serialized Release
verification passes 4,260 of 4,261 discovered tests with 0 failures and one
environment-dependent skip (local symbolic-link privilege). The software
canvas now has a Folio-owned baseline JPEG encoder, with scripting coverage
round-tripping the generated bytes through Folio's decoder. The CSSOM fuzz harness now retains and replays a mutation
that hit the configured script-memory bound; hosted CI run 40780721066 is
green across all 14 jobs. These are regression and containment results, not
evidence that any dependency replacement is complete.
The focused Folio-owned JavaScript engine/parser/evaluator/program/library
probe suites pass 131/131, and native D2–D7 conformance remains 60/60; these
are implementation slices, not completion evidence for the remaining corpus
and sandbox gates.
The Folio-owned GIF/WebP/BMP fuzz targets completed 1,167,395, 60,239 and
4,066,722 mutations respectively with zero failures. These are progress and
safety evidence, not completion: HybridShaper still routes complex scripts to
HarfBuzz and rasterisation still uses SkiaSharp. Jint is now oracle-only, and
Folio's native WebAssembly interpreter is the production path; Wasmtime remains
only as an explicit differential oracle until its package can be removed after
the remaining parity gates.

The production `win-x64` content-host publish now excludes both `Wasmtime.Dotnet`
and `wasmtime.dll`; the production Folio.Scripting build no longer references the
Wasmtime package or bridge at all. An explicit `FolioWasmOracle=true` test/oracle
build enables the bridge at runtime.
The complete differential corpus, production selection, parity bars and
dependency removal are still required for those tracks.

## Verification refresh — 2026-10-29 (current audit branch)

The current native scripting audit adds replacement-string tokens, regex capture
callback arguments, enumerable `Array.from` sources, `Object.fromEntries`,
numeric radix/precision formatting, Unicode normalization, and UTF-16
`split('')` semantics. Focused native tests pass 81/81 and the full scripting
suite passes 1,411/1,411. A fresh native conformance report passes D1–D14
94/94 and S1–S6 72/72 with no mismatches, crashes, hangs, limit hits, or
script failures.

These results strengthen the Folio-owned JavaScript evidence only. Production
now selects Folio's native engine; the complete DOM/sandbox/resource-limit/
failure corpus, Folio-owned raster/shaper/WebAssembly cutovers, and dependency
removal remain mandatory before Folio completion.

The full native scripting assembly was completed on 2026-10-29 with
`FOLIO_SCRIPT_ENGINE=native`: `Folio.Scripting.Tests` passed 1,422/1,422 in
116.7 seconds, while the focused native suite and capability-matrix slices
also remained green. A production-default cutover was then checked locally;
it exposed 25 content-process/cache/worker failures. That historical result
blocked cutover at the time and is retained as context below.

Follow-up on 2026-10-30 reran the complete assembly with
`FOLIO_SCRIPT_ENGINE=native`: **1,449/1,449 passed**. This confirms the native
unit/in-process suite has no current failures; the remaining blockers are the
separate content-process parity breadth and final dependency-removal gates.

The same native-engine run also passed the real content-process/cache/worker/
sandbox slice **52/52**. The shipped content host now defaults to Folio's native
engine; Jint is retained only in the explicitly built differential/oracle host.

The latest native D2–D7 probe passes 60/60: all library-card and canvas
artifacts pass, including `D2/pantry-react-jsx`, which now mounts and reaches
the click handler. Native `var` declarations are hoisted across conditional
scheduler branches (commit `c08da8bb`), fixing the previous dispatcher-binding
corruption. The prior React `NotFoundError` diagnosis was resolved by
labeled-break propagation in commit `b37a1f44`.
The canvas frame benchmark now has an
explicit native-engine mode. In the current compatibility-engine 30-frame run,
every library-card artifact and the snake game are below 16.7 ms p95, but
`D5/canvas-animated-bars` is 46.44 ms, dominated by script/update time, under
#519. The native 30-frame run puts every D5/D7 artifact below the target (worst
case `D7/canvas-snake-game`: 11.55 ms), so native-mode numbers are now
acceptance evidence; native D2–D7 parity remains tracked separately.

Latest focused refresh: the Folio-owned GIF/WebP decoder suite passes 26/26,
the Folio-owned raster differential/conformance/unit suite passes 12/12, and
the native D2–D7 matrix now passes 60/60 (D2–D7 each 10/10; D5 remains 10/10,
D6 10/10, D7 10/10). These results strengthen the verified slices but do not
close the JavaScript, raster-performance, shaping, or WebAssembly replacement
gates.

The shaping replacement now has a checked-in differential probe covering simple
scripts plus promoted Hebrew and Arabic complex runs, explicit Arabic joining
forms, and a deliberately perturbed kerning value that the report detects. The
focused shaping suite passes 19/19, and native S6 remains 12/12. The standalone
bundled report currently passes every listed case, and the Windows system-font
report passes all sixteen available cases, including Devanagari, Bengali, Thai,
Lao, Khmer and Myanmar. Hebrew and Arabic now route through Folio's
ComplexShaper by default; Indic, South-East Asian and other unproven scripts
remain on the HarfBuzz oracle until their per-script corpus and routing gates
are met.

The same bundled-font evidence is reproducible without the test runner:
`dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj -c Release
-- shaping-differential` reports the complete checked-in bundled case list plus a
deliberate kerning perturbation check. This is the checked-in harness evidence for #561;
system-font coverage and the remaining complex-script corpus are still required
before the shaping replacement as a whole can be considered complete.
On Windows, `dotnet run --project tests/Folio.RenderTests/Folio.RenderTests.csproj
-c Release -- shaping-differential-system` compares Arial, Times New Roman and
Segoe UI simple runs plus Hebrew, Arabic, Indic and South-East Asian runs against
HarfBuzzSharp; the current host reports all sixteen PASS. The system corpus remains a local
evidence run because installed fonts vary by host.

The artifact classifier now shares the image-format contract with `ImageLoader`:
data URLs for GIF, WebP, BMP, ICO and SVG remain in Folio alongside PNG/JPEG,
while unsupported formats such as AVIF still route to the browser. The supported
format matrix is covered by `ArtifactClassifierTests.BuiltInImageFormatsStayInFolio`.

The native-engine differential conformance probe makes the remaining JavaScript
gate concrete: D1 passes 2/2, D2–D7 passes 60/60, and the full D1–D14 sweep
passes 94/94.
The native D5 chart
bridge now renders all ten Chart.js artifacts within the reference tolerance;
the React hooks interaction artifact now passes. These residual results are not
accepted as full replacement parity.
The native matrix now clears the aggregate M5 category bar, including the React
hooks interaction artifact. This removes the React-specific blocker; the native
engine still cannot replace Jint until the complete engine, sandbox and
dependency-removal gates pass.

The earlier reproducible library bootstrap and host-commit failures are resolved:
the staged parser executes the cached minified React/ReactDOM sources, labeled
control flow is implemented, DOM event bubbling works across host wrappers, and
`createRoot(...).render(...)` produces the expected DOM tree. Function-scoped
`var` hoisting fixes the shared-internals scheduler binding; the remaining
blocker is broader Folio-owned replacement, not React host-parent selection. The
focused MessageChannel, placement and bubbling regressions pass.

The script runner now forwards uncaught timer, animation-frame and promise-job
exceptions into document diagnostics. This historical diagnostic helped expose
the React host-commit failure; current native D2–D7 conformance passes 60/60 and
the DOM mutation validity checks remain strict.

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
software raster suite passes 39/39, including glyf, CFF, CFF2 and WOFF2 outline
cases plus CBDT/CBLC, sbix and COLR colour-glyph cases. After the SIMD blur pass, the latest
repository benchmark now measures 18.4 ms versus Skia's 5.3 ms at 800x600
(3.5x slower), and 36.9 ms versus 10.8 ms at 1920x1080 (3.4x slower). Paint-only
animation p95 is 7.7 ms for the typical dashboard and scroll p95 is 6.8 ms at
800x600. The production performance gate and backend cutover therefore remain
open under issue #575 pending the full 1920x1080 review and corpus evidence.

Arabic GSUB progress (2026-10-28, commits `c3d9f83f` and `7c152852`): the Folio-owned shaper now
selects the Arabic script's required `rlig` feature and applies format-3
contextual substitutions, reverse-chain single substitutions and SingleSubst
format-2 replacement records.
Representative `سلام`, `لا` and `لم` forms match the HarfBuzz oracle; the focused
shaping suite now passes 55/55 and Arabic is production-routed through the
Folio-owned path. Indic/SE Asian shaping, broader system-font coverage and the
remaining raster/engine gates are still required for the mandatory all-Folio
replacement track.

The current parser also supports contextual and chained GSUB format-1 and
class-based format-2 rules. The Arabic corpus and current system-font probe remain
differential evidence while the promoted path is exercised in production; the
replacement gate stays open until all scripts and dependencies meet the same
standard.

The post-change Release verification remains green across the repository suites:
Folio.Tests 1,961 with one documented symlink skip, RenderTests 744/744, and
Scripting.Tests 1,390 executable tests succeeded, with six documented software skips. These results verify
that the receiver-aware and stable-identity host fixes do not regress existing
coverage; they do not close the React reconciliation or replacement gate.

## Verification refresh — 2026-10-29 (hosted and native full-suite)

Hosted CI run `40816100429` is fully green after the latest audit updates. The
exact WinForms and native capability checks passed in the hosted matrix. The
complete scripting suite also passes 1,412/1,412 with
`FOLIO_SCRIPT_ENGINE=native`, so native evidence covers the full suite rather
than only the focused parity corpus. These results strengthen the replacement
evidence but do not remove the mandatory production dependency gates.

Native RegExp now implements the previously missing sticky `y` flag contract:
stateful `lastIndex`, anchored matching, success advancement and failure reset
are covered by a focused regression and the full native suite.

The CI capability matrix now runs every capability row through both the
compatibility engine and the Folio-owned native engine. The native process
matrix and WinForms lifecycle slice pass locally for the `all` row (12/12 and
8/8), making hosted sandbox coverage a required regression gate instead of an
opt-in probe.
The Windows build also runs the complete conformance report with
`FOLIO_CONFORMANCE_ENGINE=native`; native conformance is therefore a hosted CI
requirement rather than a local-only verification command.

Verification refresh — 2026-10-30: the complete `Folio.Scripting.Tests` assembly
passes 1,449/1,449 with `FOLIO_SCRIPT_ENGINE=native` (106.4 seconds). The full
Release solution passes 4,350/4,352 with two documented skips (one
environment-only symbolic-link skip and one explicit Wasmtime oracle
benchmark skip). These are implementation-slice results; the mandatory replacement gate
still requires the broader corpus, sandbox/watchdog evidence and dependency
removal criteria below.
