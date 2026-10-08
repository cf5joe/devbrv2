<#
.SYNOPSIS
    Validates a DevBR portable release ZIP produced by build/publish.ps1.

.DESCRIPTION
    Extracts the ZIP into <zip folder>\verify\<package name> and checks:

      - SHA256SUMS next to the ZIP: the ZIP, the SBOM and every listed executable match.
      - The in-package SHA256SUMS: every file matches and every file is listed.
      - Required files: the three executables, x64\7z.dll, hostfxr.dll, notices, license texts, usage
        notes, the SBOM and release-info.json.
      - Authenticode status of DevBR*.exe and DevBR*.dll (reported; required for a signed Release).
      - The channel and version in the file name, release-info.json, the SBOM and the assemblies'
        DevBR.ReleaseChannel metadata agree.
      - The SBOM is CycloneDX, names DevBR at this version and its file hashes match the package.
      - DevBR.exe starts, stays running and opens no TCP connections or UDP sockets during startup
        (unless -SkipLaunch). This observes the process tree; it does not cut the network. The fully
        offline launch on a clean VM is a manual step in docs/ACCEPTANCE-CHECKLIST.md.

    Exits with code 1 if any check fails.

.EXAMPLE
    ./build/verify-release.ps1 artifacts/DevBR-0.1.0-win-x64-dev.zip
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $ZipPath,

    # Defaults to SHA256SUMS next to the ZIP.
    [string] $ChecksumsPath,

    [switch] $SkipLaunch,

    # How long DevBR.exe is observed after it starts.
    [ValidateRange(3, 120)]
    [int] $LaunchSeconds = 10,

    [switch] $KeepExtracted
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string] $check, [ValidateSet('PASS', 'FAIL', 'WARN', 'SKIP')] [string] $result, [string] $detail = '') {
    $results.Add([pscustomobject]@{ Check = $check; Result = $result; Detail = $detail })
}

function Get-Sha256([string] $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Read-Sums([string] $path) {
    $map = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $path) {
        if ($line -match '^(?<hash>[0-9a-fA-F]{64}) [ *](?<name>.+)$') { $map[$Matches.name] = $Matches.hash.ToLowerInvariant() }
        elseif ($line.Trim()) { throw "Malformed checksum line in ${path}: $line" }
    }
    return $map
}

# Reads [assembly: AssemblyMetadata(key, value)] without loading the assembly (it targets a newer runtime).
function Get-AssemblyMetadata([string] $path) {
    Add-Type -AssemblyName System.Reflection.Metadata
    $values = @{}
    $stream = [IO.File]::OpenRead($path)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $pe.HasMetadata) { return $values }
        $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach ($handle in $md.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute = $md.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) { continue }
            $ctor = $md.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle] $attribute.Constructor)
            if ($ctor.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
            $type = $md.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle] $ctor.Parent)
            if ($md.GetString($type.Name) -ne 'AssemblyMetadataAttribute') { continue }

            $blob = $md.GetBlobBytes($attribute.Value)
            $offset = 2  # prolog 0x0001
            $strings = foreach ($i in 0, 1) {
                if ($blob[$offset] -eq 0xFF) { $offset++; $null; continue }
                if ($blob[$offset] -lt 0x80) { $length = $blob[$offset]; $offset++ }
                else { $length = (($blob[$offset] -band 0x3F) -shl 8) + $blob[$offset + 1]; $offset += 2 }
                [Text.Encoding]::UTF8.GetString($blob, $offset, $length)
                $offset += $length
            }
            $values[$strings[0]] = $strings[1]
        }
    }
    finally {
        $stream.Dispose()
    }
    return $values
}

$zip = (Resolve-Path $ZipPath).Path
$zipName = Split-Path $zip -Leaf
$zipDir = Split-Path $zip -Parent
if (-not $ChecksumsPath) { $ChecksumsPath = Join-Path $zipDir 'SHA256SUMS' }

# --- File name ----------------------------------------------------------------------------------------
if ($zipName -notmatch '^DevBR-(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+?)?)-win-x64(?<suffix>-dev|-unsigned)?\.zip$') {
    throw "$zipName is not a DevBR release ZIP name (DevBR-<version>-win-x64[-dev|-unsigned].zip)."
}
$packageName = [IO.Path]::GetFileNameWithoutExtension($zipName)
$nameVersion = $Matches.version
$suffix = if ($Matches.ContainsKey('suffix')) { $Matches.suffix } else { '' }
$expectedChannel = if ($suffix -eq '-dev') { 'Development' } else { 'Release' }
$signatureRequired = $expectedChannel -eq 'Release' -and $suffix -ne '-unsigned'
Add-Result 'File name' 'PASS' "version $nameVersion, channel $expectedChannel$(if ($suffix -eq '-unsigned') { ' (unsigned)' })"

