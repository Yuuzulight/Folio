# Testing strategy and the conformance suite

## Goals

1. Each subsystem matches its spec (unit tests and spec-derived reftests).
2. Real artifacts render and behave correctly (the conformance suite; this is how parity is measured).
3. Nothing untrusted can crash, hang or exhaust the host (fuzzing, limits).
4. Performance and memory do not regress (benchmark harness, see [memory and performance](18-memory-and-performance.md)).

## Hard parts and pitfalls

- **Pixel tests are brittle**: antialiasing and font rasterisation change with backend versions and system fonts.
- **System fonts differ between machines** and change with Windows updates.
- **Golden images rot** if updates are accepted without review.
- **Incremental bugs** only show after a specific sequence of changes.
- **Scripted tests** are nondeterministic without control over time and randomness.

## Test layers

| Layer | What | Compared against |
|---|---|---|
| Unit | Tokenizers, parsers, selector matching, cascade, value computation, OpenType reader, image decoders, URL/path handling | Expected values in code |
| Unicode conformance | UAX #14, #9, #29 algorithms | Unicode's published test files (`LineBreakTest.txt`, `BidiTest.txt`, `BidiCharacterTest.txt`, `WordBreakTest.txt`, `GraphemeBreakTest.txt`) |
| Parser dumps | HTML tree construction, CSS parsing | Folio's own spec-derived cases: input + expected tree/rule dump in a plain text format |
| Layout dumps | Fragment tree (box positions and sizes as text) | Expected dumps; stable across rendering backend versions, so most layout tests use these, not pixels |
| Display-list dumps | Paint order, clips, transforms | `RecordingCanvas` text logs |
| Reftests (main proof of spec correctness) | A test page and a reference page that uses simpler features to produce the same picture (e.g. a flex layout vs the same boxes absolutely positioned) | Both rendered by Folio on the same machine, pixels compared; robust across machines and fonts because both sides change together |
| Golden images | Conformance artifacts, and features with no simpler equivalent (text rendering, gradients, shadows, SVG) | Reviewed PNGs stored in the repo, compared with per-test tolerance |
| Conformance suite | Whole artifacts, static and scripted | Goldens + interaction scripts (below) |
| Incremental equivalence | After every incremental update, compare with a full re-render | Fragment tree and display list must be identical |
| Fuzzing | HTML, CSS, fonts, WOFF/WOFF2, PNG, JPEG, SVG/XML, `data:` URLs, IPC messages (M4) | Must not throw, must stay within limits, must satisfy invariants |

## Decisions

