# Downloads the Intel firmware used by the airdrop-win driver from the
# linux-firmware repository into driver\firmware\ and verifies SHA-256.
# The firmware is NOT part of this repository (redistributable under
# LICENCE.iwlwifi_firmware, which is downloaded next to it). driver\firmware\
# is in .gitignore.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\fetch-firmware.ps1 [-Force]
param([switch]$Force)
$ErrorActionPreference = 'Stop'

# Pinned linux-firmware tag. iwlwifi-so-a0-hr-b0-77 is the image OpenBSD's
# iwx(4) uses for So+Hr (fw_update: iwx-so-a0-hr-b0-77); WHENCE lists it as
# version 74.f92b5fed.0.
$tag  = '20261019'
# git.kernel.org serves a bot challenge (Anubis) to browser-like User-Agents,
# so a plain tool UA is sent; the official GitLab mirror is the fallback.
$mirrors = @(
    { param($p) "https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/plain/$($p)?h=$tag" },
    { param($p) "https://gitlab.com/kernel-firmware/linux-firmware/-/raw/$tag/$p" }
)
$ua = 'airdrop-win-fetch-firmware/1.0'
$files = @(
    @{ Path = 'intel/iwlwifi/iwlwifi-so-a0-hr-b0-77.ucode'
       Name = 'iwlwifi-so-a0-hr-b0-77.ucode'
       Size = 1500532
       Sha256 = 'bed4958236366c3c451b3ec994a56b2ad83c08d70e0a1eae42fb00f2eaabbcc8' },
    @{ Path = 'LICENSES/LICENCE.iwlwifi_firmware'
       Name = 'LICENCE.iwlwifi_firmware'
       Size = $null
       Sha256 = '16d5040c7cf851fc693b7542e20870935b99802533ea1bfd231c377a2305e5c5' }
)

$dest = Join-Path $PSScriptRoot '..\driver\firmware'
New-Item -ItemType Directory -Force $dest | Out-Null
$dest = (Resolve-Path $dest).Path
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

foreach ($f in $files) {
    $out = Join-Path $dest $f.Name
    if ((Test-Path $out) -and -not $Force) {
        $h = (Get-FileHash -Algorithm SHA256 $out).Hash.ToLowerInvariant()
        if ($h -eq $f.Sha256) { Write-Host "ok (cached)  $($f.Name)"; continue }
        Write-Host "hash mismatch, re-downloading $($f.Name)"
    }
    $tmp = "$out.part"
    $ok = $false
    foreach ($m in $mirrors) {
        $url = & $m $f.Path
        try {
            Invoke-WebRequest -UseBasicParsing -UserAgent $ua -Uri $url -OutFile $tmp
        } catch {
            Write-Host "failed: $url ($($_.Exception.Message))"
            continue
        }
        $h = (Get-FileHash -Algorithm SHA256 $tmp).Hash.ToLowerInvariant()
        if ($h -eq $f.Sha256 -and (-not $f.Size -or (Get-Item $tmp).Length -eq $f.Size)) {
            $ok = $true
            break
        }
        Write-Host "SHA-256 mismatch from $url (got $h)"
    }
    if (-not $ok) {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
        throw "could not download a verified copy of $($f.Name) (expected sha256 $($f.Sha256))"
    }
    Move-Item -Force $tmp $out
    Write-Host "downloaded   $($f.Name)  sha256=$h"
}
Write-Host "firmware in $dest"
