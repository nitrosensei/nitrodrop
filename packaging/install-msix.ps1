<#
.SYNOPSIS
  Installs the dev-signed NitroDrop MSIX on THIS PC (run by the owner; never run by the build).

.DESCRIPTION
  Put this script next to NitroDrop-<ver>-x64.msix and NitroDrop-dev.cer, then:
      powershell -ExecutionPolicy Bypass -File install-msix.ps1
  1. If NitroDrop-dev.cer is not yet in Cert:\LocalMachine\TrustedPeople, imports it there in an elevated
     PowerShell (one UAC prompt, once per PC). Only TrustedPeople - not Root - so it cannot vouch for websites.
  2. Add-AppxPackage as the current user (no admin). Updates an older NitroDrop MSIX in place.

  Uninstall:  Get-AppxPackage Nitrobyte.NitroDrop | Remove-AppxPackage
  Remove cert (admin):  Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=Nitrobyte' | Remove-Item
  -Uninstall does the first one.
#>
[CmdletBinding()]
param(
    [string]$Msix,
    [string]$Cer,
    [switch]$Uninstall,
    [switch]$ImportOnly   # internal: the elevated half
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

if ($Uninstall) {
    Get-AppxPackage Nitrobyte.NitroDrop | Remove-AppxPackage
    Write-Host 'NitroDrop MSIX removed (settings in %LOCALAPPDATA%\airdrop-win are kept).'
    return
}

if (-not $Cer) { $Cer = Join-Path $here 'NitroDrop-dev.cer' }
$Cer = (Resolve-Path $Cer).Path
$c = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $Cer

if ($ImportOnly) {
    Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    return
}

if (-not $Msix) {
    $m = Get-ChildItem $here -Filter 'NitroDrop-*-x64.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $m) { throw "No NitroDrop-*-x64.msix next to this script" }
    $Msix = $m.FullName
}

$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $c.Thumbprint
if (-not $trusted) {
    Write-Host "Trusting $($c.Subject) ($($c.Thumbprint)) in LocalMachine\TrustedPeople - Windows will ask for admin once."
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-ImportOnly', '-Cer', "`"$Cer`"")
    if ($p.ExitCode) { throw "Certificate import failed ($($p.ExitCode))" }
    if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $c.Thumbprint)) { throw 'Certificate was not imported (UAC declined?)' }
}

Write-Host "Installing $Msix"
Add-AppxPackage -Path $Msix -ForceApplicationShutdown
$pkg = Get-AppxPackage Nitrobyte.NitroDrop
Write-Host "Installed $($pkg.PackageFullName)"
Write-Host 'Start: Start menu > NitroDrop  (or: explorer.exe shell:AppsFolder\' + $pkg.PackageFamilyName + '!NitroDrop)'
