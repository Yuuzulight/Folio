# Headless content broker

`Folio.ContentHost.exe --broker --arcana-parent-pid=<pid> [--allow-origin=https://host[/path-prefix]]` starts the Folio-owned headless broker. Repeat `--allow-origin` for each permitted HTTPS origin or path prefix. Without those flags, network loads are denied. With them, the host uses Folio's allowlist resource loader for top-level documents and subresources; the content worker itself has no direct network access. Redirects and resource kinds remain subject to the configured allowlist.

The broker owns one page and starts the same build's content worker through `ContentProcessClient`, which applies the Windows AppContainer and Job Object limits before the worker resumes. It watches the Arcana parent process and exits when that process ends, disposing the sandboxed worker job. The broker refuses to start on platforms without that sandbox and refuses `FOLIO_UNSANDBOXED=1`.

The transport is newline-delimited UTF-8 JSON. Each request has an `id` and an `op`; every response echoes the `id` and includes `ok`. Errors use `{ "ok": false, "error": { "code": "...", "message": "..." } }`.

Supported operations:

- `hello` reports protocol version, engine identity, and the sandbox/network boundary.
- `load` takes inline `html` and an optional `urlLabel` (`about:blank` or a `fixture:` label). It returns the current script result, rendered display-item count, diagnostics, worker sequence, and any shared canvas-frame information.
- `navigate` takes an HTTPS `url` covered by a configured allow-origin policy, fetches its top-level document through Folio's host resource broker, and loads it at the final URL after allowed redirects. Failures use `policy_refused`, `navigation_failed`, or `timeout` error codes.
- `evaluate` evaluates an expression in the current document and returns the same render result.
- `snapshot` returns the Arcana page snapshot shape. Element references remain stable for the page process lifetime and are not reused when a document is replaced.
- `screenshot` evaluates a no-op against the current document, validates the latest host-owned RGBA frame and its sequence, then returns that real frame as PNG bytes in `data` (base64), with `mimeType`, `width`, `height`, and `sequence` metadata. The response is derived from the content worker frame; it is not a placeholder or a second renderer.
- `close` stops the page worker and exits the broker.

The broker has no persistent storage, cookie jar, or browser session. Page actions return the stable `unsupported` error code until their behavior is implemented and verified. `fixture:` is a document label; it does not trigger a fetch. Evaluate JavaScript and then request a snapshot to observe DOM changes.
