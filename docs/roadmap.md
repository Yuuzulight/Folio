# Roadmap

Verification refresh (2026-10-28): the latest serialized Release solution run passed 4,174 tests with 0 failures and 7 documented skips (4,181 discovered). The mandatory Folio-owned replacement track and the remaining native-backend gates remain open.

Folio's end goal is parity with how a modern browser shows and runs AI-written HTML artifacts. Milestones are in order; each one ends when its acceptance criteria are met. Pass rates refer to the conformance suite and its categories (S1–S6 static, D1–D7 scripted), defined in [study 19](study/19-testing.md). Until a milestone lands, artifacts that need it are routed to the system browser by `ArtifactClassifier`.

The completed M0–M5 requirement-to-evidence index is maintained in
[`milestones-0-5-audit.md`](milestones-0-5-audit.md). It does not supersede the
open M6 acceptance audit or the mandatory M7 replacement gate.

## 0. Study and architecture

**Scope**: how each part of an engine works, written up as Folio's own design: [`docs/study/`](study/), [`architecture.md`](architecture.md), [`dependencies.md`](dependencies.md), this roadmap.

**Acceptance criteria**

- One study document per subsystem with spec references, pitfalls, options and a decision.
- Architecture with pipeline, project layout, public API sketch, threading model, error handling and the detailed M1 scope.
- Open questions answered by the project owner.

Project-owner decisions recorded in the design set: Folio owns parsing, DOM,
style, layout, display-list construction and policy; operating-system services
remain behind Folio interfaces; the content process is the script boundary;
the classifier fails closed to the system browser; and the all-Folio
replacement track is mandatory before release. These decisions are expanded
in [architecture.md](architecture.md), [dependencies.md](dependencies.md),
and [study 17](study/17-scripting.md).

## 1. Static rendering

**Scope**: HTML parsing, DOM, CSS parsing and selector matching, cascade with custom properties and `calc()`, box tree, block/inline/flex/grid/table layout, positioning and overflow, text shaping with font fallback, emoji and bidi, Folio-owned PNG, JPEG, GIF, WebP, BMP and ICO decoding, painting of backgrounds (including gradients), borders, radii, box and text shadows, images and text through SkiaSharp. `FolioView` (display, relayout on resize, root-page scrolling with wheel and scrollbar, clickable links through a host callback that by default opens the system browser) and the headless render-to-image API. `ArtifactClassifier`. The test and CI infrastructure (reftests, golden images with tolerance, approve workflow, diff images on PRs, Linux and Windows runners), the benchmark harness and the first fuzz harnesses.

