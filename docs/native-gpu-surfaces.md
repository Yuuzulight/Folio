# Native GPU surfaces

The first native graphics backend uses the pinned ANGLE dependency on Windows. It creates a D3D11 device under the content process's AppContainer token, tries hardware first, then explicitly identified WARP. Native creation failure reports `GraphicsUnavailable`. Other platforms currently report the native backend unavailable.

This implements the trusted surface pipe in #528. It does not expose a new page API. The WebGL shader, draw, texture and context-loss integration in #529–#531 remains separate; production artifact routing continues to require a browser for WebGL.

## Ownership and limits

Each document lazily owns one device. Each canvas retains its offscreen RGBA8 texture and framebuffer until resize, capability revocation or document teardown. Revocation disposes the device; a later grant creates fresh native state. Retained disposed handles refuse operations. Devices sharing an EGL display retain it independently, so disposing one does not invalidate another.

`ResourceLimits.MaxGpuBytes` defaults to 64 MiB. Surfaces, buffers, textures and RGBA8 renderbuffers reserve their storage against the aggregate document limit before native allocation. A refused allocation reports `ResourceLimit` and does not allocate the canvas raster. Reservations release when their resource is disposed. Native driver/context overhead is outside this logical storage quota.

The trusted clear path reads top-left straight RGBA8 pixels into the normal canvas raster. The display list composites the canvas at its used box, preserving clipping and sibling paint order. The host receives its usual premultiplied BGRA8 frame through the host-owned shared-memory store. Successful native readback clears an earlier failure placeholder.

## Verification

`AngleGpuBackendTests` exercises actual WARP readback, row orientation, native resource accounting, device independence, disposal and concurrent retained-handle access. `GpuDocumentBridgeTests` covers denied access without native creation, document ownership, refusal before raster allocation, aggregate canvas budgets, resize, revocation/regrant and failure recovery. `GpuSurfaceTests` launches the sandboxed content process and checks actual published canvas, overlapping sibling and surrounding pixels, typed missing-library fallback and budget refusal/replacement.

The benchmark command is:

```text
dotnet tests/Folio.Benchmarks/bin/Release/net10.0/Folio.Benchmarks.dll gpu
```

It records renderer identity and resource reservations alongside DXGI local and non-local process usage from the actual content process. These are separate measurements. DXGI coverage is adapter node 0; unavailable counters remain unknown. Windows CI publishes the table in its run summary. A WARP result proves the native pipe and software driver, not hardware performance.
