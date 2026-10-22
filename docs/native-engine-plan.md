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

## Implementation status

- [x] **Stage 1 (Language front end)**: NativeTokenizer, NativeToken, NativeParser, and NativeAst implement explicit AST generation for statements (ar/let/const, if, while, do-while, or, switch/case/default, 
eturn, 	hrow, 	ry-catch-finally, reak, continue), expressions (binary, unary, assignment +=/-=/*=//=, ternary ?:, member, call, new), literals, arrow functions, and closures.
- [x] **Stage 2 (Bytecode and VM)**: NativeOpCode, NativeBytecodeCompiler, BytecodeChunk, CallFrame, NativeVirtualMachine, and NativeValue implement chunked instructions, constant pools, lexical scoping, jump patching, call stacks, microtask checkpoint queues, watchdog interruption, and execution limits.
- [x] **Stage 3 (DOM host layer)**: NativeHostObject and NativeScriptEngine implement IScriptEngine and IScriptEngineFactory, delegating DOM template wrappers (IHostObjectTemplate), ArrayBuffers, and ClampedArrays.
- [x] **Stage 4 (Optimizing execution & standard built-ins)**:
  - Monomorphic Inline Caching (MIC): PropertyInlineCache tracks object versioning and caches property slot lookups.
  - Standard Globals: Math, console, Date, JSON, queueMicrotask, Object (keys, alues, entries, ssign, hasOwnProperty), and Promise (
esolve, 
eject, then-chaining).
  - Built-in Prototypes: Array.prototype (push, pop, shift, unshift, slice, join, indexOf, includes, orEach, map, ilter, 
educe) and String.prototype (charAt, charCodeAt, slice, substring, indexOf, includes, startsWith, endsWith, split, 
eplace, 	oLowerCase, 	oUpperCase, 	rim, string indexing).
- [ ] **Stage 5 (Cutover)**: Complete test coverage and benchmarking before deprecating the Jint fallback.
