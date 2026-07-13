using System.Runtime.InteropServices;

namespace Acapella.Engine.Host;

/// <summary>
/// Raw P/Invoke surface for AcapellaHostNative.dll's C ABI (v6 Phase P3a). Every instance is
/// normalized to plain stereo in/out by the native side (see HostBridge.cpp's normalizeToStereo) --
/// callers here never need to reason about a plugin's actual bus layout.
/// </summary>
internal static class NativeHostBridge
{
    private const string Lib = "AcapellaHostNative";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int aca_scan_plugin(string pluginPath,
        byte[] outName, int outNameSize,
        byte[] outVersion, int outVersionSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr aca_create_instance(string pluginPath,
        double sampleRate, int maxBlockSize,
        byte[] outError, int outErrorSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int aca_get_latency_samples(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern double aca_get_tail_seconds(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int aca_get_parameter_count(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int aca_get_parameter_name(IntPtr handle, int index, byte[] outName, int outNameSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float aca_get_parameter_value(IntPtr handle, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_set_parameter_value(IntPtr handle, int index, float value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_process_block(IntPtr handle,
        float[] inL, float[] inR, float[] outL, float[] outR, int numSamples);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int aca_get_state_size(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int aca_get_state(IntPtr handle, byte[] outBuffer, int bufferSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_set_state(IntPtr handle, byte[] data, int dataSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_release_instance(IntPtr handle);
}
