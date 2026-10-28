# Builds and runs the host (user-mode) tests for the portable iwx core.
# Same warning level as the driver: /W4 /WX. Output: driver\tests\out\.
# Usage: powershell -ExecutionPolicy Bypass -File driver\tests\build-tests.ps1 [-NoRun]
param([switch]$NoRun)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out  = Join-Path $here 'out'
New-Item -ItemType Directory -Force $out | Out-Null

# same toolchain discovery as driver\build.ps1 (Build Tools 2022 + SDK 26100)
$vs   = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools"
$msvc = (Get-ChildItem "$vs\VC\Tools\MSVC" | Sort-Object Name -Descending | Select-Object -First 1).FullName
$kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
$ver  = "10.0.26100.0"
$env:INCLUDE = "$msvc\include;$kits\Include\$ver\ucrt;$kits\Include\$ver\um;$kits\Include\$ver\shared"
$env:LIB     = "$msvc\lib\x64;$kits\Lib\$ver\ucrt\x64;$kits\Lib\$ver\um\x64"
$cl = "$msvc\bin\Hostx64\x64\cl.exe"

$core  = 'iwx_fw.c','iwx_trans.c','iwx_cmd.c','iwx_init.c','iwx_radio.c' | ForEach-Object { (Resolve-Path (Join-Path $here "..\core\$_")).Path }
$tests = 'main.c','mock_osdep.c','test_fw.c','test_init.c','test_radio.c','test_queues.c' | ForEach-Object { (Resolve-Path (Join-Path $here $_)).Path }
$exe = Join-Path $out 'iwx_tests.exe'

Push-Location $out
try {
    & $cl /nologo /W4 /WX /Zi /Od /MTd /D_CRT_SECURE_NO_WARNINGS /DAIRDROP_HOST_TEST `
        $tests $core "/Fe:$exe" /link /INCREMENTAL:NO
    if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
} finally { Pop-Location }

if ($NoRun) { return }
& $exe (Join-Path $here '..\firmware\iwlwifi-so-a0-hr-b0-77.ucode')
if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }
