# Milestone 6 status

Milestone 6 is implemented on the dependency-backed path. The work below provides bounded browser-facing behavior and capability coverage; later Folio-owned runtime replacements remain a separate track.

Current exploratory surfaces:

- WebGL resource state, limits, framebuffer/texture/query/sync operations and context loss.
- Audio/video element state, resource-backed WAV/MPEG metadata, format-aware `canPlayType()`, device tracks and playback promises.
- Dedicated workers with isolated script engines, resource-backed classic/module/blob scripts, transferable message ports, message/error events and termination limits.
- WebAssembly validation, imports/exports, bounded memory/tables/globals and a bounded i32 interpreter.
- Iframes with data or host-loaded child documents, sandbox origin/flag handling, child window identity, transferable cross-document messaging and load events.
- Static `srcdoc` frames now own a parsed child document, layout and accessibility tree; the host paints their replaced box and routes pointer and wheel input into the child context.
- Media text tracks with WebVTT parsing, cue IDs, active cue tracking, `<track>` loading and `cuechange` events.
- WebRTC negotiation, ICE/candidate state, transceivers, senders/receivers, tracks, data channels and deterministic in-process loopback transport.
- Service-worker registration, script loading, install/activate lifecycle events, controller changes and messages.

Verified capture progress (M6 #552): camera and microphone permission is host-controlled; live tracks expose
settings, capabilities and bounded synthetic video frames; `video.srcObject` can be drawn into a canvas; `FolioView`
shows a capture-in-use indicator in both in-process and content-process hosting; and a changed host permission ends
live tracks when their settings or constraints are next read. These paths are covered by the scripting suite and the
hosted Windows/Ubuntu CI matrix.

Verified issue slices now closed in the tracker include dedicated workers (#539), WebAssembly API, streaming and limits
(#543–#545), camera/microphone capture and WebRTC transport (#552–#554), iframe loading, messaging and limits
(#547–#550), media controls, captions and text tracks (#533–#537), WebGL resources and limits (#527–#531), and
service-worker lifecycle, interception, cache and limits (#555–#559). The hosted matrix covers each capability row on
both supported CI platforms.

The conformance corpus still records expected failures for D8–D11, D13 and D14 where the current artifacts require
broader GPU/media/native-WebAssembly/network or offline-reopen behavior than the deterministic host supplies. Those
are explicit acceptance gaps. The production JavaScript engine is still Jint behind `IScriptEngine`; a Folio-owned
engine, native media decoding/output and host-owned external WebRTC transport remain documented replacement work.
