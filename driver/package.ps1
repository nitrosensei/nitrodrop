# Builds the UNSIGNED driver catalog (airdropwifi.cat) with Inf2Cat from the WDK.
#
# Why unsigned: end-user machines have no WDK (no Inf2Cat/MakeCat). The catalog
# only contains hashes of the package files (inf, sys, ucode) and the OS
# attributes; it does not depend on the signer. NitroDrop's setup wizard signs
# this pre-built catalog (and the .sys) on the user's machine with a
# per-install self-signed certificate (app/AirDrop.App/Setup/CodeSigner.cs).
#
#  - .sys: catalogs store the Authenticode PE hash, which excludes the
#    certificate table, so embedding a signature into the .sys later does NOT
#    invalidate the catalog (unit-tested in AirDrop.Tests/SetupTests.cs).
#  - .ucode: not shipped; the wizard downloads it and checks the pinned SHA-256
#    (tools/fetch-firmware.ps1), so its catalog hash matches.
#
# Usage: powershell -ExecutionPolicy Bypass -File driver\package.ps1
#   needs driver\out\airdropwifi.sys (build.ps1) and driver\firmware\*.ucode (fetch-firmware.ps1)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$inf2cat = Get-ChildItem -Recurse -Filter Inf2Cat.exe $kits -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $inf2cat) { throw "Inf2Cat.exe not found under $kits (install the WDK)" }

$stage = Join-Path $root 'out\pkg'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root 'airdropwifi.inf') $stage
Copy-Item (Join-Path $root 'out\airdropwifi.sys') $stage
Copy-Item (Join-Path $root 'firmware\iwlwifi-so-a0-hr-b0-77.ucode') $stage

# INF is decorated NTamd64.10.0...22000 (Windows 11 21H2+): Cobalt, Nickel, Germanium.
& $inf2cat.FullName /driver:$stage /os:10_CO_X64,10_NI_X64,10_GE_X64 /verbose
if ($LASTEXITCODE) { throw "Inf2Cat failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $stage 'airdropwifi.cat') (Join-Path $root 'out\airdropwifi.cat') -Force
Get-Item (Join-Path $root 'out\airdropwifi.cat') | Select-Object Name, Length
