using System.Linq;
using A_Capella.Engine.Host;
using A_Capella.Engine.Pitch;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace A_Capella.Engine.Mix;

/// <summary>SourceKey, if provided, must uniquely determine Samples' exact content (e.g. source
/// path + mtime + trim + shift) -- it's used to cache automatic pitch correction across rebuilds
/// (audit B3). Null disables that cache (correction always runs fresh).
///
/// RawSamples/TrimStartSamples/TrimLengthSamples/ShiftMs/RawContentKey exist only for the Manual2A
/// (Melodyne ARA) pitch path: its persistent per-layer ARA session must keep registering the same
/// (untrimmed) audio under the same content key across a trim change, or the trim edit tears down
/// and rebuilds the session, discarding whatever Melodyne edits were made against the old one. The
/// automatic backend and the rest of the chain keep using the already-trimmed/shifted Samples.</summary>
public record MixLayerInput(
    int LayerId, float[] Samples, int SampleRate, LayerMixParameters Parameters, string? SourceKey = null,
    float[]? RawSamples = null, int TrimStartSamples = 0, int TrimLengthSamples = 0, double ShiftMs = 0,
    string? RawContentKey = null);

/// <summary>
/// Builds the live mixed preview: for each layer, applies the fixed chain (pitch correction ->
/// FX rack pre-pan slots -> pan -> post-pan slots -> gain/mute/solo -> post-gain slots) then sums
/// via NAudio's MixingSampleProvider. Parameters are read at build time, so changing a parameter
/// and rebuilding is how "live" updates apply (non-destructive: raw samples untouched). Each FX
/// slot (see FxSlots) picks its hosted FabFilter plugin or native fallback itself; hosted
/// instances are cached per (layerId, stage) in the shared HostedPluginService so they survive
/// debounced preview rebuilds.
/// </summary>
public class MixEngine : IDisposable
{
    public const string MelodynePluginLabel = "Melodyne";

    private static readonly IPitchCorrectionBackend AutomaticBackend = new AutoPitchCorrector();

    // Bug audit A1: one MelodyneAraPitchCorrector per layer (each captures its own layerId, needed
    // to key HostedPluginService's persistent per-layer ARA session) rather than one shared
    // instance -- a single shared corrector couldn't route different layers to different sessions.
    private readonly Dictionary<int, MelodyneAraPitchCorrector> _melodyneBackends = new();

    /// <summary>Master brick-wall ceiling (audit B10): applied identically to preview and export
    /// so exports sound like the preview, replacing export's old content-dependent PeakNormalizer.</summary>
    private const float MasterCeilingDb = -0.3f;

    private readonly HostedPluginService _hostedService;

    // Q1 task 3: latest per-layer / master meter taps from the most recent BuildMix* call. Rebuilt
    // (replaced, not mutated) on every rebuild, so a UI poll always reads the tap actually wired
    // into the live chain -- an old tap from a torn-down chain just stops receiving Read() calls
    // and freezes at its last value, which GetLayerLevels/GetMasterLevels callers should treat as
    // stale if PositionMs isn't advancing rather than something this class needs to clear itself.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, MeterTapSampleProvider> _layerTaps = new();
    private volatile MeterTapSampleProvider? _masterTap;

    // Live bypassable slot nodes from the most recent BuildLayerChain call, keyed by layerId then
    // stage name. Swapped in wholesale per layer (same pattern as _layerTaps) so a layer's entry
    // always reflects exactly what that layer's last rebuild actually wired into the chain -- a
    // stage that didn't get built this time (disabled and never used) just isn't in the dictionary,
    // and TrySetSlotEnabled below correctly reports "no live node" for it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyDictionary<string, ISlotBypassNode>> _slotNodes = new();

    // Background pitch rendering: when NonBlockingPitchCorrection is on (PreviewPlaybackEngine
    // only -- see GetPitchCorrectedSamples), a layer whose pitch correction isn't cached yet plays
    // uncorrected while the render runs on a worker thread. This tracks, per layerId, the task
    // behind the layer's *current* request: cleared the moment that request is satisfied from
    // cache (even by a different, already-cached key -- e.g. the user trimmed back to one that was
    // rendered earlier), and overwritten whenever a new key starts its own render. A render's own
    // completion only still owns this entry if nothing has superseded it, which TryRemove's
    // (key, task) overload reports atomically -- that's what decides whether this render is still
    // the one the layer is waiting on (and so worth a refresh) or stale (superseded, ignored).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task<float[]>> _pendingPitchRenders = new();

