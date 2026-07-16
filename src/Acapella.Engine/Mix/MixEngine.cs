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
/// noise gate -> compressor -> EQ -> pan -> reverb -> gain/mute/solo -> limiter) then sums via
/// NAudio's MixingSampleProvider. Parameters are read at build time, so changing a parameter and
/// rebuilding is how "live" updates apply (non-destructive: raw samples untouched).
///
/// v6 P3: gate/compressor/EQ/limiter each auto-select the matching hosted FabFilter plugin when
/// IHostedPluginAvailability reports it installed, falling back to the native math above
/// otherwise (native is the automatic fallback, not a user-facing choice -- see the build plan's
/// P3 task 1). Reverb is hosted-only (Pro-R 2), off by default, added if a stage isn't detected.
/// Instances are cached per (layerId, stage) so they survive debounced preview rebuilds -- Dispose
/// this MixEngine to release them (e.g. on project close).
/// </summary>
public class MixEngine : IDisposable
{
    // Stage names double as HostedPluginInstanceCache keys (alongside layerId) -- the UI's "Open
    // Pro-Q 4..." launcher buttons must fetch the exact same cache entry BuildLayerChain uses, so
    // it edits the live instance actually processing audio rather than an orphaned second one.
    // Keep these public constants as the single source of truth for both sides.
    public const string EqStage = "Eq";
    public const string NoiseGateStage = "NoiseGate";
    public const string CompressorStage = "Compressor";
    public const string LimiterStage = "Limiter";
    public const string ReverbStage = "Reverb";

    public const string EqPluginLabel = "FabFilter Pro-Q 4";
    public const string NoiseGatePluginLabel = "FabFilter Pro-G";
    public const string CompressorPluginLabel = "FabFilter Pro-C 3";
    public const string LimiterPluginLabel = "FabFilter Pro-L 2";
    public const string ReverbPluginLabel = "FabFilter Pro-R 2";

    public const string MelodynePluginLabel = "Melodyne";

    private static readonly IPitchCorrectionBackend AutomaticBackend = new AutoPitchCorrector();

    // Bug audit A1: one MelodyneAraPitchCorrector per layer (each captures its own layerId, needed
    // to key HostedPluginService's persistent per-layer ARA session) rather than one shared
    // instance -- a single shared corrector couldn't route different layers to different sessions.
    private readonly Dictionary<int, IPitchCorrectionBackend> _melodyneBackends = new();

    /// <summary>Master brick-wall ceiling (audit B10): applied identically to preview and export
    /// so exports sound like the preview, replacing export's old content-dependent PeakNormalizer.</summary>
    private const float MasterCeilingDb = -0.3f;

    /// <summary>getTailLengthSeconds() can report `inf` for some FabFilter plugins (confirmed for
    /// Pro-R 2 and Pro-Q 4 in P3a probe 2) -- clamp before using it in any duration/loop-bound math.
    /// FabFilter Pro-R 2's own decay-time control tops out well under this, so the clamp only ever
    /// bites on a bogus/inf report, not a real long reverb setting.</summary>
    private const double MaxReverbTailSeconds = 12.0;

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

    /// <summary>Q2 task 3: how much a layer's own reverb tail extends past its source material,
    /// in seconds, clamped the same way BuildLayerChain clamps it (audit's inf-tail guard). 0 if
    /// reverb isn't enabled or Pro-R 2 isn't hosted. Callers use this to extend a layer's computed
    /// duration by exactly the tail this layer's own chain will actually produce -- BuildLayerChain
    /// itself only ever *plays* the tail (HostedPluginSampleProvider keeps returning frames for
    /// tailFrames after the source runs out); nothing previously told PreviewPlaybackEngine's or
    /// ExportEngine's own duration/sample-count calculations that extra audio existed, so a reverb
    /// tail was silently truncated by both (never heard in preview past the nominal end, never
    /// written to an export). Exposed as its own method (not folded into BuildLayerChain) since
    /// duration needs to be known before the mix graph is built.</summary>
    public double GetReverbTailSeconds(int layerId, LayerMixParameters parameters, int sampleRate = 44100)
    {
        if (!parameters.ReverbEnabled || !_hostedService.IsAvailable(ReverbPluginLabel))
            return 0.0;

        var instance = GetOrCreateHostedInstance(layerId, ReverbStage, ReverbPluginLabel, parameters.ReverbHostedState, sampleRate);
        return Math.Clamp(instance.TailSeconds, 0.0, MaxReverbTailSeconds);
    }

    /// <summary>Fetches (lazily creating) the same live hosted instance BuildLayerChain uses for
    /// (layerId, stage), for the UI's "Open Pro-X..." launcher buttons to call ShowEditorWindow on
    /// -- both go through the one shared HostedPluginService, so this is never an orphaned second
    /// instance (audit A1). Safe to call from any thread: the service marshals the actual creation
    /// onto the dispatcher thread.</summary>
    public HostedPluginInstance GetOrCreateHostedInstance(int layerId, string stage, string pluginLabel, byte[]? initialState, int sampleRate = 44100) =>
        _hostedService.GetOrCreateInstance(layerId, stage, pluginLabel, initialState, sampleRate);

    /// <summary>Pulls each stage's live VST3 state into parameters' matching *HostedState field, for
    /// every stage that already has a live instance (v7 Q0 task 3, audit A2) -- call before a
    /// project snapshot (save/export/undo) is taken so the file reflects the user's actual editor
    /// tweaks, not just the state as of whenever the editor was first opened.</summary>
    public void SyncLiveStateIntoParameters(int layerId, LayerMixParameters parameters)
    {
        foreach (var stage in HostedStageStateBindings.PluginLabelByStage.Keys)
        {
            var instance = _hostedService.TryGetLiveInstance(layerId, stage);
            if (instance is not null)
                HostedStageStateBindings.Set(parameters, stage, _hostedService.PullLiveState(instance));
        }
    }

