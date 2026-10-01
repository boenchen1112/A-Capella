using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

/// <summary>Hosted-stage half of the in-place slot bypass feature: when a HostedPluginSampleProvider
/// is bypassed, its output must be the input delayed by exactly the plugin's own reported latency
/// (not processed, and not realigned to zero latency) so the layer's single combined
/// LatencySkipSampleProvider trim stays correct regardless of whether the stage is bypassed. Uses
/// the real FabFilter Pro-L 2 install on this machine, like HostedFxChainTests -- skipped (not
/// failed) if it isn't present.</summary>
[Collection("JuceHosting")]
public class InPlaceSlotBypassHostedTests
{
    private const int SampleRate = 44100;

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
    public void Bypassed_OutputsInputDelayedByThePluginsOwnLatency()
    {
        if (!HostedPluginInstance.TryScan(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-L 2"], out _))
        {
            Console.WriteLine("SKIPPED: 'FabFilter Pro-L 2' not found on this machine.");
            return;
        }

        int totalSamples = SampleRate / 2;
        var input = new float[totalSamples];
        const int impulseIndex = 4410;
        input[impulseIndex] = 1f;
        var source = new ArraySampleProvider(input, SampleRate);

        using var instance = HostedPluginInstance.Create(HostedPluginCatalog.KnownPluginPaths["FabFilter Pro-L 2"], SampleRate, HostedPluginSampleProvider.DefaultBlockSize);
        var provider = new HostedPluginSampleProvider(source, instance) { Bypassed = true };

        int latency = provider.LatencySamples;
        Console.WriteLine($"Pro-L 2 reported latency: {latency} samples");
        Assert.True(latency > 0, "expected Pro-L 2 to report non-zero lookahead latency, otherwise this test only exercises the zero-latency passthrough path");
        // Mono in, mono out -- HostedPluginSampleProvider passes a mono source's WaveFormat through
        // unchanged.
        var output = ReadAll(provider, totalSamples + latency + 100);

        for (int frame = 0; frame < totalSamples; frame++)
        {
            float expected = input[frame];
            float actual = output[frame + latency];
            if (expected != 0f)
                Assert.Equal(expected, actual, 3);
        }

        // Nothing should appear before the latency-delayed position.
        float peakBeforeDelay = 0f;
        for (int frame = 0; frame < latency; frame++)
            peakBeforeDelay = Math.Max(peakBeforeDelay, Math.Abs(output[frame]));
        Assert.Equal(0f, peakBeforeDelay, 4);
    }
}
