# Releasing Folio

Folio is released only as a GitHub release. Nothing is pushed to NuGet.org.

## Before a release

1. The commit to release is on `main` and its `CI` run (push event) completed successfully, on both Linux and Windows.
   The release workflow checks this and refuses to continue otherwise.
2. `docs/releases/<version>.md` exists on that commit, with no `TODO` placeholders left.
3. The tag `v<version>` does not exist yet. A published release is never replaced; use a new version instead.

## Publishing

Run the **Release** workflow (Actions, Release, Run workflow) with:

- `version`: for example `0.9.0` (no leading `v`).
- `prerelease`: checked for a pre-release.
- `commit`: the full SHA to release; empty means the head of the selected branch.

The workflow packs the four packages with that exact version, publishes the NativeAOT content host, runs
`tools/test-package-consumers.ps1` against the packed feed and the published host, then creates the tag `v<version>`
on that commit and the release, with the notes from `docs/releases/<version>.md`.

## What is attached

- `Folio.<package>.<version>.nupkg` and `.snupkg` for `Folio`, `Folio.Diagrams`, `Folio.Scripting` and `Folio.WinForms`
- `Folio.ContentHost-<version>-win-x64.zip`: the whole content host directory, to be extracted and deployed as one
  directory, including `fonts`
- `SHA256SUMS.txt`

## Verifying a download

Put the downloaded files and `SHA256SUMS.txt` in one directory. In PowerShell:

```powershell
Get-Content SHA256SUMS.txt | ForEach-Object {
    $hash, $name = $_ -split '  ', 2
    $actual = (Get-FileHash -LiteralPath $name -Algorithm SHA256).Hash
    "{0}  {1}" -f ($(if ($actual -eq $hash) { 'OK    ' } else { 'FAILED' })), $name
}
```

With `sha256sum`: `sha256sum -c SHA256SUMS.txt`.

## Release notes

Release notes must state honestly what is not met: every performance budget that is currently missed, with the measured
value next to the target, and every feature that is disabled by default or incomplete. Notes may not describe a
target as met unless the CI run linked in them measured it.