    /// <summary>Pushes parameters' saved *HostedState blobs into any already-live instances for
    /// this layerId (v7 Q0 task 3, audit B7) -- call after restoring a project (load, undo/redo),
    /// since GetOrCreateInstance's initialState only ever applies at first creation and a live
    /// instance surviving across the restore would otherwise keep its old (pre-restore) state.</summary>
    public void PushSavedStateIntoLiveInstances(int layerId, LayerMixParameters parameters)
    {
        foreach (var stage in HostedStageStateBindings.PluginLabelByStage.Keys)
        {
            var instance = _hostedService.TryGetLiveInstance(layerId, stage);
            var state = HostedStageStateBindings.Get(parameters, stage);
            if (instance is not null && state is { Length: > 0 })
                _hostedService.PushState(instance, state);
        }
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

        // v7 Q0 task 4 (audit A4): all five stages are FL-style insert slots, off by default --
        // gate and EQ now gate on their own Enabled flag just like compressor/limiter already did,
        // instead of always running (at the hosted plugin's untouched factory-default state,
        // audibly gating/EQing every layer even when the user never opened the panel).
        if (parameters.NoiseGateEnabled)
        {
            chain = ApplyStage(chain, layer.LayerId, NoiseGateStage, NoiseGatePluginLabel, parameters.NoiseGateHostedState,
                outputSampleRate, ref totalHostedLatency,
                s => new NoiseGateSampleProvider(s, parameters.NoiseGateThresholdDb, parameters.NoiseGateReleaseMs));
        }

        if (parameters.CompressorEnabled)
        {
            chain = ApplyStage(chain, layer.LayerId, CompressorStage, CompressorPluginLabel, parameters.CompressorHostedState,
                outputSampleRate, ref totalHostedLatency,
                s => new CompressorSampleProvider(s, parameters.CompressorThresholdDb, parameters.CompressorRatio));
        }

        if (parameters.EqEnabled)
        {
            chain = ApplyStage(chain, layer.LayerId, EqStage, EqPluginLabel, parameters.EqHostedState,
                outputSampleRate, ref totalHostedLatency,
                s => new ThreeBandEqSampleProvider(s, parameters.LowShelfGainDb, parameters.MidBellGainDb, parameters.HighShelfGainDb));
        }

        ISampleProvider afterPan = new PanningSampleProvider(chain) { Pan = parameters.Pan };

        if (parameters.ReverbEnabled && _hostedService.IsAvailable(ReverbPluginLabel))
        {
            var reverbInstance = GetOrCreateHostedInstance(layer.LayerId, ReverbStage, ReverbPluginLabel, parameters.ReverbHostedState, outputSampleRate);
            _hostedService.Reset(reverbInstance); // v7 Q0 task 5 (audit A5): clear last play's tail before reuse

            double tailSeconds = Math.Clamp(reverbInstance.TailSeconds, 0.0, MaxReverbTailSeconds);
            int tailFrames = (int)(tailSeconds * outputSampleRate);

            var hostedReverb = new HostedPluginSampleProvider(afterPan, reverbInstance, HostedPluginSampleProvider.DefaultBlockSize, tailFrames);
            totalHostedLatency += hostedReverb.LatencySamples;
            afterPan = hostedReverb;
        }

        bool effectiveMute = parameters.Mute || (anySolo && !parameters.Solo);
        float linearGain = effectiveMute ? 0f : DbToLinear(parameters.GainDb);
        ISampleProvider withGain = new VolumeSampleProvider(afterPan) { Volume = linearGain };

        if (parameters.LimiterEnabled)
        {
            withGain = ApplyStage(withGain, layer.LayerId, LimiterStage, LimiterPluginLabel, parameters.LimiterHostedState,
                outputSampleRate, ref totalHostedLatency,
                s => new LimiterSampleProvider(s, parameters.LimiterCeilingDb, parameters.LimiterGainDb));
        }

        ISampleProvider finished = totalHostedLatency > 0 ? new LatencySkipSampleProvider(withGain, totalHostedLatency) : withGain;

        var layerTap = new MeterTapSampleProvider(finished);
        _layerTaps[layer.LayerId] = layerTap;
        return layerTap;
    }

    /// <summary>Auto-selects hosted vs. native for one stage (v6 P3 task 1): if hostedPluginLabel
    /// is detected, wraps source in a HostedPluginSampleProvider against a cached instance (state
    /// restored from hostedState on first creation) and accumulates its latency; otherwise builds
    /// the native provider via buildNative.</summary>
    private ISampleProvider ApplyStage(
        ISampleProvider source, int layerId, string stageName, string hostedPluginLabel, byte[]? hostedState,
        int sampleRate, ref int totalHostedLatency, Func<ISampleProvider, ISampleProvider> buildNative)
    {
        if (!_hostedService.IsAvailable(hostedPluginLabel))
            return buildNative(source);

        var instance = GetOrCreateHostedInstance(layerId, stageName, hostedPluginLabel, hostedState, sampleRate);
        _hostedService.Reset(instance); // v7 Q0 task 5 (audit A5): clear last play's lookahead/buffer before reuse
        var hosted = new HostedPluginSampleProvider(source, instance, HostedPluginSampleProvider.DefaultBlockSize);
        totalHostedLatency += hosted.LatencySamples;
        return hosted;
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
