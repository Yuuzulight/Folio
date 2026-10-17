# Milestone 6 status

Milestone 6 is in progress on the dependency-backed path. The work below provides bounded browser-facing behavior and conformance coverage, but it is not milestone completion and does not replace the later Folio-owned runtimes.

Current exploratory surfaces:

- WebGL resource state, limits, framebuffer/texture/query/sync operations and context loss.
- Audio/video element state, resource-backed WAV/MPEG metadata, format-aware `canPlayType()`, device tracks and playback promises.
- Dedicated workers with isolated script engines, resource-backed classic/module/blob scripts, transferable message ports, message/error events and termination limits.
- WebAssembly validation, imports/exports, bounded memory/tables/globals and a bounded i32 interpreter.
- Iframes with data or host-loaded child documents, sandbox origin/flag handling, child window identity, transferable cross-document messaging and load events.
- Media text tracks with WebVTT parsing, cue IDs, active cue tracking, `<track>` loading and `cuechange` events.
- WebRTC negotiation, ICE/candidate state, transceivers, senders/receivers, tracks and data channels.
- Service-worker registration, script loading, install/activate lifecycle events, controller changes and messages.

Remaining work includes nested iframe layout/input/accessibility, media controls and cue painting, a GLSL/WebGL runtime and rasteriser, a Folio-owned WebAssembly engine, native media decoding/output, real WebRTC transport, and the remaining corpus and routing coverage. The current surfaces should not be read as milestone completion.
