# Milestone 6 status

Milestone 6 has not started as a browser-level implementation. Existing code exposes partial, dependency-backed API-shaped state, but it does not yet provide real nested execution, media decoding, WebGL rendering, or network transport.

Current exploratory surfaces:

- WebGL resource state, limits, framebuffer/texture/query/sync operations and context loss.
- Audio/video element state, resource-backed WAV/MPEG metadata, format-aware `canPlayType()`, device tracks and playback promises.
- Dedicated workers with isolated script engines, resource-backed scripts, message/error events and termination limits.
- WebAssembly validation, imports/exports, bounded memory/tables/globals and a bounded i32 interpreter.
- Iframes with data or host-loaded child documents, child window identity and cross-document messaging.
- WebRTC negotiation, ICE/candidate state, transceivers, senders/receivers, tracks and data channels.
- Service-worker registration, script loading, install/activate lifecycle events, controller changes and messages.

Completing this milestone requires separate designs and implementations for a GLSL/WebGL runtime and rasteriser, a WebAssembly engine, native media decoding/output, real workers and iframes, WebRTC transport, and service-worker execution. The current surfaces should not be read as milestone completion.
