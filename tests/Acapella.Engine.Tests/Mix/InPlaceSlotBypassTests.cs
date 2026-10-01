using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Tests.Host;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

/// <summary>Toggling a slot's power button in place (MixEngine.TrySetSlotEnabled), without a full
/// chain rebuild. These tests force the native fallback path (NoHostedPluginsAvailable) so the
/// behavior is exercised deterministically regardless of what FabFilter plugins happen to be
/// installed on the machine running them -- see InPlaceSlotBypassHostedTests for the hosted
/// (plugin-latency) path specifically.</summary>
public class InPlaceSlotBypassTests
{
    private const int SampleRate = 44100;

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

    /// <summary>Toggling a pre-pan slot (EQ) off mid-stream switches the live chain over to
    /// unprocessed passthrough from that point on, reading the same amount of the underlying source
    /// either way -- the single-reader requirement means no samples are skipped or replayed at the
    /// toggle point.</summary>
    [Fact]
    public void TrySetSlotEnabled_TogglingEqOffMidStream_MatchesAChainBuiltWithEqDisabledFromThatPoint()
    {
        var samples = GenerateSineWave(220, SampleRate, SampleRate, amplitude: 0.4f);
        var parameters = new LayerMixParameters { EqEnabled = true, LowShelfGainDb = 15f, MidBellGainDb = 15f, HighShelfGainDb = -15f };

        using var toggledEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var toggledChain = toggledEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);

        using var referenceEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var referenceDisabledParams = new LayerMixParameters { EqEnabled = false };
        var referenceChain = referenceEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, referenceDisabledParams), anySolo: false, SampleRate);

        // Sanity: with EQ enabled and heavily boosted/cut, the toggled chain's output must actually
        // differ from the always-disabled reference before the toggle takes effect.
        int preToggleCount = 2000;
        var toggledBefore = ReadAll(toggledChain, preToggleCount);
        var referenceBefore = ReadAll(referenceChain, preToggleCount);
        Assert.NotEqual(referenceBefore, toggledBefore);

        bool applied = toggledEngine.TrySetSlotEnabled(0, FxSlots.Eq.Stage, enabled: false);
        Assert.True(applied);

        int postToggleCount = SampleRate * 2 - preToggleCount;
        var toggledAfter = ReadAll(toggledChain, postToggleCount);
        var referenceAfter = ReadAll(referenceChain, postToggleCount);

        Assert.Equal(referenceAfter, toggledAfter);
    }

    /// <summary>Same as above for a post-gain slot (Limiter), proving the in-place toggle works
    /// regardless of chain position.</summary>
    [Fact]
    public void TrySetSlotEnabled_TogglingLimiterOffMidStream_MatchesAChainBuiltWithLimiterDisabledFromThatPoint()
    {
        var samples = GenerateSineWave(220, SampleRate, SampleRate, amplitude: 0.9f);
        var parameters = new LayerMixParameters { LimiterEnabled = true, LimiterCeilingDb = -6f, LimiterGainDb = 12f };

        using var toggledEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var toggledChain = toggledEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);

        using var referenceEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var referenceChain = referenceEngine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters { LimiterEnabled = false }), anySolo: false, SampleRate);

        int preToggleCount = 2000;
        var toggledBefore = ReadAll(toggledChain, preToggleCount);
        var referenceBefore = ReadAll(referenceChain, preToggleCount);
        Assert.NotEqual(referenceBefore, toggledBefore);

        Assert.True(toggledEngine.TrySetSlotEnabled(0, FxSlots.Limiter.Stage, enabled: false));

        int postToggleCount = SampleRate * 2 - preToggleCount;
        var toggledAfter = ReadAll(toggledChain, postToggleCount);
        var referenceAfter = ReadAll(referenceChain, postToggleCount);

        Assert.Equal(referenceAfter, toggledAfter);
    }

    /// <summary>Reverb always returns false: its tail extension changes the layer's computed
    /// duration, so toggling it must go through a full refresh, never an in-place flip.</summary>
    [Fact]
    public void TrySetSlotEnabled_ReturnsFalse_ForReverb()
    {
        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var samples = GenerateSineWave(220, SampleRate, 1000);
        engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters { ReverbEnabled = true }), anySolo: false, SampleRate);

        Assert.False(engine.TrySetSlotEnabled(0, FxSlots.Reverb.Stage, enabled: false));
    }

    /// <summary>No live node yet. A native-fallback stage is always built (cheap), so this only
    /// arises for a hosted stage that's both installed and has never been enabled -- FxSlot.Insert
    /// never creates its plugin instance in that case, so there's nothing to flip. Must report false
    /// rather than silently no-op, so the caller knows to fall back to a full refresh.</summary>
    [Fact]
    public void TrySetSlotEnabled_ReturnsFalse_WhenNoLiveNodeExistsForThatSlot()
    {
        using var service = new FakeHostedPluginFactory().With(FxSlots.Eq.PluginLabel).CreateService();
        using var engine = new MixEngine(service);
        var samples = GenerateSineWave(220, SampleRate, 1000);
        engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, new LayerMixParameters { EqEnabled = false }), anySolo: false, SampleRate);

        Assert.False(engine.TrySetSlotEnabled(0, FxSlots.Eq.Stage, enabled: true));
    }

    /// <summary>No chain has ever been built for this layerId at all.</summary>
    [Fact]
    public void TrySetSlotEnabled_ReturnsFalse_ForAnUnknownLayer()
    {
        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);
        Assert.False(engine.TrySetSlotEnabled(99, FxSlots.Eq.Stage, enabled: true));
    }
}
