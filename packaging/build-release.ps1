<#
.SYNOPSIS
  One command for a NitroDrop release: self-contained app, portable zip, MSIX, and the separate driver zip.

.DESCRIPTION
  1. Driver (unless -SkipDriver): driver\build.ps1 (source list from driver\README.md) -> airdropwifi.sys,
     driver\package.ps1 -> UNSIGNED airdropwifi.cat (the setup wizard signs .sys/.cat per install),
     driver\tools\adwifi-cli -> adwifi-cli.exe (hardware runbook client). Needs MSVC (VS 2022+, C++ workload) + the
     WDK 10.0.26100. -DriverOptional turns a failed/missing driver build into a warning (CI runners without the WDK).
  2. App: dotnet publish NitroDrop (win-x64, SELF-CONTAINED, not trimmed, not single-file) into
     packaging\out\NitroDrop\. NitroDrop.SyncHost.exe (and NitroDrop.Radio.exe when app\Nitro.Radio.Host exists) are
     published self-contained too and laid over the same folder, because their default build output is
     framework-dependent and a fresh PC has no .NET 10 runtime. Payload: extension\, docs\ios-shortcut.md,
     docs\test-drive.md, LICENSE, THIRD-PARTY-NOTICES.md, PRIVACY.md, CODE_SIGNING_POLICY.md, licenses\.
     NOT in the app packages (they are code-signed and may only contain open-source components we build):
       - wintun.dll (WireGuard LLC, non-OSI "Prebuilt Binaries License"): downloaded on demand by the app after the
         user agrees, SHA-256 pinned + Authenticode "WireGuard LLC" (app\Nitro.Awdl\Ip\WintunInstall.cs).
       - the kernel driver (airdropwifi.sys/.inf/.cat) and tools\adwifi-cli.exe: NitroDrop-driver-<ver>.zip below.
  3. Explorer menu (MSIX only): app\NitroDrop.ShellExt\build.ps1 -> NitroDrop.ShellExt.dll (native IExplorerCommand,
     static CRT, self-test runs first) into the MSIX layout; the manifest registers it (desktop4 + com:SurrogateServer).
     The portable build registers its classic HKCU verb from Ayarlar instead (Services/ExplorerMenu.cs).
  4. Artifacts in packaging\out\:
       NitroDrop-<ver>-win-x64-portable.zip   unpackaged (calls: ANCS control only, no PC call audio)
       NitroDrop-<ver>-x64.msix               full-trust MSIX, UNSIGNED (Identity Publisher = -Publisher)
       NitroDrop-driver-<ver>.zip             driver\{inf,sys,cat} + tools\adwifi-cli.exe, UNSIGNED (test signing only;
                                              the setup wizard imports it: "Sürücü paketini seç...")
     -Sign runs sign-dev.ps1 (self-signed CN=Nitrobyte development certificate) on the MSIX at the end. -NoSign
     guarantees nothing is signed (CI: SignPath signs the artifacts afterwards, see .github\workflows\release.yml).

  Publisher: the MSIX Identity Publisher must equal the subject of the certificate that signs it. Local dev builds
  keep CN=Nitrobyte (sign-dev.ps1). The SignPath Foundation release build passes the SignPath Foundation certificate
  subject with -Publisher (repository variable MSIX_PUBLISHER, docs\signpath-apply.md).

  Why self-contained: the .NET 10 Desktop Runtime is not part of Windows (not inbox, not pulled by Windows Update by
  default) and an MSIX cannot depend on it as a framework package, so a framework-dependent build would fail on a
  fresh PC. Not trimmed: WPF/WinForms are not trim-safe (the SDK blocks PublishTrimmed for them). Not single-file:
  the MSIX needs loose files anyway and NitroDrop.SyncHost.exe must sit next to NitroDrop.exe.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1
  powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1 -SkipDriver -Sign
  powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1 -NoSign -DriverOptional -Version 0.2.0.0 -Publisher 'CN=SignPath Foundation, ...'
