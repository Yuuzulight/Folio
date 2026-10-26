# Milestones 0–5 acceptance audit

Verification refresh (2026-10-26): a complete Release solution run passed 4,064 tests with 0 failures and 7 documented skips (4,071 discovered). Earlier totals are historical snapshots.

Latest solution verification after native class/prototype, event-handler receiver, and constructed-member repairs: 4,071 tests discovered, 4,064 succeeded, 0 failed, and 7 documented skips. Earlier counts below are historical snapshots.

This is the requirement-to-evidence index for the completed pre-M6 milestones. It
does not waive the M6 acceptance audit or the mandatory Folio-owned replacement
gate in [replacement-gate.md](replacement-gate.md).

| Milestone | Requirement group | Current evidence | Result |
|---|---|---|---|
| 0 — Study and architecture | Subsystem studies, pipeline, public API, threading, error handling, M1 scope | `docs/study/01`–`19`, `docs/architecture.md`, `docs/dependencies.md`, `docs/roadmap.md` | Verified |
| 1 — Static rendering | HTML/DOM/CSS, layout, text, painting, resources, host view, reftests, fuzzing and CI | `docs/roadmap.md` M1 acceptance section; `Folio.Tests`, `Folio.RenderTests`, `Folio.Fuzz`, `Folio.WinForms.Tests`; full Release solution run | Verified; Release benchmark closes the prior Debug-only warning |
| 2 — Static parity | transforms, filters, SVG, fonts, animation, selectors and static corpus parity | `docs/roadmap.md` M2 acceptance section; S1–S6 conformance 12/12 each; 140 focused typography/SVG/allowlist tests; 123 focused reftest/headless/font tests; SVG and font fuzzing (0 failures) | Verified |
| 3 — Interaction | scrolling, input, focus, selection, forms, accessibility, invalidation and incremental equivalence | `docs/roadmap.md` M3 audit note; 115 focused interaction/accessibility/invalidation tests; 164 incremental-equivalence tests; S1–S6 interaction scripts and three-iteration performance evidence | Verified |
| 4 — Scripting foundations | sandbox/content process, event loop, DOM bindings, timers/promises, observers, storage and classifier routing | `docs/milestone-4-scripting.md`; D1 2/2; 116 focused scripting/process tests; 4 focused WinForms watchdog/lifecycle tests; IPC fuzz 503,085 mutations with 0 failures | Verified |
| 5 — Scripted parity | mutation observers, CSSOM, canvas, fetch, diagrams, JSX/library artifacts and D2–D7 corpus | `docs/roadmap.md` M5 audit result; D2–D7 60/60; canvas frame p95 ≤ 4.01 ms; 370 focused scripting tests passed with 6 documented software-backend skips; 26 diagram and 17 cache/golden tests passed | Verified as dependency-backed product milestone; Folio-owned engine replacement remains mandatory |

The M5 result is intentionally qualified: its acceptance is complete for the
dependency-backed product, while replacement of the dependency-backed scripting
engine is a separate mandatory M7 requirement. M6 and M7 must not be inferred
complete from this table.

The repository-wide recheck on 2026-10-26 found no regressions in these
completed pre-M6 milestones: the solution builds successfully with one existing
nullable warning in the benchmark project, and the complete Release solution test
run passes 4,064 tests with zero failures and seven documented skips.

