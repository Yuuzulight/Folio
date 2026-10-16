# Conformance library cache

Library-based conformance artifacts load their libraries from pinned URLs. CI has no network, so the runner
(`Folio.RenderTests.ConformanceCache`) serves these files by URL, and `Folio.RefCapture` captures references from the same
bytes (it rewrites the library URLs in the page to these files), so Folio and the reference browser run identical library code.

- `cache.json` lists each library: its URL, the file under `files/`, its pin in Subresource Integrity form (`sha384-...`), its
  licence and the licence text under `licenses/`.
- An artifact loads a library with `<script src="URL" integrity="PIN" crossorigin="anonymous">`; the cache tests fail when the
  URL is not cached, when the pin on the element is not the cache's, when a file no longer matches its pin, and when a licence is missing.
- Add a library with `tools/Add-ConformanceLibrary.ps1 -Url ... -Licence ... -LicenceUrl ...`. Files are never converted
  (`.gitattributes` marks them `-text`): the pins are over the bytes.

## Capturing references

`tools/Folio.RefCapture/refcapture.local.json` (local, not committed) names the reference browser. With Chrome, these arguments
reproduce the committed references byte for byte (`--font-render-hinting=none` and `--disable-lcd-text` matter: without them
the glyph edges differ from Folio's by about 0.7% of pixels):

```json
{
  "primary": {
    "executable": "C:\Program Files\Google\Chrome\Application\chrome.exe",
    "measure": ["--headless=new", "--disable-gpu", "--no-first-run", "--user-data-dir={profile}", "--window-size={width},{height}",
                "--virtual-time-budget=3000", "--dump-dom", "{page}"],
    "screenshot": ["--headless=new", "--disable-gpu", "--no-first-run", "--hide-scrollbars", "--user-data-dir={profile}",
                   "--window-size={width},{height}", "--virtual-time-budget=3000", "--force-device-scale-factor={scale}",
                   "--screenshot={output}", "--font-render-hinting=none", "--disable-lcd-text", "{page}"]
  }
}
```
