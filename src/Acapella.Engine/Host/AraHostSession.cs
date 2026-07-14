using System.Runtime.InteropServices;
using System.Text;

namespace Acapella.Engine.Host;

/// <summary>
/// v7 2A tasks 38-39: thin managed wrapper around the native ARA hosting bridge (AraBridge.cpp's
/// aca_ara_* functions) -- creates a Document Controller bound to an ARA-capable plugin instance
/// (Melodyne), registers audio sources against it, attaches playback regions so Melodyne analyzes
/// them, and renders the (pitch-corrected) output back.
///
/// Requires HostedPluginInstance.Initialize() to have already run on this thread, same as every
/// other hosted-plugin lifecycle call.
/// </summary>
public sealed class AraHostSession : IDisposable
{
    private IntPtr _handle;
    // Keeps every registered source's pinned buffers alive for the session's lifetime -- the
    // native side only holds raw pointers into them (AraBridge.cpp's AraAudioSourceBuffer doesn't
    // own the memory), so an unpinned/collected array here would leave a dangling read.
    private readonly List<GCHandle> _pinnedBuffers = new();
    private readonly List<IntPtr> _audioSources = new();

    private AraHostSession(IntPtr handle)
    {
        _handle = handle;
    }

    public static AraHostSession Create(string pluginPath, double sampleRate, int maxBlockSize)
    {
        var errorBuf = new byte[512];
        var handle = NativeHostBridge.aca_ara_create_session(pluginPath, sampleRate, maxBlockSize, errorBuf, errorBuf.Length);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create ARA hosting session for '{pluginPath}': {ToString(errorBuf)}");
        return new AraHostSession(handle);
    }

    /// <summary>Registers a mono or multi-channel audio source. channelSamples are planar (one
    /// float[] per channel, each of length numSamples) -- each is pinned for this session's
    /// lifetime so the native side can read directly from managed memory without a copy.
    /// persistentId must be unique within this session's document (task 40 derives it from
    /// (layerId, sourceAudioHash); callers before that exists must supply their own unique id).</summary>
    public IntPtr RegisterAudioSource(float[][] channelSamples, int numSamples, double sourceSampleRate, string persistentId)
    {
        if (channelSamples.Length == 0)
            throw new ArgumentException("At least one channel is required.", nameof(channelSamples));

        var pins = new GCHandle[channelSamples.Length];
        var pointers = new IntPtr[channelSamples.Length];
        for (int ch = 0; ch < channelSamples.Length; ch++)
        {
            pins[ch] = GCHandle.Alloc(channelSamples[ch], GCHandleType.Pinned);
            pointers[ch] = pins[ch].AddrOfPinnedObject();
        }

        var errorBuf = new byte[512];
        var source = NativeHostBridge.aca_ara_register_audio_source(
            _handle, pointers, channelSamples.Length, numSamples, sourceSampleRate, persistentId, errorBuf, errorBuf.Length);

        if (source == IntPtr.Zero)
        {
            foreach (var pin in pins) pin.Free();
            throw new InvalidOperationException($"Failed to register ARA audio source: {ToString(errorBuf)}");
        }

        _pinnedBuffers.AddRange(pins);
        _audioSources.Add(source);
        return source;
    }

    /// <summary>Attaches audioSourceHandle to a playback region spanning its whole length --
    /// required before analysis progress or rendering means anything for that source. Melodyne
    /// begins analyzing once the region is attached and its samples are readable (already enabled
    /// at RegisterAudioSource time).</summary>
    public void AddPlaybackRegion(IntPtr audioSourceHandle)
    {
        var errorBuf = new byte[512];
        if (NativeHostBridge.aca_ara_add_playback_region(_handle, audioSourceHandle, errorBuf, errorBuf.Length) == 0)
            throw new InvalidOperationException($"Failed to add ARA playback region: {ToString(errorBuf)}");
    }

    /// <summary>-1 if analysis hasn't reported any progress yet, else 0..1 (1.0 = complete).</summary>
    public float GetAnalysisProgress(IntPtr audioSourceHandle) =>
        NativeHostBridge.aca_ara_get_analysis_progress(_handle, audioSourceHandle);

    /// <summary>Renders numSamples of stereo output starting at startSampleInRegion (relative to
    /// the start of the source) through Melodyne's playback renderer. AddPlaybackRegion must have
    /// already run for this source.</summary>
    public (float[] Left, float[] Right) RenderBlock(IntPtr audioSourceHandle, long startSampleInRegion, int numSamples)
    {
        var outL = new float[numSamples];
        var outR = new float[numSamples];
        if (NativeHostBridge.aca_ara_render_block(_handle, audioSourceHandle, startSampleInRegion, outL, outR, numSamples) == 0)
            throw new InvalidOperationException("ARA render block failed -- was AddPlaybackRegion called for this source?");
        return (outL, outR);
    }

    public void ReleaseAudioSource(IntPtr audioSourceHandle)
    {
        if (_handle == IntPtr.Zero) return;
        NativeHostBridge.aca_ara_release_audio_source(_handle, audioSourceHandle);
        _audioSources.Remove(audioSourceHandle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        NativeHostBridge.aca_ara_destroy_session(_handle);
        _handle = IntPtr.Zero;
        _audioSources.Clear();

        foreach (var pin in _pinnedBuffers)
            pin.Free();
        _pinnedBuffers.Clear();
    }

    private static string ToString(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, len < 0 ? buffer.Length : len);
    }
}
