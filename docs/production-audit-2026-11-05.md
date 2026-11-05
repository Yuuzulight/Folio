# Production audit follow-up, 2026-11-05

Base commit: fef8e00aa7ab5ab52dde5be04664d9dca2fa9250.

## Repairs

- The content host serializes loads, input, capability changes and periodic rendering through one gate. Pointer, keyboard, wheel and text messages reach the document input router.
- Input decoding accepts both the Pascal-case payloads emitted by the host client and camel-case protocol payloads, and rejects malformed/nonfinite input.
- Every loaded document publishes pixels, including ordinary DOM pages without canvas. Navigation replaces the preceding document's pixels.
- Timer tasks and animation frame callbacks advance without another Load message. Idle documents do not repaint on every periodic tick. Rendering callbacks use the engine's host-task entry point.
- JavaScript focus/blur now uses the same input router as native text entry when installed against a hosted Document.
- The production surface is reused and text shaping uses the shared shaper, avoiding a surface allocation and pixel clone for each frame.
- The scripted benchmark now runs in AppContainer, rejects diagnostic/empty frames, ignores unsolicited animation frames when measuring a Load, fails target violations, and no longer fabricates IPC latency as 15% of load duration. CI uses the isolated production publish helper described below.

## Verification

- Fresh Release solution build: no warnings/errors.
- Five new regressions pass, including a real AppContainer content process exercising navigation pixels, click/key/text handling, script focus, delayed timer pixels and animation frames without extra Loads, and disposal of the process/frame file.
- Full solution run: 4,342 discovered; 4,335 passed; one failed; six skipped. The failure was WorkerCancellationCancelsAnOutstandingHostLoad timing out waiting for the loader-start signal after two seconds. Both WorkerFetchContextTests passed in isolation. Do not describe this full run as green.
- Six one-second mutation fuzz targets passed: HTML 1,981, CSS 1,540, PNG 187,906, JPEG 26,142, font 27,584 and SVG 4,507 mutated inputs; zero failures. This is a smoke run, not an exhaustive fuzz campaign.
- Production win-x64 framework-dependent publish succeeds; artifact scan finds zero superseded engine dependencies.
- The four initially missing Unicode test files were restored from the existing checkout and each SHA-256 verified against the checked-in Unicode 18 manifest. The seven UnicodeDataTests/BidiTests passed with zero skips on the restored corpus.
- Complete scripting suite rerun with parallel test execution disabled: 1,461 passed, zero failures/skips (126.3 seconds). This confirms functional coverage under serialized scheduling; the original parallel-run worker-start timeout remains documented rather than erased.
- The original hosted CI run 41860838721 eventually completed successfully. These local changes have not been submitted to CI.

## Second audit: open production blockers

Profiling continuation: `scripted --profile` now requests opt-in host timings for retirement, parsing, engine construction, DOM bindings, globals, scripts, task draining, layout, rasterization and publication. The normal production path does not request those timing dictionaries. The protocol envelope now uses source-generated System.Text.Json metadata; the ordinary host's six production regressions pass after this change.

After the envelope change, three focused protocol/stream tests and 42 resource broker/transport tests passed with zero failures/skips. The build is clean and no content-host worker remains from these experiments.

Text/raster follow-up: ASCII runs now allocate their final shaping arrays directly instead of building a rune list plus four intermediate lists and copying them. Glyph IDs, source clusters, advances and offsets retain the existing mapping; non-ASCII shaping stays on its general path. Expired active scanline edges are removed by swap-with-last, avoiding list shifts; all crossings are still sorted before applying the unchanged fill rule and 16-sample coverage algorithm. The focused software-raster/shaping selection passes 96/96 without skips, including an exact ASCII shaping regression and the text review at 100% and 150% scale. Full render verification and fresh performance samples are recorded separately when complete.

The ordinary sandboxed build's latest profiled sample measured 646.2 ms cold startup, 144.9 ms for sortable-filter-table and 91.4 MB maximum D1 working set. Cold layout was 242.2 ms; the table's layout/raster stages were 71.8/40.2 ms. This identifies costs rather than claiming a release pass.

Historical ReadyToRun experiments initially produced both successful samples and startup fail-fast exits (-1073740286 / 0xC0000602). Further inspection identified a packaging defect: the failing non-composite folder's Folio.dll and Folio.Diagrams.dll retained composite-image references, but the folder contained no corresponding .r2r.dll. Different output folders had still shared intermediate build files. A clean, isolated non-composite build has no such references and passed all production-host regressions, including repeated incremental publishes. This corrects the earlier attribution to AppContainer incompatibility. The analogous failure mechanism is documented in [dotnet/runtime #61584](https://github.com/dotnet/runtime/issues/61584). `tools/publish-content-host.ps1` now isolates production build intermediates, pins non-composite ReadyToRun, excludes superseded engines and validates every shipped font and license notice. CI runs production-host regressions and benchmarks against that exact published artifact. The process tests fail promptly with the supervisor's stop reason if a child dies. Hosted CI for these changes is still pending.

