# GPU worker source checkpoint (not runtime acceptance)

Owner: new `GpuWorkerProtocol`, `GpuWorkerBroker`, `GpuWorkerDeviceProxy`,
`GpuWorkerServer`, `GpuWorkerIsolationTests`, plus the early Program dispatch.
No changes to ContentProcessClient, ContentDocumentRuntime, ContentHostSession,
or ContentProtocol. No builds, tests, hardware runs, commits or pushes performed.

## Attachment contract

- Parent calls `GpuWorkerBroker.Start(publishedContentHostPath, deadline)` then
  `new GpuWorkerDeviceProxy(broker, budget)` (budget 1..64 MiB). Parent must dispose
  the broker if proxy construction fails. Launch is synchronous; startup deadline
  begins at the first exchange, not CreateProcess/profile creation.
- Launch uses the same `Folio.ContentHost.exe --gpu-worker`, a fresh capability-free
  AppContainer, and its own kill-on-close, single-process memory/CPU/UI restricted
  job through existing WindowsContentLauncher. The parent, not the DOM child,
  must call Start. No child spawning or unrestricted fallback is provided.
- Program dispatch runs before DOM/frame/script/session initialization. Worker
  owns all D3D surface clear/readback calls for this slice.
- Existing content IPC owners must supply a separate bounded DOM-to-parent broker
  channel and inject a remote IGpuDevice factory via DomBindings.ConfigureGpuFactory.
  The current broker is parent-local; it cannot simply be injected across processes.
  No content-protocol message kinds or attachments have been reserved or added.
- Broker permits one outstanding request, validates command/sequence/status/length,
  and permanently loses the session on timeout, cancellation, EOF or malformed reply.
  It terminates only its own worker job. Subsequent exchanges fail; never retry on
  that stream. The integration owner maps IsLost/Unavailable to contextlost, retains
  page state, and creates a fresh session only through explicit context restoration.
- Caller cancellation is available on broker.Exchange; synchronous IGpuSurface
  calls use the fixed per-command deadline. Session Dispose terminates the job;
  final launcher disposal can wait up to five seconds. A multi-chunk readback has
  a deadline per chunk, not one aggregate frame deadline.

## Implemented surface slice

64-byte little-endian requests, 32-byte response headers, payload <=64 KiB;
bounded 2048x2048 RGBA surfaces, <=64 surfaces, <=64 MiB device reservation.
Commands: Open/CreateSurface/Clear/ClearRect/ReadPixels/Release. Readback uses
one <=16 MiB worker snapshot with ordered chunks; proxy commits pixels only
after all chunks arrive. No arbitrary paths, native pointers or handles on wire.
Worker stderr is drained without accumulation. A temporary empty ACL lease file
supports the reused launcher; it is not a page frame or shared-memory transport.

## Explicit gaps and acceptance gates

- Production bridge still directly creates D3D in the DOM process. **Boundary is
  not complete and must not be advertised or enabled as isolated production GPU.**
- No WebGL shader/program/state/draw IPC. INativeWebGlFactory currently returns a
  concrete FolioNativeWebGlContext; replace that seam with an abstract remote
  context before claiming WebGL draw isolation. Proxy does not implement it.
- Texture/buffer/renderbuffer resource allocation explicitly returns Unavailable.
  Renderer identity is a worker marker, not a native adapter identity report.
- No shared-memory transport or descriptor ownership yet; chunked pipes are the
  bounded transport for this checkpoint. No restart epochs or restoration wiring.
- GPU process creation in the actual NativeAOT publication/AppContainer, D3D access,
  security, page-alive/contextlost behavior and cleanup require coordinated tests.
- Added GPU-free tests cover injected blocking read cancellation/deadline, oversized
  reply rejection, budget rejection and dimension limits. They have NOT been run.
  Deadline test uses a finite wall-clock deadline; stall injection uses no shader.
- Keep shader classifier disabled pending runtime acceptance. This checkpoint does
  not modify classifier settings. Never run runaway GPU shaders without approval.

Next main-owner check: compile these files with the coordinated wave build, then
run GpuWorkerIsolationTests without hardware. Add fake-device surface round trips,
real sandbox process stall injection, and page-alive event assertions before any
hardware acceptance. No complete-boundary claim follows from the source tests.
