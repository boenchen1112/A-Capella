using Acapella.Engine.Host;

namespace Acapella.Engine.Pitch;

/// <summary>
/// v7 2A task 39/A1: the Manual2A pitch backend -- renders a layer's whole buffer through
/// Melodyne's ARA analyze-then-render pipeline instead of AutoPitchCorrector's automatic
/// pitch-detect/Rubber-Band-shift math.
///
/// Bug audit A1: uses one persistent ARA session per layer (HostedPluginService.
/// GetOrCreateAraLayerSource), not a fresh session per Correct() call -- a session created and
/// torn down immediately can never be the same live document a real Melodyne editor window (the
/// "Edit in Melodyne..." launcher) shows or edits. The session survives across calls; only the
/// audio source is re-registered when the content actually changes (contentKey mismatch).
///
/// Bug audit A4: only session/source lifecycle calls and the analysis-wait poll (many short
/// pump-and-check round trips) go through the dispatcher (inside HostedPluginService's methods).
/// The block-render loop itself calls AraHostSession.RenderBlock directly, unmarshaled --
/// aca_ara_render_block only touches AudioProcessor::processBlock and an atomic play head,
/// matching the existing "processBlock is the only call that stays off the JUCE-initialized
/// thread" contract every other hosted-plugin path in this project already relies on.
/// </summary>
public sealed class MelodyneAraPitchCorrector : IPitchCorrectionBackend
{
    private const int RenderBlockSize = 4096;
    private const int AnalysisWaitTimeoutMs = 30_000;

    private readonly HostedPluginService _hostedService;
    private readonly string _pluginPath;
    private readonly int _layerId;

    public MelodyneAraPitchCorrector(HostedPluginService hostedService, string pluginPath, int layerId)
    {
        _hostedService = hostedService;
        _pluginPath = pluginPath;
        _layerId = layerId;
    }

    public float[] Correct(float[] samples, int sampleRate)
    {
        // Bug audit A5: the audio source's own persistentID inside the ARA document is derived from
        // layerId (stable across calls for the same layer) rather than a random GUID -- combined
        // with a content hash so a genuinely new recording/trim re-registers the source instead of
        // silently reusing stale content under the same layer's persistent session.
        string contentKey = ContentHash(samples, sampleRate);

        var (session, source) = _hostedService.GetOrCreateAraLayerSource(
            _layerId, _pluginPath, sampleRate, RenderBlockSize, samples, contentKey);

        // Bug audit A2: rendering before analysis completes returns whatever Melodyne's playback
        // renderer does pre-analysis (implementation-defined) -- wait for it first. A timeout
        // renders best-effort rather than failing outright since ARA gives no hard completion
        // deadline; each pump+check round trip is its own short dispatcher call (A4), with the
        // poll interval's Thread.Sleep running on this calling thread instead of inside a
        // dispatcher frame.
        var deadline = DateTime.UtcNow.AddMilliseconds(AnalysisWaitTimeoutMs);
        while (true)
        {
            float progress = _hostedService.RunOnHostedThread(() =>
            {
                session.PumpModelUpdates();
                return session.GetAnalysisProgress(source);
            });
            if (progress >= 1.0f || DateTime.UtcNow >= deadline)
                break;
            Thread.Sleep(20);
        }

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

    private static string ContentHash(float[] samples, int sampleRate)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var s in samples)
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(s)) * 16777619;
            h = (h ^ (uint)sampleRate) * 16777619;
            return h.ToString("x8");
        }
    }
}
