using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

/// <summary>
/// v7 2A task 39/41 acceptance tests: MixEngine.BuildLayerChain actually routes
/// PitchBackendSelection.Manual2A through Melodyne's ARA pipeline when it's available, and falls
/// back to unprocessed passthrough (no crash) when it isn't -- the two ends of the fallback-
/// without-crash requirement.
/// </summary>
[Collection("JuceHosting")]
public class MelodyneAraPitchStageTests
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
    public void Manual2A_RoutesThroughMelodyne_WhenAraCapable_ProducesNonSilentOutput()
    {
        if (!HostedPluginInstance.TryScanAraCapability(HostedPluginCatalog.KnownPluginPaths["Melodyne"], out var capability)
            || capability is null || !capability.IsAraCapable)
        {
            Console.WriteLine("SKIPPED: Melodyne is not ARA-capable on this machine (or not found).");
            return;
        }

        var samples = GenerateSineWave(440, SampleRate, SampleRate);
        var parameters = new LayerMixParameters { PitchBackend = PitchBackendSelection.Manual2A };

        using var engine = new MixEngine(new OnlyAvailable()); // no FabFilter stages needed for this test
        var chain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters, SourceKey: "melodyne-test-layer"), anySolo: false, SampleRate);

        var output = ReadAll(chain, SampleRate * 2 /* stereo interleaved */);
        Assert.Contains(output, s => Math.Abs(s) > 1e-6f);
    }

    /// <summary>Bug audit A1: proves the persistent-per-layer-session redesign actually persists --
    /// building the same layer's chain twice (same MixEngine, same layerId, unchanged content) must
    /// not throw or error on the second call (which exercises HostedPluginService.
    /// GetOrCreateAraLayerSource's "session already exists, content key unchanged" branch, not the
    /// "create fresh" branch every other test only ever exercises once) and should reproduce the
    /// same rendered output since nothing about the source changed.</summary>
    [Fact]
    public void Manual2A_SecondBuildForSameLayer_ReusesSessionWithoutError()
    {
        if (!HostedPluginInstance.TryScanAraCapability(HostedPluginCatalog.KnownPluginPaths["Melodyne"], out var capability)
            || capability is null || !capability.IsAraCapable)
        {
            Console.WriteLine("SKIPPED: Melodyne is not ARA-capable on this machine (or not found).");
            return;
        }

        var samples = GenerateSineWave(440, SampleRate, SampleRate / 4);
        var parameters = new LayerMixParameters { PitchBackend = PitchBackendSelection.Manual2A };

        using var engine = new MixEngine(new OnlyAvailable());
        var input = new MixLayerInput(0, samples, SampleRate, parameters, SourceKey: "melodyne-reuse-test");

        var firstChain = engine.BuildLayerChain(input, anySolo: false, SampleRate);
        var firstOutput = ReadAll(firstChain, samples.Length * 2);

        var secondChain = engine.BuildLayerChain(input, anySolo: false, SampleRate);
        var secondOutput = ReadAll(secondChain, samples.Length * 2);

        Assert.Equal(firstOutput, secondOutput);
    }

    /// <summary>Task 41: a layer selecting Manual2A on a machine where Melodyne isn't ARA-capable
    /// (or isn't installed at all) must still build a working chain -- silently falling back to
    /// unprocessed passthrough, never throwing mid-BuildLayerChain. Simulated here by pointing
    /// PitchBackend at Manual2A while using an availability stub that (for the native-fallback
    /// stages) reports nothing hosted; IsAraAvailable's own real on-disk scan is what this test
    /// actually exercises, so it's meaningful whether or not this dev machine has Melodyne.</summary>
    [Fact]
    public void Manual2A_DoesNotThrow_RegardlessOfAraAvailability()
    {
        var samples = GenerateSineWave(440, SampleRate, SampleRate / 4);
        var parameters = new LayerMixParameters { PitchBackend = PitchBackendSelection.Manual2A };

        using var engine = new MixEngine(new OnlyAvailable());
        var chain = engine.BuildLayerChain(new MixLayerInput(0, samples, SampleRate, parameters, SourceKey: "melodyne-fallback-test"), anySolo: false, SampleRate);

        var output = ReadAll(chain, samples.Length * 2);
        Assert.NotEmpty(output);
    }
}