    // Freeze: a per-layer rendered buffer of its whole chain (minus Mute/Solo), replayed instead of
    // running the chain live while LayerMixParameters.Frozen is set. Static (shared across every
    // MixEngine instance, like PitchCorrectionCache above it) because MainWindow/PreviewPlaybackEngine/
    // ExportEngine each own a separate MixEngine -- a per-instance cache would re-render on every
    // export even though the preview just rendered the identical buffer. Keyed by layerId only (the
    // string key folds in everything that should invalidate it); a stale entry from a previous
    // project's same layerId simply fails the key comparison once that project's different SourceKey
    // is folded in, the same way PitchCorrectionCache never needs an explicit clear on project switch.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Key, float[] Buffer)> FrozenBufferCache = new();

    /// <summary>Only PreviewPlaybackEngine sets this. Export and every other caller leave it false,
    /// so BuildLayerChain's pitch correction stays fully synchronous for them -- a cache miss
    /// renders in place and the chain is built from the finished result, exactly as before this
    /// existed. With it on, a cache miss builds the chain from the layer's uncorrected samples and
    /// kicks off the render in the background instead of stalling the caller.</summary>
    public bool NonBlockingPitchCorrection { get; set; }

    /// <summary>Test seam: substituted for AutomaticBackend (the static, shared AutoPitchCorrector)
    /// when set, so a test can exercise the Automatic2B path against a fake/gated
    /// IPitchCorrectionBackend without waiting on real YIN detection + Rubber Band. Null in
    /// production.</summary>
    internal IPitchCorrectionBackend? AutomaticBackendOverride { get; set; }

    /// <summary>True while any layer has a pitch render in flight (BuildLayerChain hasn't yet seen
    /// its cached result) -- for a status-bar "Processing pitch..." indicator.</summary>
    public bool HasPendingPitchRender => !_pendingPitchRenders.IsEmpty;

    /// <summary>Fires once a background pitch render starts for a layer (NonBlockingPitchCorrection
    /// only). Not fired again for the same in-flight render just because BuildLayerChain is called
    /// again before it finishes.</summary>
    public event Action<int>? PitchRenderStarted;

    /// <summary>Fires once a background pitch render finishes successfully, current or not -- the
    /// bool is true if this is still the render the layer is waiting on (see _pendingPitchRenders
    /// doc above), false if a newer request has since superseded it. Always raised (one per
    /// PitchRenderStarted) so a subscriber using it to count pending renders -- a status-bar
    /// indicator -- stays balanced even when a stale render finishes after the layer's moved on;
    /// only a caller that wants to refresh the built chain needs to check the bool.</summary>
    public event Action<int, bool>? PitchRenderCompleted;

    /// <summary>Fires whenever a background pitch render throws, current or not -- the failure is
    /// still worth surfacing even if the user has since moved on to a different key.</summary>
    public event Action<int, string>? PitchRenderFailed;

    /// <summary>Fires when a frozen layer's chain-build hits a cache miss and starts (blocking)
    /// rendering its frozen buffer -- for a status-bar "Freezing layer N..." indicator, same
    /// started/completed shape as the pitch-render events above. Unlike pitch rendering this never
    /// runs in the background: BuildLayerChain blocks the calling thread (the preview's command
    /// thread, or export's own thread) until the render finishes, since both callers already rebuild
    /// their whole chain off the UI thread.</summary>
    public event Action<int>? FreezeRenderStarted;
    public event Action<int>? FreezeRenderCompleted;

    // True only when this MixEngine created its own private HostedPluginService (the
    // availability-only constructor, kept for callers/tests that don't need to share one service
    // across the whole app session) -- only then does Dispose() also dispose the service. A
    // MixEngine built from a shared HostedPluginService (v7 Q0 task 1, audit A1: MainWindow,
    // PreviewPlaybackEngine, and ExportEngine all share one) never owns or disposes it.
    private readonly bool _ownsHostedService;

    public MixEngine(IHostedPluginAvailability? availability = null) : this(new HostedPluginService(availability))
    {
        _ownsHostedService = true;
    }

    public MixEngine(HostedPluginService hostedService)
    {
        _hostedService = hostedService;
    }

    public void Dispose()
    {
        if (_ownsHostedService) _hostedService.Dispose();
    }

