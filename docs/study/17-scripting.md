# Scripting: sandbox model and the web API surface (M4–M5)

## Target

AI-written artifacts use scripts in a few recurring ways, and scripted parity (M5) means running these correctly:

- plain DOM scripting: tabs, accordions, calculators, filters, sortable tables, small games;
- declarative component UI libraries delivered as a single-file script;
- utility-CSS generators that scan class names at run time and inject CSS (they need `MutationObserver` and the CSS object model);
- charting libraries drawing to SVG or to canvas;
- diagram generators turning text into SVG;
- canvas 2D animations and games at 60 fps.

## What the specs require

- [ECMAScript](https://tc39.es/ecma262/) (the language; provided by the engine dependency).
- [HTML: scripting and the event loop](https://html.spec.whatwg.org/multipage/webappapis.html#event-loops) (task queues, microtasks, "update the rendering" steps: animation frame callbacks, resize/intersection observers, style and layout, paint), [timers](https://html.spec.whatwg.org/multipage/timers-and-user-prompts.html#timers), [`requestAnimationFrame`](https://html.spec.whatwg.org/multipage/imagebitmap-and-animations.html#animation-frames), [Web Storage](https://html.spec.whatwg.org/multipage/webstorage.html), [the canvas element](https://html.spec.whatwg.org/multipage/canvas.html), [fragment parsing for `innerHTML`](https://html.spec.whatwg.org/multipage/parsing.html#parsing-html-fragments).
- [WHATWG DOM](https://dom.spec.whatwg.org/) (nodes, events, ranges, `MutationObserver`), [CSSOM](https://www.w3.org/TR/cssom-1/) (`document.styleSheets`, `insertRule`, `CSSStyleDeclaration`, `getComputedStyle`), [CSSOM View](https://www.w3.org/TR/cssom-view-1/) (geometry, scrolling, `matchMedia`), [Resize Observer](https://www.w3.org/TR/resize-observer/), [Intersection Observer](https://www.w3.org/TR/intersection-observer/), [WebIDL](https://webidl.spec.whatwg.org/) (binding rules: type conversions, exceptions).

## Hard parts and pitfalls

- **Isolation**: a script must not reach the host process, the file system, the network or other documents, and must not be able to hang or exhaust the host.
- **Synchronous layout reads** (`offsetWidth`, `getBoundingClientRect`) need DOM, style and layout next to the script engine, in the same process, with no round trip.
- **JavaScript performance bar**: library bundles are hundreds of KB to a few MB of minified code. A tree-walking interpreter is typically one to two orders of magnitude slower than a JIT-compiling engine. Plain DOM scripting and small charts are fine in an interpreter; a large component-UI bundle may take seconds to start, and canvas animations that draw thousands of shapes per frame will not reach 60 fps.
- **Binding surface**: thousands of methods and attributes, each with WebIDL conversion rules. Libraries feature-detect, so a half-implemented API is worse than a missing one.
- **Determinism for tests**: time, `Math.random`, animation frames.

## Options

**Where scripts run**

| Option | Pros | Cons |
|---|---|---|
| A. In the host process, engine-level limits only | Simplest; no IPC | A bug in the engine or a binding exposes the host; memory limits are approximate; one runaway script can stall the host UI thread |
| B. A **content process** per scripted document that owns the DOM, style, layout and the script engine; the host process draws display lists it receives and forwards input | OS-enforced limits (memory, CPU, no network, no child processes); a crash or hang kills only the content process; layout reads stay synchronous inside the content process | One extra process (runtime baseline memory); display lists and input cross an IPC boundary |
| C. Out-of-process script engine only, DOM stays in the host | Smaller content process | Every DOM call is an IPC round trip; unusable for real libraries |

**Engine**

| Option | Pros | Cons |
|---|---|---|
| A. Jint (pure .NET interpreter) | No native code; built-in time, statement, recursion and memory constraints; easy to bind .NET objects; runs anywhere .NET runs | Interpreter speed; the performance bar above |
| B. A native JIT-compiling engine through a .NET binding | Browser-class speed for heavy libraries and canvas animation | Native dependency and binaries per architecture; larger memory; binding work |
| C. Folio's own compiler from JavaScript to .NET IL | Fast without native code | A second large project; not justified now |

## Decision

- **Process model: option B.** Static documents (no scripts, or scripting disabled by the host) stay in-process exactly as in M1–M3. A document the host allows to run scripts is loaded in `Folio.ContentHost.exe`, a small .NET executable shipped with Folio:
  - Started inside a **Windows Job Object**: memory cap (default 256 MB), CPU rate cap, no child processes, kill-on-close, UI restrictions (no clipboard, no global atoms, no desktop switching).
  - Runs with a **low-integrity / AppContainer token without network capability**, so the OS blocks sockets; allowlisted loads are performed by the host's `IResourceLoader` on the content process's behalf.
  - Talks to the host over a pipe with a versioned, length-prefixed binary protocol. Host → content: load, resize, input events, scroll, resource responses, focus, clipboard paste. Content → host: display lists (fonts referenced by id; web font bytes sent once), resource requests, diagnostics, cursor, tooltip, title, link activation, copy text, form submission.
  - Canvas bitmaps travel through shared memory sections, not through the pipe, so canvas animation can reach 60 fps.
  - A heartbeat watchdog in the host: no response within the limit → kill the process, keep the last frame with a "script stopped" notice.
- **Engine: option A through M4, option B in M5, both behind `IScriptEngine`.** M4 uses Jint with a per-task time limit, statement limit, recursion limit and memory limit; the Job Object is the hard guarantee. M5 moves to a native JIT-compiling engine inside the same content process; this is planned M5 work, not a conditional fallback. `IScriptEngine` (evaluate, call, create host objects, microtask checkpoint, interrupt, limits) is designed so the switch is a backend swap: DOM bindings, the event loop and the IPC protocol do not change, and the bindings are tested against both engines. Because the sandbox is the process, the native engine does not weaken the host's security.
- **Honest performance policy**: the conformance suite ([testing](19-testing.md)) measures each scripted category against its performance bar (start-up time and frame time). Until M5 lands, and afterwards for any category that still misses its bar, those artifacts are **routed to the system browser** by the host. Folio provides `ArtifactClassifier.Classify(html) → Static | Scripted | NeedsBrowser` (script sources, APIs referenced, origins not on the allowlist, known heavy patterns) so the host can decide before loading.
- **Web API surface, M4 (scripting foundations)**: DOM core (`Node`/`Element`/`Document`/`Text`/`DocumentFragment`, tree mutation, attributes, `classList`, `dataset`, `innerHTML`/`outerHTML`/`textContent`/`innerText`, `querySelector(All)`, `closest`, `matches`, `template`), events (`EventTarget` with options, `CustomEvent`, mouse/pointer/keyboard/input/focus/wheel/submit/change events, `preventDefault`, delegation), element `style`, `getComputedStyle`, geometry and scroll APIs, `elementFromPoint`, `matchMedia`, timers, `queueMicrotask`, promises, `requestAnimationFrame`, `ResizeObserver`, `IntersectionObserver`, `localStorage` (persisted per artifact through a host-provided `IArtifactStorage`: local, isolated per artifact id, capped at 5 MB by default; in memory only if the host provides none), `sessionStorage` (in memory), `console` → diagnostics, `JSON`, `URL`, `URLSearchParams`, `TextEncoder`/`TextDecoder`, `atob`/`btoa`, `crypto.getRandomValues`/`randomUUID`, `performance.now`, `structuredClone`, form control APIs.
- **M5 (scripted parity)** adds: `MutationObserver`, the CSS object model (`document.styleSheets`, `CSSStyleSheet` with `insertRule`/`deleteRule`/`cssRules`, `<style>` text mutation, `CSS.supports`), canvas 2D (`CanvasRenderingContext2D` in full including text, images, gradients, patterns, transforms, `getImageData`/`putImageData`, `Path2D`), `fetch` for `data:` URLs and allowlisted origins only, script loading from allowlisted CDNs ([security](16-resources-and-security.md)). Custom elements and shadow DOM are added only if the corpus shows libraries that need them.
- **JSX is compiled by the host**: artifacts written as component code with JSX reach Folio as plain JavaScript; the host (Mana) runs the JSX compilation before handing content over. Folio never runs an in-page JSX transpiler.
- **Diagrams are rendered natively (M5)**: artifacts that load a large diagram library only to turn diagram-description text (flowcharts, sequence, class, state, entity-relationship, timeline and pie diagrams) into SVG are recognised by `ArtifactClassifier` (the library's pinned URL in the host's allowlist configuration plus the diagram text blocks). Folio's own diagram renderer (`Folio.Diagrams`) parses the description languages and inserts SVG into the DOM; the library is not executed in the sandbox. Diagram syntax the renderer does not support falls back to running the library.
- **Planned after milestone 5** ([roadmap stage 6](../roadmap.md#6-beyond-artifacts)): WebGL, media playback, workers, WebAssembly (on the native script engine), iframes and cross-document messaging, WebRTC and service workers, in that order, each behind a capability the host grants.
- **Event loop**: Folio's own implementation of the HTML event loop inside the content process, driven by the host's display refresh for "update the rendering". Test mode uses a virtual clock and seeded `Math.random`.
- **Bindings** are generated from Folio's own WebIDL-style interface descriptions into C# (a build-time tool), so conversions and exceptions follow WebIDL uniformly.