Continuation: removed the second full layout on each Load and the byte-array frame copy/disk flush. Shared frame publication now commits sequence and active buffer in one aligned word, and a reader retries rather than returning pixels after observing a changed publication. This changes the private frame-header magic to FLI2; host and child must be updated together (live frame files are per-process temporary artifacts). A fresh sandboxed sample reached 91.1 MB maximum D1 working set and 693.3 ms cold startup, with sortable-filter-table at 157.6 ms. The results remain above release targets; comparisons across runs are noisy and are not a controlled performance claim.

The updated frame store, production host and Windows sandbox regression selection passes 19/19, including concurrent publication/read consistency, live mapping visibility without flushing to disk, cleanup, the watchdog and forged-descriptor containment.

1. **Viewport/resize repair implemented and locally verified.** A host-approved Viewport command now carries physical dimensions, scale and revision. Each revision gets a new bounded host-created frame file with an explicit per-AppContainer grant. The client rejects forged/current-revision descriptors, discards stale frames, and retires earlier mappings/files. The renderer lays out in CSS pixels and paints at device scale, updates viewport globals and dispatches window resize events without replacing the document. WinForms sends size/DPI/zoom updates in document-command order. The process regression verifies 320x200, 1600x900 at 2x, then 200x120, checking pixels, responsive media queries, viewport globals, resize listener counts, retained JavaScript state and bounded file retirement. A WinForms pixel test checks window resize and zoom in a live control. All ten WinForms lifecycle tests passed after integration; the latest focused engine/transport group passed 32/32.

   The window event test exposed a native evaluator bug: closures created inside for-of/in loops shared a binding. Per-iteration scopes now preserve lexical captures while var headers remain shared and body var declarations use their function scope. Four direct loop regressions and the actual native window resize listener regression pass. The complete scripting suite with FOLIO_SCRIPT_ENGINE=native and serialized test execution passed 1,468/1,468 with zero skips in 110.5 seconds after this change. That environment variable selects child-process engines; the test suite also explicitly constructs Jint oracle engines, so this count must not be represented as 1,468 native-engine tests. Hosted CI and the broader release audit remain outstanding.

2. **P1: production performance targets are not met.** The corrected sandboxed benchmark measured cold startup at 578.8 ms against 400 ms, settings-tabs-faq at 55.3 ms and sortable-filter-table at 151.5 ms against 50 ms. Later D1 working sets reached 106.1 MB against 80 MB. Early artifacts improved to 76.4-77.7 MB after surface reuse. These are local-machine measurements, not hosted acceptance results. The benchmark correctly exits 1; thresholds were not relaxed and the security mode was not bypassed.

   Final run explicitly selecting the newly published executable also fails: cold startup 1120.3 ms, sortable-filter-table 179.0 ms, peak measured D1 working set 114.5 MB. These machine-dependent samples show variability, but neither run satisfies the release thresholds.

3. **P2: test scheduling remains flaky under load.** The full solution execution encountered the two-second worker-start timeout above. Isolation passing is useful diagnostic evidence, not proof of a consistently green suite.

Verdict: the original gaps and viewport integration have local repairs and verification, but Folio must not be declared production ready while performance and reliable release-validation blockers remain. This second pass inspected the affected host/IPC/input/rendering/cleanup/benchmark paths and checked packaging, existing containment and resource-broker limits. It is not a formal proof of every parser, codec, JavaScript, WebAssembly, typography or GPU implementation.

## Continued audit and repairs

- The full rendering suite after the ASCII/raster changes passed 846/846, zero skips. The subsequent profiled sandbox benchmark failed: cold 1,109.4 ms, table 202.5 ms and maximum D1 working set 91.4 MiB. Profiling and machine scheduling affect absolute values; these remain failures, not evidence of meeting the targets.
- Template and regular-expression caches previously retained arbitrary page-controlled input indefinitely. Their retention is now bounded by both entry count and total source characters. AST and binding-pattern caches also use atomic bounded admission/eviction rather than count checks that could overshoot under concurrency. Limits bound retained source, not the exact byte size of parsed objects. Oversized inputs still execute without being cached. Concurrent retention, eviction, hit identity, and factory-failure tests pass.
- The full scripting suite after the cache/cancellation-test repairs passed 1,471/1,471 with no skips (162.9 seconds). The worker cancellation test now gives the synchronous host bridge a dedicated thread, so thread-pool starvation cannot prevent its loader-start signal.
- Block and C-style lexical loop scopes now retain correct closure values and keep block declarations private while preserving function-scoped var. Synthetic groups for comma-separated declarations are explicitly distinguished from actual scopes. The initial implementation failed production bootstrap; it was corrected and a regression added. The corrected focused program/cache/production-process group passed 51/51. A subsequent broad run exposed a heartbeat timeout in the new viewport regression when competing with other process suites; the new class now uses the existing serialized content-process collection. The failing run is not a green release check.
- The native conformance report explicitly selected NativeJavaScriptEngine and reported 100% passes across all D1–D14 and S1–S6 artifacts. This is an in-process conformance check, separate from the AppContainer production-process regressions. It predates the latest CSS animation tick repair and must be refreshed for final acceptance.
- CSS animation ticks were painting settled styles. The host now samples the document clock and uses the existing paint-only frame path when neither structure nor styles mutated; layout-changing animation retains full layout. A process test requires both red and blue frames without another Load. Verification is pending below.

