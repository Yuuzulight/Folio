# Folio-owned JavaScript engine plan

> **Current evidence correction (2026-10-30):** the historical 94/94 and 93/102
> claims in this document are superseded. The latest native sweep passes
> 102/102 D1–D14 artifacts after correcting compact conditional-expression
> lexing in the native JavaScript parser. The native engine is now the
> production content-host default; Jint remains an explicit differential oracle.

Folio's production scripting path must not depend on Jint, V8, ClearScript, QuickJS,
Node, or another external JavaScript runtime. `IScriptEngine` remains the seam, but
the shipping implementation will be owned by Folio and compiled as part of the
solution.

The replacement is staged so each step is testable:

1. **Language front end**: tokenizer, error locations, parser, and an explicit AST
   for the ECMAScript subset used by the artifact corpus.
2. **Bytecode and VM**: register or stack bytecode, lexical environments,
   closures, objects, arrays, property descriptors, exceptions, and the event-loop
   checkpoint hooks already required by `IScriptEngine`.
3. **DOM host layer**: move the existing `IHostObjectTemplate` behavior onto VM
   objects, preserving the generated bindings and the sandbox limits.
4. **Optimizing execution**: inline caches and a small native code generator for
   hot arithmetic, property, call, and loop paths. The interpreter remains the
   fallback for cold code and unsupported constructs.
5. **Cutover**: run the complete scripting, DOM, conformance, and content-process
   suites against the Folio engine; only then remove the Jint project and package
   reference.

The current Jint backend stays available while this work is in progress so the
existing artifact corpus remains runnable. It is a compatibility backend, not the
native completion criterion.

The content-host executable exposes the staged engine through the explicit
`FOLIO_SCRIPT_ENGINE=native` environment switch. The content-host default is now
Folio's native engine; Jint is retained only when an explicit oracle host is
built with `FolioOracleHost=true` and selected with `FOLIO_SCRIPT_ENGINE=jint`.
The native engine still must pass the complete content-process DOM, sandbox,
resource-limit and failure-behavior gates before the replacement track can be
closed.

The render conformance harness also accepts `FOLIO_CONFORMANCE_ENGINE=native`.
On 2026-10-26 the first D1 differential run was crash-free and produced 1/2
passes: `settings-tabs-faq` matched exactly. `sortable-filter-table` first
exposed a deterministic 3.9% pixel mismatch; after filling native syntax,
prototype/event, replacement, and assignment-expression gaps, both D1 artifacts
now match exactly (2/2, no crash/hang/limit/script failure).

The focused native DOM probe was used to drive the fixes: native prototype
lookup reaches the script-level `EventTarget` methods on wrapped elements, and
the corrected assignment parser now preserves the full conditional RHS used by
`innerHTML` updates. The D1 result is accepted as evidence for this corpus
slice only; full-corpus DOM, sandbox, limit, and failure-behavior gates remain.

The native corpus has since advanced materially. An earlier full sweep was
green across D1-D14: D1 `2/2`, D2-D7 `60/60` (including the React pantry
artifact), and D8-D14 `32/32`, for `94/94` exact passes. The current recorded
baseline is 102/102; these historical numbers are retained for the progression
record, not as the current completion claim.
The same native run also passes the scripting-focused S1-S6 corpus `72/72`
with no mismatches, crashes, hangs, limit hits, or script failures.

The follow-on parity slice now also covers native string replacement expansion
(`$&`, `$$`, prefix/suffix, numbered and named capture groups), array iteration
helpers, Unicode normalization, locale-case helpers, and the remaining common
string/array accessors. The focused native suite is `82/82`, and the full
`Folio.Scripting.Tests` suite reached `1,412/1,412` on the earlier audit branch.
Recent numeric coverage also includes radix conversion and precision/exponential
formatting; these remain differential evidence until the complete corpus and
production cutover gates pass.
Regex replacement callbacks also now preserve capture groups, offsets, and
source text across both replace variants.
Native regular expressions also honor the sticky `y` flag with stateful
`lastIndex` advancement and reset behavior.

The replacement gate remains open because the complete Folio-owned JavaScript
replacement still requires broader differential corpus, sandbox, limit,
cancellation and teardown evidence. The native switch and conformance harness
remain required probes even though native routing is now the production default;
Jint is retained only in the explicit oracle configuration.

Audit refresh (2026-10-30): the complete `Folio.Scripting.Tests` assembly
passes 1,449/1,449 with `FOLIO_SCRIPT_ENGINE=native`; the native content-host
default and the explicit Jint oracle both build successfully. The replacement
gate remains open for the broader feature matrix and final production-dependency
removal evidence, not because Jint is still selected in production.
