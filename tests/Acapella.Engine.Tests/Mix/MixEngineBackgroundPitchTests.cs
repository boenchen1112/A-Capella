using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Pitch;

namespace Acapella.Engine.Tests.Mix;

/// <summary>Covers MixEngine.NonBlockingPitchCorrection (background pitch rendering): a cache miss
/// must not stall BuildLayerChain's caller (the preview's command thread) the way the always-
/// blocking default does -- see PitchCorrectionCacheTests for the cache-level dedup/failure
/// behavior this builds on.</summary>
public class MixEngineBackgroundPitchTests
{
    // Each test gets its own layerId so the static PitchCorrectionCache's eviction groups (keyed
    // by layerId+backendType) can't collide with other tests running in parallel.
    private static int _nextLayerId = 950_000;
    private static int NextLayerId() => Interlocked.Increment(ref _nextLayerId);

    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, int length, float amplitude = 0.5f)
    {
        var samples = new float[length];
        for (int i = 0; i < length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
        return samples;
    }

    private static float[] ReadAll(NAudio.Wave.ISampleProvider provider, int count)
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

    /// <summary>Returns zeros once gate is set, so a chain built from its output is distinguishable
    /// from the sine-wave input by energy alone -- avoids asserting exact sample values, which the
    /// chain's pan/gain stages are free to scale.</summary>
    private class GatedZeroBackend : IPitchCorrectionBackend
    {
        private readonly ManualResetEventSlim _gate;
        public int CallCount;
        public GatedZeroBackend(ManualResetEventSlim gate) => _gate = gate;

        public float[] Correct(float[] samples, int sampleRate)
        {
            Interlocked.Increment(ref CallCount);
            _gate.Wait();
            return new float[samples.Length];
        }
    }

    [Fact]
    public void BuildLayerChain_NonBlockingMode_PlaysUncorrectedOnMissThenCorrectedAfterCompletion()
    {
        int layerId = NextLayerId();
        int sampleRate = 44100;
        var samples = GenerateSineWave(440, sampleRate, sampleRate);
        string sourceKey = $"key-{Guid.NewGuid()}";
        var gate = new ManualResetEventSlim(false);
        var fakeBackend = new GatedZeroBackend(gate);

        using var engine = new MixEngine(NoHostedPluginsAvailable.Instance)
        {
            NonBlockingPitchCorrection = true,
            AutomaticBackendOverride = fakeBackend,
        };

        int startedCount = 0, completedCount = 0;
        engine.PitchRenderStarted += id => { if (id == layerId) Interlocked.Increment(ref startedCount); };
        engine.PitchRenderCompleted += (id, _) => { if (id == layerId) Interlocked.Increment(ref completedCount); };

        var parameters = new LayerMixParameters { PitchBackend = PitchBackendSelection.Automatic2B };
        var layerInput = new MixLayerInput(layerId, samples, sampleRate, parameters, sourceKey);

        // Cache miss: must return immediately, playing the layer's uncorrected (nonzero) samples,
        // not block on the gated render.
        var uncorrectedChain = engine.BuildLayerChain(layerInput, anySolo: false, sampleRate);
        var uncorrectedOutput = ReadAll(uncorrectedChain, 2000);
        Assert.Contains(uncorrectedOutput, s => s != 0f);
        Assert.True(engine.HasPendingPitchRender);
        Assert.Equal(1, startedCount);

        // Let the render finish, then wait for the cache to actually observe it (the render
        // completes on a worker thread, not synchronously with gate.Set()).
        gate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!PitchCorrectionCache.TryGetCached(layerId, sourceKey, fakeBackend.GetType().Name, out _) && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.True(PitchCorrectionCache.TryGetCached(layerId, sourceKey, fakeBackend.GetType().Name, out _), "Render never completed.");

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (engine.HasPendingPitchRender && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.False(engine.HasPendingPitchRender);
        Assert.Equal(1, completedCount);
        Assert.Equal(1, fakeBackend.CallCount);

        // Same key, rebuilt: now a cache hit, so the corrected (all-zero) samples play.
        var correctedChain = engine.BuildLayerChain(layerInput, anySolo: false, sampleRate);
        var correctedOutput = ReadAll(correctedChain, 2000);
        Assert.All(correctedOutput, s => Assert.Equal(0f, s));
    }
}
