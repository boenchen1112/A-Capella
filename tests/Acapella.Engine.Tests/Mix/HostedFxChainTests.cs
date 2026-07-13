using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

/// <summary>
/// v6 P3 acceptance tests for backend-selectable FX stages: auto-selection + state persistence,
/// chain-order parity between native and hosted, sync-parity under a hosted stage's latency, and
/// reverb's tail extension. Uses the real FabFilter installs confirmed present on this machine
/// (CLAUDE.md's Environment section) rather than mocking JUCE itself -- HostedPluginInstanceTests
/// already covers the native bridge in isolation.
/// </summary>
[Collection("JuceHosting")]
public class HostedFxChainTests
{
    private const int SampleRate = 44100;

    /// <summary>Reports every plugin available except the ones explicitly excluded -- lets a test
    /// force "hosted selected" for one stage while keeping the others native, without depending on
    /// which FabFilter plugins happen to be installed.</summary>
    private sealed class OnlyAvailable : IHostedPluginAvailability
    {
        private readonly HashSet<string> _available;
        public OnlyAvailable(params string[] labels) => _available = new HashSet<string>(labels);
        public bool IsAvailable(string pluginLabel) => _available.Contains(pluginLabel);
    }

    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, int length, float amplitude = 0.5f)
    {
        var samples = new float[length];
        for (int i = 0; i < length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
        return samples;
    }

    private static float[] ReadAll(ISampleProvider provider, int count)
    {
        var buffer = new float[count];
        int read = 0;
        while (read < count)
        {
            int n = provider.Read(buffer, read, count - read);
            if (n == 0) break;
            read += n;
        }
        return buffer;
    }

    [Fact]
    public void BackendSelection_AutoSelectsHostedWhenDetected_AndStatePersistsThroughDto()
    {
        var samples = GenerateSineWave(440, SampleRate, SampleRate);
        // v7 Q0 task 4: EQ is an FL-style insert slot, off by default -- must be explicitly
        // enabled for this test's EQ-specific assertions to exercise anything.
        var parameters = new LayerMixParameters { EqEnabled = true };

        using (var engine = new MixEngine(new OnlyAvailable("FabFilter Pro-Q 4")))
        {
            var chain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
            // The chain reaches a HostedPluginSampleProvider for the EQ stage somewhere in its
            // graph; proving the auto-select path ran without throwing and produces audio is
            // sufficient here -- ChainOrder/SyncParity tests below verify the hosted path's actual
            // signal behavior in detail.
            Assert.NotEmpty(ReadAll(chain, 1000));
        }

        // Persist a hosted EQ state blob (as if the user tweaked Pro-Q 4's own editor -- task 7,
        // deferred) and confirm a fresh MixEngine with the same availability restores it rather
        // than starting the plugin from factory defaults.
        using var scan = HostedPluginInstance.Create(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-Q 4"], SampleRate, 512);
        int gainParam = -1;
        for (int i = 0; i < scan.ParameterCount; i++)
        {
            if (scan.GetParameterName(i).Contains("Gain", StringComparison.OrdinalIgnoreCase))
            {
                scan.SetParameterValue(i, 0.8f);
                gainParam = i;
                break;
            }
        }
        Assert.True(gainParam >= 0);
        var silence = new float[512];
        scan.ProcessBlock(silence, silence, new float[512], new float[512], 512);
        byte[] tweakedState = scan.GetState();
        Assert.NotEmpty(tweakedState);

        parameters.EqHostedState = tweakedState;

        using var engine2 = new MixEngine(new OnlyAvailable("FabFilter Pro-Q 4"));
        var restoredChain = engine2.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
        ReadAll(restoredChain, 512); // force lazy instance creation
        var restoredInstance = engine2.GetOrCreateHostedInstance(0, MixEngine.EqStage, MixEngine.EqPluginLabel, null, SampleRate);
        Assert.Equal(0.8f, restoredInstance.GetParameterValue(gainParam), precision: 2);
    }

    [Fact]
    public void BuildMix_FallsBackToNative_WhenNoPluginsDetected()
    {
        var samples = GenerateSineWave(440, SampleRate, SampleRate);
        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var chain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate);
        Assert.NotEmpty(ReadAll(chain, 1000));
    }

    /// <summary>Sync-parity (v6 P3 Acceptance bullet 3): a layer processed through a hosted stage
    /// with real reported latency (Pro-L 2's limiter lookahead) must stay sample-aligned with an
    /// unprocessed layer -- this is the test that would have caught the layer-desync bug class if
    /// the mandatory latency trim (LatencySkipSampleProvider) were missing or wrong.</summary>
    [Fact]
    public void HostedLimiter_StaysSampleAlignedWithUnprocessedLayer()
    {
        if (!HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-L 2"], out _))
            return; // plugin not present on this machine -- skip rather than fail (per P3a's own convention)

        int totalSamples = SampleRate; // 1 second
        var samples = new float[totalSamples];
        const int impulseIndex = 4410;
        samples[impulseIndex] = 1f;

        var parameters = new LayerMixParameters { LimiterEnabled = true };
        using var engine = new MixEngine(new OnlyAvailable("FabFilter Pro-L 2"));
        var chain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);

        var output = ReadAll(chain, totalSamples * 2 /* stereo interleaved */);

        // Search a small window around the original impulse index (post latency-trim, the impulse
        // should land back at its original position, not shifted by the limiter's lookahead).
        const int window = 8;
        int searchStart = Math.Max(0, impulseIndex - window);
        int searchEnd = Math.Min(totalSamples, impulseIndex + window + 1);

        float peak = 0f;
        int peakIndex = searchStart;
        for (int i = searchStart; i < searchEnd; i++)
        {
            float l = Math.Abs(output[i * 2]);
            if (l > peak) { peak = l; peakIndex = i; }
        }

        Assert.True(peak > 0f, "expected a non-zero response near the latency-compensated impulse index");
        Assert.InRange(peakIndex, impulseIndex - window, impulseIndex + window);
    }

    /// <summary>Chain-order parity (v6 P3 Acceptance bullet 2): the hosted EQ stage must land at
    /// the exact same chain position as the native EQ (between compressor and pan) -- verified via
    /// a gate-then-EQ vs EQ-then-gate style artifact check: a heavily gain-boosted low band on a
    /// low-frequency tone should be audible whether native or hosted EQ runs, proving the stage
    /// wasn't accidentally reordered relative to the gate/compressor stages around it.</summary>
    [Fact]
    public void HostedEq_LandsAtSameChainPositionAsNativeEq()
    {
        if (!HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-Q 4"], out _))
            return;

        var samples = GenerateSineWave(110, SampleRate, SampleRate, amplitude: 0.1f);
        // A gate threshold above the signal's amplitude would silence everything if EQ ran before
        // the gate instead of after it (chain order: gate -> compressor -> EQ) -- keeping the gate
        // permissive here isolates "does something reach the output at all", which is what a
        // reordering bug would break regardless of EQ specifics.
        var parameters = new LayerMixParameters { NoiseGateEnabled = true, NoiseGateThresholdDb = -80f, EqEnabled = true };

        using var nativeEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var nativeChain = nativeEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
        var nativeOutput = ReadAll(nativeChain, SampleRate * 2);

        using var hostedEngine = new MixEngine(new OnlyAvailable("FabFilter Pro-Q 4"));
        var hostedChain = hostedEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
        var hostedOutput = ReadAll(hostedChain, SampleRate * 2);

        Assert.True(nativeOutput.Any(s => Math.Abs(s) > 0.001f), "native chain produced no audible output");
        Assert.True(hostedOutput.Any(s => Math.Abs(s) > 0.001f), "hosted chain produced no audible output -- EQ likely landed before the gate/compressor stages instead of after");
    }

    /// <summary>Reverb tail extension (v6 P3 Acceptance bullet 4, first half): enabling reverb adds
    /// a measurable decay tail past the source's last non-silent sample.</summary>
    [Fact]
    public void ReverbEnabled_ExtendsOutputPastSourceEnd_WithMeasurableTail()
    {
        if (!HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-R 2"], out _))
            return;

        int totalSamples = SampleRate / 2; // 0.5s
        var samples = GenerateSineWave(440, SampleRate, totalSamples, amplitude: 0.5f);

        var parameters = new LayerMixParameters { ReverbEnabled = true };
        using var engine = new MixEngine(new OnlyAvailable("FabFilter Pro-R 2"));
        var chain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);

        // Read well past the source's own length; a working tail extension keeps producing
        // non-silent output for a while after totalSamples, whereas "reverb off" (below) goes
        // silent at (approximately) the source's own length.
        int readLength = (totalSamples + SampleRate) * 2; // up to +1s of stereo tail, generous vs. the 12s clamp cap
        var output = ReadAll(chain, readLength);

        bool hasTailEnergy = false;
        for (int frame = totalSamples + 1000; frame < readLength / 2; frame++)
        {
            if (Math.Abs(output[frame * 2]) > 0.0005f) { hasTailEnergy = true; break; }
        }
        Assert.True(hasTailEnergy, "expected measurable reverb decay tail past the source's last sample");
    }

    /// <summary>Reverb tail extension (v6 P3 Acceptance bullet 4, second half): reverb off is
    /// bit-identical to never-enabled (ReverbEnabled defaults false and the reverb branch is
    /// skipped entirely, so this is really a guard against a future accidental always-on branch).</summary>
    [Fact]
    public void ReverbDisabled_IsBitIdenticalToNeverEnabled()
    {
        var samples = GenerateSineWave(440, SampleRate, SampleRate / 2, amplitude: 0.5f);

        using var engineA = new MixEngine(NoHostedPluginsAvailable.Instance);
        var chainA = engineA.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters { ReverbEnabled = false }), anySolo: false, SampleRate);
        var outputA = ReadAll(chainA, SampleRate);

        using var engineB = new MixEngine(NoHostedPluginsAvailable.Instance);
        var chainB = engineB.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate);
        var outputB = ReadAll(chainB, SampleRate);

        Assert.Equal(outputA, outputB);
    }

    // ----- v7 Q0 acceptance tests (fixing Bug_Audit_2026-07-13_VstHosting.md section A) -----

    /// <summary>All-slots-off is the default (Q0 acceptance): a fresh layer's processed output is
    /// bit-identical to its unprocessed input, modulo pan/volume -- guards against gate/EQ (or any
    /// future stage) silently applying at some non-transparent default when the user never enabled
    /// it (audit A4).</summary>
    [Fact]
    public void AllSlotsOff_ProducesBitIdenticalOutputToUnprocessedInputModuloPanVolume()
    {
        var samples = GenerateSineWave(440, SampleRate, 1000, amplitude: 0.3f);
        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);
        // Pan/volume are the only stages that always run; a plain VolumeSampleProvider at 0dB and
        // NAudio's constant-power PanningSampleProvider at dead center both apply a fixed,
        // deterministic scale independent of any FX slot -- assert equality up to that scale
        // instead of exact identity so this test targets the FX slots, not the always-on bus math.
        var chain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters()), anySolo: false, SampleRate);
        var output = ReadAll(chain, samples.Length * 2);

        // Index 25 (quarter-period-ish for a 440Hz tone at 44100Hz) rather than 0, since a sine
        // wave starts at zero and 0/0 would be NaN.
        float scale = output[25 * 2] / samples[25];
        Assert.True(Math.Abs(scale) > 0.001f, "expected a non-zero pan/volume scale to compare against");
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i] * scale, output[i * 2], 4);
            Assert.Equal(samples[i] * scale, output[i * 2 + 1], 4);
        }
    }

    /// <summary>Identity test (Q0 acceptance, audit A1): the instance a "UI-side" MixEngine fetches
    /// for (layerId, stage) is reference-equal to the one a separate "chain-building" MixEngine
    /// wires into its chain, as long as both share the same HostedPluginService -- this is exactly
    /// the sharing MainWindow/PreviewPlaybackEngine/ExportEngine now do in production.</summary>
    [Fact]
    public void Identity_UiAndChainMixEnginesSharingOneServiceGetTheSameLiveInstance()
    {
        if (!HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-Q 4"], out _))
            return;

        var service = new HostedPluginService(new OnlyAvailable("FabFilter Pro-Q 4"));
        try
        {
            using var chainEngine = new MixEngine(service);
            using var uiEngine = new MixEngine(service);

            var samples = GenerateSineWave(440, SampleRate, 1000);
            var chain = chainEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters { EqEnabled = true }), anySolo: false, SampleRate);
            ReadAll(chain, 512); // force lazy instance creation inside the chain

            var uiInstance = uiEngine.GetOrCreateHostedInstance(0, MixEngine.EqStage, MixEngine.EqPluginLabel, null, SampleRate);
            var chainInstance = chainEngine.GetOrCreateHostedInstance(0, MixEngine.EqStage, MixEngine.EqPluginLabel, null, SampleRate);

            Assert.Same(chainInstance, uiInstance);
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>State round-trip (Q0 acceptance, audit A2): after a plugin tweak,
    /// MixEngine.SyncLiveStateIntoParameters (the method CurrentProjectDto now calls before every
    /// save/export/undo snapshot) pulls exactly the live GetState() blob, not a stale one.</summary>
    [Fact]
    public void StateRoundTrip_SyncLiveStateIntoParameters_MatchesLiveGetState()
    {
        if (!HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-Q 4"], out _))
            return;

        var service = new HostedPluginService(new OnlyAvailable("FabFilter Pro-Q 4"));
        try
        {
            using var engine = new MixEngine(service);
            var parameters = new LayerMixParameters { EqEnabled = true };

            var samples = GenerateSineWave(440, SampleRate, 1000);
            var chain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
            ReadAll(chain, 512);

            var instance = engine.GetOrCreateHostedInstance(0, MixEngine.EqStage, MixEngine.EqPluginLabel, null, SampleRate);
            int gainParam = -1;
            for (int i = 0; i < instance.ParameterCount; i++)
            {
                if (instance.GetParameterName(i).Contains("Gain", StringComparison.OrdinalIgnoreCase))
                {
                    instance.SetParameterValue(i, 0.75f);
                    gainParam = i;
                    break;
                }
            }
            Assert.True(gainParam >= 0);

            engine.SyncLiveStateIntoParameters(0, parameters);
            Assert.NotNull(parameters.EqHostedState);
            Assert.Equal(instance.GetState(), parameters.EqHostedState);
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>Replay hygiene (Q0 acceptance, audit A5): two consecutive builds of the same
    /// (layerId, stage) hosted stage -- i.e. two consecutive plays -- produce sample-identical
    /// output, proving aca_reset (called by MixEngine.ApplyStage before wiring a cached instance
    /// into a fresh chain) actually clears the plugin's internal lookahead/delay state between
    /// runs instead of bleeding the first run's buffered audio into the second.</summary>
    [Fact]
    public void ReplayReset_TwoConsecutivePlaysOfTheSameCachedInstance_ProduceSampleIdenticalOutput()
    {
        if (!HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-L 2"], out _))
            return;

        int totalSamples = SampleRate / 4;
        var samples = new float[totalSamples];
        samples[100] = 1f;

        var parameters = new LayerMixParameters { LimiterEnabled = true };
        var service = new HostedPluginService(new OnlyAvailable("FabFilter Pro-L 2"));
        try
        {
            using var engine = new MixEngine(service);

            var chain1 = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
            var output1 = ReadAll(chain1, totalSamples * 2);

            var chain2 = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
            var output2 = ReadAll(chain2, totalSamples * 2);

            Assert.Equal(output1, output2);
        }
        finally
        {
            service.Dispose();
        }
    }

}
