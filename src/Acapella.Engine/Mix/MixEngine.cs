using Acapella.Engine.Host;
using Acapella.Engine.Pitch;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Acapella.Engine.Mix;

/// <summary>SourceKey, if provided, must uniquely determine Samples' exact content (e.g. source
/// path + mtime + trim + shift) -- it's used to cache automatic pitch correction across rebuilds
/// (audit B3). Null disables that cache (correction always runs fresh).</summary>
public record MixLayerInput(int LayerId, float[] Samples, int SampleRate, LayerMixParameters Parameters, string? SourceKey = null);

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
    private readonly Dictionary<int, IPitchCorrectionBackend> _melodyneBackends = new();

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
        _hostedService.ReleaseAll();
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

    public ISampleProvider BuildLayerChain(MixLayerInput layer, bool anySolo, int outputSampleRate)
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
            PitchBackendSelection.Automatic2B =>
                PitchCorrectionCache.GetOrCorrect(AutomaticBackend, layer.LayerId, layer.SourceKey, layer.Samples, layer.SampleRate),
            // Bug audit A5 ("stale correction cache"): the edit generation is folded into the cache
            // key so closing Melodyne's editor (HostedPluginService.CloseAraEditor bumps it) forces
            // a fresh Correct() call on the next rebuild instead of replaying PitchCorrectionCache's
            // pre-edit output -- the layer's own SourceKey alone never changes just because the
            // user tweaked pitch inside Melodyne's own editor.
            PitchBackendSelection.Manual2A when _hostedService.IsAraAvailable(MelodynePluginLabel) =>
                PitchCorrectionCache.GetOrCorrect(
                    GetOrCreateMelodyneBackend(layer.LayerId), layer.LayerId,
                    layer.SourceKey is null ? null : $"{layer.SourceKey}-araEdit{_hostedService.GetAraEditGeneration(layer.LayerId)}",
                    layer.Samples, layer.SampleRate),
            _ => layer.Samples,
        };

        ISampleProvider chain = new ArraySampleProvider(processedSamples, layer.SampleRate);

        if (layer.SampleRate != outputSampleRate)
            chain = new WdlResamplingSampleProvider(chain, outputSampleRate);

        int totalHostedLatency = 0;

        chain = InsertSlots(FxSlotPosition.PrePan, chain, layer.LayerId, parameters, outputSampleRate, ref totalHostedLatency);

        ISampleProvider afterPan = new PanningSampleProvider(chain) { Pan = parameters.Pan };
        afterPan = InsertSlots(FxSlotPosition.PostPan, afterPan, layer.LayerId, parameters, outputSampleRate, ref totalHostedLatency);

        bool effectiveMute = parameters.Mute || (anySolo && !parameters.Solo);
        float linearGain = effectiveMute ? 0f : DbToLinear(parameters.GainDb);
        ISampleProvider withGain = new VolumeSampleProvider(afterPan) { Volume = linearGain };
        withGain = InsertSlots(FxSlotPosition.PostGain, withGain, layer.LayerId, parameters, outputSampleRate, ref totalHostedLatency);

        ISampleProvider finished = totalHostedLatency > 0 ? new LatencySkipSampleProvider(withGain, totalHostedLatency) : withGain;

        var layerTap = new MeterTapSampleProvider(finished);
        _layerTaps[layer.LayerId] = layerTap;
        return layerTap;
    }

    private ISampleProvider InsertSlots(FxSlotPosition position, ISampleProvider source, int layerId, LayerMixParameters parameters, int sampleRate, ref int totalHostedLatency)
    {
        foreach (var slot in FxSlots.All)
        {
            if (slot.Position == position)
                source = slot.Insert(source, layerId, parameters, _hostedService, sampleRate, ref totalHostedLatency);
        }
        return source;
    }

    private IPitchCorrectionBackend GetOrCreateMelodyneBackend(int layerId)
    {
        if (!_melodyneBackends.TryGetValue(layerId, out var backend))
        {
            backend = new MelodyneAraPitchCorrector(_hostedService, HostedPluginCatalog.KnownPluginPaths[MelodynePluginLabel], layerId);
            _melodyneBackends[layerId] = backend;
        }
        return backend;
    }

    private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
}