    public bool IsHosted(string pluginLabel) => _hostedService.IsAvailable(pluginLabel);

    /// <summary>Peak/RMS in dBFS for the given layer's post-slot-rack tap from the most recent
    /// chain rebuild (Q1 task 3). NegativeInfinity for both if that layer hasn't been built yet
    /// (e.g. before the first Play).</summary>
    public (float PeakDb, float RmsDb) GetLayerLevels(int layerId) =>
        _layerTaps.TryGetValue(layerId, out var tap) ? (tap.PeakDb, tap.RmsDb) : (float.NegativeInfinity, float.NegativeInfinity);

    /// <summary>Peak/RMS in dBFS for the master bus tap from the most recent BuildMix* call.</summary>
    public (float PeakDb, float RmsDb) GetMasterLevels() =>
        _masterTap is { } tap ? (tap.PeakDb, tap.RmsDb) : (float.NegativeInfinity, float.NegativeInfinity);

    /// <summary>Q2 task 3: how far this layer's chain rings on past its source material, in seconds
    /// (the enabled tail-extending slots, i.e. a hosted reverb). BuildLayerChain plays that tail,
    /// but durations are computed before the graph is built, so callers ask here -- otherwise the
    /// tail is truncated in both preview and export.</summary>
    public double GetTailSeconds(int layerId, LayerMixParameters parameters, int sampleRate = 44100) =>
        FxSlots.All.Sum(slot => slot.TailSeconds(layerId, parameters, _hostedService, sampleRate));

    /// <summary>Fetches (lazily creating) the same live hosted instance BuildLayerChain uses for
    /// (layerId, stage), for the UI's "Open Pro-X..." launcher buttons to call ShowEditorWindow on
    /// -- both go through the one shared HostedPluginService, so this is never an orphaned second
    /// instance (audit A1). Safe to call from any thread: the service marshals the actual creation
    /// onto the dispatcher thread.</summary>
    public IHostedPlugin GetOrCreateHostedInstance(int layerId, string stage, string pluginLabel, byte[]? initialState, int sampleRate = 44100) =>
        _hostedService.GetOrCreateInstance(layerId, stage, pluginLabel, initialState, sampleRate);

    /// <summary>Pulls each stage's live VST3 state into parameters' matching *HostedState field, for
    /// every stage that already has a live instance (v7 Q0 task 3, audit A2) -- call before a
    /// project snapshot (save/export/undo) is taken so the file reflects the user's actual editor
    /// tweaks, not just the state as of whenever the editor was first opened.</summary>
    public void SyncLiveStateIntoParameters(int layerId, LayerMixParameters parameters)
    {
        foreach (var slot in FxSlots.All)
        {
            var instance = _hostedService.TryGetLiveInstance(layerId, slot.Stage);
            if (instance is not null)
                slot.SetHostedState(parameters, _hostedService.PullLiveState(instance));
        }
    }

    /// <summary>Pushes parameters' saved *HostedState blobs into any already-live instances for
    /// this layerId (v7 Q0 task 3, audit B7) -- call after restoring a project (load, undo/redo),
    /// since GetOrCreateInstance's initialState only ever applies at first creation and a live
    /// instance surviving across the restore would otherwise keep its old (pre-restore) state.</summary>
    public void PushSavedStateIntoLiveInstances(int layerId, LayerMixParameters parameters)
    {
        foreach (var slot in FxSlots.All)
        {
            var instance = _hostedService.TryGetLiveInstance(layerId, slot.Stage);
            var state = slot.GetHostedState(parameters);
            if (instance is not null && state is { Length: > 0 })
                _hostedService.PushState(instance, state);
        }
    }

    /// <summary>Releases every live hosted instance and ARA session -- for a whole-project
    /// replacement (see ProjectSession.New/Open). Layer ids are positional and restart at 0 per
    /// project, so without this the next project's layer 0 would silently inherit the previous
    /// project's live plugin instances and their state (bug audit #5). Also clears the per-layer
    /// Melodyne backend cache and meter taps, which are keyed the same way and would otherwise
    /// point at a destroyed session / a torn-down chain.</summary>
    public void ReleaseAllHostedInstances()
    {
        _melodyneBackends.Clear();
        _layerTaps.Clear();
        _slotNodes.Clear();
        // Doesn't cancel a render already running on a worker thread -- nothing here can -- but
        // stops this instance from treating it as something a future layer should still wait on.
        _pendingPitchRenders.Clear();
        _hostedService.ReleaseAll();
    }

