# Consumer readiness audit — 2026-11-07

This audit follows the merged production-host work in #699. Its purpose is to check actual public consumption and Mana integration, not infer readiness from closed milestones. The user selected integration repairs first, with missing original feature systems reported separately. No stable release is published by this change.

## Confirmed integration defects and repairs

| Area | Reproduction / consequence | Repair and regression evidence |
| --- | --- | --- |
| Package dependency closure | A fresh external application restoring the production feed failed with NU1101 for Folio.Diagrams. In-repository builds concealed the missing package. | Make Diagrams packable, pack all four production packages, and restore consumers from an empty private cache using only the generated feed. |
| Public headless rendering | README required the Skia adapter; the production feed did not contain that adapter. | Portable Folio.Rendering.HeadlessRenderer uses Folio's rasterizer, complex shaper and PNG encoder. Test visible text, pixels, clipping limits, disposal and PNG round trips. Skia stays explicit. |
| Default fonts | The content host had bundled fonts, but public package consumers/control defaults did not. Replacing control options could also erase fonts. | Shared lazy BundledFontSource, transitive fonts/notices in output and publish, default normalization that preserves explicit font sources and other policies. |
| Offline resource interception | The control omitted ResourceInterceptor, and Document selected the data-only loader when no network loader was supplied. | Preserve the interceptor and permit host-owned offline responses without granting network access. Fresh public-control and headless regressions. |
| Host service forwarding | The public control forwarded capture providers but dropped media decode/output and the capture permission callback. | StartWithOptions forwards the host services; the control marshals permission to its UI thread using cancellable dispatch. Parent independently checks grants before opening a device. |
| Capture teardown | Event handlers could retain the client after a capture session retired; revocation could race provider.Open. | Owned capture leases detach every subscription and dispose once; grant/generation checks retire a session opened during revocation. Raw-child probe tests cannot bypass parent authority. |
| Prewarming | Changing options before first load could reuse a process with the previous artifact/storage/provider scope. | Reuse only when the options object is unchanged; otherwise retire and recreate. External deployed-control regression exercises provider replacement. |
| Build provenance | Invoking scripts from another repository could select that repository's SDK; package versions did not support a tagged stable version. | Build under Folio's root, include source commit in packages and native manifest, record dirty state, support an explicit package version, and reject untracked source changes in the formal pack script. |

`tools/test-package-consumers.ps1` creates applications outside the checkout, with no project references, cleared package sources and a new package cache. The portable app references only Folio; the Windows app references only Folio.WinForms. The deployed pass copies the standalone host into that application, executes a real canvas script, exercises permission/prewarm wiring with fake devices, and checks owned-process exit. Evidence is retained under artifacts/consumer-smoke. CI runs the package checks on Windows/Linux and the deployed-host check on Windows.

## Mana integration contract

Mana must pin a concrete Folio commit and matching package version, build all four packages, and deploy the whole standalone host directory. Its three artifact/document/reader views should use BundledFontSource rather than Folio.Skia. The helper verifies manifest commit, architecture, clean source and executable hash; launcher publication must fail rather than silently omit the script host.

Automatic updates select **published stable GitHub releases only**, excluding drafts, prereleases and untagged main commits. Resolve the release tag to its commit and require green Windows/Ubuntu CI. Package that tagged commit as its semantic version. Existing proposals based solely on main must not become automatically mergeable through this change. An audit integration pin/PR is not a published release.

## Remaining original feature blockers

These are feature gaps or unqualified acceptance, not defects that can be closed by declaring the existing subset complete.

* **Production routing:** ArtifactClassifier.ProductionFeatures enables WebAssembly only among M6 features. WebGL, media, workers, iframes, WebRTC and service workers remain browser-routed. Internal tests can enable future implementations explicitly; those tests do not prove public routing promotion. Do not flip flags without acceptance evidence.
* **Remote WebRTC:** WebRtcBindings pairs peers inside one page and passes local objects. SDP-like strings and in-page data/video loopback do not supply remote ICE/DTLS/SCTP or encoded media transport. Original remote/device/network scope remains unmet even though loopback tests pass.
* **Real devices and playback:** Capture providers and media decode/output are host interfaces, not shipped universal camera/microphone/video/audio adapters. Fake-device approval, revocation, budgets and lifecycle regressions are useful evidence, but real hardware, device-level synchronization and optional-format qualification remain separate.
* **Advanced child-host option transport:** ContentDocumentRuntime builds a new options object inside the child. Host WebRtcAllowedOrigins and IframeNavigationRequested are not transported as complete cross-process contracts. StartWithOptions intentionally documents host services, not arbitrary options cloning. These advanced paths require their own policy protocol and end-to-end tests before promotion.
* **Iframe and worker WebRTC routing:** `RTCPeerConnection` is pinned as denied (`undefined`) in all worker environments (dedicated, shared, and service workers). Iframes require explicit feature policy enablement (`allow="webrtc"`); if omitted or denied (`allow="webrtc 'none'"`), `new RTCPeerConnection()` throws `NotSupportedError`. Nested iframes share the root document's `WebRtcDocumentScope` (enforcing a single aggregate `RtcMediaBudget` and connection cap without budget multiplication). Cross-origin iframes cannot obtain broader `WebRtcAllowedOrigins` than their parent. Revocation of WebRTC or parent navigation teardown cascades across nested child iframes, closing all active connections and returning slots to the shared scope.
* **Per-feature isolation and interruption:** Passing the baseline restricted-process checks does not prove every original feature-specific matrix, runaway shader interruption or complete independent service-worker lifecycle requirement. Use the requirement-by-requirement evidence in milestone-6-audit.md and current runtime checks; historical counts in that document are dated snapshots.
* **Compatibility scope:** Bundled fonts cover a finite licensed corpus; the engine is an artifact subset, not full HTML/CSS/JavaScript/browser conformance. General browser compatibility must not be advertised.

## Evidence and claim boundaries

Focused parent media/capture/resource regressions passed 20/20 before the final cancellation/offline regressions were added. Mana's release-only updater passed 18/18. The earlier merged #699 performance qualification applies to that source revision; these new integration repairs do not constitute a fresh timing qualification. Final build/package/native-host/full-suite results must be recorded against the audit commit and CI before a release decision.

The original pre-fix external restore failure and repaired package logs are retained locally under artifacts/consumer-baseline-feed, artifacts/consumer-fixed-feed and artifacts/consumer-smoke. These generated artifacts are not source files or release assets. The source harness is the reproducible evidence path for another machine.

Folio can be evaluated for the verified artifact scope after integration checks pass. It cannot yet be described as completing every original feature or as having no remaining production blockers. A release and broader feature work are separate decisions.
