<#
.SYNOPSIS
    Builds the portable, self-contained DevBR folder and ZIP for Windows 11 x64.

.DESCRIPTION
    Publishes the GUI, archive worker and broker into one folder with the .NET runtime included, so no
    separately installed runtime is required. Steps:

      1. Locked restore (packages.lock.json must match) and a vulnerability audit of every direct and
         transitive package; any finding fails the build.
      2. Self-contained win-x64 publish of DevBR.exe, DevBR.ArchiveWorker.exe and DevBR.Broker.exe.
      3. Optional Authenticode signing of DevBR*.exe and DevBR*.dll with signtool (certificate store
         thumbprint or PFX file, RFC 3161 timestamp).
      4. A CycloneDX SBOM (pinned local tool, .config/dotnet-tools.json) placed in the package and
         next to the ZIP.
      5. Notices, license texts, usage and troubleshooting documents, release-info.json and an
         in-package SHA256SUMS.
      6. artifacts/DevBR-<version>-win-x64[-dev|-unsigned].zip plus artifacts/SHA256SUMS covering the
         ZIP, the SBOM and every executable.

    Development builds (the default) need no certificate and are named "-dev"; the app shows a
    "Development build" badge. Release builds refuse to produce a ZIP unless every DevBR binary carries a
    valid Authenticode signature, or -AllowUnsigned is passed, in which case the ZIP is named "-unsigned"
    and contains UNSIGNED-RELEASE.txt.

    Check the result with build/verify-release.ps1.

.EXAMPLE
    ./build/publish.ps1

.EXAMPLE
    ./build/publish.ps1 -Channel Release -CertificateThumbprint 0123...CDEF

.EXAMPLE
    ./build/publish.ps1 -Channel Release -PfxPath C:\keys\devbr.pfx -PfxPassword (Read-Host -AsSecureString)
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Development', 'Release')]
    [string] $Channel = 'Development',

    [string] $Configuration = 'Release',

    # SHA-1 thumbprint of a code-signing certificate in the CurrentUser or LocalMachine "My" store.
    [string] $CertificateThumbprint,

    # A PFX file holding the code-signing certificate and private key.
    [string] $PfxPath,

    [securestring] $PfxPassword,

    # RFC 3161 timestamp server, so signatures stay valid after the certificate expires.
    [string] $TimestampUrl = 'http://timestamp.digicert.com',

    # signtool.exe; found on PATH or in the newest Windows SDK when omitted.
    [string] $SignToolPath,

    # Release channel only: produce an unsigned release ZIP anyway. The output is named "-unsigned".
    [switch] $AllowUnsigned
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish\DevBR'
$solution = Join-Path $root 'DevBR.slnx'