    /// <summary>Flips a slot's live bypass flag on the running chain instead of rebuilding it --
    /// lets a power-toggle click apply instantly during playback. Returns false when the caller
    /// must fall back to a full preview refresh instead: the slot extends the layer's tail
    /// (reverb -- toggling it changes DurationMs, which a bypass flip can't), or there's no live
    /// node for (layerId, stage) in the most recent chain build (the slot was never enabled yet, so
    /// nothing is there to flip).</summary>
    public bool TrySetSlotEnabled(int layerId, string stage, bool enabled)
    {
        var slot = FxSlots.All.FirstOrDefault(s => s.Stage == stage);
        if (slot is null || slot.ExtendsTail) return false;
        if (!_slotNodes.TryGetValue(layerId, out var stages)) return false;
        if (!stages.TryGetValue(stage, out var node)) return false;

        node.Bypassed = !enabled;
        return true;
    }

    public ISampleProvider BuildMix(IReadOnlyList<MixLayerInput> layers, int outputSampleRate = 44100, float masterVolumeDb = 0f) =>
        BuildMixWithMasterVolumeHandle(layers, outputSampleRate, masterVolumeDb).Mix;

    /// <summary>Same chain as BuildMix, but also returns the master-volume-only VolumeSampleProvider
    /// node so a live caller (PreviewPlaybackEngine) can update its Volume mid-playback instead of
    /// only picking up a new masterVolumeDb on the next full rebuild -- BuildMix alone bakes
    /// masterVolumeDb into the graph once at build time, which is why a real-time master-volume
    /// slider previously had no audible effect until the next Play/Seek.</summary>
    public (ISampleProvider Mix, VolumeSampleProvider MasterVolumeStage) BuildMixWithMasterVolumeHandle(IReadOnlyList<MixLayerInput> layers, int outputSampleRate = 44100, float masterVolumeDb = 0f)
    {
        bool anySolo = layers.Any(l => l.Parameters.Solo);
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(outputSampleRate, 2));

        foreach (var layer in layers)
        {
            mixer.AddMixerInput(BuildLayerChain(layer, anySolo, outputSampleRate));
        }

        // MixingSampleProvider just sums its inputs; 2+ vocal layers near full scale would
        // exceed +-1.0 and hard-clip on the AAC/WAV encode. A fixed 1/sqrt(N) headroom scale is
        // enough to keep the common case under 0dBFS without needing a full limiter for v1. Kept
        // as its own fixed-gain stage (not merged into the master-volume node) so the live handle
        // below only ever carries the user's master-volume gain, not this structural constant.
        float headroomGain = layers.Count > 0 ? (float)(1.0 / Math.Sqrt(layers.Count)) : 1f;
        var headroomStage = new VolumeSampleProvider(mixer) { Volume = headroomGain };
        var masterVolumeStage = new VolumeSampleProvider(headroomStage) { Volume = DbToLinear(masterVolumeDb) };

        // Bus limiter sits after master volume so preview and export share one ceiling regardless
        // of how loud the mix or the master fader is pushed.
        ISampleProvider mix = new LimiterSampleProvider(masterVolumeStage, MasterCeilingDb, makeupGainDb: 0f);