**Acceptance criteria**: the list in [architecture.md → Milestone 1 in detail](architecture.md#milestone-1-in-detail-static-rendering), in short:

Current audit: S1 and S3 conformance are 12/12 each with no crashes, hangs or limit hits. The authoritative Release benchmark also meets the 50 ms first-paint target for `S3/bug-tracker-export` (33.4 ms median) and `S3/employee-directory-long` (25.4 ms median).

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

Current audit: S1–S6 each pass 12/12 (100%) with no crashes, hangs, limit hits or expected failures. Focused Release
verification passes 140 typography/SVG/allowlist tests and 123 reftest/headless/font tests; the SVG and font fuzz
targets completed 8,195 and 102,766 mutations respectively with zero failures. Animation timeline and reduced-motion
coverage are exercised by the animation, cascade and WinForms host tests. Milestone 2 is complete.

## 3. Interaction

**Scope**: nested scroll containers, wheel routing and chaining, keyboard scrolling, smooth scrolling, sticky behaviour under scroll; hover, active and focus states with targeted restyle; link activation events; text selection (drag, word, paragraph, select all) and copy; focus navigation and `:focus-visible`; forms and text input (text fields, `textarea`, checkboxes, radios, range, select drop-downs, buttons, `details`/`summary`) with IME; tooltips and cursors; basic accessibility tree; incremental restyle, relayout and repaint ([study 14](study/14-invalidation.md)).

**Acceptance criteria**

- Interaction scripts for static artifacts (hover, click links, select and copy, type into forms, scroll nested containers) pass in S1–S6.
- Incremental-equivalence check passes on the whole corpus and under mutation fuzzing.
- Hover restyle and scroll frame targets met.
- The accessibility tree exposes headings, links, lists, tables, text and form controls with names and values.

Current audit note: the interaction behavior, accessibility tree, incremental-equivalence checks and mutation-fuzz coverage pass. Animated documents use targeted subtree restyling on state changes, display-list item and layer bounds are cached between replays, bounded off-screen layers are skipped during damage-region replay, inline declarations are cached during restyle passes, glyph-run damage comparison is semantic rather than backing-array identity, state-only restyles skip unaffected descendant walks when inherited groups are unchanged, and the WinForms view preserves a bitmap for damage-region hover repaints. The benchmark consumes the authoritative `Document.LastDamage` computed by the production host path and uses the production `HybridShaper`; the latest three-iteration run measured 6.5 ms median typical animation (8.2 ms p95), 4.0 ms median hover repaint (5.0 ms p95), and 6.7 ms median scroll (7.7 ms p95), all within the stated budgets. Milestone 3 is complete.

## 4. Scripting foundations

**Scope**: the sandboxed content process (Job Object limits, no-network token, IPC, watchdog), the JavaScript engine behind `IScriptEngine` (Jint in this milestone), the HTML event loop, generated DOM bindings: DOM core, events, element styles, `getComputedStyle`, geometry and scrolling APIs, timers, promises, animation frames, `ResizeObserver`, `IntersectionObserver`, storage, `console`, and the other APIs listed for M4 in [study 17](study/17-scripting.md). `ArtifactClassifier` learns to recognise artifacts M4 can run.

**Acceptance criteria**

- D1 (vanilla interactive widgets) ≥ 90% pass, including interaction scripts.
- Sandbox tests pass: network access, file access and child processes are blocked; memory caps and infinite loops end in a killed content process and a "script stopped" notice, never a hung host.
- IPC protocol fuzz harness clean.
- Scripted documents keep the static rendering targets for first paint when their scripts are small.

## 5. Scripted parity

**Scope**: `MutationObserver`, the CSS object model (`document.styleSheets`, `insertRule`, `CSS.supports`), canvas 2D (full context API, `Path2D`, shared-memory frames to the host), restricted `fetch`, script loading from the host's allowlisted CDN origins with integrity pins and cache, custom elements and shadow DOM if the corpus needs them. Folio's own diagram renderer for diagram-description languages, used instead of running a large diagram library in the sandbox. Component code written with JSX arrives already compiled by the host. The dependency-backed engine is sufficient for this milestone, but it must later be replaced by the required Folio-owned engine track.

**Acceptance criteria**

- D2–D7 each ≥ 90% pass, including interaction scripts.
- D5 and D7 canvas animations reach p95 frame time ≤ 16.7 ms.
- DOM binding tests pass on the production engine; library artifacts start within the scripted start-up targets set from M4 measurements.
- D6 diagram artifacts render through `Folio.Diagrams` without executing the diagram library.
- Categories that miss their bar are documented and routed to the system browser by `ArtifactClassifier`.

**Audit result (2026-10-28):** The dependency-backed D2–D7 corpus still passes 60/60, and the focused scripting, canvas, fetch, CSSOM, observer, library and JSX checks pass. The latest Release 30-frame D5/D7 canvas benchmark keeps every library-card artifact and the snake game below the 16.7 ms p95 target, but `D5/canvas-animated-bars` remains above it at 38.13–43.48 ms total p95 across three fresh local runs; issue #519 stays open. Results vary materially between runs, so benchmark stability and optimization both need work. Diagram tests pass through `Folio.Diagrams`, and the pinned library cache/integrity tests pass. The scripted-parity milestone remains open pending the frame-time gate, in addition to the mandatory Folio-owned script-engine replacement.

## 6. Beyond artifacts

**Scope**: the web features the scripting study left out of milestones 4 and 5, planned in this order: WebGL 2 ([#169](https://github.com/Yuuzulight/Folio/issues/169)), audio and video playback through the operating system's media stack ([#170](https://github.com/Yuuzulight/Folio/issues/170)), dedicated workers ([#171](https://github.com/Yuuzulight/Folio/issues/171)), WebAssembly on the native script engine ([#172](https://github.com/Yuuzulight/Folio/issues/172)), iframes and cross-document messaging ([#173](https://github.com/Yuuzulight/Folio/issues/173)), WebRTC ([#174](https://github.com/Yuuzulight/Folio/issues/174)) and service workers ([#175](https://github.com/Yuuzulight/Folio/issues/175)). Each one is a capability the host grants; devices (camera, microphone) and the network stay off unless the host allows them.

Milestone 6 is in progress. The [2026-10-18 acceptance audit](milestone-6-audit.md) checks all 45 original milestone issues and records implementation gaps. The tracker now keeps the nine partial/incomplete child issues open (#527, #530, #531, #533, #535, #537, #545, #554 and #556); acceptance-verified slices remain closed. Existing capability gates and API tests do not establish complete GPU/GLSL execution, moving video and audio IPC, native WebAssembly, permitted device/network capture, or service-worker isolation and offline reopening. These remain Milestone 6 requirements, independently of later Folio-owned dependency replacements.

**Acceptance criteria**

- Each feature passes its own conformance category, added to the corpus when the feature starts.
- Sandbox, memory-cap and watchdog tests still pass with the feature on; a host that turns a feature off gets a clear fallback, never a blank area.
- GPU and media resources stay within budgets the host sets.
- Until a feature lands, `ArtifactClassifier` keeps routing artifacts that need it to the system browser.

## 7. Folio-owned replacements

This is a required phase after the dependency-backed product and Milestone 6 feature work. Folio must progressively replace the dependency-backed shaper, rasteriser, GIF/WebP decoders, JavaScript engine and WebAssembly runtime with Folio-owned implementations behind the existing interfaces. The replacement order is incremental, with the mature dependency remaining as a differential-test oracle until each replacement reaches parity. Scope and "good enough" criteria are in [dependencies.md → Required Folio-owned replacement track](dependencies.md#required-folio-owned-replacement-track).

This phase is a mandatory, non-waivable completion gate, not an optional follow-up or stretch goal. Folio is not finished, and the roadmap is not complete, until every listed replacement has passed its acceptance criteria and the corresponding production dependency has been removed. The exact release rule and evidence requirements are recorded in [`replacement-gate.md`](replacement-gate.md).

**Current status:** in progress and incomplete. Hybrid shaping covers the proven
simple-script set plus Hebrew base and isolated-mark runs; multiple-mark Hebrew and
other complex scripts still use the differential shaper. GIF and WebP decoding are
Folio-owned and production-wired with differential/fuzz evidence; bounded BMP and
ICO decoding are also Folio-owned slices. Rasterisation and the JavaScript engine
remain dependency-backed. CFF2 now has a Folio-owned parser path, a pinned
two-glyph fixture and software-raster regressions, including Skia differential and
malformed-limit checks for both OpenType and WOFF2; CFF2 variation-font support and
a broader malformed/limit corpus remain. This phase
cannot be marked complete until all five production replacement tracks are complete and their superseded production dependencies are removed.

**Acceptance criteria**

- Every targeted dependency has a Folio-owned implementation selected by the production backend.
- Differential conformance, golden, fuzz, performance and failure-behaviour tests match the dependency-backed baseline.
- The dependency is removable from the production build without reducing supported features or safety guarantees.
- No replacement is considered complete based only on compilation or unit-test coverage; corpus and runtime evidence are required.
