<#
.SYNOPSIS
    Builds the portable, self-contained DevBR folder and ZIP for Windows 11 x64.

.DESCRIPTION
    Publishes the GUI, archive worker and broker into one folder with the .NET runtime included, so no
    separately installed runtime is required. Produces artifacts/DevBR-<version>-win-x64[-dev].zip and a
    SHA-256 checksum file. Builds are "Development" unless -Channel Release is passed; release builds
    must additionally be Authenticode-signed (not performed by this script).
#>
[CmdletBinding()]
param(
    [ValidateSet('Development', 'Release')]
    [string] $Channel = 'Development',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish\DevBR'

[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$suffix = if ($Channel -eq 'Release') { '' } else { '-dev' }
$zip = Join-Path $artifacts "DevBR-$version-win-x64$suffix.zip"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force $publish | Out-Null

# The GUI first; the worker and broker are then published over it so their self-contained runtime
# configuration wins over the framework-dependent copies the GUI's project references bring along.
foreach ($project in 'src\DevBR.App', 'src\DevBR.ArchiveWorker', 'src\DevBR.Broker') {
    Write-Host "Publishing $project ($Channel)..."
    dotnet publish (Join-Path $root $project) `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $publish `
        -p:DevBRReleaseChannel=$Channel `
        -p:ContinuousIntegrationBuild=true `
        -p:DebugType=none `
        -p:GenerateDocumentationFile=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $publish -Force
Copy-Item (Join-Path $root 'docs\USAGE.md') $publish -Force

foreach ($required in 'DevBR.exe', 'DevBR.ArchiveWorker.exe', 'DevBR.Broker.exe', 'x64\7z.dll', 'hostfxr.dll') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "Published folder is missing $required" }
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content -Encoding ascii "$zip.sha256"

Write-Host ''
Write-Host "Portable folder: $publish"
Write-Host "ZIP:             $zip"
Write-Host "SHA-256:         $hash"
