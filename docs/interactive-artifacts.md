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

The host owns approvals and presentation. Folio does not know about Mana,
chat sessions, or user accounts.
