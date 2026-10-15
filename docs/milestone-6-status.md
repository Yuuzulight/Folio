# Milestone 6 status

Milestone 6 is being built dependency-first. Each capability is exposed only when the host grants it, and every bounded resource path remains subject to the document's limits.

Implemented dependency-backed surfaces:

- WebGL resource state, limits, framebuffer/texture/query/sync operations and context loss.
- Audio/video element state, resource-backed WAV metadata, device tracks and playback promises.
- Dedicated workers with isolated script engines, resource-backed scripts, message/error events and termination limits.
- WebAssembly validation, imports/exports, bounded memory/tables/globals and a bounded i32 interpreter.
- Iframes with data or host-loaded child documents, child window identity and cross-document messaging.
- WebRTC negotiation, ICE/candidate state, transceivers, senders/receivers, tracks and data channels.
- Service-worker registration, script loading, install/activate lifecycle events, controller changes and messages.

Remaining work for a complete browser-level milestone includes native audio/video decoding and output, broader WebAssembly instruction coverage, and real network WebRTC transport. Those areas stay capability-gated and route unsupported artifacts to the system browser.
