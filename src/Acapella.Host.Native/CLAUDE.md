# CLAUDE.md — Acapella.Host.Native

**Toolchain (as of 2026-09-13):** VS Build Tools (C++ workload) is installed at
`D:\Tools\VisualStudio\BuildTools` (MSVC 14.51.36231, Windows 10 SDK 10.0.26100.0) — large tools go
on D:. `build.ps1` looks there first, then the old `C:\Program Files\Microsoft Visual Studio\18\Community`
path. Confirmed working: `build.ps1` produces `AcapellaHostNative.dll` and `AcaJuceHostProbe.exe`
cleanly, and all native-bridge tests pass.

**Known environment defect — native (C++) toolchain (old VS 18 Community install):**
`C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\v145\Microsoft.VCToolsVersion.VC.14.51.props`
(and its sibling `.txt`) were corrupted (zero-byte) on that old install, which broke
`vcvarsall.bat`'s toolset-version resolution and the CMake Visual-Studio-generator's `VCTargetsPath`
probe. **Workaround in place:** `build.ps1` sets `INCLUDE`/`LIB`/`PATH` by hand and uses CMake's
`Ninja` generator, which calls `cl.exe`/`link.exe` directly and never touches that machinery. Keep
`build.ps1` as the documented build path even now that a fresh VS Build Tools install exists — it's
not known to be broken the same way, but Ninja works and there's no reason to switch back.

**Known benign quirk — debug-heap assertion on the plugin-scan path:** running the app (Debug
config) logs two debug-CRT assertions during/after startup:
```
minkernel\crts\ucrt\src\appcrt\heap\debug_heap.cpp(904) : Assertion failed: _CrtIsValidHeapPointer(block)
minkernel\crts\ucrt\src\appcrt\heap\debug_heap.cpp(908) : Assertion failed: is_block_type_valid(header->_block_use)
```
Root cause traced to `MainWindow`'s startup `Task.Run(() => _hostedService.EnsureScanned())`, which
calls `aca_scan_plugin` (JUCE's `VST3PluginFormat::findAllTypesForFile`) once per known FabFilter/
Melodyne VST3 path, off the JUCE-message thread that `aca_initialize()` bound. Investigated once
already: re-ran the full test
suite with `_CRTDBG_CHECK_ALWAYS_DF` (a full heap walk on *every* alloc/free — catches
overruns/double-frees at the exact call site) and it passed clean, i.e. no real corruption was ever
found — just a debug-CRT report triggered by cross-thread/plugin-DLL-unload interaction that the
lighter default check flags. `HostBridge.cpp`'s `juceInit()` deliberately routes CRT assertions to
stderr instead of a blocking modal dialog for this reason (a modal would hang a headless test run
forever). Don't chase this again without a new symptom (an actual crash, not just the log lines); if
it resurfaces, re-enable `_CRTDBG_CHECK_ALWAYS_DF` locally (it's ~10x slower, so not left on by
default) to re-confirm before assuming a real regression.

- JUCE (`JUCE/`) and the ARA SDK (`ARA_SDK/`) are cloned locally (gitignored). JUCE bundles its own
  VST3 SDK — do not also clone Steinberg's VST3 SDK separately.
