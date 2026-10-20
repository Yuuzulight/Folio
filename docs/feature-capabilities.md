# Host feature capabilities

`FolioOptions.Capabilities` grants features to one document. All grants default
to `false`: `WebGl`, `Media`, `Sound`, `Workers`, `WebAssembly`, `Iframes`,
`Camera`, `Microphone`, `WebRtc` and `ServiceWorkers`.

For example, a host permitting media playback with sound can configure:

```csharp
new FolioOptions
{
    Capabilities = new FeatureCapabilities { Media = true, Sound = true }
};
```

`Media` alone permits silent playback. `AllowSoundAutoplay` is a separate
autoplay policy and never grants sound. Camera and microphone grants are
independent of peer connections. The optional `MediaCapturePermission` callback
can further restrict granted capture; it cannot override a denied grant.
Child-frame grants intersect the parent's grants with the frame's `allow`
policy, so a frame cannot grant itself a feature its parent lacks.

For an installed in-process scripting context, use
`DomBindings.UpdateCapabilities(engine, capabilities)`. For a separate content
process, use `ContentProcessClient.UpdateCapabilitiesAsync`; an attached
`FolioView` exposes the same update operation. The view orders document loads
and updates and rejects updates queued for a replaced document. An update's
completion confirms the transport write. The child's `Capabilities` message
confirms application; its payload includes the request sequence and effective
grants. `FolioView.Capabilities` describes the requested set.

Invalid capability updates fail closed and receive `accepted: false` with an
empty effective set. A new HTML document with omitted grants starts denied;
script-only observations without a capability field preserve the current
document's grants. Repeated use of an ungranted feature produces one diagnostic
per feature per document.

Revoking sound flushes and stops output; regrant requires another playback
request. Revoking camera or microphone ends the corresponding capture tracks.
Revoking peer connections or service workers retires existing leases; regrant
permits fresh objects and does not revive retired handles. Revoking workers
terminates them and their descendants. Retained WebGL contexts cannot draw,
allocate resources or restore themselves while permission is denied.

These grants control the implementations that exist. They do not establish
native GPU/GLSL execution, moving video/audio IPC, real camera/microphone
capture, network WebRTC, native WebAssembly or isolated service workers. Those
remain listed in the [Milestone 6 acceptance audit](milestone-6-audit.md).
Window `caches` is independently governed by the host storage configuration and
quota described in [Cache API storage](cache-api-storage.md).
