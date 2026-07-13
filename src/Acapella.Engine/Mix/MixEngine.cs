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

    private static readonly IPitchCorrectionBackend AutomaticBackend = new AutoPitchCorrector();

    /// <summary>Master brick-wall ceiling (audit B10): applied identically to preview and export
    /// so exports sound like the preview, replacing export's old content-dependent PeakNormalizer.</summary>
    private const float MasterCeilingDb = -0.3f;

    /// <summary>getTailLengthSeconds() can report `inf` for some FabFilter plugins (confirmed for
    /// Pro-R 2 and Pro-Q 4 in P3a probe 2) -- clamp before using it in any duration/loop-bound math.
    /// FabFilter Pro-R 2's own decay-time control tops out well under this, so the clamp only ever
    /// bites on a bogus/inf report, not a real long reverb setting.</summary>
    private const double MaxReverbTailSeconds = 12.0;

    private readonly IHostedPluginAvailability _availability;
    private readonly HostedPluginInstanceCache _hostedInstances = new();

    public MixEngine(IHostedPluginAvailability? availability = null)
    {
        _availability = availability ?? new HostedPluginAvailability();
    }

    public void Dispose() => _hostedInstances.Dispose();

    public bool IsHosted(string pluginLabel) => _availability.IsAvailable(pluginLabel);

    /// <summary>Fetches (lazily creating) the same cached hosted instance BuildLayerChain uses for
    /// (layerId, stage), for the UI's "Open Pro-X..." launcher buttons to call ShowEditorWindow on.
    /// Must be called on the app's UI thread (see HostedPluginInstance.Initialize's doc comment).</summary>
    public HostedPluginInstance GetOrCreateHostedInstance(int layerId, string stage, string pluginLabel, byte[]? initialState, int sampleRate = 44100) =>
        _hostedInstances.GetOrCreate(layerId, stage, HostedPluginCatalog.KnownPluginPaths[pluginLabel], sampleRate, HostedPluginSampleProvider.DefaultBlockSize, initialState);

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
        return (mix, masterVolumeStage);
    }

    public ISampleProvider BuildLayerChain(MixLayerInput layer, bool anySolo, int outputSampleRate)
    {
        var parameters = layer.Parameters;

        float[] processedSamples = parameters.PitchBackend == PitchBackendSelection.Automatic2B
            ? PitchCorrectionCache.GetOrCorrect(AutomaticBackend, layer.LayerId, layer.SourceKey, layer.Samples, layer.SampleRate)
            : layer.Samples;

        ISampleProvider chain = new ArraySampleProvider(processedSamples, layer.SampleRate);

        if (layer.SampleRate != outputSampleRate)
            chain = new WdlResamplingSampleProvider(chain, outputSampleRate);

        int totalHostedLatency = 0;

        chain = ApplyStage(chain, layer.LayerId, NoiseGateStage, NoiseGatePluginLabel, parameters.NoiseGateHostedState,
            outputSampleRate, ref totalHostedLatency,
            s => new NoiseGateSampleProvider(s, parameters.NoiseGateThresholdDb, parameters.NoiseGateReleaseMs));

        if (parameters.CompressorEnabled)
        {
            chain = ApplyStage(chain, layer.LayerId, CompressorStage, CompressorPluginLabel, parameters.CompressorHostedState,
                outputSampleRate, ref totalHostedLatency,
                s => new CompressorSampleProvider(s, parameters.CompressorThresholdDb, parameters.CompressorRatio));
        }

        chain = ApplyStage(chain, layer.LayerId, EqStage, EqPluginLabel, parameters.EqHostedState,
            outputSampleRate, ref totalHostedLatency,
            s => new ThreeBandEqSampleProvider(s, parameters.LowShelfGainDb, parameters.MidBellGainDb, parameters.HighShelfGainDb));

        ISampleProvider afterPan = new PanningSampleProvider(chain) { Pan = parameters.Pan };

        if (parameters.ReverbEnabled && _availability.IsAvailable(ReverbPluginLabel))
        {
            var reverbInstance = GetOrCreateHostedInstance(layer.LayerId, ReverbStage, ReverbPluginLabel, parameters.ReverbHostedState, outputSampleRate);

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

        return totalHostedLatency > 0 ? new LatencySkipSampleProvider(withGain, totalHostedLatency) : withGain;
    }

    /// <summary>Auto-selects hosted vs. native for one stage (v6 P3 task 1): if hostedPluginLabel
    /// is detected, wraps source in a HostedPluginSampleProvider against a cached instance (state
    /// restored from hostedState on first creation) and accumulates its latency; otherwise builds
    /// the native provider via buildNative.</summary>
    private ISampleProvider ApplyStage(
        ISampleProvider source, int layerId, string stageName, string hostedPluginLabel, byte[]? hostedState,
        int sampleRate, ref int totalHostedLatency, Func<ISampleProvider, ISampleProvider> buildNative)
    {
        if (!_availability.IsAvailable(hostedPluginLabel))
            return buildNative(source);

        var instance = GetOrCreateHostedInstance(layerId, stageName, hostedPluginLabel, hostedState, sampleRate);
        var hosted = new HostedPluginSampleProvider(source, instance, HostedPluginSampleProvider.DefaultBlockSize);
        totalHostedLatency += hosted.LatencySamples;
        return hosted;
    }

    private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
}
