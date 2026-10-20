# Feature failure presentation

An attempted feature that cannot display its content has a typed feature and reason. Painting and accessibility read the same state. Host permission changes update that state in the document; they do not change CSS sizing or insert replacement DOM elements.

The reasons distinguish host denial, missing graphics support, context loss, unsupported formats, decoding failures, blocked or unavailable sources, resource limits and closed frames. An ordinary transparent 2D canvas, an allowed idle media player without a source and a successfully loaded empty frame are valid content. They do not receive a failure placeholder.

Failure backgrounds and text follow the host's light or dark colour scheme, with an opaque high-contrast label. Labels use the document's font source and text shaper when available. They remain clipped to the element's used box. Accessibility names include the feature and reason, preserving an authored name as a prefix. Failed canvases acquire an image role so their failure is exposed.

Video keeps its available poster behind a short label strip. The static renderer loads PNG, JPEG and secure static SVG posters through the ordinary image resource policy, including when scripting is disabled. The legacy scripting poster loader still uses its media resource policy; its duplicate decode and memory accounting remain tracked under #537. The placeholder does not replace the poster's natural sizing or the element's explicit CSS dimensions.

Failed media loads return their shared decoder reservation. A valid audio-only stream in a video element remains playable. Successful recovery displays the available video frame separately from the poster preserved on failure. Poster changes do not restart playback. Source loads and frame startup retain ownership across reentrant host callbacks, so retirement or replacement cannot commit stale content or dispose a newer context. A failed frame source is cached until its source or configuration changes, so repainting does not repeatedly load the same refused source or dispatch new error callbacks. WebGL tracks explicit context requests, context loss, permission retirement and failed drawing-surface allocation; choosing a successful 2D context after denied WebGL removes the old failure.

The tests separate presentation from runtime evidence:

- `FeatureFallbackRenderTests` uses host-side failure sources to compare every reason in both schemes against reviewed exact-pixel references. Its hidden-content reftest also compares adjacent layout and element bounds. Static poster tests use actual PNG and SVG data sources.
- `FeatureFallbackPipelineTests` drives Jint bindings, permission updates, context loss/restoration, actual raster surfaces, refused frame loads and media failure paths through painting and accessibility. Decoder-budget recovery and oversized-canvas recovery have direct regressions.
- `FeatureFallbackLifecycleTests` covers attribute changes, playback preservation and source retirement during loaders and decoders. `FeatureFallbackContentProcessTests` reads real published bitmap pixels through permission denial, grant, revocation and recovery.
- Core tests cover authored accessibility names, hidden failures, used sizes and avoiding stale canvas pixel reads.

Run the render fixture before reviewing and approving changed references:

```powershell
dotnet tests/Folio.RenderTests/bin/Release/net10.0/Folio.RenderTests.dll -class '*FeatureFallbackRenderTests'
dotnet tests/Folio.RenderTests/bin/Release/net10.0/Folio.RenderTests.dll approve feature-fallback
```

The Windows and Linux Skia font backends produce different glyph coverage pixels even with the same bundled font. Each has separately reviewed exact-pixel references, selected explicitly by the test fixture; tolerances remain zero and Windows references are preserved.

Programmatic reference approval accepts only the fixture's registered case names. It does not approve arbitrary images without a corresponding page or registered fixture.

This presentation contract does not establish a native GPU/shader backend, timed video playback, real capture or network transport. The synthetic-stream recovery fixtures exercise the existing compatibility API and do not prove camera or microphone capture. Poster/capture memory accounting remains open under #537. Those original Milestone 6 implementation requirements remain separate, and incomplete feature implementations continue to route to the browser.
