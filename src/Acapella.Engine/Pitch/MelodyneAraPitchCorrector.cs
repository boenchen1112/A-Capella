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
/// Bug audit A4: only session creation/registration/region-attach (fast, one-shot lifecycle calls)
/// and the analysis-wait poll (many short pump-and-check round trips) go through
/// HostedPluginService.RunOnHostedThread. The block-render loop itself calls AraHostSession.
/// RenderBlock directly, unmarshaled -- aca_ara_render_block only touches AudioProcessor::
/// processBlock and an atomic play head, matching the existing "processBlock is the only call that
/// stays off the JUCE-initialized thread" contract every other hosted-plugin path in this project
/// already relies on. The original all-in-one-dispatcher-frame design would freeze the WPF message
/// pump for the whole analyze+render duration of a track, and -- worse -- while frozen, no message-
/// loop-dependent part of Melodyne's analysis handshake could run either.
/// </summary>
public sealed class MelodyneAraPitchCorrector : IPitchCorrectionBackend
{
    private const int RenderBlockSize = 4096;
    private const int AnalysisWaitTimeoutMs = 30_000;

    private readonly HostedPluginService _hostedService;
    private readonly string _pluginPath;

    public MelodyneAraPitchCorrector(HostedPluginService hostedService, string pluginPath)
    {
        _hostedService = hostedService;
        _pluginPath = pluginPath;
    }

    public float[] Correct(float[] samples, int sampleRate)
    {
        // Bug audit A5: a random GUID per call meant an exported archive could never re-attach to
        // this same content on a later load (archives match audio sources by persistentID). A
        // content hash is stable for the same samples across separate Correct() calls/sessions,
        // matching the "(layerId, sourceAudioHash)" key the plan specifies -- layerId isn't
        // available at this interface's level (IPitchCorrectionBackend.Correct only gets samples),
        // so the hash alone is what this one-shot design can offer; full stability additionally
        // needs the persistent-per-layer session (see MelodyneEditorWindow's follow-up note).
        string persistentId = "acapella-source-" + ContentHash(samples, sampleRate);

        var (session, source) = _hostedService.RunOnHostedThread(() =>
        {
            var s = AraHostSession.Create(_pluginPath, sampleRate, RenderBlockSize);
            var src = s.RegisterAudioSource(new[] { samples }, samples.Length, sampleRate, persistentId);
            s.AddPlaybackRegion(src);
            return (s, src);
        });

        try
        {
            // Bug audit A2: rendering before analysis completes returns whatever Melodyne's
            // playback renderer does pre-analysis (implementation-defined) -- wait for it first.
            // A timeout renders best-effort rather than failing outright since ARA gives no hard
            // completion deadline; each pump+check round trip is its own short dispatcher call
            // (A4), with the poll interval's Thread.Sleep running on this calling thread instead of
            // inside a dispatcher frame.
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
                // Melodyne's mono-source-forced-to-stereo-bus output duplicates the mono signal to
                // both channels; averaging is a safe general downmix if that ever isn't exactly true.
                for (int i = 0; i < n; i++)
                    output[pos + i] = 0.5f * (left[i] + right[i]);
            }
            return output;
        }
        finally
        {
            _hostedService.RunOnHostedThread(session.Dispose);
        }
    }

    private static string ContentHash(float[] samples, int sampleRate)
    {
        var hash = new System.Text.StringBuilder();
        unchecked
        {
            uint h = 2166136261;
            foreach (var s in samples)
            {
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(s)) * 16777619;
            }
            h = (h ^ (uint)sampleRate) * 16777619;
            hash.Append(h.ToString("x8"));
        }
        return hash.ToString();
    }
}
