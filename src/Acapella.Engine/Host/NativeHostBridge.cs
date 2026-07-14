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

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_initialize();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int aca_show_editor_window(IntPtr handle, string title);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_close_editor_window(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int aca_scan_plugin(string pluginPath,
        byte[] outName, int outNameSize,
        byte[] outVersion, int outVersionSize);

    /// <summary>v7 2A task 37: 0 = not found, 1 = found but not ARA-capable, 2 = ARA-capable.
    /// Safe to call without aca_initialize() first -- see AraBridge.cpp's doc comment.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int aca_scan_ara_capability(string pluginPath,
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
    public static extern void aca_reset(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_process_block(IntPtr handle,
        float[] inL, float[] inR, float[] outL, float[] outR, int numSamples);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int aca_get_state(IntPtr handle, byte[]? outBuffer, int bufferSize, out int outRequiredSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_set_state(IntPtr handle, byte[] data, int dataSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_release_instance(IntPtr handle);

    // v7 2A task 38: ARA hosting session (Document Controller + audio source registration bridge).
    // Requires aca_initialize() to have already run on this thread, same as aca_create_instance.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr aca_ara_create_session(string pluginPath,
        double sampleRate, int maxBlockSize,
        byte[] outError, int outErrorSize);

    /// <summary>channelBuffers is an array of pointers, one per channel, each pointing at a pinned
    /// caller-owned float buffer of at least numSamples floats -- the native side reads directly
    /// from these pointers (see AraBridge.cpp's AraAudioSourceBuffer), so the caller must keep them
    /// pinned and alive for as long as the returned handle is in use.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr aca_ara_register_audio_source(IntPtr sessionHandle,
        IntPtr[] channelBuffers, int numChannels, long numSamples, double sourceSampleRate,
        string persistentId,
        byte[] outError, int outErrorSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_ara_release_audio_source(IntPtr sessionHandle, IntPtr audioSourceHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void aca_ara_destroy_session(IntPtr sessionHandle);
}