#>
[CmdletBinding()]
param(
    [string]$Version,                        # MSIX/assembly version a.b.c.d; default 0.1.<yyDDD>.<HHmm>
    [string]$Publisher = 'CN=Nitrobyte',     # MSIX Identity Publisher = subject of the signing certificate
                                             # (dev: sign-dev.ps1 -Subject; CI: the SignPath Foundation subject)
    [switch]$SkipDriver,                     # reuse driver\out\airdropwifi.{sys,cat} as they are
    [switch]$DriverOptional,                 # driver build/files missing -> warning, no driver zip (CI without WDK)
    [switch]$SkipZip,
    [switch]$SkipMsix,
    [switch]$Sign,                           # run sign-dev.ps1 on the MSIX afterwards (local development)
    [switch]$NoSign                          # never sign anything (CI; SignPath signs the uploaded artifacts)
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
if ($Sign -and $NoSign) { throw '-Sign and -NoSign exclude each other' }
if ($Publisher -notmatch '^CN=') { throw "-Publisher must be an X.500 subject starting with CN= (got '$Publisher')" }

$repo    = Split-Path -Parent $PSScriptRoot
$pkgRoot = $PSScriptRoot
$out     = Join-Path $pkgRoot 'out'
$work    = Join-Path $pkgRoot 'obj'
$stage   = Join-Path $out 'NitroDrop'
$app     = Join-Path $repo 'app'
$rid     = 'win-x64'

if (-not $Version) {
    $now = Get-Date
    $Version = '0.1.{0}{1:000}.{2}' -f $now.ToString('yy'), $now.DayOfYear, [int]$now.ToString('HHmm')
}
if ($Version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$' -or ($Version.Split('.') | Where-Object { [int]$_ -gt 65535 })) {
    throw "Version must be a.b.c.d with parts <= 65535 (got '$Version')"
}

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE) { throw "$what failed (exit $LASTEXITCODE)" }
}

$kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
function Find-SdkTool([string]$name) {
    $t = Get-ChildItem "$kits\bin\10.*\x64\$name" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $t) { throw "$name not found under $kits\bin (install the Windows SDK)" }
    $t.FullName
}

New-Item -ItemType Directory -Force $out, $work | Out-Null

# ---------------------------------------------------------------- 1. driver (separate artifact)
function Build-Driver {
    Step 'Driver: airdropwifi.sys (driver\build.ps1)'
    $sources = 'kmdf\driver.c','kmdf\osdep_kmdf.c','kmdf\ioctl.c','core\iwx_fw.c','core\iwx_trans.c','core\iwx_cmd.c','core\iwx_init.c','core\iwx_radio.c'
    & (Join-Path $repo 'driver\build.ps1') -Name airdropwifi -Sources $sources

    Step 'Driver: firmware for the catalog hash (tools\fetch-firmware.ps1, pinned SHA-256; not shipped)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\fetch-firmware.ps1')
    if ($LASTEXITCODE) { throw 'fetch-firmware.ps1 failed' }

    Step 'Driver: unsigned catalog (driver\package.ps1)'
    & (Join-Path $repo 'driver\package.ps1')

    Step 'Driver: adwifi-cli.exe (hardware runbook client)'
    $msvc = & (Join-Path $repo 'tools\find-msvc.ps1')
    $sdk  = '10.0.26100.0'
    $cliOut = Join-Path $repo 'driver\out\adwifi-cli'
    New-Item -ItemType Directory -Force $cliOut | Out-Null
    $incs = @("$msvc\include", "$kits\Include\$sdk\ucrt", "$kits\Include\$sdk\um", "$kits\Include\$sdk\shared") | ForEach-Object { "/I$_" }
    $libs = @("$msvc\lib\x64", "$kits\Lib\$sdk\ucrt\x64", "$kits\Lib\$sdk\um\x64") | ForEach-Object { "/LIBPATH:$_" }
    $src  = Join-Path $repo 'driver\tools\adwifi-cli\adwifi-cli.c'
    $inc2 = "/I" + (Join-Path $repo 'driver\include')
    Invoke-Checked 'cl adwifi-cli' { & "$msvc\bin\Hostx64\x64\cl.exe" /nologo /W4 /WX /sdl /O2 @incs $inc2 "/Fo$cliOut\adwifi-cli.obj" "/Fe$cliOut\adwifi-cli.exe" $src /link @libs cfgmgr32.lib }
}

$haveDriver = $true
if (-not $SkipDriver) {
    try { Build-Driver }
    catch {
        if (-not $DriverOptional) { throw }
        Write-Warning "driver build failed, continuing without the NitroDrop-driver zip (-DriverOptional): $($_.Exception.Message)"
        $haveDriver = $false
    }
}
foreach ($f in 'airdropwifi.sys', 'airdropwifi.cat') {
    if (-not (Test-Path (Join-Path $repo "driver\out\$f"))) {
        if (-not $DriverOptional) { throw "driver\out\$f missing (run without -SkipDriver)" }
        $haveDriver = $false
    }
}
if ($haveDriver) {
    $sys = Get-Item (Join-Path $repo 'driver\out\airdropwifi.sys')
    $cat = Get-Item (Join-Path $repo 'driver\out\airdropwifi.cat')
    if ($cat.LastWriteTimeUtc -lt $sys.LastWriteTimeUtc) { Write-Warning 'airdropwifi.cat is older than airdropwifi.sys - catalog hash will not match; rerun driver\package.ps1' }
} else {
    Write-Warning 'no driver files: the NitroDrop-driver zip will not be produced'
}

