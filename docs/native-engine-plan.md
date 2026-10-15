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
