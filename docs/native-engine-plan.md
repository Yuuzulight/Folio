# Folio-owned JavaScript engine plan

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
`FOLIO_SCRIPT_ENGINE=native` environment switch. This is a differential/runtime
probe only: the default remains Jint until the native engine passes the full
corpus, DOM, sandbox, resource-limit and failure-behavior gates. The switch lets
real content-host paths run against Folio's engine without changing the shipping
default prematurely.

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

The native corpus has since advanced materially. A fresh full sweep is now
green across D1-D14: D1 `2/2`, D2-D7 `60/60` (including the React pantry
artifact), and D8-D14 `32/32`, for `94/94` exact passes. These are verified
corpus results, not a claim that the engine is production-complete.
The same native run also passes the scripting-focused S1-S6 corpus `72/72`
with no mismatches, crashes, hangs, limit hits, or script failures.

The follow-on parity slice now also covers native string replacement expansion
(`$&`, `$$`, prefix/suffix, numbered and named capture groups), array iteration
helpers, Unicode normalization, locale-case helpers, and the remaining common
string/array accessors. The focused native suite is `82/82`, and the full
`Folio.Scripting.Tests` suite is `1,412/1,412` on the audit branch.
Recent numeric coverage also includes radix conversion and precision/exponential
formatting; these remain differential evidence until the complete corpus and
production cutover gates pass.
Regex replacement callbacks also now preserve capture groups, offsets, and
source text across both replace variants.
Native regular expressions also honor the sticky `y` flag with stateful
`lastIndex` advancement and reset behavior.

The replacement gate remains open because the production content-host default
is still Jint and the native path has not yet passed the complete DOM host,
sandbox, resource-limit, failure-behavior, worker/iframe, and dependency
removal gates. The native switch and conformance harness therefore remain
differential probes until those requirements are met.
