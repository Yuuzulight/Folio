These 32×32 PNG goldens were rendered independently by Google Chrome
155.0.8059.85, with ANGLE SwiftShader, on 22 October 2026. Folio's tests run the
same `draw.js` through the generated bindings and ANGLE D3D11 WARP, then compare
every RGBA byte of the resulting canvas bitmap against these images.

Each HTML page sets its draw mode and exports the canvas PNG into the body's
`data-reference` attribute. Capture using a fresh, isolated profile and these
Chrome arguments:

```
--headless=new --no-sandbox --use-gl=angle --use-angle=swiftshader
--enable-unsafe-swiftshader --user-data-dir=<temporary-profile> --dump-dom <file-url>
```

Decode the base64 `data:image/png` attribute to replace a golden. Review the
image before committing it. Never regenerate a reference from Folio's output.
