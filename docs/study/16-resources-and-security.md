# Resource loading and the security model

Folio renders content written by a language model. It must be treated as untrusted input: it can be malformed, huge, or crafted to exfiltrate data (a tracking image URL) or hang the host.

## What the specs require

- [URL Standard](https://url.spec.whatwg.org/) (parsing and resolution against the document base, `<base href>`), [Fetch: `data:` URLs](https://fetch.spec.whatwg.org/#data-urls), [MIME Sniffing](https://mimesniff.spec.whatwg.org/) (image type detection by signature), [Subresource Integrity](https://www.w3.org/TR/SRI/) (`integrity="sha384-…"`).
- Where resources come from: `<img src/srcset>`, `<picture><source>`, `<link rel=stylesheet>`, `@import`, CSS `url()` in backgrounds/borders/list markers/masks, `@font-face src`, SVG `<image>`/`href`, and from M4 `<script src>`.

## Hard parts and pitfalls

- **Exfiltration by URL**: any network fetch leaks that the artifact was opened, plus whatever the URL encodes.
- **Path traversal** for local files: `..`, absolute paths, UNC paths (`\\server\share`), device paths (`\\.\`, `\\?\`), NTFS alternate data streams (`file.txt:stream`), reserved names (`CON`, `NUL`), and symlinks/junctions that point outside the allowed folder.
- **Decompression bombs**: a 10 KB PNG that decodes to 50,000 × 50,000 pixels; WOFF2 fonts that expand hugely; deeply nested SVG `use`.
- **Parser-level denial of service**: huge inputs, deep nesting, quadratic cases.
- **CDN scripts** (M5) change under the same URL unless the URL is versioned; a compromised CDN serves hostile code.

## Options

| Option | Pros | Cons |
|---|---|---|
| A. Folio fetches URLs itself with an on/off switch | Easy for hosts | Folio makes security decisions for every host; hard to audit |
| B. All loads go through a host-supplied `IResourceLoader`; Folio ships a few ready-made loaders the host can compose | The host decides and sees every request; the default allows nothing | Hosts must opt in to anything beyond `data:` |

## Decision

**Option B, deny by default.**

- **`IResourceLoader.LoadAsync(ResourceRequest, CancellationToken) → ResourceResponse`**. The request carries the absolute URL, the kind (`Image`, `Stylesheet`, `Font`, `Script`, `SvgImage`), and the integrity metadata if any. A loader may answer, refuse, or pass to the next loader.
- **Default**: `data:` URLs only (decoded in-process with a size cap). Everything else is refused and reported as a diagnostic, and the page renders without it (images show `alt` text).
- **Loaders Folio ships** (host opts in):
  - `LocalFolderLoader(root)`: serves files under one folder. Paths are resolved with `Path.GetFullPath`, then the final target is resolved through links (`FileSystemInfo.ResolveLinkTarget`) and must still be under `root` (case-insensitive). UNC, device paths, alternate data streams and reserved names are refused.
  - `AllowlistHttpLoader` (M5; fonts and styles from M2): HTTPS only; origins on the host's allowlist (exact host, optional path prefix, allowed resource kinds per origin); uses `HttpClient` with no cookies, no credentials, no `Referer`, a fixed user-agent string, redirects only to allowlisted origins, response size caps, timeouts, and content-type checks (scripts and styles must have script/CSS MIME types).
  - **Local cache** for the HTTP loader: content-addressed by SHA-256 in a host-provided folder with a size cap. Versioned URLs (the path contains a version) are cached as immutable; unversioned URLs are refused by default (the host may allow revalidation).
  - **Integrity**: if the element has `integrity`, the response must match it. The host can also supply **pinned hashes** for known library URLs and a policy "scripts require integrity", in which case unpinned scripts without `integrity` are refused. The cache only stores responses that passed integrity checks.
- **Navigation never happens inside Folio**: link activation and form submission are events for the host ([interaction](15-interaction.md)). `<meta http-equiv=refresh>` is ignored; `<base href>` only affects URL resolution.
- **Inert content until M4**: `<script>`, inline event handler attributes (`onclick`), `javascript:` URLs, `<iframe>`, `<object>`, `<embed>` are never executed or loaded. From M4, scripts run only in the [sandbox](17-scripting.md) and only when the host enables scripting for that document.
- **No sanitiser pass** is needed for static rendering: nothing in HTML/CSS can act without scripts or loads, both of which are gated here. (Hosts that re-export HTML elsewhere should sanitise for that destination themselves.)
- **Limits** (host-configurable, defaults below). Exceeding one stops that part of the work, keeps what is done, and reports a diagnostic; it never throws into the host.

| Limit | Default |
|---|---|
| HTML input size | 16 MB |
| DOM nodes | 200,000 |
| Element nesting depth | 512 |
| Stylesheet size / rules | 8 MB / 100,000 |
| `@import` depth | 4 |
| Image dimensions (checked from the header before allocating) | 16,384 px per side, 64 megapixels |
| Total decoded image memory per document | 64 MB (least recently painted images evicted and re-decoded on demand) |
| Font file size (after WOFF/WOFF2 decoding) | 20 MB |
| Total loaded bytes per document | 64 MB |
| SVG `use` expansion | 10,000 elements |
| Time per `Update()` | 2 s, checked cooperatively inside parsing, style and layout loops |

- **Untrusted binary formats** (PNG, JPEG, fonts, WOFF/WOFF2, SVG/XML) are parsed with bounds-checked readers and fuzzed continuously ([testing](19-testing.md)).
- **No telemetry**, no background requests, no persistent state unless the host provides a cache folder.