## Resume verification

- The animation regression now uses explicit red/blue plateaus, waiting for both endpoint colors rather than stopping after any two interpolated colors. It passes against both ordinary and ReadyToRun production hosts. The earlier step-end/alternate test was not a reliable endpoint-observation test.
- The production host previously supplied no font source. It now ships pinned, unmodified Source Sans 3, Source Code Pro, Source Serif 4 and Noto Arabic/Hebrew/JP/SC/Color Emoji fonts with their licenses. Fonts load one family at a time and parsed faces are shared across navigations. Both font additions were explicitly approved. Real AppContainer pixel regressions cover default sans, serif, monospace and each script family without page-supplied web fonts. The 20 published font faces are all checked against their hashes; incomplete packages fail the publish script.
- Focused raster/font-pin group: 46/46 passed before rectangular-ring optimization. The subsequent software raster group passes 47/47, including fractional ring coverage, raster-oracle comparison, all four single-border sides, and text reviews at 100%/150% scale. Whole-render acceptance must still be rerun after these latest changes.
- The in-process capability matrix previously instantiated Jint explicitly even in native jobs. It now selects the requested implementation, defaulting to native. All 24 native/Jint × capability configurations pass locally (seven tests per configuration, including feature-specific branches where enabled). This does not replace the broader Windows isolation matrix.
- NuGet vulnerability query across all 16 solution projects, including transitive packages, reported zero vulnerable packages. This is advisory-database evidence, not proof of an absence of source vulnerabilities.
- The script caches now coalesce concurrent parses of a retained key. New concurrent/failure-retry coverage and the program-scope selection pass 26/26. This avoids duplicate foreground/bootstrap parsing while retaining bounded eviction.
- Host-frame, viewport, input and resource-limit messages now use generated JSON metadata with existing wire property names preserved. Input remains case-insensitive. Message writes no longer serialize to a UTF-16 string and then parse/clone it before transport. The focused production/protocol/stream/capability IPC group passes 33/33.
- CPU sampling of the trusted, in-process table workload identified rasterized border rings and text as substantial costs. Single unrounded solid border sides now draw their exact strip; two nested unrounded rectangles use analytic even-odd coverage. The scanline path remains for other shapes, transformations and fill rules.
- The ten-second native shader fuzz run completed 1,920 mutations from seven seeds with zero failures.

Performance remains a blocker. The verified canonical ReadyToRun publish before the latest protocol/ring change measured 480.4 ms cold and 148.2 ms table, with maximum D1 working set 79.0 MiB. The 400/50 ms thresholds remain unchanged, AppContainer remains enabled, and these failures return a nonzero benchmark result. Fresh combined samples and whole-solution verification are recorded when complete.

## Broader verification and native WebGL follow-up

The whole solution run after the protocol/ring repairs passed: 4,385 discovered,
4,383 passed, zero failures, two skips in 4m 18.981s. The skips were the existing
Windows symlink-permission case and the explicitly disabled Wasmtime-oracle
benchmark. This run predates the additional native sequence/buffer repairs below.

The latest published performance sample remained over budget (723.1 ms cold,
163.9 ms table; maximum D1 working set 78.8 MiB). The machine also had FFXIV
running and reported 63% CPU load. These samples cannot establish uncontended
performance or a performance win from the latest changes. The user was asked
whether to close FFXIV temporarily or defer timing qualification; correctness
work continues independently.

The 76 `NativeWebGlPageTests` cases exercised Folio's GPU backend through Jint,
not Folio's JavaScript engine. Switching that group to the native engine exposed
eight sequence-conversion failures, including typed-array color clears and
independent Chrome draw goldens. The test group now selects native by default,
with explicit Jint selection retained for oracle coverage; CI runs both.

Native sequence conversion now handles typed-array views (including offsets and
detachment), arrays, sets, maps, Unicode strings and custom iterators. It bounds
conversion by time, steps and conservative allocation accounting, closes an
iterator on conversion failure while preserving the original error, and does
not close after a failing iterator step. BigInt zero truthiness is corrected.
All 76 native-engine WebGL cases now pass.

The detached-view regression exposed another native bug: typed-array
construction from ArrayBuffer copied bytes rather than sharing storage.
ArrayBuffer views now share storage with validated offset, element alignment and
length; constructing from another typed array copies converted elements rather
than reinterpreting bytes. The focused native engine/WebGL/sequence selection
passed 186/186, followed by 31/31 sequence and production-host cases including
invalid offset/length checks and actual monospace canvas metrics. The complete
scripting and native conformance reruns for these repairs are pending.

## Continued measurements and native execution-limit audit

