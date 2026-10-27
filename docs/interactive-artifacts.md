# Interactive artifacts

Folio keeps scripted artifacts out of the host process. A host opts in with
`FolioOptions.Scripting = ScriptingPolicy.ContentProcess` and supplies the
path to `Folio.ContentHost` through `ContentProcessPath`. The path may point
to the published executable or to its `.dll`; Folio launches the latter with
the `dotnet` host.

The content process is deliberately capability-poor:

- scripting runs in the Jint engine with time, statement, recursion, and
  memory limits;
- the resource policy denies network and filesystem access unless the host
  explicitly handles a resource request;
- the process is placed in a Windows Job Object when available;
- heartbeat, memory, and process-exit supervision stop a wedged process;
- disposing the client terminates the process tree and waits for exit.

Static artifacts should continue using the normal in-process Folio path. A
host should classify an artifact first, enable the content process only for
`ArtifactKind.Scripted`, and keep `ArtifactKind.NeedsBrowser` as its external
browser fallback until Folio supports the required feature.

Milestone-6 routing checks both implementation availability and the host grant. WebGL, media, workers, WebAssembly, iframes, WebRTC and service workers remain browser-routed while their original feature acceptance is incomplete, even when their capability is granted. Each feature has its own implementation entry; completing one cannot unlock another. SharedWorker uses the workers capability and shares the isolated worker runtime; the remaining worker acceptance requirements still apply. The scan covers recognizable calls in script elements, event handlers and `javascript:` URLs, including common optional and literal-property access forms. It is a conservative source scan, not evaluation of dynamically constructed JavaScript; runtime capability checks remain necessary.

The host owns approvals and presentation. Folio does not know about Mana,
chat sessions, or user accounts.

## JSX and TypeScript

Folio never runs a JSX or TypeScript compiler inside a page. An artifact that asks for one is routed to the system browser by `ArtifactClassifier`, with the reason "JSX must be compiled by the host" (or "TypeScript must be compiled by the host"). That covers scripts of type `text/babel`, `text/jsx`, `application/jsx`, `text/tsx`, `text/typescript` and `application/typescript`, and calls to `Babel.transform`, `Babel.transformScriptTags`, `ts.transpile` and `ts.transpileModule`.

A host that wants such an artifact in Folio compiles it first and hands over the same page with plain JavaScript in place of each JSX or TypeScript script. Classified again, the compiled page is an ordinary scripted artifact (and the compiler's own script, which the page no longer needs, is dropped). The host keeps the original for the system browser.

The conformance corpus works the same way: an artifact that needs compiling stores its compiled form next to `index.html`, named by `compiled` in `manifest.json`. RefCapture captures the reference from `index.html`, where the browser runs the compiler, and the runner renders `compiled.html`. `tests/conformance/D2/pantry-react-jsx` is one.
