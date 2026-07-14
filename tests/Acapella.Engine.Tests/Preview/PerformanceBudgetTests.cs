using System.Diagnostics;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Preview;

/// <summary>
/// Q3 performance-budget checks: Play warm-cache under 1s and slot-toggle refresh under 500ms,
/// with real hosted FabFilter slots in the chain. Measured directly against MixEngine
/// (BuildLayerChain/BuildMix), synchronously on the test's own thread -- NOT through
/// PreviewPlaybackEngine's internal async command thread. A bare PreviewPlaybackEngine (no
/// explicit hostedService) defaults to InlineHostedPluginDispatcher, which has no thread
/// marshaling; touching the native JUCE bridge from that engine's own command thread is unsafe
/// (JUCE's MessageManager is bound permanently to whichever thread first called aca_initialize,
/// which for this test collection is JuceHostingFixture's own thread) and previously crashed the
/// whole test host with a heap-corruption assertion. Every other passing hosted-plugin test in
/// HostedFxChainTests.cs uses this same direct/synchronous MixEngine pattern.
/// </summary>
[Collection("JuceHosting")]
public class PerformanceBudgetTests
{
    private const int SampleRate = 44100;

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

    private static void ReadAll(ISampleProvider provider, int count)
    {
        var buffer = new float[count];
        int read = 0;
        while (read < count)
        {
            int n = provider.Read(buffer, read, count - read);
            if (n == 0) break;
            read += n;
        }
    }

    [Fact]
    public void Play_WarmCacheWithHostedSlots_StartsWithinOneSecond()
    {
        if (!HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-Q 4"], out _))
        {
            Console.WriteLine("SKIPPED: 'FabFilter Pro-Q 4' not found on this machine.");
            return;
        }

        var samples = GenerateSineWave(440, SampleRate, SampleRate);
        var parameters = new LayerMixParameters { EqEnabled = true };
        using var engine = new MixEngine(new OnlyAvailable("FabFilter Pro-Q 4"));

        // Cold: forces instance creation + first-block processing (JIT, hosted-instance cache
        // population). Not timed -- only the warm path (instances already cached, mirroring a
        // second Play after the first) is subject to the 1s budget.
        var coldChain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
        ReadAll(coldChain, 4096);

        var stopwatch = Stopwatch.StartNew();
        var warmChain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
        ReadAll(warmChain, 4096);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"Warm-cache chain build+first-block took {stopwatch.ElapsedMilliseconds}ms, budget is 1000ms.");
    }

    [Fact]
    public void SlotToggleRefresh_WithHostedSlots_CompletesWithinFiveHundredMs()
    {
        if (!HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-Q 4"], out _))
        {
            Console.WriteLine("SKIPPED: 'FabFilter Pro-Q 4' not found on this machine.");
            return;
        }

        var samples = GenerateSineWave(440, SampleRate, SampleRate);
        var parameters = new LayerMixParameters { EqEnabled = false };
        using var engine = new MixEngine(new OnlyAvailable("FabFilter Pro-Q 4"));

        // Warm up the instance cache with the slot already off once, then time the rebuild that
        // happens the moment a user flips the slot on -- this is the refresh a UI toggle triggers.
        var initialChain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
        ReadAll(initialChain, 4096);

        parameters.EqEnabled = true;

        var stopwatch = Stopwatch.StartNew();
        var toggledChain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters), anySolo: false, SampleRate);
        ReadAll(toggledChain, 4096);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"Slot-toggle refresh took {stopwatch.ElapsedMilliseconds}ms, budget is 500ms.");
    }
}