FFXIV was confirmed closed before the next measurements. Three samples before
the glyph-cache change measured 554.8/402.9/410.4 ms cold and
113.5/110.6/111.9 ms for the table. A subsequent glyph-cache sample measured
465.2 ms cold, 94.6 ms table, ten of twelve warm artifacts within 50 ms, and
78.4 MiB maximum D1 working set. These are individual samples, not a statistically
controlled performance qualification.

Glyph masks now share the existing 8 MiB per-font cache budget with outlines and
flattened paths. Eligible non-overlapping glyphs reuse immutable raster coverage;
overlapping, joined, rotated, scaled, blurred and color glyphs retain their
existing paths. Rounded clip interiors permit the exact opaque rectangle fill
only after verifying full mask coverage. A first clip can retain the immutable
rasterizer buffer instead of copying every pixel. Initial document loading no
longer forces collection when there is no preceding document to retire.

The 228-test raster/typography/conformance selection passed after the glyph-cache
repair. A later full rendering run passed 856/856; the final focused raster run
passed 51/51, including independent narrow-clip checks of wide scanlines and
both fill rules. Experimental scanline probes and a rounded-shape cache did not
demonstrate a reliable speedup and were removed. The retained-code measurement
in `artifacts/audit-verified-performance.log` still failed: 448.6 ms cold,
52.2 ms multi-facet filters, 65.6 ms settings, and 88.7 ms table, with maximum
D1 working set 76.7 MiB. Two Opera processes were subsequently observed using
nearly two CPU cores. Timing qualification remains open; no thresholds changed.

The refreshed solution run discovered 4,407 tests: 4,404 passed, one failed,
two skipped in 3m 53.758s. The worker loader-start timeout recurred under
intra-module parallel load despite the dedicated outer fetch thread, because
the production bridge deliberately dispatches the host loader to the pool.
Its test now joins the existing nonparallel process-test collection and always
cancels and joins its fetch on assertion failure. The two-second cancellation
assertions remain unchanged. This failing run predates that scheduling repair
and the native execution-budget changes below.

Further source review found that the recursion/memory/interrupt limit tests
instantiated Jint, while the production native adapter ignored the configured
recursion limit. Native tasks now share statement, recursion and managed
allocation accounting through nested function/method/dynamic-function calls and
microtasks. Task failures remain sticky if JavaScript catches an exception;
the following task receives a fresh budget. Native interruption is volatile and
checked during execution. Isolated engines inherit all configured limits.
Parser/evaluator recursion checks fail before exhausting the CLR stack. Large
array, buffer and string allocations are checked before materialization;
managed allocations are also sampled during execution. This is managed
allocation accounting per synchronous task, not a claim to measure all native
or GPU memory; the existing process/GPU controls remain necessary.

The first focused native-limit/worker group passed 13/13. Broader verification
is in progress. The first full native-budget scripting rerun identified two
test expectations requiring updates: the typed statement-limit exception is
now asserted, and the sequence test restores its normal budget before its
separate post-failure inspection task. The sequence test now gives each iterator
method a 32 KiB execution budget, verifies exactly 512 admitted elements under
the existing 64-byte admission accounting, and verifies iterator closure. The
old 64-byte budget could no longer execute the iterator method itself once
native call allocation accounting was enforced. Production limits were not
relaxed. The corrected focused native-engine/sequence/limit/worker selection
passes 133/133. A fresh full-solution run is underway after these changes.
Folio remains unqualified for production until these repairs and all release
gates, especially performance, pass against the final artifact.

Latest follow-up: the strict native conformance command passed all 174 artifacts.
The explicit Jint WebGL selection passed 76/76. Both bundled and installed-font
shaping differential commands passed. The pinned production publish passed its
dependency/font checks and all 16 production-host regressions, followed by the
hostile-script diagnostic regression. The latter now also accepts the typed
Memory diagnostic: the native engine can exhaust its actual allocation budget
before the timeout/statement budget. The dedicated timeout and statement tests
still require their specific diagnostic types.

The next complete solution run had 4,422 discovered, 4,419 passed, one failure
(that old diagnostic expectation) and two skips in 3m 40.959s. The expectation
was corrected and passed in isolation; it is not presented as a green complete
run. The worker startup/cancellation regression passed in this broad run after
its scheduling/cleanup repair.

The scanline optimization retained after measurement uses explicit hardware
Vector<float> equality on fixed-size zero-delta blocks. This is distinct from
the rejected generic-span searches. It preserves scalar rounding, sampling and
edge handling and passed the 51 raster checks. The production sample measured
419.6 ms cold, 11/12 warm artifacts within 50 ms, table 79.5 ms, and maximum
D1 working set 81.1 MiB. This remains a failed release gate, including memory.

The explicit native 30-frame canvas check also failed: animated-bars p95
19.68 ms against 16.70 ms, with snake at 6.35 ms. Polling time/allocation every
64 steps rather than on every function entry retained immediate recursion and
sticky-failure checks and passed the 133-test native selection; the next native
canvas sample improved animated-bars to 17.51 ms, still failing. The command now
prints its selected engine. An earlier compatibility-engine canvas sample also
failed (43.29 ms animated-bars) and is not native-engine evidence. Timing work
must not be declared finished based on the passing snake case alone.

