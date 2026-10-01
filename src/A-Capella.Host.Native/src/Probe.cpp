// Probe target for v6 Phase P3a task 0: proves the CMake -> DLL -> P/Invoke -> dotnet build
// pipeline works before any JUCE/VST3 hosting code is added. Once probe 2 (JUCE hosting a real
// plugin headlessly) is confirmed, this file is superseded by the real hosting bridge.

extern "C" __declspec(dllexport) int aca_probe()
{
    return 42;
}
