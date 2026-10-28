<#
.SYNOPSIS
  Signs the NitroDrop MSIX with a self-signed DEVELOPMENT code-signing certificate (CN=Nitrobyte).

.DESCRIPTION
  - Reuses (or creates) a code-signing certificate with subject -Subject in Cert:\CurrentUser\My (no admin).
  - Exports the public part to packaging\out\NitroDrop-dev.cer.
  - Signs the MSIX with signtool from the installed Windows SDK (SHA-256). -Timestamp adds an RFC 3161 timestamp
    (network) so the signature stays valid after the certificate expires.
  - Never touches LocalMachine stores.

  The subject must equal the MSIX Identity Publisher (build-release.ps1 -Publisher, default CN=Nitrobyte).

  To INSTALL on a PC, Windows must trust the certificate machine-wide. One-time, admin, per PC:
      Import-Certificate -FilePath NitroDrop-dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
  then, as the normal user:
      Add-AppxPackage -Path NitroDrop-<ver>-x64.msix
  (packaging\install-msix.ps1 does both; only the import asks for UAC.) Developer Mode is NOT needed for a signed
  .msix; it only replaces the certificate when registering a loose layout (Add-AppxPackage -Register).

  Store/GitHub later: the Store re-signs with Microsoft's certificate (Publisher = the Partner Center publisher id,
  e.g. CN=XXXXXXXX-...), so no certificate step for users; for GitHub sideloading use a real code-signing
  certificate (e.g. Azure Trusted Signing works for MSIX, unlike for drivers) and set -Publisher to its subject.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\sign-dev.ps1
  powershell -ExecutionPolicy Bypass -File packaging\sign-dev.ps1 -Msix packaging\out\NitroDrop-0.1.26301.1200-x64.msix -Timestamp
#>
[CmdletBinding()]
param(
    [string]$Msix,
    [string]$Subject = 'CN=Nitrobyte',
    [switch]$Timestamp,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'out'

if (-not $Msix) {
    $m = Get-ChildItem $out -Filter 'NitroDrop-*-x64.msix' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $m) { throw "No MSIX in $out - run packaging\build-release.ps1 first" }
    $Msix = $m.FullName
}
$Msix = (Resolve-Path $Msix).Path

$codeSigningEku = '1.3.6.1.5.5.7.3.3'
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7) -and
                   ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq $codeSigningEku }) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($cert) {
    Write-Host "Reusing $($cert.Subject) $($cert.Thumbprint) (until $($cert.NotAfter.ToString('yyyy-MM-dd')))"
} else {
    Write-Host "Creating self-signed code-signing certificate $Subject in Cert:\CurrentUser\My"
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Subject -FriendlyName 'NitroDrop development signing' `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(2) `
        -TextExtension @("2.5.29.37={text}$codeSigningEku", '2.5.29.19={text}')
    Write-Host "Created $($cert.Thumbprint)"
}

$cer = Join-Path $out 'NitroDrop-dev.cer'
Export-Certificate -Cert $cert -FilePath $cer -Type CERT -Force | Out-Null

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK)' }

$signArgs = @('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint, '/s', 'My')
if ($Timestamp) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
& $signtool.FullName @signArgs $Msix
if ($LASTEXITCODE) { throw "signtool failed ($LASTEXITCODE) - is the MSIX Publisher exactly '$Subject'?" }

$sig = Get-AuthenticodeSignature $Msix
Write-Host ''
Write-Host "Signed : $Msix"
Write-Host "Signer : $($sig.SignerCertificate.Subject) $($sig.SignerCertificate.Thumbprint)"
Write-Host "Status : $($sig.Status) (UnknownError/NotTrusted is expected until NitroDrop-dev.cer is trusted)"
Write-Host "Cert   : $cer"
Write-Host ''
Write-Host 'Install on the target PC (copy the .msix, the .cer and install-msix.ps1 together):'
Write-Host '  powershell -ExecutionPolicy Bypass -File install-msix.ps1'
Write-Host 'or by hand:'
Write-Host '  (admin, once)  Import-Certificate -FilePath NitroDrop-dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople'
Write-Host "  (user)         Add-AppxPackage -Path $(Split-Path $Msix -Leaf)"