The five-second-per-target mutation campaign completed all 17 configured
targets with 17,244,784 mutated inputs and zero failures (seed 49579656).
This includes image codecs, fonts, canvas, resource/cache IPC and native shader
translation. It is a finite, mutation-based campaign without coverage feedback,
not an exhaustive security proof and not a general JavaScript execution fuzzer.
Log: `artifacts/audit-all-fuzz.log`.

## Latest verified checkpoint

- Complete solution run: 4,422 discovered, 4,420 passed, zero failures, two
  documented skips, 3m 30.972s (`artifacts/audit-green-solution.log`).
- That full run predates the final sequence-interruption follow-up. The follow-up
  keeps interruption sticky inside a containing task during sequence conversion,
  and verifies that JavaScript catch blocks cannot suppress interruption. The
  rebuilt native-engine/sequence/limit/worker selection passes 134/134 with zero
  skips (`artifacts/audit-followup-tests.log`). Release build: zero warnings/errors.
- Final production publish repeats all dependency/font checks; its 16 host
  regressions pass in 8.315s (`artifacts/audit-final-host-tests.log`). No
  Folio.ContentHost or Folio.SandboxProbe process remained after verification;
  process/frame-file disposal is also asserted by the host tests.
- Sandboxed GPU probe succeeded on Hardware:Direct3D11-FL0xB000: reservation and
  peak reservation both 1,228,800 bytes. DXGI separately reported 10,166,272 local
  and 2,121,728 nonlocal bytes (`artifacts/audit-followup-gpu.log`). Reservations
  are not substituted for actual OS GPU-memory readings.

**Unresolved:** production cold first frame <=400 ms, all D1 warm first frames
<=50 ms, all measured D1 working sets <=80 MiB, and every native canvas frame
p95 <=16.70 ms. Last failed samples are recorded above. A quiet-machine timing
confirmation is pending; Opera activity was still substantial after FFXIV closed.
The local fixes have not been pushed or verified by hosted CI. The audit remains
open and Folio is not declared production ready.

## Quiet-machine continuation and requested pause

After the user paused busy browser tabs, CPU sampling reported approximately 6%
load. The first production sample passed cold startup at 389.8 ms, but failed
multi-facet filters (56.6 ms), the table (80.1 ms / 80.8 MiB), and native animated
bars (20.05 ms p95). Browser contention was therefore not the sole blocker.

Two additional rendering repairs were made before the requested pause:

- Solid square border rings can paint their border pixels directly, without
  allocating and scanning a coverage bitmap for the transparent center. The
  shared ring recognizer preserves the existing analytic path for other paints.
  Fractional scaled-ring comparisons exposed a prior snapping mismatch: a
  multi-contour ring needs the path converter's quarter-row edge grid. Both
  the direct path and the coverage fallback now use that grid when requested;
  unsnapped analytic coverage remains available and tested.
- Glyphs now composite in paint order instead of unioning every outline in a
  run into one fill. This repairs translucent overlapping text and permits
  overlapping/RTL glyphs to use the bounded glyph-mask cache. Scaled/rotated
  fallback painting also preserves per-glyph composition. Native-scale Skia
  comparison and exact run-versus-individual-glyph reftests at 100%, 110% and
  150% pass. The per-font 8 MiB cache budget is unchanged.

The focused raster selection passes 57/57. A separate cross-engine pixel
comparison of translucent overlapping glyphs at 110% still exceeds the generic
pixel thresholds (mean 0.489; >8 differences 2.78%; >40 differences 0.67%).
That broader scaled-text variance is unresolved; its cause has not been
established. The exact scaled compositing reftest validates paint order without
claiming that arbitrary-zoom raster parity is solved. Existing oracle thresholds
and release targets were not relaxed.

The latest production sample after these repairs measured cold startup 415.5 ms,
table 63.9 ms and maximum D1 working set 79.8 MiB. Most warm artifacts pass;
accordion also missed in that sample at 56.4 ms. The table still needs roughly
a 22% reduction in first-frame time to reach 50 ms. Cold-start and memory results
need consistent qualification rather than a selected best sample. Logs:
`artifacts/audit-quiet-performance-1.log`,
`artifacts/audit-quiet-canvas-1.log`,
`artifacts/audit-direct-ring-performance.log`, and
`artifacts/audit-glyph-order-performance.log`.

The user requested finishing verification of this change, then pausing. Final
render-suite, native-conformance, canvas and published-host results follow below.
No further optimization is authorized before resuming the paused task.

Pause checkpoint verification:

- Full current rendering suite: 864/864 passed, zero skips, 88.596 seconds
  (`artifacts/audit-pause-render-suite.log`).
- Strict native conformance: all 174 artifacts passed
  (`artifacts/audit-pause-native-conformance.log`).
- Published production host: 16/16 passed, zero skips, 8.565 seconds
  (`artifacts/audit-pause-published-host.log`). No content-host or sandbox-probe
  process remained after this run.