        var masterTap = new MeterTapSampleProvider(mix);
        _masterTap = masterTap;
        return (masterTap, masterVolumeStage);
    }

    /// <summary>Frozen layers (LayerMixParameters.Frozen) skip the live chain entirely and instead
    /// play a cached pre-rendered buffer of it, re-rendered only when the baked parameters actually
    /// change -- see BuildFrozenLayerChain. Mute/Solo are never baked: they're re-applied live on top
    /// of the frozen buffer every time, same as a normal layer.</summary>
    public ISampleProvider BuildLayerChain(MixLayerInput layer, bool anySolo, int outputSampleRate) =>
        layer.Parameters.Frozen
            ? BuildFrozenLayerChain(layer, anySolo, outputSampleRate)
            : BuildLiveLayerChain(layer, anySolo, outputSampleRate);

    private ISampleProvider BuildFrozenLayerChain(MixLayerInput layer, bool anySolo, int outputSampleRate)
    {
        _hostedService.EnsureScanned();
        var buffer = GetOrRenderFrozenBuffer(layer, outputSampleRate);

        ISampleProvider source = new StereoArraySampleProvider(buffer, outputSampleRate);
        bool effectiveMute = layer.Parameters.Mute || (anySolo && !layer.Parameters.Solo);
        ISampleProvider withGain = new VolumeSampleProvider(source) { Volume = effectiveMute ? 0f : 1f };

        var layerTap = new MeterTapSampleProvider(withGain);
        _layerTaps[layer.LayerId] = layerTap;
        // A frozen layer has no live bypassable nodes (its FX rack isn't built at all) -- drop any
        // stale entry from before it was frozen so TrySetSlotEnabled correctly reports "nothing to
        // flip" rather than toggling a node that isn't actually wired into this chain anymore.
        _slotNodes.TryRemove(layer.LayerId, out _);
        return layerTap;
    }

    /// <summary>Renders (or reuses the cached) frozen buffer for this layer: the whole live chain
    /// built with Mute/Solo forced off, in blocking pitch mode, read to the end (including the
    /// reverb tail and latency skip) into an interleaved stereo float[] at outputSampleRate.
    /// FreezeRenderStarted/Completed fire only around an actual render (a cache miss), mirroring the
    /// pitch-render status pair, for a "Freezing layer N..." status-bar indicator.</summary>
    private float[] GetOrRenderFrozenBuffer(MixLayerInput layer, int outputSampleRate)
    {
        string? key = BuildFrozenCacheKey(layer, outputSampleRate);
        if (key is not null && FrozenBufferCache.TryGetValue(layer.LayerId, out var cached) && cached.Key == key)
            return cached.Buffer;

        FreezeRenderStarted?.Invoke(layer.LayerId);
        try
        {
            var buffer = RenderFrozenBuffer(layer, outputSampleRate);
            // Null key (no SourceKey) means "render every time" -- never cache it, so the next call
            // always misses and re-renders, same as PitchCorrectionCache's null-sourceKey behavior.
            if (key is not null) FrozenBufferCache[layer.LayerId] = (key, buffer);
            else FrozenBufferCache.TryRemove(layer.LayerId, out _);
            return buffer;
        }
        finally
        {
            FreezeRenderCompleted?.Invoke(layer.LayerId);
        }
    }

    /// <summary>Cache key for the frozen buffer: the layer's SourceKey, outputSampleRate, the ARA
    /// edit generation, and every LayerMixParameters property except Mute/Solo/Frozen (reflected over
    /// rather than listed by hand, so a future parameter can't be silently left out of the key -- see
    /// the design note on what gets baked). byte[] hosted-state blobs are hashed rather than embedded
    /// raw so the key string doesn't itself carry megabytes of VST3 state. Null SourceKey disables
    /// caching entirely, same meaning as everywhere else SourceKey is used.</summary>
    private string? BuildFrozenCacheKey(MixLayerInput layer, int outputSampleRate)
    {
        if (layer.SourceKey is null) return null;

        var sb = new System.Text.StringBuilder();
        sb.Append(layer.SourceKey).Append('|').Append(outputSampleRate).Append('|')
          .Append(_hostedService.GetAraEditGeneration(layer.LayerId)).Append('|');

        foreach (var prop in typeof(LayerMixParameters).GetProperties().OrderBy(p => p.Name))
        {
            if (prop.Name is nameof(LayerMixParameters.Mute) or nameof(LayerMixParameters.Solo) or nameof(LayerMixParameters.Frozen))
                continue;

            var value = prop.GetValue(layer.Parameters);
            string piece = value switch
            {
                byte[] bytes when bytes.Length > 0 => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                byte[] => "",
                null => "null",
                _ => value.ToString() ?? "",
            };
            sb.Append(prop.Name).Append('=').Append(piece).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>Builds a Mute/Solo/Frozen-off variant of the layer and runs it through the normal
    /// live chain (BuildLiveLayerChain, forFreezeRender: true -- blocking pitch mode, no tap/slot-node
    /// registration since this chain is never played live), reading it to completion. Bounded by
    /// maxFrames (source length + this layer's own tail, generously padded) so a chain that somehow
    /// never returns 0 -- a bug, not an expected case -- fails fast instead of hanging the calling
    /// thread (the preview's command thread, or export's thread) forever.</summary>
    private float[] RenderFrozenBuffer(MixLayerInput layer, int outputSampleRate)
    {
        var renderParams = layer.Parameters.Clone();
        renderParams.Mute = false;
        renderParams.Solo = false;
        renderParams.Frozen = false;
        var renderInput = layer with { Parameters = renderParams };

        var chain = BuildLiveLayerChain(renderInput, anySolo: false, outputSampleRate, forFreezeRender: true);

        int sourceFrames = (int)Math.Ceiling((double)layer.Samples.Length / layer.SampleRate * outputSampleRate);
        double tailSeconds = GetTailSeconds(layer.LayerId, layer.Parameters, outputSampleRate);
        int tailFrames = (int)Math.Ceiling(tailSeconds * outputSampleRate);
        int marginFrames = outputSampleRate * 5; // headroom for hosted latency + resampler slop
        int maxFrames = sourceFrames + tailFrames + marginFrames;

        return ReadToEnd(chain, maxFrames);
    }

    private static float[] ReadToEnd(ISampleProvider provider, int maxFrames)
    {
        int maxFloats = checked(maxFrames * 2);
        float[] buffer = new float[Math.Min(maxFloats, 1 << 20)];
        int length = 0;
        var scratch = new float[16384];
        int n;
        while ((n = provider.Read(scratch, 0, scratch.Length)) > 0)
        {
            if (length + n > maxFloats)
                throw new InvalidOperationException("Freeze render exceeded its expected length -- the layer's chain may not be terminating.");
            if (length + n > buffer.Length)
                Array.Resize(ref buffer, Math.Min(maxFloats, Math.Max(buffer.Length * 2, length + n)));
            Array.Copy(scratch, 0, buffer, length, n);
            length += n;
        }
        Array.Resize(ref buffer, length);
        return buffer;
    }

    /// <summary>The normal (unfrozen) chain: pitch correction -> FX rack pre-pan slots -> pan ->
    /// post-pan slots -> gain/mute/solo -> post-gain slots -> latency skip -> meter tap.
    /// forFreezeRender, when true (RenderFrozenBuffer only), forces pitch correction to run in
    /// blocking mode regardless of NonBlockingPitchCorrection (a background-rendered placeholder must
    /// never get baked into a frozen buffer) and skips registering this chain as the layer's live
    /// meter tap/slot nodes, since it's never actually played -- only read to produce the frozen
    /// buffer.</summary>
    private ISampleProvider BuildLiveLayerChain(MixLayerInput layer, bool anySolo, int outputSampleRate, bool forFreezeRender = false)
    {
        // v7 Q0 (audit A3): MainWindow fires EnsureScanned() via Task.Run at startup, but that's
        // fire-and-forget -- if a chain build (on the command thread or export's Task.Run thread)
        // reaches IsAvailable() before that pre-warm finishes, HostedPluginAvailability's lazy scan
        // would run its first native aca_scan_plugin call on THIS (non-UI) thread instead, which is
        // exactly the off-thread-scanning hazard A3 calls out. Calling EnsureScanned() here blocks
        // until the marshaled scan completes on the dispatcher thread; every call after the first
        // short-circuits on _scanned before touching the dispatcher, so this is free afterward.
        _hostedService.EnsureScanned();

        var parameters = layer.Parameters;

        // v7 2A task 39/41: Manual2A only actually routes through Melodyne's ARA pipeline if it's
        // both installed and ARA-capable (IsAraAvailable) -- a plain-VST3-tier or missing Melodyne
        // falls back to unprocessed passthrough (task 41's fallback-without-crash) rather than
        // throwing mid-chain-build, the same "native math is the automatic fallback" shape every
        // other hosted stage already uses when its matching plugin isn't detected.
        float[] processedSamples = parameters.PitchBackend switch
        {
            PitchBackendSelection.Automatic2B => BuildAutomaticCorrectedSamples(layer, forFreezeRender),
            // Bug audit A5 ("stale correction cache"): the edit generation is folded into the cache
            // key so closing Melodyne's editor (HostedPluginService.CloseAraEditor bumps it) forces
            // a fresh Correct() call on the next rebuild instead of replaying PitchCorrectionCache's
            // pre-edit output -- the layer's own SourceKey alone never changes just because the
            // user tweaked pitch inside Melodyne's own editor.
            PitchBackendSelection.Manual2A when _hostedService.IsAraAvailable(MelodynePluginLabel) =>
                BuildMelodyneCorrectedSamples(layer, forFreezeRender),
            _ => layer.Samples,
        };

        ISampleProvider chain = new ArraySampleProvider(processedSamples, layer.SampleRate);

        if (layer.SampleRate != outputSampleRate)
            chain = new WdlResamplingSampleProvider(chain, outputSampleRate);

        int totalHostedLatency = 0;
        var slotNodes = new Dictionary<string, ISlotBypassNode>();

        chain = InsertSlots(FxSlotPosition.PrePan, chain, layer.LayerId, parameters, outputSampleRate, ref totalHostedLatency, slotNodes);

        ISampleProvider afterPan = new PanningSampleProvider(chain) { Pan = parameters.Pan };
        afterPan = InsertSlots(FxSlotPosition.PostPan, afterPan, layer.LayerId, parameters, outputSampleRate, ref totalHostedLatency, slotNodes);

        bool effectiveMute = parameters.Mute || (anySolo && !parameters.Solo);
        float linearGain = effectiveMute ? 0f : DbToLinear(parameters.GainDb);
        ISampleProvider withGain = new VolumeSampleProvider(afterPan) { Volume = linearGain };
        withGain = InsertSlots(FxSlotPosition.PostGain, withGain, layer.LayerId, parameters, outputSampleRate, ref totalHostedLatency, slotNodes);

        ISampleProvider finished = totalHostedLatency > 0 ? new LatencySkipSampleProvider(withGain, totalHostedLatency) : withGain;

        // Re-applies each node's bypass flag from parameters one last time, right before this
        // chain goes live: a slot power toggle landing on TrySetSlotEnabled between this method
        // reading `parameters` above and this point would otherwise flip the node by reference --
        // safe once it's actually wired into the chain, but if that race lands mid-build, the node
        // it flipped could still get overwritten by the freshly-built one below with its original,
        // now-stale enabled state. Re-asserting here closes that window.
        foreach (var slot in FxSlots.All)
            if (slotNodes.TryGetValue(slot.Stage, out var node)) node.Bypassed = !slot.IsEnabled(parameters);

        if (forFreezeRender) return finished;

        var layerTap = new MeterTapSampleProvider(finished);
        _layerTaps[layer.LayerId] = layerTap;
        _slotNodes[layer.LayerId] = slotNodes;
        return layerTap;
    }

    private ISampleProvider InsertSlots(FxSlotPosition position, ISampleProvider source, int layerId, LayerMixParameters parameters, int sampleRate, ref int totalHostedLatency, Dictionary<string, ISlotBypassNode> slotNodes)
    {
        foreach (var slot in FxSlots.All)
        {
            if (slot.Position != position) continue;
            var result = slot.Insert(source, layerId, parameters, _hostedService, sampleRate, ref totalHostedLatency);
            source = result.Provider;
            if (result.Node is not null) slotNodes[slot.Stage] = result.Node;
        }
        return source;
    }

    private MelodyneAraPitchCorrector GetOrCreateMelodyneBackend(int layerId)
    {
        if (!_melodyneBackends.TryGetValue(layerId, out var backend))
        {
            backend = new MelodyneAraPitchCorrector(_hostedService, HostedPluginCatalog.KnownPluginPaths[MelodynePluginLabel], layerId);
            _melodyneBackends[layerId] = backend;
        }
        return backend;
    }

    private float[] BuildAutomaticCorrectedSamples(MixLayerInput layer, bool forceBlocking = false)
    {
        var backend = AutomaticBackendOverride ?? AutomaticBackend;
        return GetPitchCorrectedSamples(layer.LayerId, layer.SourceKey, backend.GetType().Name, layer.Samples,
            () => backend.Correct(layer.Samples, layer.SampleRate), forceBlocking);
    }

    /// <summary>Trim/Melodyne-edit-survival fix: renders through the layer's persistent ARA session
    /// using the untrimmed RawSamples/RawContentKey (so a trim change alone never changes what's
    /// registered with Melodyne) and only slices out the trimmed span, shifting it afterward -- see
    /// MelodyneAraPitchCorrector.CorrectTrimmedSpan.
    ///
    /// Resolves the layer's MelodyneAraPitchCorrector up front, on whichever thread calls
    /// BuildLayerChain, rather than inside the correction closure below: with
    /// NonBlockingPitchCorrection on, that closure runs on a worker thread, and _melodyneBackends is
    /// a plain Dictionary -- looking it up (and possibly inserting into it) from both that worker
    /// thread and a later BuildLayerChain call on the calling thread at the same time would race.</summary>
    private float[] BuildMelodyneCorrectedSamples(MixLayerInput layer, bool forceBlocking = false)
    {
        var melodyneBackend = GetOrCreateMelodyneBackend(layer.LayerId);
        string? sourceKey = layer.SourceKey is null
            ? null
            : $"{layer.SourceKey}-araEdit{_hostedService.GetAraEditGeneration(layer.LayerId)}";

        return GetPitchCorrectedSamples(layer.LayerId, sourceKey, nameof(MelodyneAraPitchCorrector), layer.Samples, () =>
        {
            // Callers that don't populate RawSamples (tests, and anyone building a MixLayerInput
            // directly rather than via LayerTimeline.AudioInput) have no separate trim window --
            // treat the whole given buffer as the span.
            bool hasRaw = layer.RawSamples is not null;
            var rawSamples = layer.RawSamples ?? layer.Samples;
            int trimStart = hasRaw ? layer.TrimStartSamples : 0;
            int trimLength = hasRaw ? layer.TrimLengthSamples : layer.Samples.Length;
            var corrected = melodyneBackend.CorrectTrimmedSpan(
                rawSamples, layer.SampleRate, trimStart, trimLength,
                layer.RawContentKey ?? layer.SourceKey ?? string.Empty);
            return AudioShiftHelper.ApplyShift(corrected, layer.ShiftMs, layer.SampleRate);
        }, forceBlocking);
    }

    /// <summary>Blocking by default (NonBlockingPitchCorrection off -- export, tests, and anyone
    /// else): a cache miss renders correct() in place and returns the finished result, same as
    /// before this seam existed.
    ///
    /// With NonBlockingPitchCorrection on (PreviewPlaybackEngine only): a cache hit returns
    /// immediately. A miss starts (or joins) the render in the background via
    /// PitchCorrectionCache.GetOrStartCorrect and returns fallbackSamples -- the layer's
    /// uncorrected audio -- so BuildLayerChain never blocks on it. PitchRenderStarted/Completed/
    /// Failed track that render for a status-bar indicator and so the caller (PreviewPlaybackEngine)
    /// knows when to rebuild the chain again and pick up the finished result.</summary>
    private float[] GetPitchCorrectedSamples(int layerId, string? sourceKey, string backendType, float[] fallbackSamples, Func<float[]> correct, bool forceBlocking = false)
    {
        if (forceBlocking || !NonBlockingPitchCorrection || sourceKey is null)
            return PitchCorrectionCache.GetOrCorrect(layerId, sourceKey, backendType, correct);

        if (PitchCorrectionCache.TryGetCached(layerId, sourceKey, backendType, out var cached))
        {
            // This layer's current request is already resolved -- drop any render still running
            // for a key it's since moved away from (e.g. trimmed back to one rendered earlier);
            // it's no longer what this layer is waiting on.
            _pendingPitchRenders.TryRemove(layerId, out _);
            return cached;
        }

        var task = PitchCorrectionCache.GetOrStartCorrect(layerId, sourceKey, backendType, correct);
        bool isNewRender = !_pendingPitchRenders.TryGetValue(layerId, out var tracked) || tracked != task;
        _pendingPitchRenders[layerId] = task;

        if (isNewRender)
        {
            PitchRenderStarted?.Invoke(layerId);
            task.ContinueWith(t =>
            {
                // True only if nothing has superseded this render as the layer's current one --
                // see _pendingPitchRenders' doc. Always raised regardless (every PitchRenderStarted
                // gets exactly one matching PitchRenderCompleted/Failed, even when stale) so a
                // subscriber counting pending renders for a status indicator stays balanced; only a
                // refresh needs to check the flag -- a superseded render's own completion changed
                // nothing the layer is currently showing.
                bool stillCurrent = _pendingPitchRenders.TryRemove(new KeyValuePair<int, Task<float[]>>(layerId, task));
                if (t.IsCompletedSuccessfully)
                {
                    PitchRenderCompleted?.Invoke(layerId, stillCurrent);
                }
                else if (t.IsFaulted)
                {
                    var ex = t.Exception!.InnerException ?? t.Exception;
                    PitchRenderFailed?.Invoke(layerId, ex.Message);
                }
            }, TaskScheduler.Default);
        }

        return fallbackSamples;
    }

    private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
}
