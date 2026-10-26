# Milestones 0–5 acceptance audit

Latest solution verification after native service-worker controller interception, typed-array bridge coverage, worker-scope isolation, native worker message delivery, imported native funcref/externref bridging, brokered worker isolation, typed page-function table bridging, iframe allowlist parsing, native-engine timeout enforcement, host capture-provider wiring, GPU-reset coverage, iframe message-source/target/port coverage, media switch coverage, reviewed media goldens, starvation-safe host loading, transactional WebGL texture/renderbuffer reservations, retained media-frame budget accounting, transform-feedback capture/readback plus next-draw round trip, signed and unsigned integer vertex-attribute input, signed integer 2D/3D texture sampling, `UNPACK_FLIP_Y_WEBGL` upload handling, pack row/skip readback controls and `clients.claim()` controller wiring in both engines: 3,827 total tests, 3,826 succeeded, 0 failed, and 1 environment-only symbolic-link skip. Earlier counts below are historical snapshots.

This is the requirement-to-evidence index for the completed pre-M6 milestones. It
does not waive the M6 acceptance audit or the mandatory Folio-owned replacement
gate in [replacement-gate.md](replacement-gate.md).

| Milestone | Requirement group | Current evidence | Result |
|---|---|---|---|
| 0 — Study and architecture | Subsystem studies, pipeline, public API, threading, error handling, M1 scope | `docs/study/01`–`19`, `docs/architecture.md`, `docs/dependencies.md`, `docs/roadmap.md` | Verified |
| 1 — Static rendering | HTML/DOM/CSS, layout, text, painting, resources, host view, reftests, fuzzing and CI | `docs/roadmap.md` M1 acceptance section; `Folio.Tests`, `Folio.RenderTests`, `Folio.Fuzz`, `Folio.WinForms.Tests`; full solution run | Verified |
| 2 — Static parity | transforms, filters, SVG, fonts, animation, selectors and static corpus parity | `docs/roadmap.md` M2 acceptance section; typography, SVG, image and render regression suites | Verified |
| 3 — Interaction | scrolling, input, focus, selection, forms, accessibility, invalidation and incremental equivalence | `docs/roadmap.md` M3 audit note; interaction/accessibility/invalidation tests and the three-iteration performance evidence recorded there | Verified |
| 4 — Scripting foundations | sandbox/content process, event loop, DOM bindings, timers/promises, observers, storage and classifier routing | `docs/milestone-4-scripting.md`; `Folio.Scripting.Tests`; process, sandbox, IPC and fuzz coverage | Verified |
| 5 — Scripted parity | mutation observers, CSSOM, canvas, fetch, diagrams, JSX/library artifacts and D2–D7 corpus | `docs/roadmap.md` M5 audit result; D2–D7 60/60, serialized scripting suite 1,146/1,146, and the canvas/library tests | Verified as dependency-backed product milestone |

The M5 result is intentionally qualified: its acceptance is complete for the
dependency-backed product, while replacement of the dependency-backed scripting
engine is a separate mandatory M7 requirement. M6 and M7 must not be inferred
complete from this table.

The repository-wide recheck on 2026-10-26 found no regressions in these
completed pre-M6 milestones: the solution builds with zero warnings and errors,
and the solution test run passes 3,816 tests (3,815 succeeded and one skipped) with one
machine-only symbolic-link skip.

