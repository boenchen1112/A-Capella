# Canonical native-module build command (v6 P3a task 0). Documented here (and referenced from
# CLAUDE.md's Environment section) since the standard `cmake -G "Visual Studio 18 2026"` path is
# broken on this machine: VC/Auxiliary/Build/v145/Microsoft.VCToolsVersion.VC.14.51.props is a
# corrupted (zero-byte) file inside the VS install, which breaks vcvarsall.bat's toolset-version
# resolution and the VS-generator's VCTargetsPath probe. Fixing that requires admin rights (Program
# Files) or a VS "Repair" -- neither available/appropriate to do unattended here. This script
# routes around it entirely: it sets INCLUDE/LIB/PATH by hand from the known-good MSVC/SDK install
# paths and uses CMake's Ninja generator (installed via winget), which invokes cl.exe/link.exe
# directly and never touches the broken vcxproj/VCTargetsPath machinery. NMake Makefiles was tried
# first and configures fine for a trivial DLL, but produced a broken link step once JUCE's larger
# mixed-C/C++ module tree was added (a real .obj silently missing from the link command despite its
# own compile step reporting success) -- Ninja is the generator JUCE's own CMake support is tested
# against day to day and did not reproduce that failure.
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

$ninjaDir = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Ninja-build.Ninja_Microsoft.Winget.Source_8wekyb3d8bbwe"
$env:PATH = "$msvc\bin\Hostx64\x64;$sdkRoot\bin\$sdkVersion\x64;$ninjaDir;$env:PATH"
$env:INCLUDE = "$msvc\include;$sdkRoot\Include\$sdkVersion\ucrt;$sdkRoot\Include\$sdkVersion\um;$sdkRoot\Include\$sdkVersion\shared;$sdkRoot\Include\$sdkVersion\winrt"
$env:LIB = "$msvc\lib\x64;$sdkRoot\Lib\$sdkVersion\ucrt\x64;$sdkRoot\Lib\$sdkVersion\um\x64"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Join-Path $scriptDir "build"

# Forward slashes: CMake writes this path verbatim into a generated .cmake file, where a backslash
# is a string-escape character -- "\x64\rc.exe" broke on the (already-escape-like) "\x64" sequence.
$rcExe = "$sdkRoot\bin\$sdkVersion\x64\rc.exe" -replace '\\', '/'
$mtExe = "$sdkRoot\bin\$sdkVersion\x64\mt.exe" -replace '\\', '/'

if (-not (Test-Path $buildDir)) {
    # Must be a double-quoted (interpolated) string, not a bareword -DFOO=$var token -- PowerShell
    # does not reliably expand $var inside an unquoted native-command argument that also contains
    # literal text, and silently passed the literal string "$Configuration" through to CMake here.
    # CMAKE_RC_COMPILER/CMAKE_MT are passed explicitly: without vcvarsall.bat's environment (which
    # this script deliberately avoids, see top comment), CMake's Ninja+MSVC toolchain detection
    # doesn't reliably auto-find rc.exe/mt.exe, which otherwise silently breaks C-language rule
    # generation entirely (CMAKE_C_COMPILE_OBJECT missing) rather than failing on the RC step itself.
    cmake -B $buildDir -S $scriptDir -G "Ninja" "-DCMAKE_BUILD_TYPE=$Configuration" "-DCMAKE_RC_COMPILER=$rcExe" "-DCMAKE_MT=$mtExe"
}
cmake --build $buildDir --config $Configuration
