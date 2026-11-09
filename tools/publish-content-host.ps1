param(
    [string]$Destination = 'artifacts/content-host',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Destination)) { $Destination = Join-Path $repoRoot $Destination }
$Destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
$root = [IO.Path]::GetPathRoot($Destination).TrimEnd('\', '/')
if ($Destination -eq $root -or $Destination -eq $repoRoot -or $repoRoot.StartsWith($Destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish destination must be a dedicated content-host directory, not a drive or repository ancestor.'
}
if (Test-Path -LiteralPath $Destination) {
    $existing = Get-Item -LiteralPath $Destination
    if (-not $existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Publish destination must be an ordinary directory.' }
    if (@(Get-ChildItem -LiteralPath $Destination -Force).Count -gt 0 -and
        (-not (Test-Path -LiteralPath (Join-Path $Destination 'Folio.ContentHost.exe')) -or
         -not (Test-Path -LiteralPath (Join-Path $Destination 'fonts/SHA256SUMS')))) {
        throw 'Refusing to replace a nonempty directory that is not a Folio content-host publish.'
    }
}
$parentDirectory = Split-Path -Parent $Destination
New-Item -ItemType Directory -Path $parentDirectory -Force | Out-Null
$stageDirectory = Join-Path $parentDirectory ('.folio-publish-stage-' + [Guid]::NewGuid().ToString('N'))
$backupDirectory = Join-Path $parentDirectory ('.folio-publish-backup-' + [Guid]::NewGuid().ToString('N'))
$buildDirectory = Join-Path $repoRoot "artifacts/content-host-build/native-aot-$Runtime"

function Assert-PublishSibling([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if ((Split-Path -Parent $absolute) -ne $parentDirectory -or (Split-Path -Leaf $absolute) -notlike '.folio-publish-*') {
        throw "Unsafe publish cleanup target: $absolute"
    }
    if ((Test-Path -LiteralPath $absolute) -and ((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Publish cleanup target is a link: $absolute"
    }
}

Push-Location -LiteralPath $repoRoot
try {
    # Build separately, validate completely, then exchange directories. An incremental publish must not retain
    # framework-dependent DLLs or replace a working artifact with a partially compiled helper.
    dotnet publish (Join-Path $PSScriptRoot 'Folio.ContentHost/Folio.ContentHost.csproj') `
        -c Release -r $Runtime --self-contained true -warnaserror `
        -p:FolioOracleHost=false -p:FolioAngleOracle=false -p:FolioWasmOracle=false `
        -p:PublishAot=true -p:PublishReadyToRun=false -p:PublishReadyToRunComposite=false `
        -p:IlcTreatWarningsAsErrors=true --artifacts-path $buildDirectory -o $stageDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Production native content-host publish failed.' }

    $executable = Join-Path $stageDirectory 'Folio.ContentHost.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw 'Native content-host executable is missing.' }
    $sidecars = @(Get-ChildItem -LiteralPath $stageDirectory -Recurse -File | Where-Object {
        $_.Extension -eq '.dll' -or $_.Name -like '*.deps.json' -or $_.Name -like '*.runtimeconfig.json'
    })
    if ($sidecars.Count) { throw "Standalone native publish contains runtime/engine sidecars: $($sidecars.Name -join ', ')" }

    if ($PSVersionTable.PSEdition -eq 'Core') {
        Add-Type -AssemblyName System.Reflection.Metadata
    } else {
        $metaPath = (Get-ChildItem -Path "$env:ProgramFiles\dotnet\sdk" -Filter "System.Reflection.Metadata.dll" -Recurse | Where-Object { $_.FullName -like "*net472*" } | Select-Object -First 1).FullName
        if ($metaPath) { [System.Reflection.Assembly]::LoadFrom($metaPath) | Out-Null }
    }
    $stream = [IO.File]::OpenRead($executable)
    $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if ($reader.HasMetadata) { throw 'Production executable contains managed metadata instead of NativeAOT code.' }
        $expectedMachine = if ($Runtime -eq 'win-arm64') { 'Arm64' } else { 'Amd64' }
        if ($reader.PEHeaders.CoffHeader.Machine.ToString() -ne $expectedMachine) { throw 'Native executable architecture does not match the requested runtime.' }
    } finally { $reader.Dispose(); $stream.Dispose() }

    $assetsPath = Join-Path $buildDirectory 'obj/Folio.ContentHost/project.assets.json'
    $rawAssets = Get-Content -LiteralPath $assetsPath -Raw
    $compilerPackages = @(
        if ($PSVersionTable.PSEdition -eq 'Core') {
            (ConvertFrom-Json $rawAssets -AsHashtable).libraries.Keys | Where-Object { $_ -match '(^|\.)Microsoft\.DotNet\.ILCompiler/' }
        } else {
            (ConvertFrom-Json $rawAssets).libraries.PSObject.Properties.Name | Where-Object { $_ -match '(^|\.)Microsoft\.DotNet\.ILCompiler/' }
        }
    )
    if ($compilerPackages.Count -lt 2) { throw 'Native compiler package provenance is incomplete.' }
    foreach ($package in $compilerPackages) {
        if ([version]($package.Split('/')[-1]) -lt [version]'10.0.14') { throw "Unpatched native runtime/compiler: $package" }
    }
    $fontDirectory = Join-Path $stageDirectory 'fonts'
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

    $manifest = [ordered]@{
        format = 1; runtime = $Runtime; sdk = (dotnet --version).Trim()
        sourceCommit = (git -C $repoRoot rev-parse HEAD).Trim()
        sourceDirty = [bool](git -C $repoRoot status --porcelain --untracked-files=all)
        compilerPackages = $compilerPackages
        executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
        fontManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $fontDirectory 'SHA256SUMS') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stageDirectory 'native-build.json') -Encoding utf8

    Assert-PublishSibling $stageDirectory
    Assert-PublishSibling $backupDirectory
    if (Test-Path -LiteralPath $Destination) { Move-Item -LiteralPath $Destination -Destination $backupDirectory }
    try { Move-Item -LiteralPath $stageDirectory -Destination $Destination }
    catch {
        if ((Test-Path -LiteralPath $backupDirectory) -and -not (Test-Path -LiteralPath $Destination)) {
            Assert-PublishSibling $backupDirectory
            Move-Item -LiteralPath $backupDirectory -Destination $Destination
        }
        throw
    }
    if (Test-Path -LiteralPath $backupDirectory) {
        Assert-PublishSibling $backupDirectory
        Remove-Item -LiteralPath $backupDirectory -Recurse -Force
    }
    Write-Host "Verified standalone native production host: $Destination"
} finally {
    try {
        if (Test-Path -LiteralPath $stageDirectory) {
            Assert-PublishSibling $stageDirectory
            Remove-Item -LiteralPath $stageDirectory -Recurse -Force
        }
    } finally { Pop-Location }
}
