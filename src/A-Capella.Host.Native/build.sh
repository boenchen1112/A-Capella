#!/usr/bin/env bash
# macOS counterpart of build.ps1: builds the native VST3/ARA host as build/bin/<Configuration>/
# libA-CapellaHostNative.dylib. Requires Xcode Command Line Tools, CMake 3.22+ and Ninja
# (e.g. `pip3 install cmake ninja`, or Homebrew). Re-run whenever this folder changes, *before*
# `dotnet build`: the app and test projects copy the dylib into their output.
#
# Usage: ./build.sh [Debug|Release]
set -euo pipefail

# If the linker rejects the default SDK ("unknown architecture" in a .tbd file), point SDKROOT at an
# older one that matches your macOS, e.g. SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk.
CONFIG="${1:-Debug}"
DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$DIR"

[ -d JUCE ] || git clone --branch 8.0.6 --depth 1 https://github.com/juce-framework/JUCE.git JUCE
[ -d ARA_SDK ] || git clone --recursive --branch releases/2.2.0 --depth 1 https://github.com/Celemony/ARA_SDK.git ARA_SDK

cmake -S . -B "build/cmake-$CONFIG" -G Ninja -DCMAKE_BUILD_TYPE="$CONFIG"
cmake --build "build/cmake-$CONFIG"
echo "Built $DIR/build/bin/$CONFIG/libA-CapellaHostNative.dylib"
