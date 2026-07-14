using Acapella.Engine.Host;

namespace Acapella.Engine.Pitch;

/// <summary>
/// v7 2A task 39: the Manual2A pitch backend -- renders a layer's whole buffer through Melodyne's
/// ARA analyze-then-render pipeline (AraHostSession) instead of AutoPitchCorrector's automatic
/// pitch-detect/Rubber-Band-shift math. A fresh session is created and torn down per Correct() call
/// (no cross-call session pooling): PitchCorrectionCache already caches the *output* per
/// (layerId, sourceKey), so Correct() only runs once per unique source content, and Melodyne
/// instantiation/analysis cost is paid once per that same cache key -- pooling sessions would only
/// save cost on a cache miss that's about to be cached anyway.
///
/// Every native call goes through HostedPluginService.RunOnHostedThread so this is safe to call
/// from any thread (BuildLayerChain may run on PreviewPlaybackEngine's own command thread) --
/// touching the ARA bridge off the JUCE-initialized thread is the exact heap-corruption hazard
/// documented across this project's hosted-plugin tests.
/// </summary>
public sealed class MelodyneAraPitchCorrector : IPitchCorrectionBackend
{
    private const int RenderBlockSize = 4096;

    private readonly HostedPluginService _hostedService;
    private readonly string _pluginPath;

    public MelodyneAraPitchCorrector(HostedPluginService hostedService, string pluginPath)
    {
        _hostedService = hostedService;
        _pluginPath = pluginPath;
    }

    public float[] Correct(float[] samples, int sampleRate) =>
        _hostedService.RunOnHostedThread(() => RenderWhole(samples, sampleRate));

    private float[] RenderWhole(float[] samples, int sampleRate)
    {
        using var session = AraHostSession.Create(_pluginPath, sampleRate, RenderBlockSize);
        var source = session.RegisterAudioSource(new[] { samples }, samples.Length, sampleRate, Guid.NewGuid().ToString("N"));
        session.AddPlaybackRegion(source);

        var output = new float[samples.Length];
        for (int pos = 0; pos < samples.Length; pos += RenderBlockSize)
        {
            int n = Math.Min(RenderBlockSize, samples.Length - pos);
            var (left, right) = session.RenderBlock(source, startSampleInRegion: pos, numSamples: n);
            // Melodyne's mono-source-forced-to-stereo-bus output duplicates the mono signal to both
            // channels; averaging is a safe general downmix if that ever isn't exactly true.
            for (int i = 0; i < n; i++)
                output[pos + i] = 0.5f * (left[i] + right[i]);
        }
        return output;
    }
}
