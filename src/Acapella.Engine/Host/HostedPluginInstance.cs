using System.Text;

namespace Acapella.Engine.Host;

public sealed record HostedPluginDescription(string Name, string Version);

/// <summary>v7 2A task 37: a plugin can be a valid VST3 (HostedPluginDescription/TryScan already
/// proves that) while still not exposing the ARA factory extension -- IsAraCapable is the third
/// state TryScan's bool can't represent.</summary>
public sealed record AraCapabilityDescription(string Name, string Version, bool IsAraCapable);

/// <summary>
/// Managed wrapper around one native-hosted VST3 plugin instance (v6 Phase P3a). The native side
/// always normalizes to plain stereo in/out, so ProcessBlock here always deals in stereo float
/// arrays regardless of the wrapped plugin's real bus layout.
/// </summary>
public sealed class HostedPluginInstance : IDisposable
{
    // Defensive cap per the v6 plan (P3a task 8): a state chunk this large indicates something has
    // gone wrong (corrupt plugin state, bridge bug) rather than a legitimate FabFilter/Melodyne
    // preset, which are all a few KB.
    private const int MaxStateBytes = 1024 * 1024;

    private IntPtr _handle;

    private HostedPluginInstance(IntPtr handle)
    {
        _handle = handle;
    }

    public static bool TryScan(string pluginPath, out HostedPluginDescription? description)
    {
        var nameBuf = new byte[256];
        var versionBuf = new byte[64];
        int found = NativeHostBridge.aca_scan_plugin(pluginPath, nameBuf, nameBuf.Length, versionBuf, versionBuf.Length);
        if (found == 0)
        {
            description = null;
            return false;
        }
        description = new HostedPluginDescription(ToString(nameBuf), ToString(versionBuf));
        return true;
    }

    /// <summary>v7 2A task 37: mirrors TryScan, but calls aca_scan_ara_capability instead of
    /// aca_scan_plugin -- safe to call without Initialize() first (reads factory metadata only, no
    /// MessageManager touch; see AraBridge.cpp's doc comment).</summary>
    public static bool TryScanAraCapability(string pluginPath, out AraCapabilityDescription? description)
    {
        var nameBuf = new byte[256];
        var versionBuf = new byte[64];
        int result = NativeHostBridge.aca_scan_ara_capability(pluginPath, nameBuf, nameBuf.Length, versionBuf, versionBuf.Length);
        if (result == 0)
        {
            description = null;
            return false;
        }
        description = new AraCapabilityDescription(ToString(nameBuf), ToString(versionBuf), result == 2);
        return true;
    }

    public static HostedPluginInstance Create(string pluginPath, double sampleRate, int maxBlockSize)
    {
        var errorBuf = new byte[512];
        var handle = NativeHostBridge.aca_create_instance(pluginPath, sampleRate, maxBlockSize, errorBuf, errorBuf.Length);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create hosted plugin instance for '{pluginPath}': {ToString(errorBuf)}");
        return new HostedPluginInstance(handle);
    }

    public int LatencySamples => NativeHostBridge.aca_get_latency_samples(_handle);

    public double TailSeconds => NativeHostBridge.aca_get_tail_seconds(_handle);

    public int ParameterCount => NativeHostBridge.aca_get_parameter_count(_handle);

    public string GetParameterName(int index)
    {
        var buf = new byte[256];
        NativeHostBridge.aca_get_parameter_name(_handle, index, buf, buf.Length);
        return ToString(buf);
    }

    public float GetParameterValue(int index) => NativeHostBridge.aca_get_parameter_value(_handle, index);

    public void SetParameterValue(int index, float value) => NativeHostBridge.aca_set_parameter_value(_handle, index, value);

    /// <summary>Opens the plugin's own editor in its own top-level window (P3a task 7 -- never
    /// embedded in WPF). Must be called on the same thread as HostedPluginInstance.Initialize()
    /// (the WPF UI thread) -- JUCE's MessageManager is bound to whichever thread that was.
    /// Returns false if the plugin has no editor.</summary>
    public bool ShowEditorWindow(string title) => NativeHostBridge.aca_show_editor_window(_handle, title) != 0;

    /// <summary>Closes the editor window if open. Safe to call when none is open. Same
    /// thread-affinity requirement as ShowEditorWindow.</summary>
    public void CloseEditorWindow() => NativeHostBridge.aca_close_editor_window(_handle);

    /// <summary>Binds JUCE's MessageManager to the calling thread. Call exactly once, from the
    /// app's WPF UI thread, before any other HostedPluginInstance/HostedPluginAvailability call --
    /// every plugin-lifecycle and editor-window call must then happen on that same thread
    /// (ProcessBlock is the only call meant to run on a different, audio-processing thread).</summary>
    public static void Initialize() => NativeHostBridge.aca_initialize();

    public void ProcessBlock(float[] inL, float[] inR, float[] outL, float[] outR, int numSamples) =>
        NativeHostBridge.aca_process_block(_handle, inL, inR, outL, outR, numSamples);

    /// <summary>Clears the plugin's internal DSP state (lookahead/delay lines etc.) without
    /// destroying/recreating it (v7 Q0 task 5, audit A5). Call before reusing a cached instance in
    /// a freshly built chain -- otherwise the first samples out on a second/subsequent play are
    /// stale audio buffered from the previous run.</summary>
    public void Reset() => NativeHostBridge.aca_reset(_handle);

    // Optimistic first-attempt buffer size (v7 Q0, audit B5): every known FabFilter/Melodyne
    // preset chunk is well under this, so GetState is a single native round-trip in the common
    // case; only a plugin reporting more than this retries once with its actual required size.
    private const int OptimisticStateBufferBytes = 8192;

    /// <summary>A plugin with no state at all returns an empty array (a legitimate value --
    /// factory default, never tweaked). A plugin that DOES report state but it couldn't be
    /// retrieved (capped by MaxStateBytes, or an inconsistent size on retry) throws instead of
    /// silently persisting an empty blob over real edited state (the old get-size-then-get-data
    /// pair could do exactly that).</summary>
    public byte[] GetState()
    {
        var buf = new byte[OptimisticStateBufferBytes];
        int written = NativeHostBridge.aca_get_state(_handle, buf, buf.Length, out int requiredSize);
        if (requiredSize <= 0)
            return Array.Empty<byte>();
        if (written == requiredSize)
            return buf[..written];

        if (requiredSize > MaxStateBytes)
            throw new InvalidOperationException($"Hosted plugin reported a state size ({requiredSize} bytes) beyond the {MaxStateBytes}-byte cap.");

        buf = new byte[requiredSize];
        written = NativeHostBridge.aca_get_state(_handle, buf, buf.Length, out int requiredSize2);
        if (written != requiredSize || requiredSize2 != requiredSize)
            throw new InvalidOperationException("Hosted plugin state changed between size query and read; failed to capture a consistent state blob.");
        return buf;
    }

    public void SetState(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxStateBytes)
            return;
        NativeHostBridge.aca_set_state(_handle, data, data.Length);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeHostBridge.aca_release_instance(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static string ToString(byte[] nulTerminatedAscii)
    {
        int len = Array.IndexOf(nulTerminatedAscii, (byte)0);
        if (len < 0) len = nulTerminatedAscii.Length;
        return Encoding.ASCII.GetString(nulTerminatedAscii, 0, len);
    }
}
