# Builds a KMDF driver with cl/link directly (Build Tools has no WDK MSBuild integration).
# Usage: .\build.ps1 -Name smoke -Sources smoke\smoke.c [-Out out]
param(
    [Parameter(Mandatory)] [string]$Name,
    [Parameter(Mandatory)] [string[]]$Sources,
    [string]$Out = "$PSScriptRoot\out",
    [string]$Kmdf = "1.33"   # 1.33 = Windows 11 21H2+ inbox
)
$ErrorActionPreference = 'Stop'
$msvc = & (Join-Path $PSScriptRoot '..\tools\find-msvc.ps1')   # any VS 2022+ with C++ (vswhere), else Build Tools
$kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
$ver  = "10.0.26100.0"
$cl   = "$msvc\bin\Hostx64\x64\cl.exe"
$link = "$msvc\bin\Hostx64\x64\link.exe"
$kmMaj, $kmMin = $Kmdf.Split('.')

New-Item -ItemType Directory -Force $Out | Out-Null
$inc = @("$kits\Include\$ver\km", "$kits\Include\$ver\shared", "$kits\Include\$ver\km\crt",
         "$kits\Include\wdf\kmdf\$Kmdf", "$msvc\include") | ForEach-Object { "/I`"$_`"" }

$cflags = @('/nologo','/c','/W4','/WX','/Zi','/O2','/GS','/Gy','/kernel','/Zp8','/Gz',
    '/guard:cf','/Qspectre','/D_AMD64_','/D_WIN64','/DAMD64','/wd4324','/DPOOL_NX_OPTIN=1',
    '/DNTDDI_VERSION=0x0A00000C','/D_WIN32_WINNT=0x0A00','/DWINVER=0x0A00',
    "/DKMDF_VERSION_MAJOR=$kmMaj","/DKMDF_VERSION_MINOR=$kmMin","/Fd$Out\$Name.pdb")

$objs = @()
foreach ($s in $Sources) {
    $o = Join-Path $Out ([IO.Path]::GetFileNameWithoutExtension($s) + '.obj')
    & $cl @cflags @inc "/Fo$o" (Resolve-Path (Join-Path $PSScriptRoot $s)).Path
    if ($LASTEXITCODE) { throw "cl failed: $s" }
    $objs += $o
}

$libs = @("$kits\Lib\$ver\km\x64\ntoskrnl.lib", "$kits\Lib\$ver\km\x64\hal.lib",
          "$kits\Lib\$ver\km\x64\wmilib.lib", "$kits\Lib\$ver\km\x64\BufferOverflowFastFailK.lib",
          "$kits\Lib\wdf\kmdf\x64\$Kmdf\WdfDriverEntry.lib", "$kits\Lib\wdf\kmdf\x64\$Kmdf\WdfLdr.lib")
& $link /nologo /DRIVER /SUBSYSTEM:NATIVE,10.00 /ENTRY:FxDriverEntry /NODEFAULTLIB /INTEGRITYCHECK `
    /GUARD:CF /NXCOMPAT /DYNAMICBASE /DEBUG /OPT:REF /OPT:ICF /MERGE:_TEXT=.text`;_PAGE=PAGE `
    "/OUT:$Out\$Name.sys" "/PDB:$Out\$Name.pdb" @objs @libs
if ($LASTEXITCODE) { throw "link failed" }
Get-Item "$Out\$Name.sys" | Select-Object Name, Length
