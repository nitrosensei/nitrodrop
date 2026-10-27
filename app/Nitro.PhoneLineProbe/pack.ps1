# Builds nitro-phoneline-probe and an UNSIGNED sparse package (identity only) for it.
#   powershell -ExecutionPolicy Bypass -File app\Nitro.PhoneLineProbe\pack.ps1
# Output: bin\pkg\NitroPhoneLineProbe.msix  (payload = AppxManifest.xml + 3 logos, no executable)
#         bin\Release\net10.0-windows10.0.22621.0\win-x64\  = external location (the exe)
#
# Install (see docs/continuity-plan.md, "PhoneLine / Phone Link yolu"):
#   Add-AppxPackage -Path <msix> -ExternalLocation <exe dir> -AllowUnsigned
# Remove:
#   Get-AppxPackage Nitrobyte.NitroDrop.PhoneLineProbe | Remove-AppxPackage
$ErrorActionPreference = 'Stop'
$proj = $PSScriptRoot
$bin  = Join-Path $proj 'bin'
$exeDir = Join-Path $bin 'Release\net10.0-windows10.0.22621.0\win-x64'
$layout = Join-Path $bin 'pkg\layout'
$msix = Join-Path $bin 'pkg\NitroPhoneLineProbe.msix'

dotnet build (Join-Path $proj 'Nitro.PhoneLineProbe.csproj') -c Release --nologo -v q
if ($LASTEXITCODE) { throw "dotnet build failed" }

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
Copy-Item (Join-Path $proj 'Package\AppxManifest.xml') $layout

Add-Type -AssemblyName System.Drawing
foreach ($a in @(@('StoreLogo', 50), @('Square150x150Logo', 150), @('Square44x44Logo', 44))) {
    $bmp = New-Object System.Drawing.Bitmap $a[1], $a[1]
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 0, 120, 215))
    $g.Dispose()
    $bmp.Save((Join-Path $layout "Assets\$($a[0]).png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found (install the Windows SDK)" }
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx.FullName pack /d $layout /p $msix /nv /o | Out-Host
if ($LASTEXITCODE) { throw "makeappx failed" }

Write-Host ""
Write-Host "Package : $msix"
Write-Host "ExtLoc  : $exeDir"
