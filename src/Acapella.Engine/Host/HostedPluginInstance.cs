using System.Text;

namespace Acapella.Engine.Host;

public sealed record HostedPluginDescription(string Name, string Version);

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

    public void ProcessBlock(float[] inL, float[] inR, float[] outL, float[] outR, int numSamples) =>
        NativeHostBridge.aca_process_block(_handle, inL, inR, outL, outR, numSamples);

    public byte[] GetState()
    {
        int size = NativeHostBridge.aca_get_state_size(_handle);
        if (size <= 0 || size > MaxStateBytes)
            return Array.Empty<byte>();
        var buf = new byte[size];
        int written = NativeHostBridge.aca_get_state(_handle, buf, buf.Length);
        return written == size ? buf : Array.Empty<byte>();
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
