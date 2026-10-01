using System.Runtime.InteropServices;

namespace Acapella.Engine.Tests.Host;

/// <summary>Probe 1 for v6 Phase P3a task 0: proves the CMake -> DLL -> P/Invoke -> dotnet test
/// pipeline works end to end before any JUCE/VST3 hosting code is added. Superseded once the real
/// hosting bridge exists; kept as a canary that the native build + copy-to-output wiring hasn't
/// silently broken.</summary>
public static class NativeBridgeProbe
{
    [DllImport("AcapellaHostNative", CallingConvention = CallingConvention.Cdecl)]
    public static extern int aca_probe();
}

public class NativeBridgeProbeTests
{
    [Fact]
    public void AcaProbe_ReturnsExpectedConstant()
    {
        Assert.Equal(42, NativeBridgeProbe.aca_probe());
    }
}
