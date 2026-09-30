# Painting: display list and the SkiaSharp backend

## What the specs require

- Paint order: [CSS 2.2 Appendix E](https://www.w3.org/TR/CSS22/zindex.html) (see [stacking](10-layout-positioning-overflow-stacking.md)).
- [CSS Backgrounds and Borders 3](https://www.w3.org/TR/css-backgrounds-3/): multiple background layers, `background-size/position/repeat/origin/clip/attachment`, border styles, `border-radius` (elliptical corners, [overlap scaling](https://www.w3.org/TR/css-backgrounds-3/#corner-overlap)), `border-image`, `box-shadow` (outer/inset, blur, spread; outer shadows are clipped out of the border box).
- [CSS Images 3](https://www.w3.org/TR/css-images-3/) / [Images 4](https://www.w3.org/TR/css-images-4/): linear/radial/conic and repeating gradients, colour-stop fix-up, `object-fit`, `object-position`, `image-rendering`; interpolation colour space from [Color 4](https://www.w3.org/TR/css-color-4/#interpolation).
- [CSS Transforms 1](https://www.w3.org/TR/css-transforms-1/) (2D), [Transforms 2](https://www.w3.org/TR/css-transforms-2/) (3D, individual `translate`/`rotate`/`scale` properties).
- [Compositing and Blending 1](https://www.w3.org/TR/compositing-1/): `opacity` as group opacity, `mix-blend-mode`, `isolation`.
- [Filter Effects 1](https://www.w3.org/TR/filter-effects-1/) (`filter`), [Filter Effects 2](https://drafts.fxtf.org/filter-effects-2/) (`backdrop-filter`), [CSS Masking 1](https://www.w3.org/TR/css-masking-1/) (`clip-path`, `mask`).
- [CSS Text Decoration 3](https://www.w3.org/TR/css-text-decor-3/): underline position/thickness, `text-decoration-skip-ink`, `text-shadow`.
- [CSS UI 4](https://www.w3.org/TR/css-ui-4/): `outline`, `outline-offset`, `accent-color`, `caret-color`.

## Hard parts and pitfalls

- **Borders with radii and different side colours/styles** need per-side clipping along the corner diagonal; dashed/dotted borders must look even around corners.
- **Box shadows**: CSS blur radius is 2× the Gaussian standard deviation. Outer shadows must not draw under a transparent box. Inset shadows need a clip plus an inverse rounded rect. Large blurs are expensive: cache by (size, radii, blur, spread).
- **Group opacity** needs an offscreen layer (`SaveLayer`); per-item alpha multiplication is only correct when the group has one non-overlapping item.
- **Gradient colour interpolation**: CSS interpolates in premultiplied sRGB by default (or the requested space); transparent stops must not produce grey fringes. Hard stops need exact positions.
- **Clipping** by rounded `overflow` boxes, antialiased, without seams between adjacent clipped content.
- **Pixel snapping**: fractional layout positions must not blur 1px borders or produce hairline gaps.
- **Scrolling** must not rebuild everything; **hover** should not re-raster the whole window.
- **Raster memory**: a full-HD backing surface is 8 MB; 4K is 33 MB.

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Immediate mode: walk fragments and call SkiaSharp directly | Least code | Painting tied to SkiaSharp; can't cull, cache or test paint output without pixels; scrolling repaints by re-walking layout |
| B. Flat display list of Folio's own commands (with push/pop for clip, transform, opacity layer, scroll offset), replayed onto an `ICanvas` | Backend-independent; cull by bounds; textual dumps make paint testable; scroll = change offset and replay | One more data structure |
| C. Display list plus separate transform/clip/effect/scroll trees and a compositor with cached layers | Cheapest scrolling and animation at scale; partial invalidation | A lot of machinery that small documents don't need yet |

## Decision

**Option B now, designed so option C can grow out of it later.**

- **Milestone split**: M1 paints solid and gradient backgrounds and background images, all border styles with radii, outlines, box and text shadows, images, text with decorations, `opacity`, and overflow clipping (gradients and shadows moved into M1 because most S1 and S3 artifacts in the conformance corpus use them). M2 (static parity) adds transforms, filters, `backdrop-filter`, blend modes, `clip-path`, masks and `border-image`. Until then, unsupported paint properties are skipped and reported as diagnostics.

- **Display list**: an array of compact item structs (command kind, bounds, index into side arrays for paths/glyph runs/images/gradients). Commands: `FillRect`, `FillRoundedRect`, `StrokeBorder` (the full border description, drawn by the backend helper), `FillPath`, `DrawGlyphRun`, `DrawImage`, `DrawGradient`, `DrawBoxShadow`, `PushClipRect/RoundedRect/Path`, `PushTransform`, `PushOpacityLayer`, `PushFilterLayer`, `PushBlendLayer`, `PushScrollFrame(scrollId)`, `Pop`. There are no hit-test items: hit testing walks the [stacking tree](10-layout-positioning-overflow-stacking.md) directly.
- **Built from** the stacking tree in paint order. Out-of-view items are still recorded (the list is cheap); culling happens at replay against the dirty rectangle.
- **Scroll frames**: `PushScrollFrame` reads the current offset from `ScrollState` at replay time, so scrolling (and sticky offsets) never rebuilds the list.
- **`ICanvas`** is the backend boundary (see [dependencies](../dependencies.md)). Implementations: `SkiaCanvas` (SkiaSharp) and `RecordingCanvas` (writes a text log, used by tests).
- **Rasterisation**: CPU raster through SkiaSharp into the control's backing bitmap, repainting only the invalidated rectangle (union of damaged item bounds) on hover/selection changes. GPU rendering is a later option; the display list does not depend on it.
- **Borders**: solid, dashed, dotted, double, groove, ridge, inset, outset; elliptical radii with overlap scaling; per-side colours with diagonal corner joins; dotted/dashed spacing adjusted to fit each side.
- **Shadows**: outer and inset, multiple, spread, blur via Gaussian mask; blurred shadow masks cached; `text-shadow` via glyph-run blur.
- **Gradients**: linear (angles, `to` corners with the spec's corner-angle rule), radial (shapes, extent keywords), conic, repeating variants, hints, interpolation in premultiplied sRGB or the requested `in <space>`, with extra stops inserted when the space is not sRGB.
- **Images**: `DrawImage` with source/destination rects, `object-fit`/`object-position`, `background-repeat` (including `space`/`round`), mipmapped downscaling, `image-rendering: pixelated`.
- **Transforms**: full 2D including individual `translate`/`rotate`/`scale`; 3D transforms are flattened to their 2D projection (no `preserve-3d`), `perspective` ignored with a diagnostic. `backface-visibility: hidden` honoured for 180° flips (common in card-flip artifacts).
- **Effects**: `opacity` (optimised to per-item alpha when the group is one item), `filter` (blur, brightness, contrast, grayscale, sepia, saturate, hue-rotate, invert, opacity, drop-shadow), `backdrop-filter` (blur + colour filters), `mix-blend-mode`, `clip-path` basic shapes (`inset`, `circle`, `ellipse`, `polygon`, `path`), `mask-image` with gradients.
- **Text**: glyph runs drawn by glyph id with subpixel positioning and, from M1, subpixel (LCD) antialiasing that follows the system's subpixel text setting and pixel layout; text drawn where the backdrop is not known to be opaque (inside opacity/filter/blend layers, under non-integer transforms, on transparent surfaces) falls back to greyscale antialiasing, as does headless rendering unless the caller asks for subpixel; decorations (underline/overline/line-through, styles solid/double/dotted/dashed/wavy, `text-decoration-skip-ink: auto` using glyph intercepts, `text-underline-offset`, thickness); selection highlight drawn behind glyphs.
- **Snapping**: border boxes snap edge-by-edge to device pixels; transforms other than integer translation disable snapping for their subtree.
- **Colour**: everything in sRGB, 8-bit per channel surfaces, premultiplied alpha.
