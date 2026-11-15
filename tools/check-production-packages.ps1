# Fails when a superseded package (SkiaSharp, HarfBuzzSharp, Jint, Wasmtime, Avalonia.Angle) is in the production
# projects' package graph, direct or transitive, with the production oracle switches off. Needs a restored solution.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projects = 'src/Folio', 'src/Folio.Diagrams', 'src/Folio.Scripting', 'src/Folio.WinForms', 'tools/Folio.ContentHost'
$banned = '^(SkiaSharp|HarfBuzzSharp|Jint|Wasmtime|Avalonia\.Angle)(\.|$)'
$failed = $false
# `dotnet list package` takes no -p: switches, so the production oracle switches go in as environment variables.
$env:FolioOracleHost = 'false'; $env:FolioWasmOracle = 'false'; $env:FolioAngleOracle = 'false'
foreach ($project in $projects) {
    $out = dotnet list (Join-Path $repoRoot $project) package --include-transitive
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed for $project" }
    $hits = @($out | ForEach-Object { if ($_ -match '>\s+(\S+)') { $Matches[1] } } | Where-Object { $_ -match $banned } | Sort-Object -Unique)
    if ($hits) { $failed = $true; Write-Host "${project}: superseded packages in the graph: $($hits -join ', ')" }
    else { Write-Host "${project}: clean" }
}
if ($failed) { throw 'Superseded packages leaked into the production package graph.' }
