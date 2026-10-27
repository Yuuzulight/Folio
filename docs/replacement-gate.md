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

## Verification refresh — 2026-10-27

The replacement gate remains open. The current focused native-engine/library
probe set passes 75/75, and the Folio-owned GIF/WebP/BMP fuzz targets completed
1,147,094, 63,926 and 3,753,186 mutations respectively with zero failures. These
are progress and safety evidence, not completion: HybridShaper still routes
complex scripts to HarfBuzz, rasterisation still uses SkiaSharp, Jint remains the
production JavaScript engine, and Wasmtime remains part of the native WebAssembly
path. The complete differential corpus, production selection, parity bars and
dependency removal are still required for those tracks.

Latest focused refresh: the Folio-owned GIF/WebP decoder suite passes 26/26,
the Folio-owned raster differential/conformance/unit suite passes 12/12, and
the native D2–D7 matrix now passes 59/60 (D2 9/10, D3 10/10, D4 10/10, D5 10/10,
D6 10/10, D7 10/10). These results strengthen the verified slices but do not
close the JavaScript, raster-performance, shaping, or WebAssembly replacement
gates.

The native-engine differential conformance probe makes the remaining JavaScript
gate concrete: D1 passes 2/2. The latest native D2–D7 run passes 59/60: D2
9/10, D3 10/10, D4 10/10, D5 10/10, D6 10/10 and D7 10/10. The native D5 chart
bridge now renders all ten Chart.js artifacts within the reference tolerance;
the remaining native parity failure is the React hooks interaction artifact,
tracked in #645. These residual results are not accepted as full parity.
The native matrix now clears the aggregate M5 category bar, but it cannot select
the native engine as Folio's production engine until the React artifact passes.

The earlier reproducible library bootstrap failure is resolved: the staged parser
now executes the cached minified React/ReactDOM sources, and the checked-in native
probe loads both bundles successfully. The remaining React failure is later in
the lifecycle: `createRoot(...).render(...)` does not produce the expected DOM
tree, while a minimal synchronous render reaches a real DOM `NotFoundError`.
The focused MessageChannel regression passes, so the remaining blocker is
reconciliation/shared-internals behavior rather than basic scheduler transport.

The UMD repair landed in `a0d2877`: native non-strict calls without an explicit
receiver bind `this` to the global object. Subsequent native engine work also
corrected array enumeration, relative slicing, null/undefined join fields and
`Array.from` strings/array-likes. The full scripting suite passes 1,363 of 1,369
discovered tests with six documented skips. These fixes do not yet establish React DOM mounting,
the D2–D7 parity bar, or production replacement of Jint.

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
blur/filter suite passes 12/12, but the repository benchmark measured 318.7 ms
versus Skia's 7.9 ms at 800x600 (40.4x slower), and 709.0 ms versus 15.3 ms at
1920x1080 (46.3x slower). The production performance gate and backend cutover
therefore remain open under issue #575.

The post-change Release verification remains green across the repository suites:
Folio.Tests 1,961 with one documented symlink skip, RenderTests 744/744, and
Scripting.Tests 1,369 discovered, 1,363 succeeded, with six documented software skips. These results verify
that the receiver-aware and stable-identity host fixes do not regress existing
coverage; they do not close the React reconciliation or replacement gate.