- **Test fonts are bundled**, never taken from the system: a Folio-generated "box font" where every glyph is a filled em square with known metrics (makes text layout tests exact and backend-independent), plus a small set of openly licensed text fonts for Latin, CJK, Arabic, Hebrew, Devanagari and colour emoji. Tests run with an `IFontSource` that only sees these fonts.
- **Reftests are written by Folio from the specs**: each test file links the spec section it checks. Directory per spec module (`tests/reftests/css-flexbox/…`). A manifest lists expected-to-fail tests so progress is visible.
- **Two kinds of image test, both from M1.** Reftests are the main way to prove layout and spec correctness: they need no stored images and survive font and backend changes. Golden-image tests cover whole artifacts and the few features with no simpler equivalent.
- **Golden comparison has tolerance built in from the start**: a per-pixel channel threshold (absorbs antialiasing noise) plus a maximum ratio of differing pixels, with defaults and per-test overrides stored in the manifest. On failure the runner writes the actual image, the golden and a highlighted diff image.
- **Approve/update workflow**: goldens change only through an explicit command (`folio-test approve <test|category>`) that copies the actual images over the goldens; the new PNGs are then reviewed in the PR like code. CI never updates goldens.
- **Visual diffs on PRs**: CI uploads before/after/diff images of every changed or failing golden as build artifacts and posts a summary comment listing them, so reviewers see what moved.
- **Rendering determinism**: same input → identical pixels on the same machine and backend version, checked by rendering every reftest twice.
- **Fuzzing**: a coverage-guided fuzzing harness for .NET in a separate test project (tool choice made at the start of M1), plus a grammar-based generator for HTML/CSS that produces valid-but-unusual documents. Invariants checked: no exception escapes the public API, every `Update()` finishes within the limits, fragments have finite sizes, DOM invariants hold, incremental equals full re-render. Crashes become regression tests. Fuzzers run in CI for a short time per commit and longer on a schedule.
- **CI** (part of M1's acceptance criteria): GitHub Actions on **Linux and Windows** runners. The engine and headless renderer target plain `net10.0` and run on both; WinForms control tests run on Windows only. Unit, dump and reftests on every push; golden tests, the conformance suite and benchmarks on every PR; fuzzing on a schedule. Goldens are shared across platforms when differences stay within tolerance; a test may carry a per-platform golden only with a written reason.

## Conformance suite

A corpus of real-world-style single-file HTML artifacts, like the ones AI assistants write, stored in `tests/conformance/`. Each artifact has a manifest entry: category, features used (tags), viewport sizes to test, and for scripted ones an interaction script.

**Sources**: artifacts written by language models from a fixed list of prompts per category, plus artifacts Mana produces in real use (with personal content removed). Library-based artifacts reference their libraries through pinned, integrity-checked URLs served from the test cache (no network in CI).

**Categories**

| Id | Category | Exercises |
|---|---|---|
| S1 | Documents and reports | Headings, lists, code blocks, quotes, tables, links, typography |
| S2 | Dashboards and cards | Flex, grid, custom properties, radii, shadows, gradients |
| S3 | Data tables | Wide tables, sticky headers, zebra striping, overflow scrolling |
| S4 | Landing-style pages | Web fonts, hero sections, gradients, transforms, `backdrop-filter`, CSS animations |
| S5 | Static SVG charts, diagrams and icons | Inline SVG, SVG images, text in SVG |
| S6 | International text | CJK, Arabic/Hebrew with bidi, emoji sequences, mixed scripts |
| D1 | Vanilla interactive widgets | Tabs, accordions, calculators, forms, filters, sortable tables |
| D2 | Component-UI apps | Declarative component library from a single script |
| D3 | Utility-CSS generator pages | Run-time CSS generation (`MutationObserver`, CSSOM) |
| D4 | SVG charting | Charting libraries drawing SVG, tooltips on hover |
| D5 | Canvas charts and animation | Canvas 2D, `requestAnimationFrame` |
| D6 | Diagram generators | Text-to-SVG diagram libraries |
| D7 | Games and simulations | Canvas, keyboard input, timers |

**How an artifact passes**

- **Two images per artifact and viewport**:
  - `reference.png` — the parity target, captured once from a reference browser under a fixed configuration (same bundled fonts, viewport, device scale, animations settled or disabled), reviewed and committed. It never changes because Folio changed. The capture tool is a separate utility, not part of the engine or its build.
  - `approved.png` — Folio's last approved output, updated only through the approve workflow. It guards against regressions and is what PR before/after diffs show.
- **Static artifact passes** when: it renders without crash, timeout or limit hit; it meets the time and memory targets; and the image matches `reference.png` (default: at most 0.5% of pixels differ by more than a small per-channel tolerance; per-artifact overrides only with a written reason).
- **Scripted artifact passes** when, additionally: the settled image after load matches (virtual clock, seeded randomness, "settled" = no pending timers or animation frames within a window, or a fixed virtual time), and every step of its **interaction script** succeeds. Steps are simple: `click <selector>`, `hover <selector>`, `type <selector> <text>`, `key <key>`, `wheel <selector> <dx> <dy>`, `wait <frames|ms>`, then assertions: `text <selector> <expected>`, `exists <selector>`, `image <region>` against a golden region.
- **Performance categories** (D5, D7) also require p95 frame time ≤ 16.7 ms over a fixed animation window.

**Regression check**: any change against `approved.png` beyond tolerance fails CI until approved.

**Parity measure**: pass rate against `reference.png` per category, reported on every PR and tracked over time. Milestone exit criteria in the [roadmap](../roadmap.md) are stated as pass rates. An artifact that fails because of a known, deliberately unsupported feature stays in the corpus and counts as a failure; the number is meant to be honest.
