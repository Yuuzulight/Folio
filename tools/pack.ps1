# Packs the complete production package graph into a local NuGet feed.
# The version is 0.1.0-m1.N, where N is the number of commits in HEAD's history, so each commit on main packs as its
# own version. The working tree must match HEAD: the version names the commit.
#
#   pwsh -File tools/pack.ps1 [-Output <folder>]    (default: artifacts/packages; powershell -File works too)
param(
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot) 'artifacts/packages'),
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

if (git -C $root status --porcelain --untracked-files=all) {
    throw 'Commit or stash the changes first: a package version stands for a commit.'
}
if ((git -C $root rev-parse --is-shallow-repository) -eq 'true') {
    throw 'The version counts commits, so a shallow clone would pack the wrong one: git fetch --unshallow first.'
}
$build = git -C $root rev-list --count HEAD
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$commit = git -C $root rev-parse HEAD
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw 'Version must be a NuGet version such as 1.0.0 or 0.1.0-preview.1.'
}
$Output = [IO.Path]::GetFullPath($Output)
Push-Location -LiteralPath $root
try {
    foreach ($project in 'Folio', 'Folio.Diagrams', 'Folio.Scripting', 'Folio.WinForms') {
        $packArgs = @('pack', (Join-Path $root "src/$project/$project.csproj"), '-c', 'Release', '-o', $Output, "-p:FolioBuild=$build", "-p:RepositoryCommit=$commit")
        if ($Version) { $packArgs += "-p:Version=$Version" }
        dotnet @packArgs
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
} finally { Pop-Location }
Write-Host "Packed the production Folio package graph from commit-count build $build into $Output"
