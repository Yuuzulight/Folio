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
