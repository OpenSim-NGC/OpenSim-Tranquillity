# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

# =====================================================================================
# assert-joltc-native.ps1 - checks the joltc natives in a build or publish output: each
# is a build the Jolt physics module has a record of.
#
# The module loads its native from runtimes/<rid>/native/ (JoltNative.cs in the backend),
# after checking its SHA-256 against the same record as the tables below. By default that
# file is the stock one of the JoltPhysics.Native package, which runs one job pool.
# This script checks an output before it is deployed:
#   - every runtimes/<folder>/native/ joltc file of a platform the module supports must be
#     a recorded build for that folder;
#   - at least one of them must be present;
#   - any other joltc file (a copy at the root, the double-precision variant) is a stray:
#     the module never loads it, but it should not be in the output. Strays fail the check
#     unless -AllowStray is given (for example on an installation that still holds files
#     from an older deploy).
# It runs in Windows PowerShell and in PowerShell 7 (pwsh) on Linux and macOS.
#
# The recorded hashes: Docs/JoltcNative.md.
#
# Usage:
#     powershell -File assert-joltc-native.ps1 -PublishDir "<output or publish directory>" [-AllowStray]
#     pwsh -File assert-joltc-native.ps1 -PublishDir "<output or publish directory>"
#     powershell -File assert-joltc-native.ps1 -NativePath "<path to a joltc.dll or libjoltc.so>"
# Exit 0 = every joltc under runtimes/ is recorded and (without -AllowStray) nothing else is there.
# Exit 1 = a file is unknown, none was found, or a stray file is present.
# =====================================================================================
[CmdletBinding()]
param(
    [string]$PublishDir,
    [Alias("JoltcPath")]
    [string]$NativePath,
    [switch]$AllowStray
)

$ErrorActionPreference = "Stop"

# The files the module loads, by SHA-256. Must equal the entries of JoltNative.Known in
# OpenSim.Region.PhysicsModules.Jolt.Backend/JoltNative.cs (a unit test checks both against the files).
# JoltPhysics.Native 1.1.0. linux-x64 is the deterministic rebuild of joltc 886e0886 (Jolt e77f1755,
# CROSS_PLATFORM_DETERMINISTIC), not the bytes of that package's linux-x64 file. The other four rows are
# the unmodified files of the package.
$stockVersion = "1.1.0"
$stock = [ordered]@{
    "79125AB6882A1AEEECD5509BFE9EDF383E8E48A34D9A8B642A6B9A197D44DF74" = @{ Folder = "win-x64"; File = "joltc.dll" }
    "ED9B88B77C8F27EF035BD9FD809AF7A425757E5B8C37D11EC4527374A06DBA59" = @{ Folder = "linux-x64"; File = "libjoltc.so" }
    "D32B29B9AADFD7887E4681302A766C5476A196C6AD77FF1F86473BBFB47325E0" = @{ Folder = "win-arm64"; File = "joltc.dll" }
    "DAF06442FDF25BD2FC4234C252E3862CA3A94E32792A4466A9D93723C8BB03CB" = @{ Folder = "linux-arm64"; File = "libjoltc.so" }
    "386DBEEC5E6892C218F9692572528EE3AF748184C9298861C90AAC1E3443C2EC" = @{ Folder = "osx"; File = "libjoltc.dylib" }
}
# The runtimes/ folders the module loads from, and the file it loads in each.
$folders = @{}
foreach ($h in $stock.Keys) { $folders[$stock[$h].Folder] = $stock[$h].File }

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }

# What a hash is, for a file in runtimes/<folder>/native/: "stock" or $null (unknown or another folder's).
function Get-Kind([string]$folder, [string]$hash) {
    if ($stock.Contains($hash) -and $stock[$hash].Folder -eq $folder) { return "stock" }
    return $null
}

function Get-Name([string]$hash) {
    if ($stock.Contains($hash) -and $stock[$hash].Folder -eq "linux-x64") { return "joltc 886e0886 Jolt e77f1755 CROSS_PLATFORM_DETERMINISTIC" }
    if ($stock.Contains($hash)) { return ("stock JoltPhysics.Native {0} {1}" -f $stockVersion, $stock[$hash].Folder) }
    return "an unknown build"
}

