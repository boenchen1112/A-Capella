using Acapella.Engine.Pitch;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Acapella.Engine.Mix;

/// <summary>SourceKey, if provided, must uniquely determine Samples' exact content (e.g. source
/// path + mtime + trim + shift) -- it's used to cache automatic pitch correction across rebuilds
/// (audit B3). Null disables that cache (correction always runs fresh).</summary>
public record MixLayerInput(int LayerId, float[] Samples, int SampleRate, LayerMixParameters Parameters, string? SourceKey = null);

/// <summary>
/// Builds the live mixed preview: for each layer, applies the fixed chain
/// (pitch correction -> noise gate -> EQ -> pan -> gain) then sums via NAudio's
/// MixingSampleProvider. Parameters are read at build time, so changing a parameter and
/// rebuilding is how "live" updates apply (non-destructive: raw samples untouched).
/// </summary>
public class MixEngine
{
    private static readonly IPitchCorrectionBackend AutomaticBackend = new AutoPitchCorrector();

    public ISampleProvider BuildMix(IReadOnlyList<MixLayerInput> layers, int outputSampleRate = 44100)
    {
        bool anySolo = layers.Any(l => l.Parameters.Solo);
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(outputSampleRate, 2));

        foreach (var layer in layers)
        {
            mixer.AddMixerInput(BuildLayerChain(layer, anySolo, outputSampleRate));
        }

        // MixingSampleProvider just sums its inputs; 2+ vocal layers near full scale would
        // exceed +-1.0 and hard-clip on the AAC/WAV encode. A fixed 1/sqrt(N) headroom scale is
        // enough to keep the common case under 0dBFS without needing a full limiter for v1.
        float headroomGain = layers.Count > 0 ? (float)(1.0 / Math.Sqrt(layers.Count)) : 1f;
        return new VolumeSampleProvider(mixer) { Volume = headroomGain };
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

        chain = new NoiseGateSampleProvider(chain, parameters.NoiseGateThresholdDb, parameters.NoiseGateReleaseMs);

        if (parameters.CompressorEnabled)
            chain = new CompressorSampleProvider(chain, parameters.CompressorThresholdDb, parameters.CompressorRatio);

        chain = new ThreeBandEqSampleProvider(chain, parameters.LowShelfGainDb, parameters.MidBellGainDb, parameters.HighShelfGainDb);

        var panned = new PanningSampleProvider(chain) { Pan = parameters.Pan };

        bool effectiveMute = parameters.Mute || (anySolo && !parameters.Solo);
        float linearGain = effectiveMute ? 0f : DbToLinear(parameters.GainDb);
        ISampleProvider withGain = new VolumeSampleProvider(panned) { Volume = linearGain };

        if (parameters.LimiterEnabled)
            withGain = new LimiterSampleProvider(withGain, parameters.LimiterCeilingDb, parameters.LimiterGainDb);

        return withGain;
    }

    private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
}
