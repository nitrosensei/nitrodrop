<#
.SYNOPSIS
  Downloads Wintun 0.14.1 (WireGuard LLC) into vendor\wintun, verifying the pinned SHA-256.

.DESCRIPTION
  Only downloads and extracts: it installs nothing, creates no adapter and needs no admin rights.
  wintun.dll is redistributed under the "Prebuilt Binaries License" (vendor\wintun\LICENSE.txt): unmodified, alongside
  software that uses it only through the API in wintun.h. vendor\ is gitignored; the build copies
  vendor\wintun\bin\{amd64,arm64}\wintun.dll next to Nitro.Awdl as wintun\{arch}\wintun.dll when present.
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\vendor\wintun'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Version = '0.14.1'
$Url = "https://www.wintun.net/builds/wintun-$Version.zip"
$ZipSha256 = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'
$DllSha256 = @{
    'amd64' = 'e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce'
    'arm64' = 'f7ba89005544be9d85231a9e0d5f23b2d15b3311667e2dad0debd344918a3f80'
}

$Destination = [IO.Path]::GetFullPath($Destination)
$marker = Join-Path $Destination 'VERSION'
if (-not $Force -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $Version)) {
    Write-Host "Wintun $Version already in $Destination"
    exit 0
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("wintun-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp "wintun-$Version.zip"
    Write-Host "Downloading $Url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $ZipSha256) { throw "SHA-256 mismatch for wintun-$Version.zip: got $hash, expected $ZipSha256" }

    $x = Join-Path $tmp 'x'
    Expand-Archive -Path $zip -DestinationPath $x
    $root = Join-Path $x 'wintun'
    foreach ($arch in $DllSha256.Keys) {
        $dll = Join-Path $root "bin\$arch\wintun.dll"
        $h = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($h -ne $DllSha256[$arch]) { throw "SHA-256 mismatch for bin\$arch\wintun.dll: got $h" }
    }

    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    foreach ($arch in $DllSha256.Keys) {
        New-Item -ItemType Directory -Force (Join-Path $Destination "bin\$arch") | Out-Null
        Copy-Item (Join-Path $root "bin\$arch\wintun.dll") (Join-Path $Destination "bin\$arch\wintun.dll")
    }
    Copy-Item (Join-Path $root 'LICENSE.txt') (Join-Path $Destination 'LICENSE.txt')
    Copy-Item (Join-Path $root 'include\wintun.h') (Join-Path $Destination 'wintun.h')
    Set-Content -Path $marker -Value $Version -Encoding ascii
    Write-Host "Wintun $Version -> $Destination (zip SHA-256 verified)"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
