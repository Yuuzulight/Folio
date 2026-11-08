# Remote WebGL through the GPU worker — plan

Status: approved design, not implemented. Builds on `docs/gpu-worker-checkpoint.md`.
Production WebGL still runs in the document process until stage 5 lands and passes its gate.

## Decisions (Yuuzulight, 2026-11-07)

1. **Direct pipe.** The parent launches the GPU worker and owns its lifetime. It creates a
   dedicated pipe pair and duplicates one end into the DOM child and one into the worker.
   GPU traffic never crosses the content protocol. The parent can still terminate the worker
   job at any time; the DOM child cannot launch, signal or reach anything else.
2. **Command buffer.** WebGL calls are encoded into a bounded buffer and flushed as one batch
   per frame, or immediately when a call needs a result.
3. **Shared memory now.** Bulk data (buffer/texture uploads, readPixels, presented frames)
   moves through bounded shared-memory sections, not the pipe.

## Wire model

- **Handles are client-allocated.** The DOM side assigns 32-bit IDs for every created object
  (shader, program, buffer, texture, framebuffer, renderbuffer, vertex array, query, sampler,
  sync, transform feedback). Create calls never wait. The worker maps IDs to native objects
  and rejects unknown, reused or cross-kind IDs.
- **Two command classes.**
  - *Deferred* (state, binds, uploads, draws, deletes): encoded and return success at once.
  - *Synchronous* (getError, get*Parameter, compile/link status, info logs, locations,
    checkFramebufferStatus, readPixels, getBufferSubData, fenceSync waits, finish): flush the
    batch, then wait for one reply within the per-command deadline.
- **Deferred errors.** The worker records native failures in order. Every reply carries them;
  the remote context hands them to the binding's existing WebGL error queue, so `getError`
  returns them in call order. Nothing reports success for a call the worker later rejects
  without that error becoming observable at the next sync point.
- **Batch bounds.** A batch is at most 256 KiB of commands and 4096 commands; overflow forces a
  flush. Inline payloads are limited to 4 KiB per command; larger data must be in shared memory.

## Shared memory

- The parent creates two unnamed file-mapping sections per session and duplicates them into
  the DOM child (read/write) and the worker (read/write), with no other access rights:
  - **Upload arena** (DOM writes, worker reads): ring allocator, 16 MiB default. Commands refer
    to `(offset, length)`; the worker validates both against the arena and the ring's
    published head and copies before use. Space is reclaimed only after the worker acknowledges
    the batch that used it.
  - **Readback/present section** (worker writes, DOM reads): one drawing-buffer-sized slot per
    context plus a readPixels slot, capped by the existing device budget (64 MiB).
- Sections count against the content job's and worker job's memory budgets. No handle values,
  native pointers or paths are ever trusted from the other side; offsets are always validated.

## Failure and context loss

- Any worker timeout, malformed reply, EOF or job termination makes the session lost
  permanently, as the checkpoint broker already does. The DOM side then reports
  `webglcontextlost` to every context in that session and keeps the page running.
- `webglcontextrestored` needs an explicit restore: the DOM child asks the parent (one new
  content-protocol message) for a fresh session; the parent launches a new worker and hands
  over new pipe/section handles. The page recreates its resources, as WebGL requires.
- Runaway shaders are bounded by the per-command deadline plus worker job termination.
  Deliberate runaway-shader tests still need explicit approval before they are run.

## Stages and gates

1. **Transport.** Pipe and section handoff helpers; ring allocator; batch framing with
   validation. Gate: fuzz and bounds tests for every header and offset, in process.
2. **Encoder/decoder.** `RemoteWebGlContext : INativeWebGlContext` (DOM side) and a worker
   dispatcher that drives the existing `FolioNativeWebGlContext`. Gate: the existing WebGL
   binding tests run unchanged against an in-process loopback pair and match direct results.
   *Done:* `GpuWebGlSession`, `RemoteWebGlContext`, `GpuWebGlDispatcher`, `LoopbackGpuDevice`.
   `FOLIO_WEBGL_REMOTE=loopback` routes every test's WebGL through the remote path; the full
   scripting suite passes in that mode and CI runs the WebGL classes in it. Notes:
   - Deferred failures reach the WebGL error queue when the page calls `getError` or
     `isContextLost`; a budget failure therefore loses the context at that point, not at the
     call that caused it.
   - GPU metrics are a synchronization point (queued calls run before the device is measured).
   - Parameter values carry every field, because native contexts fill more than the field their
     kind names; binding queries are translated back to client handles in both fields.
   - A creation that fails in the worker leaves its client handle unmapped; later uses of that
     handle report INVALID_OPERATION instead of losing the session.
3. **Deferred errors and sync points.** Error ordering and flush-before-query rules.
   Gate: dedicated ordering tests, plus the WebGL conformance subset Folio already runs.
   *Done:* the bindings drain deferred failures before recording any newer error, so
   `getError` order equals call order (one round trip, only on calls that already failed).
   After a budget failure the worker answers the context's later queued calls as calls on a
   lost context, and a lost or disposed remote context fails calls at once, as a native one
   does. `RemoteWebGlOrderingTests` compares exact `getError` sequences, `isContextLost` and
   post-loss calls between direct and remote execution; D8 conformance passes 6/6 through the
   remote path and runs in CI that way. Most native WebGL errors live in the native context's
   single pending-error slot (first error wins) and are read by `getError` itself, so they
   are reproduced exactly without the deferred path.
