cmake_minimum_required(VERSION 3.22)
project(AcapellaHostNative VERSION 0.1.0 LANGUAGES CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Match the app's build config folder naming (Debug/Release) so a documented post-build copy step
# can find the DLL predictably regardless of generator. Single-config generators (NMake Makefiles,
# used by build.ps1 -- see its own comment for why) only honor the unsuffixed property and don't
# expand per-config generator expressions in it; multi-config generators (Visual Studio) only honor
# the per-configuration ones. Branch on CMAKE_CONFIGURATION_TYPES (only set for multi-config) rather
# than setting all of them at once, which produced a literal unexpanded "$Configuration" path under
# NMake.
function(aca_set_output_dir target)
    if(CMAKE_CONFIGURATION_TYPES)
        set_target_properties(${target} PROPERTIES
            RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/bin/Debug"
            RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin/Release"
        )
    else()
        if(NOT CMAKE_BUILD_TYPE)
            set(CMAKE_BUILD_TYPE "Debug")
        endif()
        set_target_properties(${target} PROPERTIES
            RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/${CMAKE_BUILD_TYPE}"
        )
    endif()
endfunction()

add_library(AcapellaHostNative SHARED
    src/Probe.cpp
)
aca_set_output_dir(AcapellaHostNative)

# v6 P3a probe 2 (go/no-go gate, see reviews/ or commit message): proves a real commercial
# FabFilter plugin can be scanned/instantiated/prepared/processed/released with no GUI session,
# before the full hosting abstraction is built on top of this assumption. Standalone console app
# (not part of AcapellaHostNative.dll) so the iteration loop is a quick build+run .exe.
option(ACA_BUILD_JUCE_PROBE "Build the JUCE headless-hosting probe console app" ON)
if(ACA_BUILD_JUCE_PROBE)
    add_subdirectory(JUCE)

    juce_add_console_app(AcaJuceHostProbe
        PRODUCT_NAME "AcaJuceHostProbe"
    )

    target_sources(AcaJuceHostProbe PRIVATE src/JuceHostProbe.cpp)

    target_compile_definitions(AcaJuceHostProbe PRIVATE
        JUCE_PLUGINHOST_VST3=1
        JUCE_USE_CURL=0
        JUCE_WEB_BROWSER=0
        JUCE_STANDALONE_APPLICATION=0
    )

    target_link_libraries(AcaJuceHostProbe PRIVATE
        juce::juce_audio_basics
        juce::juce_audio_devices
        juce::juce_audio_formats
        juce::juce_audio_processors
        juce::juce_audio_utils
        juce::juce_core
        juce::juce_data_structures
        juce::juce_events
        juce::juce_graphics
        juce::juce_gui_basics
        juce::juce_gui_extra
        juce::juce_recommended_config_flags
        juce::juce_recommended_lto_flags
        juce::juce_recommended_warning_flags
    )

    aca_set_output_dir(AcaJuceHostProbe)
endif()