# The folder a path's runtimes/<folder>/native/<file> location names, if the module loads from it.
function Get-Folder([string]$path) {
    $parts = $path -split '[\\/]'
    if ($parts.Count -lt 4) { return $null }
    $file = $parts[-1]; $native = $parts[-2]; $folder = $parts[-3]; $runtimes = $parts[-4]
    if ($runtimes -ne "runtimes" -or $native -ne "native") { return $null }
    if (-not $folders.ContainsKey($folder)) { return $null }
    if ($folders[$folder] -cne $file) { return $null }
    return $folder
}

# A file is good when it is recorded for its folder.
function Test-Good([string]$folder, [string]$kind) {
    return [bool]$kind
}

if ($NativePath) {
    if (-not (Test-Path -LiteralPath $NativePath)) { Write-Host "JOLTC GUARD FAIL: missing $NativePath"; exit 1 }
    $full = (Resolve-Path -LiteralPath $NativePath).Path
    $h = Get-Sha256 $full
    $ok = $false
    foreach ($folder in $folders.Keys) {
        $kind = Get-Kind $folder $h
        if (Test-Good $folder $kind) { Write-Host ("  OK      {0,-11} {1}  {2}  ({3})" -f $folder, $h.Substring(0,8), $full, (Get-Name $h)); $ok = $true; break }
    }
    if ($ok) { exit 0 }
    Write-Host ("  FAIL    {0,-11} {1}  {2}   <== {3}" -f "", $h.Substring(0,8), $full, (Get-Name $h))
    exit 1
}

if (-not $PublishDir) { Write-Host "Provide -PublishDir or -NativePath."; exit 1 }
if (-not (Test-Path -LiteralPath $PublishDir)) { Write-Host "JOLTC GUARD FAIL: no such directory $PublishDir"; exit 1 }

$root = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\', '/')
$all = @(Get-ChildItem -LiteralPath $root -Recurse -File |
         Where-Object { $_.Name -like "joltc*.dll" -or $_.Name -like "libjoltc*.so*" -or $_.Name -like "libjoltc*.dylib" } |
         Sort-Object FullName)

Write-Host ("Checking {0} joltc file(s) under {1}:" -f $all.Count, $root)
$bad = 0; $stray = 0; $found = @()
foreach ($f in $all) {
    $rel = $f.FullName.Substring($root.Length).TrimStart('\', '/')
    $h = Get-Sha256 $f.FullName
    $folder = Get-Folder $f.FullName
    if ($folder) {
        $kind = Get-Kind $folder $h
        if (Test-Good $folder $kind) {
            Write-Host ("  OK      {0,-11} {1}  {2}  ({3})" -f $folder, $h.Substring(0,8), $rel, (Get-Name $h))
            $found += "$folder $kind"
        } else {
            Write-Host ("  FAIL    {0,-11} {1}  {2}   <== {3}, not a recorded build" -f $folder, $h.Substring(0,8), $rel, (Get-Name $h))
            $bad++
        }
        continue
    }
    $label = if ($AllowStray) { "stray" } else { "FAIL" }
    Write-Host ("  {0,-7} {1,-11} {2}  {3}   <== {4}; the module never loads it" -f $label, "", $h.Substring(0,8), $rel, (Get-Name $h))
    $stray++
}

if ($found.Count -eq 0 -and $bad -eq 0) {
    Write-Host ("JOLTC GUARD FAIL: no joltc under {0} (expected runtimes/<folder>/native/ for one of: {1})" -f $root, (($folders.Keys | Sort-Object) -join ", "))
    exit 1
}
if ($bad -gt 0) {
    Write-Host ("JOLTC GUARD FAIL: {0} native(s) are not a build the module has a record of; it refuses them at start." -f $bad)
    Write-Host "Restore the files from the JoltPhysics.Native package (a clean build)."
    exit 1
}
if ($stray -gt 0 -and -not $AllowStray) {
    Write-Host ("JOLTC GUARD FAIL: {0} stray joltc file(s) beside the ones the module loads; the output should hold only runtimes/<folder>/native/ files." -f $stray)
    exit 1
}
Write-Host ("joltc guard OK: {0} recorded ({1})." -f $found.Count, ($found -join ", "))
exit 0