4. **Process wiring.** Parent launches the worker lazily on the first WebGL context, duplicates
   handles, child installs the remote factory through `DomBindings.ConfigureGpuFactory`.
   Gate: real AppContainer round trip, worker kill keeps the page alive and fires contextlost.
   *Done, opt-in (`FOLIO_GPU_WORKER=1`):* `GpuWebGlWorker` (parent) starts
   `Folio.ContentHost.exe --gpu-webgl` in its own AppContainer and job on the first load that
   grants WebGL, writes a validated startup record (`GpuWorkerStartup`) to its control pipe and
   hands the document's ends to the content child once, inside a host-owned `gpuChannel` load
   field (a caller-supplied value is always dropped). No public protocol type changed. The
   parent then holds no channel handle. `RemoteGpuChannel` (child) runs every document's WebGL
   over the shared session. The worker kills itself when a batch exceeds the 10 s command
   deadline; the child's own deadline is 15 s. The child also gets a wait-only handle to the
   worker process, so a worker exit loses the session at once even on an idle page, and
   `DomBindings.NotifyGpuDeviceLost` runs on the document's event loop.
   `GpuWorkerProcessTests`: WebGL draws through the worker (an AppContainer process in a job,
   separate from the content process); killing it keeps the page running and reports the loss.
   Notes: canvas surface clears are not routed through the worker and report unavailable;
   Folio signals loss through `gl.oncontextlost` and does not yet dispatch the canvas
   `webglcontextlost` event (existing gap, also in direct mode). Lease files live in a
   private folder: the launcher grants traverse on the lease's folder, which on `%TEMP%`
   itself took minutes (also fixed in the older `GpuWorkerBroker`).
5. **Restore and qualification.** Restore protocol, frame-time benchmark against the 16.7 ms
   budget, memory accounting. Gate: full suites, conformance, published NativeAOT host, then a
   user-approved hardware pass. Only after that may the classifier route WebGL here.
   *Benchmark done:* `Folio.Benchmarks webgl [frames]` animates an 800x600 page (4000 vertices uploaded
   through the upload section, then 20 draws with uniform changes, per requestAnimationFrame) directly,
   through the in-process remote path and through the GPU worker process, and fails when the worker's p95
   is over 16.7 ms or any path reports errors, loss or missed frames. CI runs it on Windows. First local
   run (Windows, hardware GPU): direct 4.5 / 5.1 ms median / p95, in-process remote 4.4 / 5.2 ms, worker
   5.5 / 8.0 ms. The binding presents after every draw (one synchronous readback each), which costs the
   worker about 1 ms median here; batching presents per frame is not needed at this size.
   *Memory accounting done:* the parent commits both shared sections, so neither job's commit limit sees
   them. The content job's limit is lowered by their 32 MiB when the channel is handed over, and the
   worker job starts at 512 MiB minus 32 MiB; `GpuWorkerProcessTests` checks the content job's limit as
   the OS reports it. The benchmark prints the worker's peak working set and private bytes.
   *Restore done (`ContentMessageKind.GpuRestore`, approved 2026-11-08):* when the child's session is
   lost it sends `GpuRestore`; the parent retires the old worker whatever its state, starts a new one and
   answers with `GpuRestore { gpuChannel }`. The child replaces its channel and, on the event loop, loses
   any context that has not seen the loss yet, drops the document's device and recreates every context
   on the new channel, firing `webglcontextrestored`; the page recreates its resources. The kind is
   host-owned: `ContentProcessClient.SendAsync` refuses it. The parent replaces a worker at most 3 times
   per content process (`MaxGpuRestores`); after that the dead worker is retired and WebGL stays lost,
   so a page that keeps crashing its worker cannot make the parent start processes without bound.
   The shared sections' memory is reserved from the content job only once. As in direct mode, Folio
   restores without requiring the page to call `preventDefault()` on the loss event.
   `GpuWorkerProcessTests` kills the worker and checks one loss, one restore, a different worker process
   and a new draw, and that the fourth loss stays lost.
   *Published NativeAOT host done:* CI's published production-host step already runs the whole scripting
   suite, `GpuWorkerProcessTests` included, against `artifacts/content-host/Folio.ContentHost.exe`, so
   the worker there is the NativeAOT executable. CI also runs the WebGL benchmark against that host; the
   report names the worker host. Local run (Windows, hardware GPU): the worker process was the published
   `Folio.ContentHost.exe --gpu-webgl`; GPU worker tests 34/34 with the sandbox classes; benchmark worker
   6.4-7.4 / 8.7-9.5 ms median / p95, peak working set 96 MB, private 133 MB.
   *Still open:* the user-approved hardware pass. Until it passes, the worker stays opt-in
   (`FOLIO_GPU_WORKER=1`) and the classifier does not route WebGL to it.
