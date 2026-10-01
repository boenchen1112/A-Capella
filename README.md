# Acapella

A Windows desktop app for recording and mixing multi-layer acapella videos: one singer records up to four layers (melody, harmony, bass, beatbox…), and Acapella combines them into a single 2×2 grid video with all layers singing in sync.

## Features

- **Record or upload up to four layers.** Layer 1 can be recorded live (webcam + mic) or uploaded from a file. Later layers are recorded while the earlier ones play back through headphones.
- **Automatic latency calibration.** A one-time click-track measurement compensates for recording delay, so layers recorded minutes apart line up sample-accurately.
- **Optional metronome** with adjustable BPM.
- **Trim with a live preview.** The 2×2 composited preview updates as trims, mixes and effects change; there's no render-to-see step.
- **Per-layer effects chain:** noise gate, EQ, compressor, limiter, reverb, pan and pitch correction, each switchable per layer, with level meters on the mixing screen.
- **VST3 plugin hosting.** If FabFilter Pro-Q 4, Pro-C 3, Pro-L 2, Pro-G or Pro-R 2 is installed, it is used for its slot and opened in its own editor window. Otherwise Acapella falls back to its built-in processing.
- **Melodyne pitch editing via ARA**, alongside a built-in automatic pitch-correction mode.
- **Undo/redo and project save/load**, including live plugin edits.
- **MP4 export.** Four synced video feeds in a 2×2 grid with one mixed audio track. What you hear while editing is what gets exported.

## How it's built

- C# / .NET 8, WPF UI
- NAudio for audio I/O, plus a custom mixing engine
- FFmpeg (run as a subprocess over raw pipes) for capture, decode, encode and mux
- SkiaSharp for real-time 2×2 compositing
- Rubber Band for the built-in pitch correction
- A native C++ module (`src/Acapella.Host.Native`) built on JUCE that hosts VST3/ARA plugins and exposes a plain C ABI, called from C# via P/Invoke

```
src/Acapella.App           WPF application
src/Acapella.Engine        Audio, video, project, export and plugin-hosting logic
src/Acapella.Host.Native   JUCE-based VST3/ARA host (C++)
tests/                     xUnit test suites for the engine and the app
tools/HardwareChecks       Manual real-hardware checks (devices, sync, throughput)
```

## Requirements

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [FFmpeg](https://ffmpeg.org/) with `ffmpeg` and `ffprobe` on `PATH`
- To build the native plugin host:
  - Visual Studio Build Tools with the C++ workload (MSVC + Windows 10/11 SDK)
  - [CMake](https://cmake.org/) 3.22+
  - [Ninja](https://ninja-build.org/) (`winget install Ninja-build.Ninja`)
- Optional: FabFilter and/or Melodyne VST3 installs in `C:\Program Files\Common Files\VST3`. The app works without them using its built-in effects.

## Building

1. Clone the plugin SDKs into the native module folder. Both folders are gitignored.

   ```powershell
   cd src/Acapella.Host.Native
   git clone --branch 8.0.6 --depth 1 https://github.com/juce-framework/JUCE.git JUCE
   git clone --recursive https://github.com/Celemony/ARA_SDK.git ARA_SDK
   ```

   JUCE bundles its own VST3 SDK, so Steinberg's VST3 SDK doesn't need to be cloned separately.

2. Build the native module:

   ```powershell
   powershell -File src/Acapella.Host.Native/build.ps1            # Debug
   powershell -File src/Acapella.Host.Native/build.ps1 -Configuration Release
   ```

   `build.ps1` sets the MSVC/SDK environment by hand and uses CMake's Ninja generator instead of the Visual Studio generator. It looks for MSVC under `D:\Tools\VisualStudio\BuildTools`, then `C:\Program Files\Microsoft Visual Studio\18\Community`; edit `$msvcRoot` at the top of the script if yours is elsewhere. Re-run it whenever `src/Acapella.Host.Native` changes, *before* `dotnet build`: the app and test projects copy `AcapellaHostNative.dll` into their output.

3. Build, test and run the app:

   ```powershell
   dotnet build Acapella.sln
   dotnet test Acapella.sln
   dotnet run --project src/Acapella.App
   ```

   Tests that need a specific FabFilter or Melodyne plugin are skipped, with a message, when that plugin isn't installed.

## Third-party plugins

Acapella never bundles or redistributes FabFilter or Melodyne plugins. It only hosts copies the user has installed and licensed themselves.

## License

Copyright (C) 2026 boenchen1112

Licensed under the GNU Affero General Public License v3.0. See [LICENSE](LICENSE). Acapella links [JUCE](https://juce.com/), which is available under AGPLv3 (or a commercial license), and uses the [ARA SDK](https://github.com/Celemony/ARA_SDK) (Apache-2.0), [Rubber Band](https://breakfastquay.com/rubberband/) (GPL), NAudio (MIT) and SkiaSharp (MIT).
