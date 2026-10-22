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

## Implementation status (audited 2026-10-23)

- [ ] **Stage 1 (Language front end)**: partial. The tokenizer, parser and AST cover functions, closures, objects, arrays, loops, switch, do-while, try/catch/finally, arrow functions and basic operators. Missing: classes, destructuring, spread and rest, template-literal interpolation, regular-expression literals, `for...of`, `for...in`, default parameters, optional chaining, `??`, `**`, bitwise operators, labels, accessors, generators and async functions.
- [ ] **Stage 2 (Bytecode and VM)**: partial. Chunked instructions, call frames, limits, a microtask queue and a watchdog exist. Known wrong: postfix `++` on a member returns the wrong value, `let` in a `for` loop is not per iteration, `Error` objects have no `message`, `Function.prototype.call/apply/bind` and `arguments` are missing.
- [ ] **Stage 3 (DOM host layer)**: not done. `NativeHostObject` wraps a template for a unit test; the DOM bindings, the content host and the conformance corpus do not run on this engine, and the content host references only the Jint backend.
- [ ] **Stage 4 (Optimizing execution)**: not done. A property inline cache exists; there is no code generator, and the standard library is a small part of what artifacts use (`JSON`, `Map`, `Set`, `RegExp`, `Array.from`, `Date` methods and sorting are missing).
- [ ] **Stage 5 (Cutover)**: not started. It needs the full scripting, DOM and conformance suites to pass on this engine and the same fuzzing and limits as the Jint backend.

Measured on 2026-10-23: a 40-case battery of ordinary scripts passes 4 on this engine and 40 on the Jint backend.