- Native canvas gate still fails: animated-bars total p95 18.84 ms
  (script/update 17.14 ms, display list 0.87 ms, host paint 1.06 ms), target
  16.70 ms (`artifacts/audit-pause-native-canvas.log`).
- `git diff --check` is clean. Changes remain local and uncommitted on
  `fix/folio-production-audit`; no push, PR or hosted CI verification occurred.

Remaining work on resume:

1. Reduce table first-frame time from the last 63.9 ms sample to <=50 ms
   (about 22% faster) and animated-bars frame p95 from 18.84 ms to <=16.70 ms
   (about 11% faster). Qualify cold startup, all other warm pages and memory
   consistently; one favorable sample does not close those gates.
2. Investigate and resolve the cross-engine scaled-text pixel variance at 110%.
   Native-scale overlap parity and exact scaled paint-order equivalence are
   already verified; they do not substitute for this remaining comparison.
3. Rebuild and rerun the complete solution, affected fuzz/release checks and
   hosted CI on the final changes, then finish the second audit and release
   evidence. The 4,420-pass whole-solution checkpoint and 17-target fuzz run
   predate the latest border/text repairs and must not be called final-head
   validation.

Work is paused at the user's request. Folio is not yet production qualified.

## Resumed 2026-11-05 (handoff to Claude)

The uncommitted checkpoint was preserved; a full patch and the untracked files were backed up outside the worktree
before any change. Attribution came from measurement, not assumption: an in-process probe of each workload (outside
the repo) and a dotnet-trace CPU profile of 150 table loads.

**Canvas (animated-bars).** JavaScript is 0.16 ms of the frame; the drawing it does is the rest. Removing one feature at a
time showed the cost in shadowed gradient fills and in text.

- A linear gradient along device y gives the same bits for every pixel of a row (no x term reaches `t` but through a
  zero), so `BlendShader` looks the row up once (`Shader.TryRowColor`, exact by construction; any other gradient keeps
  the per-pixel path). A bar's gradient fill went from 0.55 to 0.17 ms.
- `GaussianBlur` sent any image with 64 or more lines to `Parallel.ForEach`; a canvas shadow mask (86 x 278) cost more to
  hand to the thread pool than to blur. Below 256K samples it now runs serially. Lines are independent, so the output
  is identical.
- Native canvas gate, three runs: animated-bars total p95 **14.46 / 15.61 / 15.25 ms** against 16.70 (was 18.84); snake
  4.39 to 5.70. Margin is 1 to 2 ms on this machine; the blur (0.32 ms per bar) is the next cost if hosted CI needs more.

**Table (sortable-filter-table), in-process warm load at 1280 x 720.** Median 61.8 ms at the start.

- Raster was led by 75 border items costing 14.7 ms: with collapsed borders, 66 cells per page carry a top and a bottom
  side, which missed both existing shortcuts and took the general path (two path clip masks and two ring rasters per
  cell). Facing sides with no width between them never meet, so each side's region is exactly its strip; `PaintBorder`
  now draws a lone side or a facing pair as strips. Borders 14.7 -> 3.2 ms; raster 20.6 -> 10.4 ms.
- The profile put about a fifth of the load under the native engine's deadline check and another fifth in .NET
  exception dispatch:
  - Every function call starts a fresh evaluator at step 0, and `ThrowIfExpired` polled the clock at step 0, so it ran
    once per call. Each evaluator now polls on its 64th step; anything unbounded still runs that many steps in some
    evaluator, and the shared task budget still polls every 64 steps across calls.
  - `return`, `break` and `continue` were thrown as exceptions (`ReturnSignal` and friends). They are now completions
    kept on the evaluator (ECMA-262 §6.2.4): statement lists stop on an abrupt completion; loops, `switch` and labels take
    their own; child scopes (blocks, lexical `for`, `for-in/of` iterations, `with`) pass theirs to the parent; a function
    body or program turns a return into its value. `finally` runs whole with the pending completion saved; an abrupt
    completion of its own replaces it, an in-flight exception included, as before. A stray break or continue still
    leaves as the old signal exception.
- In-process medians after these changes ranged 42.8 to 49.1 ms between runs on a machine that later turned out to be
  running FFXIV (55% CPU, 88% RAM). They are not a qualification. The production (published, sandboxed) table sample has
  not been re-measured.

**Verification so far (this session).**

- Render suite 864/864, zero skips — run after the gradient and blur changes, **before** the border-strip change; it must
  be rerun.
- Folio.Tests painting and SVG namespaces 354/354; scripting canvas classes 273/273 (same point in time).
- New `GradientTests.AVerticalLinearGradientShadesARowWithOneLookup`: the row colour equals `ColorAt` bit for bit across
  rows and two transforms, and is refused for horizontal or rotated gradients. It fails when the `d.X == 0` guard is
  removed.
- New `NativeJavaScriptProgramTests.ControlFlowCompletesWhereItBelongs`, 20 cases (nested and labeled loops of every
  kind, switch inside loops, labeled blocks, nested lexical scopes, return/break in `finally` over a return and over a
  throw, a throw passing through `finally`, callbacks). The two override-a-throw cases fail when that rule is removed.
