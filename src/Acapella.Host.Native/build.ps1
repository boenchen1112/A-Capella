# Canonical native-module build command (v6 P3a task 0). Documented here (and referenced from
# CLAUDE.md's Environment section) since the standard `cmake -G "Visual Studio 18 2026"` path is
# broken on this machine: VC/Auxiliary/Build/v145/Microsoft.VCToolsVersion.VC.14.51.props is a
# corrupted (zero-byte) file inside the VS install, which breaks vcvarsall.bat's toolset-version
# resolution and the VS-generator's VCTargetsPath probe. Fixing that requires admin rights (Program
# Files) or a VS "Repair" -- neither available/appropriate to do unattended here. This script
# routes around it entirely: it sets INCLUDE/LIB/PATH by hand from the known-good MSVC/SDK install
# paths and uses CMake's NMake Makefiles generator, which invokes cl.exe/link.exe directly instead
# of going through the broken vcxproj/VCTargetsPath machinery.
#
# Usage: powershell -File build.ps1 [-Configuration Debug|Release]
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$msvcRoot = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC"
$msvcVersion = (Get-ChildItem $msvcRoot | Sort-Object Name -Descending | Select-Object -First 1).Name
$msvc = Join-Path $msvcRoot $msvcVersion

$sdkRoot = "C:\Program Files (x86)\Windows Kits\10"
$sdkVersion = (Get-ChildItem (Join-Path $sdkRoot "Include") | Sort-Object Name -Descending | Select-Object -First 1).Name

$env:PATH = "$msvc\bin\Hostx64\x64;$sdkRoot\bin\$sdkVersion\x64;$env:PATH"
$env:INCLUDE = "$msvc\include;$sdkRoot\Include\$sdkVersion\ucrt;$sdkRoot\Include\$sdkVersion\um;$sdkRoot\Include\$sdkVersion\shared;$sdkRoot\Include\$sdkVersion\winrt"
$env:LIB = "$msvc\lib\x64;$sdkRoot\Lib\$sdkVersion\ucrt\x64;$sdkRoot\Lib\$sdkVersion\um\x64"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Join-Path $scriptDir "build"

if (-not (Test-Path $buildDir)) {
    # Must be a double-quoted (interpolated) string, not a bareword -DFOO=$var token -- PowerShell
    # does not reliably expand $var inside an unquoted native-command argument that also contains
    # literal text, and silently passed the literal string "$Configuration" through to CMake here.
    cmake -B $buildDir -S $scriptDir -G "NMake Makefiles" "-DCMAKE_BUILD_TYPE=$Configuration"
}
cmake --build $buildDir --config $Configuration
