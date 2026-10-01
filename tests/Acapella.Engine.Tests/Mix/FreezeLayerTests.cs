using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Persistence;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

/// <summary>Covers LayerMixParameters.Frozen: a frozen layer plays a pre-rendered buffer of its
/// whole chain instead of running it live. Every engine here is built with NoHostedPluginsAvailable
/// (native slots), like MixEngineTests -- see the build plan's hosted-parity note for why a real
/// FabFilter plugin isn't exercised here.</summary>
public class FreezeLayerTests
{
    // Each test gets its own layerId so the static frozen-buffer cache (keyed by layerId, shared
    // across every MixEngine instance) can't collide with another test running in parallel.
    private static int _nextLayerId = 980_000;
    private static int NextLayerId() => Interlocked.Increment(ref _nextLayerId);

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
    public void FrozenLayer_MatchesUnfrozenChain_SampleForSample()
    {
        int sampleRate = 44100;
        int layerId = NextLayerId();
        var samples = GenerateSineWave(440, sampleRate, sampleRate);
        string sourceKey = $"key-{layerId}";

        var parameters = new LayerMixParameters
        {
            GainDb = -6f,
            Pan = 0.3f,
            NoiseGateEnabled = true,
            CompressorEnabled = true,
            EqEnabled = true,
            LowShelfGainDb = 2f,
            LimiterEnabled = true,
        };

        using var liveEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var liveInput = new MixLayerInput(layerId, samples, sampleRate, parameters, sourceKey);
        var liveChain = liveEngine.BuildLayerChain(liveInput, anySolo: false, sampleRate);
        var liveOutput = ReadAll(liveChain, sampleRate * 2);

        var frozenParams = parameters.Clone();
        frozenParams.Frozen = true;
        using var frozenEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
        var frozenInput = new MixLayerInput(layerId, samples, sampleRate, frozenParams, sourceKey);
        var frozenChain = frozenEngine.BuildLayerChain(frozenInput, anySolo: false, sampleRate);
        var frozenOutput = ReadAll(frozenChain, sampleRate * 2);

        Assert.Equal(liveOutput.Length, frozenOutput.Length);
        for (int i = 0; i < liveOutput.Length; i++)
            Assert.Equal(liveOutput[i], frozenOutput[i], 5);
    }

    [Fact]
    public void FrozenLayer_MuteAndSolo_StillApplyLive()
    {
        int sampleRate = 44100;
        int layerId = NextLayerId();
        var samples = GenerateSineWave(440, sampleRate, sampleRate);
        string sourceKey = $"key-{layerId}";

        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);

        var muted = new LayerMixParameters { Frozen = true, Mute = true };
        var mutedChain = engine.BuildLayerChain(new MixLayerInput(layerId, samples, sampleRate, muted, sourceKey), anySolo: false, sampleRate);
        var mutedOutput = ReadAll(mutedChain, 2000);
        Assert.All(mutedOutput, s => Assert.Equal(0f, s));

        var notSoloed = new LayerMixParameters { Frozen = true, Solo = false };
        var soloedOutChain = engine.BuildLayerChain(new MixLayerInput(layerId, samples, sampleRate, notSoloed, sourceKey), anySolo: true, sampleRate);
        var soloedOutOutput = ReadAll(soloedOutChain, 2000);
        Assert.All(soloedOutOutput, s => Assert.Equal(0f, s));

        var soloed = new LayerMixParameters { Frozen = true, Solo = true };
        var soloedChain = engine.BuildLayerChain(new MixLayerInput(layerId, samples, sampleRate, soloed, sourceKey), anySolo: true, sampleRate);
        var soloedOutput = ReadAll(soloedChain, 2000);
        Assert.Contains(soloedOutput, s => s != 0f);
    }

    [Fact]
    public void FrozenLayer_ReusesCache_UntilABakedParameterChanges()
    {
        int sampleRate = 44100;
        int layerId = NextLayerId();
        var samples = GenerateSineWave(440, sampleRate, sampleRate);
        string sourceKey = $"key-{layerId}";

        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);
        int renderCount = 0;
        engine.FreezeRenderStarted += id => { if (id == layerId) renderCount++; };

        var parameters = new LayerMixParameters { Frozen = true, GainDb = -3f };
        var input = new MixLayerInput(layerId, samples, sampleRate, parameters, sourceKey);

        engine.BuildLayerChain(input, anySolo: false, sampleRate);
        Assert.Equal(1, renderCount);

        // Same key, rebuilt again (e.g. a Mute toggle elsewhere triggering a refresh): must reuse
        // the cached buffer, not re-render.
        engine.BuildLayerChain(input, anySolo: false, sampleRate);
        Assert.Equal(1, renderCount);

        // Mute/Solo are excluded from the key -- flipping them must not force a re-render either.
        var muteToggled = new LayerMixParameters { Frozen = true, GainDb = -3f, Mute = true };
        engine.BuildLayerChain(new MixLayerInput(layerId, samples, sampleRate, muteToggled, sourceKey), anySolo: false, sampleRate);
        Assert.Equal(1, renderCount);

        // A baked parameter (GainDb) changing must force a re-render.
        var gainChanged = new LayerMixParameters { Frozen = true, GainDb = -9f };
        engine.BuildLayerChain(new MixLayerInput(layerId, samples, sampleRate, gainChanged, sourceKey), anySolo: false, sampleRate);
        Assert.Equal(2, renderCount);
    }

    [Fact]
    public void FrozenLayer_NullSourceKey_RendersEveryTime()
    {
        int sampleRate = 44100;
        int layerId = NextLayerId();
        var samples = GenerateSineWave(440, sampleRate, sampleRate);

        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance);
        int renderCount = 0;
        engine.FreezeRenderStarted += id => { if (id == layerId) renderCount++; };

        var parameters = new LayerMixParameters { Frozen = true };
        var input = new MixLayerInput(layerId, samples, sampleRate, parameters, SourceKey: null);

        engine.BuildLayerChain(input, anySolo: false, sampleRate);
        engine.BuildLayerChain(input, anySolo: false, sampleRate);

        Assert.Equal(2, renderCount);
    }

    [Fact]
    public void ProjectFileDto_RoundTripsFrozenFlag()
    {
        var persistence = new ProjectPersistenceService();
        var layers = new Acapella.Engine.Project.LayerCollection();
        var layer = new Acapella.Engine.Project.LayerModel
        {
            LayerId = 0,
            Kind = Acapella.Engine.Project.LayerKind.UploadedVideo,
            SourcePath = "x.mp4",
        };
        layer.MixParameters.Frozen = true;
        layers.Restore(new[] { layer });

        var dto = persistence.ToDto(layers, 120, null);
        Assert.True(dto.Layers[0].MixParameters.Frozen);

        var (restored, _, _, _) = persistence.FromDto(dto);
        Assert.True(restored.Layers[0].MixParameters.Frozen);
    }

    [Fact]
    public void ProjectFileDto_MissingFrozenField_LoadsAsNotFrozen()
    {
        // Simulates an old project file saved before Frozen existed: the DTO's default (false)
        // applies, same as how NoiseGateEnabled's own old-file migration note describes absence.
        var dto = new MixParametersDto();
        Assert.False(dto.Frozen);
    }
}
