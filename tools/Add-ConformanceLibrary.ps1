<#
.SYNOPSIS
Adds a pinned library file to the conformance test cache (tests/conformance-cache).

.DESCRIPTION
Downloads the URL once, stores the bytes under tests/conformance-cache/files, records their SHA-384 in Subresource
Integrity form and the licence in tests/conformance-cache/cache.json. Conformance artifacts reference the library by this
URL with the same integrity value; the runner serves it from the cache and RefCapture captures references from the same
bytes, so no run needs the network.

.EXAMPLE
tools/Add-ConformanceLibrary.ps1 -Url https://cdnjs.cloudflare.com/ajax/libs/react/18.3.1/umd/react.production.min.js -Licence MIT -LicenceUrl https://raw.githubusercontent.com/facebook/react/v18.3.1/LICENSE
#>
param(
    [Parameter(Mandatory)] [string] $Url,
    [Parameter(Mandatory)] [string] $Licence,
    [Parameter(Mandatory)] [string] $LicenceUrl,
    [string] $Name
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root 'tests/conformance-cache'
$files = Join-Path $cache 'files'
$licences = Join-Path $cache 'licenses'
New-Item -ItemType Directory -Force $files, $licences | Out-Null

$uri = [Uri] $Url
if (-not $Name) { $Name = [IO.Path]::GetFileName($uri.AbsolutePath) }
$path = Join-Path $files $Name
Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing
$bytes = [IO.File]::ReadAllBytes($path)
$integrity = 'sha384-' + [Convert]::ToBase64String([Security.Cryptography.SHA384]::Create().ComputeHash($bytes))

$licenceFile = ($uri.Host + '-' + $Name + '.LICENSE.txt') -replace '[^A-Za-z0-9._-]', '_'
Invoke-WebRequest -Uri $LicenceUrl -OutFile (Join-Path $licences $licenceFile) -UseBasicParsing

$manifestPath = Join-Path $cache 'cache.json'
$manifest = if (Test-Path $manifestPath) { Get-Content $manifestPath -Raw | ConvertFrom-Json } else { [pscustomobject] @{ entries = @() } }
$entries = @($manifest.entries | Where-Object { $_.url -ne $Url })
$entries += [pscustomobject] @{ url = $Url; file = $Name; integrity = $integrity; licence = $Licence; licenceFile = $licenceFile }
$manifest.entries = @($entries | Sort-Object url)
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

Write-Host "$Url"
Write-Host "  $Name  $($bytes.Length) bytes"
Write-Host "  integrity=$integrity"
