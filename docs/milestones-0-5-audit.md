# Milestones 0–5 acceptance audit

Verification refresh (2026-10-27): the focused pre-M6 Release runs passed
`Folio.Tests` 1,961/1,961 with one documented symbolic-link privilege skip,
`Folio.RenderTests` 744/744, and `Folio.Scripting.Tests` 1,368/1,368 with six
documented software-backend skips. A complete Release solution run also passed
4,094 tests with 0 failures and 7 documented skips (4,101 discovered). Earlier
totals are historical snapshots.

Latest solution verification after native class/prototype, event-handler receiver, constructed-member, worker, WebRTC, iframe, and service-worker audit repairs: 4,101 tests discovered, 4,094 succeeded, 0 failed, and 7 documented skips. Earlier counts below are historical snapshots.

This is the requirement-to-evidence index for the completed pre-M6 milestones. It
does not waive the M6 acceptance audit or the mandatory Folio-owned replacement
gate in [replacement-gate.md](replacement-gate.md).

| Milestone | Requirement group | Current evidence | Result |
|---|---|---|---|
| 0 — Study and architecture | Subsystem studies, pipeline, public API, threading, error handling, M1 scope | `docs/study/01`–`19`, `docs/architecture.md`, `docs/dependencies.md`, `docs/roadmap.md` | Verified |
| 1 — Static rendering | HTML/DOM/CSS, layout, text, painting, resources, host view, reftests, fuzzing and CI | `docs/roadmap.md` M1 acceptance section; `Milestone1AcceptanceTests`; `Folio.Tests`, `Folio.RenderTests`, `Folio.Fuzz`, `Folio.WinForms.Tests`; S1/S3 12/12 conformance; Release benchmark | Verified; CI/static corpus wiring, reference pairs, and integration/safety projects are executable checks |
| 2 — Static parity | transforms, filters, SVG, fonts, animation, selectors and static corpus parity | `Milestone2AcceptanceTests` enforces 12 reference-backed artifacts in each S1–S6 corpus and the SVG/font/image fuzz targets; S1–S6 conformance 12/12 each; 140 focused typography/SVG/allowlist tests; 123 focused reftest/headless/font tests; SVG and font fuzzing (0 failures) | Verified |
| 3 — Interaction | scrolling, input, focus, selection, forms, accessibility, invalidation and incremental equivalence | `Milestone3AcceptanceTests` checks the M3 audit claim and executable interaction/equivalence suites; `docs/roadmap.md` M3 audit note; 115 focused interaction/accessibility/invalidation tests; 164 incremental-equivalence tests; S1–S6 interaction scripts and three-iteration performance evidence | Verified |
| 4 — Scripting foundations | sandbox/content process, event loop, DOM bindings, timers/promises, observers, storage and classifier routing | `docs/milestone-4-scripting.md`; D1 2/2; 116 focused scripting/process tests; 4 focused WinForms watchdog/lifecycle tests; IPC fuzz 503,085 mutations with 0 failures | Verified |
| 5 — Scripted parity | mutation observers, CSSOM, canvas, fetch, diagrams, JSX/library artifacts and D2–D7 corpus | `docs/roadmap.md` M5 audit result; dependency-backed D2–D7 60/60; native D2–D7 37/60; the artifact-level canvas benchmark now reports per-stage p95 and exposes misses against 16.7 ms; 370 focused scripting tests passed with 6 documented software-backend skips; 26 diagram and 17 cache/golden tests passed | Dependency-backed product baseline verified; native parity and the Folio-owned replacement gate remain open |

The M5 result is intentionally qualified: its acceptance is complete for the
dependency-backed product, while replacement of the dependency-backed scripting
engine is a separate mandatory M7 requirement. M6 and M7 must not be inferred
complete from this table.

The repository-wide recheck on 2026-10-26 found no regressions in these
completed pre-M6 milestones: the solution builds successfully with one existing
nullable warning in the benchmark project, and the complete Release solution test
run passes 4,094 tests with zero failures and seven documented skips.