- Complete scripting suite after the completion rewrite, serialized: **1,548/1,548**, zero skips, 132.0 s
  (`artifacts/resume-scripting-suite.log`).
- `git diff --check` clean; no temporary instrumentation remains.

**Still open.** Rerun the render suite, painting tests and strict native conformance after the border and evaluator
changes; publish the content host and take the production scripted samples (table, all warm pages, cold start, memory)
and the canvas gate on a quiet machine, more than one sample each; the 110% scaled-text comparison; the complete
solution run and fuzz campaign on the final code; hosted CI (needs a push, not done).

## Resumed 2026-11-05, quiet machine (no game, RAM about 60%)

**Verification on the final code** (logs `artifacts/resume2-*`):
- Render suite 864/864, zero skips, 68.5 s.
- Strict native conformance: 174/174 artifacts.
- Folio.Tests 2,004, zero failures. One skip, `LocalFolderTests.RefusesLinksThatLeaveTheFolder`: this machine can't create symbolic links. The test is unchanged.
- Native canvas gate, three runs. Animated-bars total p95 12.59, 11.63 and 14.57 ms against 16.70 ms. Snake 4.38 to 5.23 ms.
- Content host republished with `tools/publish-content-host.ps1`. Production-host regressions 16/16.

**Production scripted samples** (published, sandboxed host, three runs):

| | Run 1 | Run 2 | Run 3 | Target |
|---|--:|--:|--:|--:|
| Cold start | 391.0 | 415.2 | 387.0 | 400 ms |
| sortable-filter-table | 63.8 | 61.9 | 56.0 | 50 ms |
| Next slowest warm page (settings-tabs-faq) | 41.4 | 41.3 | 38.5 | 50 ms |
| Largest working set | 77.8 | 77.7 | 77.8 | 80 MiB |

- Every other warm page passes, at 23 to 41 ms.
- **The table still fails in production.** The in-process gains (about 62 → 47 ms median) mostly don't reach the ReadyToRun host. `scripted --profile` for the table: retire 4.7, bindings 3.7, scripts 3.5, layout 23.6, raster 15.9 and publish 1.7 ms. In-process with tiered JIT, layout is 17.6 ms.
- **Cold start is right at its target** and fails one sample in three.

**Two experiments, both reverted:**
- **Skipping the per-load aggressive GC in `DisposeDocument`.** It costs 4 to 7 ms of every warm first frame, but it holds the working set down: without it, D1 pages reach 92 to 96 MiB, against the 80 MiB target. Moving it after the response would only hide that peak from the measurement, so it stays. Even without it, the table took 49.6 to 54.1 ms.
- **`DOTNET_TC_CallCountingDelayMs=0` for faster tier-up:** no measurable change.

**Where the table's layout goes** (in-process trace, 150 loads):
- Style resolution is 48%: the cascade, the sharing cache and stylesheet parsing.
- Table layout is 27%: column ranges and intrinsic sizes 9 to 13%; collapsed-border resolution's identity hashing 3%.
- The box tree is 9%. The display-list build is 8%.
- About 39% of layout samples fall under GC polls, so allocation volume is a cross-cutting cost.
- No single item is near the 6 to 14 ms still needed. The remaining work is a set of allocation and style-resolution reductions, or a decision about how the production host is compiled (composite ReadyToRun or a different tiering setup), and both need a cold-start check.

Still open: the table's production target and a consistent cold start; the 110% scaled-text comparison; the complete solution run and fuzz campaign; hosted CI (needs a push).

**Rounded shapes in the raster (table page).** The table's wrapper has `border-radius: 12px; overflow: hidden`.
- **What it cost.** Its 858-pixel rounded clip is pushed three times per frame, and its rounded border ring and 51 rounded fills go through the general scanline rasterizer. That rasterizer is about 58% of the table's raster time, and it samples each pixel row 16 times.
- **Repeated samples.** `Rasterizer.Fill` now repeats a sample's walk for the rest of its row when nothing can change:
  - that sample added or removed no edge;
  - no edge starts, ends or slants before the row's last sample.
  - The remaining samples would build the same crossing list in the same order, so the same spans are added in the same order, and the coverage is bit-identical.
  - Measured alone: the wrapper clip went from 0.46 to 0.35 ms, and its border ring from 1.63 to 0.93 ms.
- **Clip reuse.** `SoftwareCanvas.ClipRoundedRect` reuses its last result when the box, transform, clip and clip mask are the same. Masks are never written after they're made. That removes two of the three 0.35 ms clips.
- **Test.** New `SoftwareClipTests.ARepeatedRoundedClipFollowsTheTransformAndTheClipAroundIt` fails when the cache key leaves out the transform and clip.
- **Verification.** Render suite 864/864, Folio.Tests 2,004 (the one symlink skip), strict native conformance 174/174 (`artifacts/resume3-*`).
- **Not yet measured in production.** The whole-page timings were noisy (other apps at 45% CPU). The production sample is still to be taken.

