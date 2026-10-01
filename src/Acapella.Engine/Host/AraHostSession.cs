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

    /// <summary>Bug audit A3: ARA analysis-progress notifications are pull-based -- must be pumped
    /// periodically (on this session's hosted thread) for GetAnalysisProgress to ever see anything
    /// other than -1.</summary>
    public void PumpModelUpdates() => NativeHostBridge.aca_ara_pump_model_updates(_handle);

    /// <summary>-1 if analysis hasn't reported any progress yet, else 0..1 (1.0 = complete). Only
    /// updates if PumpModelUpdates is also called.</summary>
    public float GetAnalysisProgress(IntPtr audioSourceHandle) =>
        NativeHostBridge.aca_ara_get_analysis_progress(_handle, audioSourceHandle);

    /// <summary>Bug audit A2: pumps model updates and polls GetAnalysisProgress until it reports
    /// complete (1.0) or timeoutMs elapses. Rendering before this completes returns whatever
    /// Melodyne's playback renderer does pre-analysis (implementation-defined -- unanalyzed source
    /// data or silence for the not-yet-transferred span), which is exactly the nondeterminism the
    /// audit flagged: callers must wait for analysis before treating rendered output as
    /// Melodyne-corrected audio. Must be called on this session's hosted thread (it calls
    /// PumpModelUpdates, which requires that). Returns true if analysis completed, false on
    /// timeout -- callers should treat a timeout as "render anyway, best effort" rather than fail
    /// outright, since ARA gives no hard completion deadline.</summary>
    public bool WaitForAnalysisComplete(IntPtr audioSourceHandle, int timeoutMs = 30_000, int pollIntervalMs = 20)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            PumpModelUpdates();
            float progress = GetAnalysisProgress(audioSourceHandle);
            if (progress >= 1.0f)
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(pollIntervalMs);
        }
    }

    /// <summary>Renders numSamples of stereo output starting at startSampleInRegion (relative to
    /// the start of the source) through Melodyne's playback renderer. AddPlaybackRegion must have
    /// already run for this source; callers should have already awaited WaitForAnalysisComplete
    /// (A2) so the render reflects Melodyne's analyzed model rather than a pre-analysis state.</summary>
    public (float[] Left, float[] Right) RenderBlock(IntPtr audioSourceHandle, long startSampleInRegion, int numSamples)
    {
        var outL = new float[numSamples];
        var outR = new float[numSamples];
        if (NativeHostBridge.aca_ara_render_block(_handle, audioSourceHandle, startSampleInRegion, outL, outR, numSamples) == 0)
            throw new InvalidOperationException("ARA render block failed -- was AddPlaybackRegion called for this source?");
        return (outL, outR);
    }

    /// <summary>Bug audit A1: opens Melodyne's own editor GUI for this session, in its own
    /// top-level window (never embedded). Brings an already-open window to front on a second call.
    /// Must be called on this session's hosted thread. Returns false if the plugin has no editor.</summary>
    public bool ShowEditorWindow(string title) => NativeHostBridge.aca_ara_show_editor_window(_handle, title) != 0;

    /// <summary>Closes the editor window if open. Safe to call when none is open. Must be called on
    /// this session's hosted thread.</summary>
    public void CloseEditorWindow() => NativeHostBridge.aca_ara_close_editor_window(_handle);

    // Mirrors HostedPluginInstance.GetState's cap and optimistic-buffer-then-retry shape (task 40).
    private const int MaxArchiveBytes = 1024 * 1024;
    private const int OptimisticArchiveBufferBytes = 8192;

    /// <summary>Serializes the whole document's analyzed state (every registered audio source's
    /// Melodyne edits) into one blob -- the caller stores this keyed by whatever identifies the
    /// layer/source it belongs to (task 40's (layerId, sourceAudioHash) key lives at the caller's
    /// level, not in this bridge). Empty array if the document has no state yet.</summary>
    public byte[] ExportState()
    {
        var buf = new byte[OptimisticArchiveBufferBytes];
        int written = NativeHostBridge.aca_ara_export_state(_handle, buf, buf.Length, out int requiredSize);
        if (requiredSize <= 0)
            return Array.Empty<byte>();
        if (written == requiredSize)
            return buf[..written];

        if (requiredSize > MaxArchiveBytes)
            throw new InvalidOperationException($"ARA session reported an archive size ({requiredSize} bytes) beyond the {MaxArchiveBytes}-byte cap.");

        buf = new byte[requiredSize];
        written = NativeHostBridge.aca_ara_export_state(_handle, buf, buf.Length, out int requiredSize2);
        if (written != requiredSize || requiredSize2 != requiredSize)
            throw new InvalidOperationException("ARA archive size changed between query and read; failed to capture a consistent state blob.");
        return buf;
    }

    /// <summary>Restores previously-exported state. Every audio source referenced by the archive
    /// must already be registered with the same persistentId it was exported under -- archived
    /// objects are matched to the current graph by persistent ID, not re-created.</summary>
    public void ImportState(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxArchiveBytes)
            return;
        if (NativeHostBridge.aca_ara_import_state(_handle, data, data.Length) == 0)
            throw new InvalidOperationException("Failed to import ARA archive state.");
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
