param(
    [Parameter(Mandatory = $true)][string]$Feed,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ContentHostDirectory
)
$ErrorActionPreference = 'Stop'
$feedPath = (Resolve-Path -LiteralPath $Feed).Path
$nativePath = if ($ContentHostDirectory) { (Resolve-Path -LiteralPath $ContentHostDirectory).Path } else { $null }
if ($nativePath -and -not (Test-Path -LiteralPath (Join-Path $nativePath 'Folio.ContentHost.exe'))) {
    throw 'The supplied native publish does not contain Folio.ContentHost.exe.'
}
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$runName = 'folio-consumer-' + [Guid]::NewGuid().ToString('N')
$root = Join-Path $tempParent $runName
$reports = Join-Path (Split-Path $PSScriptRoot) "artifacts/consumer-smoke/$runName"
New-Item -ItemType Directory -Path $root, $reports -Force | Out-Null
$complete = $false
try {
    $cases = @('portable')
    if ($env:OS -eq 'Windows_NT') { $cases += 'windows' }
    foreach ($case in $cases) {
        $caseRoot = Join-Path $root $case
        New-Item -ItemType Directory -Path $caseRoot | Out-Null
        $template = Join-Path $PSScriptRoot "consumer-smoke/$case"
        (Get-Content -LiteralPath (Join-Path $template 'Consumer.csproj.in') -Raw).Replace('FOLIO_CONSUMER_VERSION', $Version) |
            Set-Content -LiteralPath (Join-Path $caseRoot 'Consumer.csproj') -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $template 'Program.cs') -Destination $caseRoot
        $escapedFeed = [Security.SecurityElement]::Escape($feedPath)
        "<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/></packageSources></configuration>" |
            Set-Content -LiteralPath (Join-Path $caseRoot 'NuGet.Config') -Encoding utf8
        $project = Join-Path $caseRoot 'Consumer.csproj'
        $publish = Join-Path $caseRoot 'publish'
        # Neither the repository nor the user's package cache can satisfy a missing dependency or supply missing assets.
        dotnet restore $project --configfile (Join-Path $caseRoot 'NuGet.Config') --packages (Join-Path $root 'packages') *> (Join-Path $reports "$case-restore.log")
        if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $reports "$case-restore.log") -Tail 30; throw "Fresh $case package restore failed; see $reports" }
        dotnet publish $project -c Release --no-restore -o $publish *> (Join-Path $reports "$case-build.log")
        if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $reports "$case-build.log") -Tail 30; throw "Fresh $case package publish failed; see $reports" }
        if ($case -eq 'windows' -and $nativePath) {
            $bundle = Join-Path $publish 'Folio'
            New-Item -ItemType Directory -Path $bundle | Out-Null
            Get-ChildItem -LiteralPath $nativePath | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $bundle -Recurse }
        }
        dotnet (Join-Path $publish 'Consumer.dll') *> (Join-Path $reports "$case-run.log")
        $runExit = $LASTEXITCODE
        Get-Content -LiteralPath (Join-Path $reports "$case-run.log")
        if ($runExit -ne 0) { throw "Fresh $case package execution failed; see $reports" }
        if (Test-Path -LiteralPath (Join-Path $publish 'consumer.png')) {
            Copy-Item -LiteralPath (Join-Path $publish 'consumer.png') -Destination (Join-Path $reports "$case.png")
        }
    }
    $complete = $true
    Write-Host "Package consumer checks passed; evidence: $reports"
} finally {
    # Remove only this invocation's fresh child of the OS temp directory. Preserve failed cases for diagnosis.
    $absolute = [IO.Path]::GetFullPath($root)
    if ($complete -and (Split-Path -Parent $absolute) -eq $tempParent -and (Split-Path -Leaf $absolute) -eq $runName) {
        if ((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Refusing cleanup of a redirected consumer-test root.'
        }
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
