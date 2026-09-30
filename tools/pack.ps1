# Packs Folio, Folio.Skia and Folio.WinForms into a folder that works as a local NuGet feed.
# The version is 0.1.0-m1.N, where N is the number of commits in HEAD's history, so each commit on main packs as its
# own version. The working tree must match HEAD: the version names the commit.
#
#   pwsh -File tools/pack.ps1 [-Output <folder>]    (default: artifacts/packages; powershell -File works too)
param([string]$Output = (Join-Path (Split-Path $PSScriptRoot) 'artifacts/packages'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

if (git -C $root status --porcelain --untracked-files=no) {
    throw 'Commit or stash the changes first: a package version stands for a commit.'
}
if ((git -C $root rev-parse --is-shallow-repository) -eq 'true') {
    throw 'The version counts commits, so a shallow clone would pack the wrong one: git fetch --unshallow first.'
}
$build = git -C $root rev-list --count HEAD
if ($LASTEXITCODE) { exit $LASTEXITCODE }

foreach ($project in 'Folio', 'Folio.Skia', 'Folio.WinForms') {
    dotnet pack (Join-Path $root "src/$project/$project.csproj") -c Release -o $Output "-p:FolioBuild=$build"
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
Write-Host "Packed Folio 0.1.0-m1.$build into $Output"
