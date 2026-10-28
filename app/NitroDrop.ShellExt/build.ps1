<#
.SYNOPSIS
  Builds NitroDrop.ShellExt.dll (MSIX "NitroDrop ile gönder" IExplorerCommand) and runs its self-test.

.DESCRIPTION
  VS 2022 Build Tools (MSVC) + Windows SDK 10.0.26100, x64, static CRT (/MT; the MSIX carries no VC++ runtime),
  /W4 /WX. SelfTest.exe includes ShellExt.cpp with NITRODROP_SELFTEST: real IShellItemArray + COM factory path,
  command line quoting, --send-list file; it registers and launches nothing. Output: <OutDir>\NitroDrop.ShellExt.dll.
  Called by packaging\build-release.ps1 (the DLL goes into the MSIX layout only, not the portable zip).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File app\NitroDrop.ShellExt\build.ps1 -OutDir app\NitroDrop.ShellExt\out
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [switch]$SkipSelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$src = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutDir) { $OutDir = Join-Path $src 'out' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$objDir = Join-Path $OutDir 'obj'
New-Item -ItemType Directory -Force $objDir | Out-Null

$vs   = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools"
$msvc = (Get-ChildItem "$vs\VC\Tools\MSVC" -ErrorAction Stop | Sort-Object Name -Descending | Select-Object -First 1).FullName
$kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
$sdk  = '10.0.26100.0'
$cl   = "$msvc\bin\Hostx64\x64\cl.exe"
if (-not (Test-Path $cl)) { throw "cl.exe not found under $msvc (install VS 2022 Build Tools, C++ workload)" }

$incs = @("$msvc\include", "$kits\Include\$sdk\ucrt", "$kits\Include\$sdk\um", "$kits\Include\$sdk\shared") | ForEach-Object { "/I$_" }
$libs = @("$msvc\lib\x64", "$kits\Lib\$sdk\ucrt\x64", "$kits\Lib\$sdk\um\x64") | ForEach-Object { "/LIBPATH:$_" }
$flags = '/nologo', '/W4', '/WX', '/sdl', '/O2', '/MT', '/EHsc', '/GS', '/guard:cf', '/utf-8', '/std:c++17',
         '/DUNICODE', '/D_UNICODE', '/DWIN32_LEAN_AND_MEAN'
$sysLibs = 'ole32.lib', 'shell32.lib', 'shlwapi.lib', 'uuid.lib', 'user32.lib', 'kernel32.lib'

$dll = Join-Path $OutDir 'NitroDrop.ShellExt.dll'
& $cl @flags @incs "/Fo$objDir\ShellExt.obj" "/Fe$dll" /LD (Join-Path $src 'ShellExt.cpp') `
    /link /WX /DLL /NXCOMPAT /DYNAMICBASE /guard:cf "/DEF:$(Join-Path $src 'ShellExt.def')" "/IMPLIB:$objDir\NitroDrop.ShellExt.lib" @libs @sysLibs
if ($LASTEXITCODE) { throw "cl NitroDrop.ShellExt.dll failed (exit $LASTEXITCODE)" }

if (-not $SkipSelfTest) {
    $test = Join-Path $objDir 'SelfTest.exe'
    & $cl @flags @incs "/I$src" "/Fo$objDir\SelfTest.obj" "/Fe$test" (Join-Path $src 'SelfTest.cpp') /link /WX /SUBSYSTEM:CONSOLE @libs @sysLibs
    if ($LASTEXITCODE) { throw "cl SelfTest.exe failed (exit $LASTEXITCODE)" }
    & $test
    if ($LASTEXITCODE) { throw "NitroDrop.ShellExt self-test failed (exit $LASTEXITCODE)" }
}

# The two COM entry points must be exported (Explorer's surrogate calls DllGetClassObject).
$dumpbin = "$msvc\bin\Hostx64\x64\dumpbin.exe"
if (Test-Path $dumpbin) {
    $exports = & $dumpbin /nologo /exports $dll | Out-String
    foreach ($e in 'DllGetClassObject', 'DllCanUnloadNow') {
        if ($exports -notmatch $e) { throw "NitroDrop.ShellExt.dll does not export $e" }
    }
    $deps = & $dumpbin /nologo /dependents $dll | Out-String
    if ($deps -match '(?i)vcruntime|msvcp') { throw 'NitroDrop.ShellExt.dll depends on the VC++ runtime (expected /MT)' }
}
Write-Host "   $dll"
