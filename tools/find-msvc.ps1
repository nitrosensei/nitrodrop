<#
.SYNOPSIS
  Prints the newest MSVC toolset folder (...\VC\Tools\MSVC\<version>) that has an x64 cl.exe.

.DESCRIPTION
  Any Visual Studio 2022+ edition with the C++ workload (vswhere: Build Tools on dev PCs, Enterprise on GitHub's
  windows-latest runners), falling back to the classic VS 2022 Build Tools path. Used by driver\build.ps1,
  app\NitroDrop.ShellExt\build.ps1 and packaging\build-release.ps1:
      $msvc = & (Join-Path $repo 'tools\find-msvc.ps1')
#>
$ErrorActionPreference = 'Stop'
$roots = @()
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $roots += & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
$roots += "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools"
foreach ($r in $roots) {
    if (-not $r -or -not (Test-Path "$r\VC\Tools\MSVC")) { continue }
    $m = Get-ChildItem "$r\VC\Tools\MSVC" | Sort-Object Name -Descending | Select-Object -First 1
    if ($m -and (Test-Path "$($m.FullName)\bin\Hostx64\x64\cl.exe")) { return $m.FullName }
}
throw 'MSVC (cl.exe) not found: install Visual Studio 2022 (Build Tools) with the "Desktop development with C++" workload'