function Invoke-Native([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

function Get-Sha256([string] $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Find-SignTool {
    if ($SignToolPath) {
        if (-not (Test-Path $SignToolPath)) { throw "signtool not found at $SignToolPath." }
        return $SignToolPath
    }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object { [version]($_.Directory.Parent.Name -replace '[^\d.]', '0') } -Descending |
        Select-Object -First 1
    if (-not $found) { throw 'signtool.exe was not found. Install the Windows SDK signing tools or pass -SignToolPath.' }
    return $found.FullName
}

[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_['Version'] } | Select-Object -First 1).Version
$wantsSigning = [bool] ($CertificateThumbprint -or $PfxPath)
if ($CertificateThumbprint -and $PfxPath) { throw 'Pass either -CertificateThumbprint or -PfxPath, not both.' }
if ($AllowUnsigned -and $Channel -ne 'Release') { throw '-AllowUnsigned only applies to -Channel Release.' }
if ($Channel -eq 'Release' -and -not $wantsSigning -and -not $AllowUnsigned) {
    throw 'Release builds must be Authenticode-signed. Pass -CertificateThumbprint or -PfxPath, or -AllowUnsigned to produce a ZIP clearly marked as unsigned.'
}
$signTool = if ($wantsSigning) { Find-SignTool } else { $null }

Push-Location $root
try {
    # 1. Tools, locked restore and vulnerability audit -------------------------------------------------
    Write-Host 'Restoring local tools...'
    Invoke-Native 'dotnet tool restore' { dotnet tool restore }

    Write-Host 'Restoring packages in locked mode...'
    Invoke-Native 'Locked restore' { dotnet restore $solution -p:ContinuousIntegrationBuild=true -p:RestoreLockedMode=true }

    Write-Host 'Checking packages for known vulnerabilities...'
    $audit = dotnet list $solution package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw "Vulnerability check failed (exit code $LASTEXITCODE): $audit" }
    $report = ($audit -join "`n") | ConvertFrom-Json
    if ($report.PSObject.Properties['problems'] -and $report.problems) {
        throw "Vulnerability check reported problems: $(($report.problems | ForEach-Object { $_.text }) -join '; ')"
    }
    $findings = foreach ($project in $report.projects) {
        if (-not $project.PSObject.Properties['frameworks']) { continue }
        foreach ($framework in $project.frameworks) {
            foreach ($list in 'topLevelPackages', 'transitivePackages') {
                if (-not $framework.PSObject.Properties[$list]) { continue }
                foreach ($package in $framework.$list) {
                    foreach ($v in $package.vulnerabilities) {
                        "$(Split-Path $project.path -Leaf): $($package.id) $($package.resolvedVersion) [$($v.severity)] $($v.advisoryurl)"
                    }
                }
            }
        }
    }
    if ($findings) { throw "Vulnerable packages found:`n$($findings -join "`n")" }

    # 2. Publish ----------------------------------------------------------------------------------------
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    New-Item -ItemType Directory -Force $publish | Out-Null

    # The GUI first; the worker and broker are then published over it so their self-contained runtime
    # configuration wins over the framework-dependent copies the GUI's project references bring along.
    foreach ($project in 'src\DevBR.App', 'src\DevBR.ArchiveWorker', 'src\DevBR.Broker') {
        Write-Host "Publishing $project ($Channel)..."
        Invoke-Native "dotnet publish $project" {
            dotnet publish (Join-Path $root $project) `
                --configuration $Configuration `
                --runtime win-x64 `
                --self-contained true `
                --output $publish `
                -p:DevBRReleaseChannel=$Channel `
                -p:ContinuousIntegrationBuild=true `
                -p:DebugType=none `
                -p:GenerateDocumentationFile=false
        }
    }

    foreach ($required in 'DevBR.exe', 'DevBR.ArchiveWorker.exe', 'DevBR.Broker.exe', 'x64\7z.dll', 'hostfxr.dll') {
        if (-not (Test-Path (Join-Path $publish $required))) { throw "Published folder is missing $required" }
    }

    # 3. Authenticode signing ---------------------------------------------------------------------------
    $signable = @(Get-ChildItem $publish -File | Where-Object { $_.Name -like 'DevBR*' -and $_.Extension -in '.exe', '.dll' })
    if ($wantsSigning) {
        Write-Host "Signing $($signable.Count) files with $signTool..."
        $signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'DevBR')
        if ($CertificateThumbprint) {
            $signArgs += @('/sha1', ($CertificateThumbprint -replace '\s', ''))
        }
        else {
            $signArgs += @('/f', (Resolve-Path $PfxPath).Path)
            if ($PfxPassword) { $signArgs += @('/p', [Net.NetworkCredential]::new('', $PfxPassword).Password) }
        }
        & $signTool @signArgs @($signable.FullName) | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit code $LASTEXITCODE)." }
    }

    $unsignedFiles = @($signable | Where-Object { (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid' })
    $signed = $unsignedFiles.Count -eq 0
    if ($Channel -eq 'Release' -and -not $signed -and -not $AllowUnsigned) {
        throw "Release build refused: these files have no valid Authenticode signature: $($unsignedFiles.Name -join ', ')"
    }

    $suffix = if ($Channel -ne 'Release') { '-dev' } elseif ($signed) { '' } else { '-unsigned' }
    $packageName = "DevBR-$version-win-x64$suffix"
    $zip = Join-Path $artifacts "$packageName.zip"
    $sbomOut = Join-Path $artifacts "$packageName.cdx.json"

    # 4. SBOM -------------------------------------------------------------------------------------------
    Write-Host 'Generating the CycloneDX SBOM...'
    $sbomWork = Join-Path $artifacts 'sbom'
    if (Test-Path $sbomWork) { Remove-Item $sbomWork -Recurse -Force }
    # Restore already happened (locked); the tool reads project.assets.json and the local NuGet cache.
    Invoke-Native 'CycloneDX' {
        dotnet tool run dotnet-CycloneDX $solution --exclude-test-projects --exclude-dev --disable-package-restore `
            --runtime win-x64 --spec-version 1.6 --output-format Json --output $sbomWork --filename bom.json `
            --set-name DevBR --set-version $version --set-type Application
    }
    $bom = Get-Content (Join-Path $sbomWork 'bom.json') -Raw | ConvertFrom-Json -AsHashtable
    $extra = [System.Collections.Generic.List[object]]::new()

    $runtimeConfig = Get-Content (Join-Path $publish 'DevBR.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $pack = "$($framework.name).Runtime.win-x64"
        $extra.Add([ordered]@{
                type      = 'framework'
                'bom-ref' = "pkg:nuget/$pack@$($framework.version)"
                name      = $pack
                version   = $framework.version
                publisher = 'Microsoft'
                licenses  = @(@{ license = @{ id = 'MIT' } })
                purl      = "pkg:nuget/$pack@$($framework.version)"
            })
    }

    $sevenZip = Join-Path $publish 'x64\7z.dll'
    $sevenZipVersion = (Get-Item $sevenZip).VersionInfo.ProductVersion
    $extra.Add([ordered]@{
            type        = 'library'
            'bom-ref'   = "7-zip@$sevenZipVersion"
            name        = '7-Zip'
            version     = $sevenZipVersion
            publisher   = 'Igor Pavlov'
            description = 'Native 7z.dll (x64), redistributed unmodified from the SharpSevenZip package.'
            licenses    = @(@{ expression = 'LGPL-2.1-or-later AND BSD-3-Clause AND BSD-2-Clause' })
            hashes      = @(@{ alg = 'SHA-256'; content = Get-Sha256 $sevenZip })
            externalReferences = @(@{ type = 'website'; url = 'https://www.7-zip.org/' })
        })

    foreach ($file in $signable) {
        $extra.Add([ordered]@{
                type      = 'file'
                'bom-ref' = "file:$($file.Name)"
                name      = $file.Name
                version   = $version
                hashes    = @(@{ alg = 'SHA-256'; content = Get-Sha256 $file.FullName })
            })
    }

    if (-not $bom.Contains('components')) { $bom['components'] = @() }
    $bom['components'] = @($bom['components']) + $extra
    $bom['metadata']['properties'] = @(
        @{ name = 'devbr:channel'; value = $Channel },
        @{ name = 'devbr:signed'; value = $signed.ToString().ToLowerInvariant() },
        @{ name = 'devbr:package'; value = "$packageName.zip" }
    )
    $bom | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $sbomOut -Encoding utf8NoBOM
    Copy-Item $sbomOut (Join-Path $publish 'sbom.cdx.json') -Force
    Remove-Item $sbomWork -Recurse -Force

    # 5. Documents and metadata -------------------------------------------------------------------------
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $publish -Force
    foreach ($doc in 'USAGE.md', 'MIGRATION-GUIDE.md', 'TROUBLESHOOTING.md') {
        $source = Join-Path $root "docs\$doc"
        if (Test-Path $source) { Copy-Item $source $publish -Force }
    }
    Copy-Item (Join-Path $root 'build\licenses') (Join-Path $publish 'licenses') -Recurse -Force

    $commit = try { (git -C $root rev-parse HEAD 2>$null) } catch { $null }
    [ordered]@{
        product       = 'DevBR'
        version       = $version
        channel       = $Channel
        signed        = $signed
        allowUnsigned = [bool] $AllowUnsigned
        package       = "$packageName.zip"
        runtime       = 'win-x64'
        commit        = $commit
        builtUtc      = [DateTime]::UtcNow.ToString('o')
        sdk           = (dotnet --version)
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'release-info.json') -Encoding utf8NoBOM

    if ($Channel -eq 'Release' -and -not $signed) {
        @"
UNSIGNED RELEASE BUILD

This DevBR $version package was built with -AllowUnsigned. Its executables carry no Authenticode
signature, so Windows SmartScreen will warn before running them and the publisher cannot be verified.
Do not distribute it as a production release.
"@ | Set-Content -LiteralPath (Join-Path $publish 'UNSIGNED-RELEASE.txt') -Encoding utf8NoBOM
    }

    # Checksums of every file in the package, written last so that it covers everything else.
    $innerSums = Join-Path $publish 'SHA256SUMS'
    Get-ChildItem $publish -File -Recurse | Where-Object FullName -ne $innerSums | Sort-Object FullName | ForEach-Object {
        "$(Get-Sha256 $_.FullName)  $([IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\', '/'))"
    } | Set-Content -LiteralPath $innerSums -Encoding ascii

    # 6. ZIP and checksums ------------------------------------------------------------------------------
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

    $hash = Get-Sha256 $zip
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content -Encoding ascii "$zip.sha256"

    # Executables are listed under the folder that Explorer's "Extract All" creates, so that
    # "sha256sum -c SHA256SUMS" works after extraction.
    $sums = @("$hash  $packageName.zip", "$(Get-Sha256 $sbomOut)  $packageName.cdx.json")
    $sums += Get-ChildItem $publish -Filter *.exe -File -Recurse | Sort-Object FullName | ForEach-Object {
        "$(Get-Sha256 $_.FullName)  $packageName/$([IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\', '/'))"
    }
    $sums | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS') -Encoding ascii
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host "Channel:         $Channel$(if ($Channel -eq 'Release' -and -not $signed) { ' (UNSIGNED)' })"
Write-Host "Signed:          $signed"
Write-Host "Portable folder: $publish"
Write-Host "ZIP:             $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Write-Host "SBOM:            $sbomOut"
Write-Host "Checksums:       $(Join-Path $artifacts 'SHA256SUMS')"
Write-Host "SHA-256:         $hash"
