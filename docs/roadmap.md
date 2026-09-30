# Roadmap

Folio's end goal is parity with how a modern browser shows and runs AI-written HTML artifacts. Milestones are in order; each one ends when its acceptance criteria are met. Pass rates refer to the conformance suite and its categories (S1–S6 static, D1–D7 scripted), defined in [study 19](study/19-testing.md). Until a milestone lands, artifacts that need it are routed to the system browser by `ArtifactClassifier`.

## 0. Study and architecture

**Scope**: how each part of an engine works, written up as Folio's own design: [`docs/study/`](study/), [`architecture.md`](architecture.md), [`dependencies.md`](dependencies.md), this roadmap.

**Acceptance criteria**

- One study document per subsystem with spec references, pitfalls, options and a decision.
- Architecture with pipeline, project layout, public API sketch, threading model, error handling and the detailed M1 scope.
- Open questions answered by the project owner.

## 1. Static rendering

**Scope**: HTML parsing, DOM, CSS parsing and selector matching, cascade with custom properties and `calc()`, box tree, block/inline/flex/grid/table layout, positioning and overflow, text shaping with font fallback, emoji and bidi, PNG and JPEG decoding, painting of backgrounds (including gradients), borders, radii, box and text shadows, images and text through SkiaSharp. `FolioView` (display, relayout on resize, root-page scrolling with wheel and scrollbar, clickable links through a host callback that by default opens the system browser) and the headless render-to-image API. `ArtifactClassifier`. The test and CI infrastructure (reftests, golden images with tolerance, approve workflow, diff images on PRs, Linux and Windows runners), the benchmark harness and the first fuzz harnesses.

**Acceptance criteria**: the list in [architecture.md → Milestone 1 in detail](architecture.md#milestone-1-in-detail-static-rendering), in short:

- CI green on Linux and Windows with unit, dump, Unicode conformance, reftests and golden tests; approve workflow and diff artifacts working.
- Every M1 CSS feature covered by a test.
- The simple static categories, S1 and S3, each ≥ 95% pass; no crash, hang or limit hit anywhere in the corpus.
- Time and memory targets met for S1 and S3.
- No network access possible with default options; fuzzers running with no open crash bugs.
- Mana's "Open in Mana" window can use `FolioView`.

## 2. Static parity

**Scope**: 2D transforms and individual transform properties, filters and `backdrop-filter`, blend modes, `clip-path`, masks, `border-image`; CSS transitions and animations with a real timeline (paint-only fast path for opacity/transform/filter/colour); web fonts (`@font-face` with WOFF/WOFF2, `unicode-range`, `font-display`) from `data:` URLs, local folders or the host's allowlisted font and style origins with cache and integrity checks; SVG ([study 13](study/13-svg.md): inline and as images, shapes, paths, text, gradients, clipping, markers, `use`, then masks, patterns, `foreignObject`, filter primitives); `:has()` matching; `subgrid`; `@property` syntax checking; any M1 gaps the corpus exposes (for example `::first-letter`, multi-column).

**Acceptance criteria**

- All static categories S1–S6 each ≥ 98% pass against the reference images ("most artifacts look identical").
- Paint-only animation frames within the M2 target; animations respect `prefers-reduced-motion` from the OS.
- SVG reftests and goldens pass; SVG and font fuzz harnesses added and clean.
- Web font loading honours the allowlist, cache and integrity rules; blocked fonts fall back without layout errors.

## 3. Interaction

**Scope**: nested scroll containers, wheel routing and chaining, keyboard scrolling, smooth scrolling, sticky behaviour under scroll; hover, active and focus states with targeted restyle; link activation events; text selection (drag, word, paragraph, select all) and copy; focus navigation and `:focus-visible`; forms and text input (text fields, `textarea`, checkboxes, radios, range, select drop-downs, buttons, `details`/`summary`) with IME; tooltips and cursors; basic accessibility tree; incremental restyle, relayout and repaint ([study 14](study/14-invalidation.md)).

**Acceptance criteria**

- Interaction scripts for static artifacts (hover, click links, select and copy, type into forms, scroll nested containers) pass in S1–S6.
- Incremental-equivalence check passes on the whole corpus and under mutation fuzzing.
- Hover restyle and scroll frame targets met.
- The accessibility tree exposes headings, links, lists, tables, text and form controls with names and values.

## 4. Scripting foundations

**Scope**: the sandboxed content process (Job Object limits, no-network token, IPC, watchdog), the JavaScript engine behind `IScriptEngine` (Jint in this milestone), the HTML event loop, generated DOM bindings: DOM core, events, element styles, `getComputedStyle`, geometry and scrolling APIs, timers, promises, animation frames, `ResizeObserver`, `IntersectionObserver`, storage, `console`, and the other APIs listed for M4 in [study 17](study/17-scripting.md). `ArtifactClassifier` learns to recognise artifacts M4 can run.

**Acceptance criteria**

- D1 (vanilla interactive widgets) ≥ 90% pass, including interaction scripts.
- Sandbox tests pass: network access, file access and child processes are blocked; memory caps and infinite loops end in a killed content process and a "script stopped" notice, never a hung host.
- IPC protocol fuzz harness clean.
- Scripted documents keep the static rendering targets for first paint when their scripts are small.

## 5. Scripted parity

**Scope**: `MutationObserver`, the CSS object model (`document.styleSheets`, `insertRule`, `CSS.supports`), canvas 2D (full context API, `Path2D`, shared-memory frames to the host), restricted `fetch`, script loading from the host's allowlisted CDN origins with integrity pins and cache, custom elements and shadow DOM if the corpus needs them. The switch from Jint to a native JIT-compiling engine inside the content process (a backend swap behind `IScriptEngine`). Folio's own diagram renderer for diagram-description languages, used instead of running a large diagram library in the sandbox. Component code written with JSX arrives already compiled by the host.

**Acceptance criteria**

- D2–D7 each ≥ 90% pass, including interaction scripts.
- D5 and D7 canvas animations reach p95 frame time ≤ 16.7 ms.
- DOM binding tests pass on both engines; library artifacts start within the scripted start-up targets set from M4 measurements.
- D6 diagram artifacts render through `Folio.Diagrams` without executing the diagram library.
- Categories that miss their bar are documented and routed to the system browser by `ArtifactClassifier`.

## Optional track: towards all-Folio

Replacing dependencies with Folio's own code (extended shaper, own rasteriser, own GIF/WebP decoders) is optional and never blocks the milestones above. Scope and "good enough" criteria are in [dependencies.md → Towards all-Folio](dependencies.md#towards-all-folio).