## Continuation: native conformance correction and owned font hinting

**Important correction to the earlier 174/174 claims.** The old `native` conformance command selected Folio's
JavaScript engine, but still rendered through Skia and HybridShaper. It did not validate the shipped all-Folio
renderer. The command now uses SoftwareRasterBackend and ComplexShaper by default, including compatibility-JS
runs. The explicit diagnostic `--oracle-renderer` switch cannot be combined with native mode. Tests reject a
request for native shaping with the oracle rasterizer. The initial correctly wired result was **139/174**.

PR **#699** is open as a draft, branch `fix/folio-production-audit`. CI on the old head `3026baab`, run
**42058001987**, passed all 24 capability jobs but failed Ubuntu reference rendering (two text/iframe mismatches)
and the Windows animation gate (bars 22.60 ms, snake 17.39 ms). Later production/fuzz steps were skipped in that
run. Those failures are not evidence that the current continuation code passes CI.

### Correctness repairs

- Native ASCII shaping now applies the font's Latin ligature substitutions, pair kerning and pair placement.
  Three independent HarfBuzz comparisons cover glyph ids, clusters, advances and offsets.
- Bitmap emoji shaping now resolves variation selectors, regional flags, modifiers, keycaps and supported ZWJ
  sequences through the font's own substitutions, preserving source grapheme clusters. Seven oracle cases pass.
- Default iframe borders now use the browser's inset border style rather than a flat gray border. D12 passes 4/4;
  its two obsolete known-gap exclusions were removed.
- The user approved an owned font-hint interpreter, keeping the visual requirements. TrueType `fpgm`, `prep` and
  glyph programs run with bounded stack, storage, CVT, point count, calls and execution steps. Glyphs get isolated
  copies of prepared state; composite components share an overall execution budget. Phantom points and contour
  interpolation are supported. Invalid programs fall back to complete original outlines, never partial fitted data.
- Hinting uses device pixel size, including 110% and 150% scale. Its per-face cache shares the existing 8 MiB
  total glyph budget; no production font-rendering library was added. Malformed-input, isolation, size-churn,
  bundled-font execution and raster comparisons are covered by tests.
- **170/174** full native corpus after TrueType hinting: `artifacts/continue-hinted-native-conformance.log`.
  All remaining failures were CFF CJK pages. The 110% and 150% overlapping-text oracle checks now pass and were
  restored; the whole-page text-review loop also includes 110% again.
- Added owned CFF stem/mask/blue-zone fitting, bounded counter preservation, quarter-pixel stem widths and optical
  compensation for thin vertical strokes. Declared hints are bounded to 96 and masks are checked before use.
  Dense grid fits preserve contour order. All Chinese/Japanese corpus glyphs execute across five sizes without
  falling back. The selected **S6 corpus now passes 12/12**, with no tolerance changes.
- Added colour-aware grayscale text coverage correction, using eight fixed luminance tables (2 KiB). Cached
  coverage includes its luminance key; opacity and clipping are applied after mask correction. Scaling checks at
  100%, 110% and 150% pass. The old text-hint exclusions were removed entirely.
- Page-run oracle comparisons exposed Japanese pair kerning and Latin ligatures disabled by mixed punctuation.
  Both are repaired. The two native shapers also replace malformed UTF-16 within the requested run rather than
  throwing or reading the next run's surrogate. Regression tests cover these cases.
- Preparation supports font/CVT function definitions and forward-compatible instruction definitions; point-flag
  operations respect the selected zone. Unsupported or malformed font programs retain whole-outline fallback.

### Current validation and remaining work

- Full solution snapshot before the last safety refinements: **4,523 total, 4,521 passed, two skips, no failures**
  (`artifacts/continue-final-solution-tests.log`). Skips: unavailable local symlink privilege and the explicitly
  excluded Wasmtime-oracle benchmark. Final-code rebuild and rerun are still required.
- Final focused owned-text checks: **154/154**, including TrueType/CFF execution, resources, safety, text coverage,
  mixed-language page runs and independent shaping/raster comparisons (`artifacts/continue-owned-text-tests.log`).
- Added font-program mutation coverage and extended font mutation rendering to TrueType as well as CFF.
  The first campaign found an empty CFF font-dictionary indexing crash; the parser now rejects invalid dictionaries,
  and the failing binary is a permanent regression. Recheck: **501,933 font-program mutations and 349,205 font
  mutations, no failures** (`artifacts/continue-hint-fuzz-recheck.log`). The final full campaign is still pending.
- An ignored, development-only FreeType oracle was installed under `artifacts/font-oracle` for diagnosis.
  It is not referenced by production projects or packages. Pixel probes are also ignored artifacts.
- Remaining: final complete native corpus run, safety review, full solution and fuzz
  verification; production-host republish and checks; current-head CI; consistent cold/warm/animation/memory
  qualification. **Timing measurements are deferred at the user's request while other applications are busy.**
  The older table/cold-start timings above remain the latest production evidence, not a pass on the new code.
