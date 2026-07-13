cmake_minimum_required(VERSION 3.22)
project(AcapellaHostNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(AcapellaHostNative SHARED
    src/Probe.cpp
)

# Match the app's build config folder naming (Debug/Release) so a documented post-build copy step
# can find the DLL predictably regardless of generator. Single-config generators (NMake Makefiles,
# used by build.ps1 -- see its own comment for why) only honor the unsuffixed property and don't
# expand per-config generator expressions in it; multi-config generators (Visual Studio) only honor
# the per-configuration ones. Branch on CMAKE_CONFIGURATION_TYPES (only set for multi-config) rather
# than setting all of them at once, which produced a literal unexpanded "$Configuration" path under
# NMake.
if(CMAKE_CONFIGURATION_TYPES)
    set_target_properties(AcapellaHostNative PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/bin/Debug"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin/Release"
    )
else()
    if(NOT CMAKE_BUILD_TYPE)
        set(CMAKE_BUILD_TYPE "Debug")
    endif()
    set_target_properties(AcapellaHostNative PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/${CMAKE_BUILD_TYPE}"
    )
endif()
