param(
    [string]$Destination = 'artifacts/content-host',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Destination)) { $Destination = Join-Path $repoRoot $Destination }
$buildDirectory = Join-Path $repoRoot "artifacts/content-host-build/native-noncomposite-$Runtime"

# The normal solution build and experimental composite/oracle publishes must
# never supply incremental binaries for the shipped production host.
dotnet publish (Join-Path $PSScriptRoot 'Folio.ContentHost/Folio.ContentHost.csproj') `
    -c Release -r $Runtime --self-contained false `
    -p:FolioOracleHost=false -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=false `
    --artifacts-path $buildDirectory -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'Production content-host publish failed.' }

$superseded = Get-ChildItem -LiteralPath $Destination -Recurse -File | Where-Object {
    $_.Name -match '^(Jint|Folio\.Scripting\.Jint|Folio\.Skia|Wasmtime|SkiaSharp|libSkiaSharp|HarfBuzzSharp|libHarfBuzzSharp|av_libglesv2|av_libegl|Avalonia\.Angle)(\.|$)'
}
if ($superseded) { throw "Superseded engine in production publish: $($superseded.Name -join ', ')" }
if (Get-ChildItem -LiteralPath $Destination -Filter '*.r2r.dll') { throw 'Unexpected composite image in non-composite production publish.' }

$fontDirectory = Join-Path $Destination 'fonts'
$fontPins = @(Get-Content (Join-Path $fontDirectory 'SHA256SUMS') | Where-Object { $_ -match '^[0-9a-f]{64}  (SourceSans3|SourceCodePro|SourceSerif4|Noto)' })
if ($fontPins.Count -ne 20) { throw 'The production font manifest must contain all 20 shipped faces.' }
foreach ($pin in $fontPins) {
    $fontPath = Join-Path $fontDirectory $pin.Substring(66)
    if (-not (Test-Path -LiteralPath $fontPath) -or (Get-FileHash -LiteralPath $fontPath -Algorithm SHA256).Hash -ne $pin.Substring(0, 64)) {
        throw "Published font is missing or does not match its pin: $($pin.Substring(66))"
    }
}
foreach ($notice in 'LICENSES.md', 'SourceCodePro-OFL.txt', 'SourceSerif4-OFL.txt', 'NotoSansArabic-OFL.txt', 'NotoSansHebrew-OFL.txt', 'NotoSansCJK-OFL.txt', 'NotoColorEmoji-OFL.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $fontDirectory $notice))) { throw "Missing font notice: $notice" }
}
Write-Host "Verified production host: $Destination"