# --- Outer checksums ----------------------------------------------------------------------------------
$outer = $null
if (Test-Path -LiteralPath $ChecksumsPath) {
    $outer = Read-Sums $ChecksumsPath
    if (-not $outer.Contains($zipName)) { Add-Result 'SHA256SUMS: ZIP' 'FAIL' "$zipName is not listed in $ChecksumsPath" }
    elseif ($outer[$zipName] -ne (Get-Sha256 $zip)) { Add-Result 'SHA256SUMS: ZIP' 'FAIL' 'hash mismatch' }
    else { Add-Result 'SHA256SUMS: ZIP' 'PASS' $outer[$zipName] }

    $sbomName = "$packageName.cdx.json"
    $sbomBeside = Join-Path $zipDir $sbomName
    if (-not $outer.Contains($sbomName)) { Add-Result 'SHA256SUMS: SBOM' 'FAIL' "$sbomName is not listed" }
    elseif (-not (Test-Path -LiteralPath $sbomBeside)) { Add-Result 'SHA256SUMS: SBOM' 'FAIL' "$sbomName is missing next to the ZIP" }
    elseif ($outer[$sbomName] -ne (Get-Sha256 $sbomBeside)) { Add-Result 'SHA256SUMS: SBOM' 'FAIL' 'hash mismatch' }
    else { Add-Result 'SHA256SUMS: SBOM' 'PASS' }
}
else {
    Add-Result 'SHA256SUMS' 'FAIL' "not found: $ChecksumsPath"
}

# --- Extract ------------------------------------------------------------------------------------------
$extracted = Join-Path $zipDir "verify\$packageName"
if (Test-Path $extracted) { Remove-Item $extracted -Recurse -Force }
New-Item -ItemType Directory -Force $extracted | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)

