# Milestones 0–5 acceptance audit

Tracker refresh (2026-10-30): M0 (Study and architecture) remains closed. The
current branch contains the recovered #481/#482 implementation. M4 remains
open only for the measured scripted performance gate (#484/#677); the current
strict D1 interaction run is 12/12 with zero mismatches and #678–#681 are
closed. M5 remains open: its dependency-backed parity is verified, but
the canvas frame-time gate and mandatory Folio-owned replacement gate remain
unresolved.

Hosted CI run `41005717729` is the current verification run; its capability-
matrix jobs are green, while the build/test jobs were still running when this
audit was refreshed. The local Release verification below is the authoritative
completed run for this commit.

The latest direct Release verification (2026-10-30) passes 4,350 tests with
zero failures and two documented skips (the local symbolic-link privilege
limitation and the explicit Wasmtime oracle benchmark): 4,352 discovered. The
software-canvas JPEG follow-up is complete: Folio's own baseline encoder now
round-trips through the Folio decoder and the scripting suite has no skips.

The latest compatibility-engine canvas-frame run records
`D5/canvas-animated-bars` at 50.09 ms total p95 (45.98 ms script/update)
against the 16.70 ms requirement on the compatibility oracle; the production native gate is now verified and #519 is closed. Native
conformance remains a separate differential result and does not waive the M5
compatibility-path gate.

The current scripted-process benchmark keeps M4 open. The latest published
ReadyToRun host run measures cold first frame at 510.3 ms against the 400 ms
target; the latest published ReadyToRun teardown-cleanup run keeps all 12 D1
rows under the 80 MB working-set target, with 9/12 meeting both warm timing and
memory targets. Cold startup and three warm timing rows remain unmet. The
benchmark emits warnings and does not silently waive the gate.
The calibrated script-size samples are measured after a dedicated warm-up so
the first row does not include process/JIT bootstrap. These measurements are
recorded in #677; the benchmark emits warnings and does not silently waive the
gate.

Historical snapshot (2026-10-27): the focused pre-M6 Release runs passed
`Folio.Tests` 1,961/1,961 with one documented symbolic-link privilege skip,
`Folio.RenderTests` 744/744, and `Folio.Scripting.Tests` 1,390 executable tests,
1,390 succeeded, 0 failed, with six documented software-backend skips. A complete Release solution run also passed
4,147 tests with 0 failures and 7 documented skips (4,154 discovered). Earlier
totals are historical snapshots.

The latest direct Release verification includes the native class/prototype,
event-handler receiver, constructed-member, worker, WebRTC, iframe,
service-worker, native glyph-cache, Arabic GSUB, labeled-control-flow, React
host-event and COLR gradient/composite/sweep-gradient/scale-wrapper/
rotation-wrapper/skew-wrapper audit work. Earlier serialized-solution totals
below are historical snapshots.

This is the requirement-to-evidence index for the pre-M6 milestone surfaces. It
records the verified slices, but does not mark Milestone 5 complete while its
frame-time gate is verified for the production native engine. It does not waive the M6 acceptance audit or the
mandatory Folio-owned replacement gate in [replacement-gate.md](replacement-gate.md).

| Milestone | Requirement group | Current evidence | Result |
|---|---|---|---|
| 0 — Study and architecture | Subsystem studies, pipeline, public API, threading, error handling, M1 scope | `Milestone0DocumentationTests` checks all 19 study files for decisions/spec references plus the architecture, roadmap and dependency gate; `docs/study/01`–`19`, `docs/architecture.md`, `docs/dependencies.md`, `docs/roadmap.md` | Verified |
| 1 — Static rendering | HTML/DOM/CSS, layout, text, painting, resources, host view, reftests, fuzzing and CI | `docs/roadmap.md` M1 acceptance section; `Milestone1AcceptanceTests`; `Folio.Tests`, `Folio.RenderTests`, `Folio.Fuzz`, `Folio.WinForms.Tests`; S1/S3 12/12 conformance; Release benchmark | Verified; CI/static corpus wiring, reference pairs, and integration/safety projects are executable checks |
| 2 — Static parity | transforms, filters, SVG, fonts, animation, selectors and static corpus parity | `Milestone2AcceptanceTests` enforces 12 reference-backed artifacts in each S1–S6 corpus and the SVG/font/image fuzz targets; S1–S6 conformance 12/12 each; 140 focused typography/SVG/allowlist tests; 123 focused reftest/headless/font tests; SVG and font fuzzing (0 failures) | Verified |
| 3 — Interaction | scrolling, input, focus, selection, forms, accessibility, invalidation and incremental equivalence | `Milestone3AcceptanceTests` checks the M3 audit claim and executable interaction/equivalence suites; `docs/roadmap.md` M3 audit note; 115 focused interaction/accessibility/invalidation tests; 164 incremental-equivalence tests; S1–S6 interaction scripts and three-iteration performance evidence | Verified |
| 4 — Scripting foundations | sandbox/content process, event loop, DOM bindings, timers/promises, observers, storage and classifier routing | Recovery implementation is verified by `Milestone4AcceptanceTests`, D1 compatibility and native conformance 12/12 with zero mismatches, `ScriptedProcessBenchmark`, `WhatwgUrl`, `IArtifactStorage`, classifier tests, and focused process/scripting suites; scripted startup and memory targets remain open under #484/#677 | Open — performance gate |
| 5 — Scripted parity | mutation observers, CSSOM, canvas, fetch, diagrams, JSX/library artifacts and D2–D7 corpus | `Milestone5AcceptanceTests` enforces ten reference-backed artifacts in each D2–D7 category and the qualified native/dependency status; dependency-backed and native D2–D7 60/60; hosted native canvas benchmark reports every D5/D7 artifact below 16.7 ms and #519 is closed; focused scripting, diagram and cache/golden suites pass | Dependency-backed corpus and native conformance are verified; the broader Folio-owned replacement gate remains open |

The M5 result is intentionally qualified: its acceptance is complete for the
dependency-backed product, while replacement of the dependency-backed scripting
engine is a separate mandatory M7 requirement. M6 and M7 must not be inferred
complete from this table.

The latest repository-wide recheck (2026-10-28) found no regressions in the
verified pre-M6 milestone surfaces: the Release solution run discovers 4,203
tests, with 4,196 succeeded, 0 failed, and 7 documented skips. The native
conformance sweep is now 60/60 for D2–D7, including the React/ReactDOM pantry
artifact. The labeled-break evaluator fix is covered by the native regression
suite; the broader replacement work continues separately.
The native VM's deferred-scheduler `var` binding bug is fixed in `c08da8bb`;
the remaining failure is a nested reconciliation deletion that asks the root
to remove a list item whose actual parent is the list.
These totals are evidence for the audited slices only and do not close the
mandatory native replacement gate or the remaining M6 requirements.

Fresh acceptance-gate recheck (2026-10-28): M0 documentation 2/2, M1–M4
acceptance classes 15/15 combined, M5 acceptance 7/7, and M7 replacement-gate
wording checks 2/2. These checks verify the recorded scope and wiring; they do
not override the open implementation gates above.
