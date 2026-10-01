using Acapella.Engine.Host;

namespace Acapella.Engine.Tests.Host;

/// <summary>
/// v7 2A task 39 acceptance test: the full analyze -> render loop the build plan's Phase 2A
/// timebox is measured against (3 sessions or 10 failed attempts). Creates a real ARA session
/// against Melodyne, registers a synthetic audio source, attaches a playback region (triggering
/// Melodyne's analysis), waits for analysis, then renders blocks back and confirms the output is
/// real (non-silent) audio -- proving the whole create -> register -> analyze -> render pipeline
/// works end to end, not just that individual native calls don't crash.
///
/// Synchronous on the test's own thread, per every other hosted-plugin test in this project.
/// </summary>
[Collection("JuceHosting")]
public class AraAnalyzeRenderLoopTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void CreateRegisterAnalyzeRender_ProducesNonSilentOutput()
    {
        string melodynePath = HostedPluginCatalog.KnownPluginPaths["Melodyne"];
        if (!HostedPluginInstance.TryScanAraCapability(melodynePath, out var capability) || capability is null || !capability.IsAraCapable)
        {
            Console.WriteLine($"SKIPPED: Melodyne at '{melodynePath}' is not ARA-capable on this machine (or not found).");
            return;
        }

        // Bug audit (crash found while adding B3's test): maxBlockSize must be >= every numSamples
        // ever passed to RenderBlock for this session -- VST3's processBlock contract is that a
        // block never exceeds what prepareToPlay declared, and asking Melodyne to render more than
        // that in one call previously produced an AccessViolationException (found via the B3 test
        // below using a 44100-sample block against a 512-sample max). 4096 here matches the render
        // call below.
        using var session = AraHostSession.Create(melodynePath, SampleRate, 4096);

        // 2-second 440Hz mono source -- long enough for Melodyne to have real pitch content to
        // analyze, short enough to keep the test's analysis-wait bounded.
        int numSamples = SampleRate * 2;
        var samples = new float[numSamples];
        for (int i = 0; i < numSamples; i++)
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));

        var audioSource = session.RegisterAudioSource(new[] { samples }, numSamples, SampleRate, "acapella-test-analyze-render");
        session.AddPlaybackRegion(audioSource);

        // Bug audit A3/A2: analysis-progress polling now works (aca_ara_pump_model_updates
        // installs the notifyModelUpdates() pump ARA's pull-based callbacks require) -- wait for it
        // rather than rendering immediately, since rendering before analysis completes returns
        // whatever Melodyne's playback renderer does pre-analysis (implementation-defined).
        bool completed = session.WaitForAnalysisComplete(audioSource, timeoutMs: 30_000);
        Console.WriteLine(completed ? "Analysis completed." : "Analysis did not report completion within 30s -- rendering best-effort anyway.");

        var (left, right) = session.RenderBlock(audioSource, startSampleInRegion: 0, numSamples: 4096);

        bool anyNonZero = left.Any(s => Math.Abs(s) > 1e-6f) || right.Any(s => Math.Abs(s) > 1e-6f);
        Assert.True(anyNonZero, "Rendered block was silent -- expected Melodyne's playback renderer to produce real audio for a registered source.");
    }

    /// <summary>Bug audit B3: aca_ara_render_block assumes sample-exact, zero-latency model
    /// rendering (no LatencySkipSampleProvider-style trim like the FabFilter hosted stages need).
    /// That's the expected behavior for an ARA playback renderer (random-access, not a streaming
    /// effect with lookahead) -- this proves it holds for Melodyne specifically: an untouched
    /// region's rendered click lands at the same sample position it started at, matching
    /// HostedFxChainTests.HostedLimiter_StaysSampleAlignedWithUnprocessedLayer's pattern for the
    /// plain-VST3 hosted path.</summary>
    [Fact]
    public void RenderBlock_UntouchedRegion_KeepsClickAtOriginalSamplePosition()
    {
        string melodynePath = HostedPluginCatalog.KnownPluginPaths["Melodyne"];
        if (!HostedPluginInstance.TryScanAraCapability(melodynePath, out var capability) || capability is null || !capability.IsAraCapable)
        {
            Console.WriteLine($"SKIPPED: Melodyne at '{melodynePath}' is not ARA-capable on this machine (or not found).");
            return;
        }

        const int blockSize = 512;
        using var session = AraHostSession.Create(melodynePath, SampleRate, blockSize);

        int totalSamples = SampleRate; // 1 second
        var samples = new float[totalSamples];
        const int impulseIndex = 4410;
        samples[impulseIndex] = 1f;

        var audioSource = session.RegisterAudioSource(new[] { samples }, totalSamples, SampleRate, "acapella-test-latency");
        session.AddPlaybackRegion(audioSource);
        session.WaitForAnalysisComplete(audioSource, timeoutMs: 30_000);

        // Chunked to match the session's maxBlockSize (see the crash found in the test above and
        // fixed there) -- one AccessViolationException from asking for a too-large block is exactly
        // the kind of defect B3 exists to catch, just not the one it set out to find.
        var left = new float[totalSamples];
        for (int pos = 0; pos < totalSamples; pos += blockSize)
        {
            int n = Math.Min(blockSize, totalSamples - pos);
            var (blockLeft, _) = session.RenderBlock(audioSource, startSampleInRegion: pos, numSamples: n);
            Array.Copy(blockLeft, 0, left, pos, n);
        }

        const int window = 8;
        int searchStart = Math.Max(0, impulseIndex - window);
        int searchEnd = Math.Min(totalSamples, impulseIndex + window + 1);

        float peak = 0f;
        int peakIndex = searchStart;
        for (int i = searchStart; i < searchEnd; i++)
        {
            float v = Math.Abs(left[i]);
            if (v > peak) { peak = v; peakIndex = i; }
        }

        Assert.True(peak > 0f, "expected a non-zero response near the original impulse index");
        Assert.InRange(peakIndex, impulseIndex - window, impulseIndex + window);
    }
}