try {
    if ($outer) {
        $exeEntries = @($outer.Keys | Where-Object { $_ -like "$packageName/*" })
        $bad = @($exeEntries | Where-Object {
                $file = Join-Path $extracted ($_.Substring($packageName.Length + 1))
                -not (Test-Path -LiteralPath $file) -or (Get-Sha256 $file) -ne $outer[$_]
            })
        $exes = @(Get-ChildItem $extracted -Filter *.exe -File -Recurse | ForEach-Object { "$packageName/$([IO.Path]::GetRelativePath($extracted, $_.FullName).Replace('\', '/'))" })
        $unlisted = @($exes | Where-Object { $_ -notin $exeEntries })
        if ($bad -or $unlisted) { Add-Result 'SHA256SUMS: executables' 'FAIL' "mismatched: $($bad -join ', '); not listed: $($unlisted -join ', ')" }
        else { Add-Result 'SHA256SUMS: executables' 'PASS' "$($exeEntries.Count) executables" }
    }

    # --- Required files -----------------------------------------------------------------------------------
    $required = 'DevBR.exe', 'DevBR.ArchiveWorker.exe', 'DevBR.Broker.exe', 'x64\7z.dll', 'hostfxr.dll',
    'THIRD-PARTY-NOTICES.md', 'USAGE.md', 'sbom.cdx.json', 'release-info.json', 'SHA256SUMS',
    'licenses\MIT.txt', 'licenses\Apache-2.0.txt', 'licenses\BSD-2-Clause.txt', 'licenses\LGPL-3.0-or-later.txt',
    'licenses\LGPL-2.1-or-later.txt', 'licenses\7-Zip.txt'
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $extracted $_)) })
    if ($missing) { Add-Result 'Required files' 'FAIL' "missing: $($missing -join ', ')" }
    else { Add-Result 'Required files' 'PASS' "$($required.Count) present" }
    foreach ($doc in 'MIGRATION-GUIDE.md', 'TROUBLESHOOTING.md') {
        if (-not (Test-Path (Join-Path $extracted $doc))) { Add-Result "Document $doc" 'WARN' 'not included' }
    }

    # --- In-package checksums -----------------------------------------------------------------------------
    $innerPath = Join-Path $extracted 'SHA256SUMS'
    if (Test-Path $innerPath) {
        $inner = Read-Sums $innerPath
        $mismatched = @($inner.Keys | Where-Object {
                $file = Join-Path $extracted $_
                -not (Test-Path -LiteralPath $file) -or (Get-Sha256 $file) -ne $inner[$_]
            })
        $all = @(Get-ChildItem $extracted -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($extracted, $_.FullName).Replace('\', '/') } | Where-Object { $_ -ne 'SHA256SUMS' })
        $unlisted = @($all | Where-Object { -not $inner.Contains($_) })
        if ($mismatched -or $unlisted) { Add-Result 'Package SHA256SUMS' 'FAIL' "mismatched: $($mismatched -join ', '); not listed: $($unlisted -join ', ')" }
        else { Add-Result 'Package SHA256SUMS' 'PASS' "$($inner.Count) files" }
    }

    # --- Authenticode ---------------------------------------------------------------------------------------
    $binaries = @(Get-ChildItem $extracted -File | Where-Object { $_.Name -like 'DevBR*' -and $_.Extension -in '.exe', '.dll' })
    $signatures = foreach ($b in $binaries) {
        $s = Get-AuthenticodeSignature -LiteralPath $b.FullName
        [pscustomobject]@{ File = $b.Name; Status = $s.Status; Signer = if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { $null }; Timestamped = [bool] $s.TimeStamperCertificate }
    }
    $signatures | Format-Table -AutoSize | Out-String | Write-Host
    $notValid = @($signatures | Where-Object Status -ne 'Valid')
    $notTimestamped = @($signatures | Where-Object { $_.Status -eq 'Valid' -and -not $_.Timestamped })
    if ($signatureRequired) {
        if ($notValid) { Add-Result 'Authenticode' 'FAIL' "Release requires signatures; not valid: $($notValid.File -join ', ')" }
        elseif ($notTimestamped) { Add-Result 'Authenticode' 'FAIL' "not timestamped: $($notTimestamped.File -join ', ')" }
        else { Add-Result 'Authenticode' 'PASS' "$($binaries.Count) files signed by $(($signatures.Signer | Select-Object -Unique) -join '; ')" }
    }
    elseif ($notValid.Count -eq $binaries.Count) { Add-Result 'Authenticode' 'WARN' "unsigned ($($binaries.Count) files); acceptable for $expectedChannel$(if ($suffix) { " $suffix" }) builds only" }
    elseif ($notValid) { Add-Result 'Authenticode' 'FAIL' "partially signed; not valid: $($notValid.File -join ', ')" }
    else { Add-Result 'Authenticode' 'PASS' "$($binaries.Count) files signed" }

    # --- Channel and version metadata ---------------------------------------------------------------------
    $infoPath = Join-Path $extracted 'release-info.json'
    if (Test-Path $infoPath) {
        $info = Get-Content $infoPath -Raw | ConvertFrom-Json
        $problems = @()
        if ($info.channel -ne $expectedChannel) { $problems += "release-info channel '$($info.channel)'" }
        if ($info.version -ne $nameVersion) { $problems += "release-info version '$($info.version)'" }
        if ($info.package -ne $zipName) { $problems += "release-info package '$($info.package)'" }
        if ($signatureRequired -and -not $info.signed) { $problems += 'release-info says unsigned' }
        if ($suffix -eq '-unsigned' -and -not (Test-Path (Join-Path $extracted 'UNSIGNED-RELEASE.txt'))) { $problems += 'UNSIGNED-RELEASE.txt missing' }
        if ($suffix -ne '-unsigned' -and (Test-Path (Join-Path $extracted 'UNSIGNED-RELEASE.txt'))) { $problems += 'UNSIGNED-RELEASE.txt present in a package not named -unsigned' }
        foreach ($assembly in 'DevBR.dll', 'DevBR.ArchiveWorker.dll', 'DevBR.Broker.dll') {
            $path = Join-Path $extracted $assembly
            if (-not (Test-Path $path)) { $problems += "$assembly missing"; continue }
            $channel = (Get-AssemblyMetadata $path)['DevBR.ReleaseChannel']
            if ($channel -ne $expectedChannel) { $problems += "$assembly channel '$channel'" }
            $productVersion = ((Get-Item $path).VersionInfo.ProductVersion -split '\+')[0]
            if ($productVersion -ne $nameVersion) { $problems += "$assembly version '$productVersion'" }
        }
        if ($problems) { Add-Result 'Channel and version' 'FAIL' ($problems -join '; ') }
        else { Add-Result 'Channel and version' 'PASS' "$expectedChannel $nameVersion in name, release-info.json and assemblies" }
    }

    # --- SBOM -------------------------------------------------------------------------------------------------
    $sbomPath = Join-Path $extracted 'sbom.cdx.json'
    if (Test-Path $sbomPath) {
        $bom = Get-Content $sbomPath -Raw | ConvertFrom-Json
        $problems = @()
        if ($bom.bomFormat -ne 'CycloneDX') { $problems += "bomFormat '$($bom.bomFormat)'" }
        if ($bom.metadata.component.name -ne 'DevBR' -or $bom.metadata.component.version -ne $nameVersion) { $problems += 'metadata.component is not DevBR at this version' }
        $channelProperty = @($bom.metadata.properties | Where-Object name -eq 'devbr:channel').value
        if ($channelProperty -ne $expectedChannel) { $problems += "devbr:channel '$channelProperty'" }
        foreach ($name in 'SharpSevenZip', '7-Zip', 'Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') {
            if (-not ($bom.components | Where-Object name -eq $name)) { $problems += "component $name missing" }
        }
        foreach ($component in @($bom.components | Where-Object type -eq 'file')) {
            $file = Join-Path $extracted $component.name
            $expected = @($component.hashes | Where-Object alg -eq 'SHA-256').content
            if (-not (Test-Path -LiteralPath $file) -or (Get-Sha256 $file) -ne $expected) { $problems += "file hash $($component.name)" }
        }
        if ($outer -and (Test-Path (Join-Path $zipDir "$packageName.cdx.json")) -and (Get-Sha256 $sbomPath) -ne (Get-Sha256 (Join-Path $zipDir "$packageName.cdx.json"))) {
            $problems += 'in-package SBOM differs from the published one'
        }
        if ($problems) { Add-Result 'SBOM' 'FAIL' ($problems -join '; ') }
        else { Add-Result 'SBOM' 'PASS' "CycloneDX $($bom.specVersion), $(@($bom.components).Count) components" }
    }

    # --- Launch -------------------------------------------------------------------------------------------
    if ($SkipLaunch) {
        Add-Result 'Launch without network activity' 'SKIP' 'skipped (-SkipLaunch)'
    }
    else {
        $exe = Join-Path $extracted 'DevBR.exe'
        $process = Start-Process -FilePath $exe -WorkingDirectory $extracted -PassThru
        $seen = @{}
        $connections = [System.Collections.Generic.List[string]]::new()
        $deadline = (Get-Date).AddSeconds($LaunchSeconds)
        while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
            Start-Sleep -Milliseconds 500
            $ids = @($process.Id) + @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)" | ForEach-Object ProcessId)
            foreach ($id in $ids) { $seen[$id] = $true }
            foreach ($c in @(Get-NetTCPConnection -OwningProcess $ids -ErrorAction SilentlyContinue)) {
                $connections.Add("TCP $($c.LocalAddress):$($c.LocalPort) -> $($c.RemoteAddress):$($c.RemotePort) ($($c.State), pid $($c.OwningProcess))")
            }
            foreach ($u in @(Get-NetUDPEndpoint -OwningProcess $ids -ErrorAction SilentlyContinue)) {
                $connections.Add("UDP $($u.LocalAddress):$($u.LocalPort) (pid $($u.OwningProcess))")
            }
        }
        $survived = -not $process.HasExited
        $window = $false
        if ($survived) {
            $process.Refresh()
            $window = $process.MainWindowHandle -ne [IntPtr]::Zero
            [void] $process.CloseMainWindow()
            if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
        }
        Start-Sleep -Seconds 2
        foreach ($id in $seen.Keys) {
            if ($id -ne $process.Id -and (Get-Process -Id $id -ErrorAction SilentlyContinue)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
        }

        $unique = @($connections | Select-Object -Unique)
        if (-not $survived) { Add-Result 'Launch without network activity' 'FAIL' "DevBR.exe exited during startup (exit code $($process.ExitCode))" }
        elseif ($unique) { Add-Result 'Launch without network activity' 'FAIL' ($unique -join '; ') }
        else {
            Add-Result 'Launch without network activity' 'PASS' "ran $LaunchSeconds s, $(if ($window) { 'main window shown' } else { 'no main window detected' }), $($seen.Count) process(es), no sockets"
        }
    }
}
finally {
    if (-not $KeepExtracted) {
        Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue
        $verifyRoot = Join-Path $zipDir 'verify'
        if ((Test-Path $verifyRoot) -and -not (Get-ChildItem $verifyRoot)) { Remove-Item $verifyRoot -Force }
    }
}

$results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object Result -eq 'FAIL')
if ($failed) {
    Write-Host "$($failed.Count) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "All checks passed for $zipName$(if (@($results | Where-Object Result -eq 'WARN')) { ' (with warnings)' })." -ForegroundColor Green
