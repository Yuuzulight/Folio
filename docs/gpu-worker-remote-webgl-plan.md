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
3. **Deferred errors and sync points.** Error ordering and flush-before-query rules.
   Gate: dedicated ordering tests, plus the WebGL conformance subset Folio already runs.
4. **Process wiring.** Parent launches the worker lazily on the first WebGL context, duplicates
   handles, child installs the remote factory through `DomBindings.ConfigureGpuFactory`.
   Gate: real AppContainer round trip, worker kill keeps the page alive and fires contextlost.
5. **Restore and qualification.** Restore protocol, frame-time benchmark against the 16.7 ms
   budget, memory accounting. Gate: full suites, conformance, published NativeAOT host, then a
   user-approved hardware pass. Only after that may the classifier route WebGL here.