# ---------------------------------------------------------------- 2. app
# NitroBundleDriver=false: the driver goes into NitroDrop-driver-<ver>.zip, not next to NitroDrop.exe.
$common = @('-c', 'Release', '--nologo', '-v', 'q', "-p:Version=$Version", '-p:SatelliteResourceLanguages=tr%3Ben%3Bde%3Bfr%3Bru%3Bpl',
            '-p:NitroBundleDriver=false')

Step 'Nitro.Sync.Host framework-dependent build (paths AirDrop.App.csproj copies from)'
Invoke-Checked 'dotnet build Nitro.Sync.Host' { dotnet build (Join-Path $app 'Nitro.Sync.Host\Nitro.Sync.Host.csproj') @common }
# AirDrop.App.csproj also copies NitroDrop.Radio.* from Nitro.Radio.Host\bin\Release; build it first so a clean
# machine (CI) has those files.
$radioProj = Join-Path $app 'Nitro.Radio.Host\Nitro.Radio.Host.csproj'
if (Test-Path $radioProj) {
    Step 'Nitro.Radio.Host framework-dependent build (paths AirDrop.App.csproj copies from)'
    Invoke-Checked 'dotnet build Nitro.Radio.Host' { dotnet build $radioProj @common }
}

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
Step "dotnet publish NitroDrop ($rid, self-contained, untrimmed) -> $stage"
Invoke-Checked 'dotnet publish AirDrop.App' {
    dotnet publish (Join-Path $app 'AirDrop.App\AirDrop.App.csproj') @common -r $rid --self-contained true `
        -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $stage
}

# Extra executables: published self-contained on their own, then laid over $stage. Their own files
# (<AssemblyName>.exe/.dll/.deps.json/.runtimeconfig.json/.pdb) overwrite; shared runtime files are only added
# when missing (same SDK -> same runtime pack, so they are identical anyway).
function Publish-Overlay([string]$csproj, [string]$assemblyName) {
    $tmp = Join-Path $work ("publish-" + $assemblyName)
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    Invoke-Checked "dotnet publish $assemblyName" {
        dotnet publish $csproj @common -r $rid --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o $tmp
    }
    Get-ChildItem $tmp -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($tmp.Length + 1)
        $dest = Join-Path $stage $rel
        $own = $_.Name.StartsWith($assemblyName + '.', [StringComparison]::OrdinalIgnoreCase)
        if ($own -or -not (Test-Path $dest)) {
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item $_.FullName $dest -Force
        }
    }
}
Step 'NitroDrop.SyncHost.exe self-contained overlay'
Publish-Overlay (Join-Path $app 'Nitro.Sync.Host\Nitro.Sync.Host.csproj') 'NitroDrop.SyncHost'

Step 'nitro-phoneline-probe.exe self-contained overlay (MSIX alias for PhoneLine diagnostics)'
Publish-Overlay (Join-Path $app 'Nitro.PhoneLineProbe\Nitro.PhoneLineProbe.csproj') 'nitro-phoneline-probe'

$radio = Get-ChildItem (Join-Path $app 'Nitro.Radio.Host') -Filter *.csproj -ErrorAction SilentlyContinue | Select-Object -First 1
if ($radio) {
    [xml]$x = Get-Content $radio.FullName
    $an = ($x.Project.PropertyGroup | ForEach-Object { $_.AssemblyName } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $an) { $an = 'NitroDrop.Radio' }
    Step "$an.exe self-contained overlay ($($radio.Name))"
    Publish-Overlay $radio.FullName $an
} else {
    Write-Host '   (app\Nitro.Radio.Host not present - NitroDrop.Radio.exe not included)'
}

Step 'Payload: docs, licences (no wintun, no driver)'
New-Item -ItemType Directory -Force (Join-Path $stage 'licenses'), (Join-Path $stage 'docs') | Out-Null
foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'PRIVACY.md', 'CODE_SIGNING_POLICY.md') {
    $p = Join-Path $repo $f
    if (Test-Path $p) { Copy-Item $p (Join-Path $stage $f) -Force }
}
$fwLic = Join-Path $repo 'driver\firmware\LICENCE.iwlwifi_firmware'
if (Test-Path $fwLic) { Copy-Item $fwLic (Join-Path $stage 'licenses\LICENCE.iwlwifi_firmware') -Force }
foreach ($d in 'test-drive.md', 'ios-shortcut.md') {
    $p = Join-Path $repo "docs\$d"
    if (Test-Path $p) { Copy-Item $p (Join-Path $stage "docs\$d") -Force }
}
# Never in the signed app packages: firmware (the wizard downloads it, licence shown first), Wintun (downloaded on
# demand after consent), the kernel driver and its CLI (NitroDrop-driver zip). Remove leftovers defensively.
Get-ChildItem $stage -Recurse -File -Include *.ucode, 'wintun*.dll', 'wintun-LICENSE.txt', 'airdropwifi.*', 'adwifi-cli.exe' | Remove-Item -Force
foreach ($d in 'driver', 'wintun', 'tools') {
    $p = Join-Path $stage $d
    if ((Test-Path $p) -and -not (Get-ChildItem $p -Recurse -File)) { Remove-Item -Recurse -Force $p }
}

$required = 'NitroDrop.exe', 'NitroDrop.dll', 'NitroDrop.SyncHost.exe', 'NitroDrop.SyncHost.dll', 'nitro-phoneline-probe.exe', 'hostfxr.dll', 'coreclr.dll',
            'PresentationFramework.dll', 'Nitro.Awdl.dll', 'extension\manifest.json', 'docs\ios-shortcut.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md'
$missing = $required | Where-Object { -not (Test-Path (Join-Path $stage $_)) }
if ($missing) { throw "payload incomplete: $($missing -join ', ')" }
$forbidden = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -match '(?i)^(wintun.*\.dll|airdropwifi\..*|adwifi-cli\.exe|.*\.ucode)$' }
if ($forbidden) { throw "payload contains files that must not be shipped in the signed packages: $($forbidden.FullName -join ', ')" }
$rc = Get-Content (Join-Path $stage 'NitroDrop.SyncHost.runtimeconfig.json') -Raw
if ($rc -notmatch 'includedFrameworks') { throw 'NitroDrop.SyncHost is framework-dependent in the payload (overlay failed)' }

$size = (Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("   {0} files, {1:N1} MB" -f (Get-ChildItem $stage -Recurse -File).Count, ($size / 1MB))

# ---------------------------------------------------------------- 3a. portable zip
$zip = Join-Path $out "NitroDrop-$Version-win-x64-portable.zip"
if (-not $SkipZip) {
    Step "Portable zip -> $zip"
    Get-ChildItem $out -Filter 'NitroDrop-*-portable.zip' | Remove-Item -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
}

# ---------------------------------------------------------------- 3b. MSIX
$msix = Join-Path $out "NitroDrop-$Version-x64.msix"
if (-not $SkipMsix) {
    Step 'MSIX layout'
    $layout = Join-Path $work 'msix-layout'
    if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
    New-Item -ItemType Directory -Force $layout | Out-Null
    Copy-Item (Join-Path $stage '*') $layout -Recurse -Force
    $assets = Join-Path $layout 'Assets'
    New-Item -ItemType Directory -Force $assets | Out-Null
    Copy-Item (Join-Path $repo 'brand\final\app\store\*.png') $assets -Force

    Step 'Explorer menu: NitroDrop.ShellExt.dll (app\NitroDrop.ShellExt\build.ps1, self-test included)'
    $shellExtOut = Join-Path $work 'shellext'
    & (Join-Path $app 'NitroDrop.ShellExt\build.ps1') -OutDir $shellExtOut -Version $Version
    Copy-Item (Join-Path $shellExtOut 'NitroDrop.ShellExt.dll') $layout -Force

    $manifest = (Get-Content (Join-Path $pkgRoot 'AppxManifest.xml') -Raw).Replace('__VERSION__', $Version).Replace('__PUBLISHER__', [Security.SecurityElement]::Escape($Publisher))
    [IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))

    # resources.pri: maps Assets\X.png to the scale-*/targetsize-* variants and the localized manifest strings
    # (packaging\Strings\<lang>\Resources.resw -> ms-resource:AppDescription ...). Indexed from a mini tree
    # (manifest + Assets + Strings only), otherwise makepri would index the whole 200 MB payload.
    Step 'resources.pri (makepri)'
    $makepri = Find-SdkTool 'makepri.exe'
    $priRoot = Join-Path $work 'pri'
    if (Test-Path $priRoot) { Remove-Item -Recurse -Force $priRoot }
    New-Item -ItemType Directory -Force $priRoot | Out-Null
    Copy-Item $assets (Join-Path $priRoot 'Assets') -Recurse
    Copy-Item (Join-Path $pkgRoot 'Strings') (Join-Path $priRoot 'Strings') -Recurse
    Copy-Item (Join-Path $layout 'AppxManifest.xml') $priRoot
    $priCfg = Join-Path $work 'priconfig.xml'
    Invoke-Checked 'makepri createconfig' { & $makepri createconfig /cf $priCfg /dq tr-TR_en-US_de-DE_fr-FR_ru-RU_pl-PL /pv 10.0.0 /o | Out-Null }
    Invoke-Checked 'makepri new' { & $makepri new /pr $priRoot /cf $priCfg /mn (Join-Path $priRoot 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null }

    Step "makeappx pack (validates the manifest schema) -> $msix"
    $makeappx = Find-SdkTool 'makeappx.exe'
    Get-ChildItem $out -Filter 'NitroDrop-*-x64.msix' | Remove-Item -Force
    Invoke-Checked 'makeappx pack' { & $makeappx pack /d $layout /p $msix /o | Select-Object -Last 3 | Out-Host }
    Copy-Item (Join-Path $pkgRoot 'install-msix.ps1') $out -Force
}

# ---------------------------------------------------------------- 3c. driver zip (separate, unsigned)
$driverZip = Join-Path $out "NitroDrop-driver-$Version.zip"
Get-ChildItem $out -Filter 'NitroDrop-driver-*.zip' | Remove-Item -Force
if ($haveDriver) {
    Step "Driver zip (UNSIGNED, test signing only) -> $driverZip"
    $dstage = Join-Path $work 'NitroDrop-driver'
    if (Test-Path $dstage) { Remove-Item -Recurse -Force $dstage }
    New-Item -ItemType Directory -Force (Join-Path $dstage 'driver'), (Join-Path $dstage 'tools'), (Join-Path $dstage 'licenses') | Out-Null
    Copy-Item (Join-Path $repo 'driver\airdropwifi.inf') (Join-Path $dstage 'driver') -Force
    Copy-Item (Join-Path $repo 'driver\out\airdropwifi.sys'), (Join-Path $repo 'driver\out\airdropwifi.cat') (Join-Path $dstage 'driver') -Force
    $cli = Join-Path $repo 'driver\out\adwifi-cli\adwifi-cli.exe'
    if (Test-Path $cli) { Copy-Item $cli (Join-Path $dstage 'tools\adwifi-cli.exe') -Force } else { Write-Warning 'adwifi-cli.exe not built (run without -SkipDriver)' }
    Copy-Item (Join-Path $repo 'LICENSE'), (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $dstage -Force
    if (Test-Path $fwLic) { Copy-Item $fwLic (Join-Path $dstage 'licenses\LICENCE.iwlwifi_firmware') -Force }
    $readme = @(
        "NitroDrop Wi-Fi driver $Version (airdropwifi, Intel So/Hr cards) - UNSIGNED",
        '',
        'This package is NOT code-signed and is NOT part of the signed NitroDrop app. A kernel driver needs Microsoft''s',
        'attestation signature to load normally; this one only loads with Windows test signing enabled. NitroDrop''s',
        'Advanced Setup wizard explains every step (BitLocker, Secure Boot, test signing), signs these files with a',
        'certificate created on your PC and can roll everything back.',
        '',
        'Use: extract this zip, open NitroDrop > Settings > Wi-Fi driver > Advanced Setup and, on the install step, click',
        '"Choose driver package..." and pick this zip or driver\airdropwifi.inf. The Intel firmware is downloaded by the',
        'wizard (its licence is shown first). Source: driver\ in the NitroDrop repository (ISC/BSD, THIRD-PARTY-NOTICES.md).',
        'tools\adwifi-cli.exe is the hardware runbook client (docs/test-drive.md).'
    ) -join "`r`n"
    [IO.File]::WriteAllText((Join-Path $dstage 'README.txt'), $readme, (New-Object Text.UTF8Encoding($true)))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($dstage, $driverZip, [IO.Compression.CompressionLevel]::Optimal, $true)
}

Step 'Done'
foreach ($a in @($zip, $msix, $driverZip)) {
    if (Test-Path $a) { Write-Host ("   {0}  {1:N1} MB" -f $a, ((Get-Item $a).Length / 1MB)) }
}
if ($Sign -and -not $SkipMsix) {
    & (Join-Path $pkgRoot 'sign-dev.ps1') -Msix $msix -Subject $Publisher
} elseif ($NoSign) {
    Write-Host '   -NoSign: artifacts are unsigned (SignPath signs them in CI)'
} elseif (-not $SkipMsix) {
    Write-Host '   MSIX is unsigned: powershell -ExecutionPolicy Bypass -File packaging\sign-dev.ps1'
}
