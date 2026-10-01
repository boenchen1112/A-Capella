using A_Capella.Engine.Pitch;

namespace A_Capella.Engine.Tests.Pitch;

public class PitchCorrectionCacheTests
{
    // Every test gets its own layerId (as well as its own sourceKey/backendType, where the test
    // needs one) so the static cache's eviction groups (keyed by layerId+backendType, capped at 2
    // entries) can never collide between tests running in parallel.
    private static int _nextLayerId = 900_000;
    private static int NextLayerId() => Interlocked.Increment(ref _nextLayerId);

    private class CountingBackend : IPitchCorrectionBackend
    {
        public int CallCount;

        public float[] Correct(float[] samples, int sampleRate)
        {
            CallCount++;
            return samples;
        }
    }

    /// <summary>Regression test for audit B3: a second correction request for the same
    /// (layerId, sourceKey, backend) must reuse the cached result instead of re-running
    /// YIN + Rubber Band, which used to happen on every debounced mix rebuild.</summary>
    [Fact]
    public void GetOrCorrect_SecondCallWithSameSourceKey_ReusesCachedResultWithoutRecomputing()
    {
        var backend = new CountingBackend();
        float[] samples = { 0.1f, 0.2f, 0.3f };
        string sourceKey = $"key-{Guid.NewGuid()}";

        var first = PitchCorrectionCache.GetOrCorrect(backend, layerId: 0, sourceKey, samples, 44100);
        var second = PitchCorrectionCache.GetOrCorrect(backend, layerId: 0, sourceKey, samples, 44100);

        Assert.Equal(1, backend.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrCorrect_NullSourceKey_AlwaysRecomputes()
    {
        var backend = new CountingBackend();
        float[] samples = { 0.1f, 0.2f, 0.3f };

        PitchCorrectionCache.GetOrCorrect(backend, layerId: 0, null, samples, 44100);
        PitchCorrectionCache.GetOrCorrect(backend, layerId: 0, null, samples, 44100);

        Assert.Equal(2, backend.CallCount);
    }

    [Fact]
    public void GetOrCorrect_DifferentSourceKey_Recomputes()
    {
        var backend = new CountingBackend();
        float[] samples = { 0.1f, 0.2f, 0.3f };
        string baseKey = $"key-{Guid.NewGuid()}";

        PitchCorrectionCache.GetOrCorrect(backend, layerId: 0, baseKey + "-a", samples, 44100);
        PitchCorrectionCache.GetOrCorrect(backend, layerId: 0, baseKey + "-b", samples, 44100);

        Assert.Equal(2, backend.CallCount);
    }

    /// <summary>ConcurrentDictionary.GetOrAdd can run its factory more than once under contention,
    /// which for the Melodyne path would mean two concurrent renders against one ARA session.
    /// GetOrStartCorrect must hand out the same in-flight task to every concurrent caller for a key
    /// instead.</summary>
    [Fact]
    public async Task GetOrStartCorrect_ConcurrentRequestsForSameKey_RunsCorrectionOnce()
    {
        int layerId = NextLayerId();
        string sourceKey = $"key-{Guid.NewGuid()}";
        string backendType = $"Backend-{Guid.NewGuid()}";
        int callCount = 0;
        var gate = new ManualResetEventSlim(false);
        var expected = new float[] { 9f };

        float[] Correct()
        {
            Interlocked.Increment(ref callCount);
            gate.Wait();
            return expected;
        }

        var task1 = PitchCorrectionCache.GetOrStartCorrect(layerId, sourceKey, backendType, Correct);
        var task2 = PitchCorrectionCache.GetOrStartCorrect(layerId, sourceKey, backendType, Correct);
        gate.Set();

        var result1 = await task1;
        var result2 = await task2;

        Assert.Equal(1, callCount);
        Assert.Same(expected, result1);
        Assert.Same(result1, result2);
    }

    /// <summary>The other half of the same hazard: a blocking caller (export) that hits a key
    /// already being rendered in the background must wait for that same task rather than starting a
    /// second render.</summary>
    [Fact]
    public void GetOrCorrect_WhileRenderInFlight_WaitsForSameRenderInsteadOfStartingAnother()
    {
        int layerId = NextLayerId();
        string sourceKey = $"key-{Guid.NewGuid()}";
        string backendType = $"Backend-{Guid.NewGuid()}";
        int callCount = 0;
        var gate = new ManualResetEventSlim(false);
        var expected = new float[] { 7f };

        float[] Correct()
        {
            Interlocked.Increment(ref callCount);
            gate.Wait();
            return expected;
        }

        // Simulates the preview's non-blocking path already having kicked this render off.
        PitchCorrectionCache.GetOrStartCorrect(layerId, sourceKey, backendType, Correct);
        _ = Task.Run(() => { Thread.Sleep(50); gate.Set(); });

        var result = PitchCorrectionCache.GetOrCorrect(layerId, sourceKey, backendType, Correct);

        Assert.Equal(1, callCount);
        Assert.Same(expected, result);
    }

    /// <summary>A failed render must not be cached, and must not permanently wedge the key either --
    /// the next request (blocking here) retries it.</summary>
    [Fact]
    public void GetOrCorrect_RenderThrows_IsNotCachedAndRetryRunsAgain()
    {
        int layerId = NextLayerId();
        string sourceKey = $"key-{Guid.NewGuid()}";
        string backendType = $"Backend-{Guid.NewGuid()}";
        int attempt = 0;

        float[] Correct()
        {
            attempt++;
            if (attempt == 1) throw new InvalidOperationException("render failed");
            return new float[] { 3f };
        }

        Assert.Throws<InvalidOperationException>(() => PitchCorrectionCache.GetOrCorrect(layerId, sourceKey, backendType, Correct));
        Assert.False(PitchCorrectionCache.TryGetCached(layerId, sourceKey, backendType, out _));

        var result = PitchCorrectionCache.GetOrCorrect(layerId, sourceKey, backendType, Correct);

        Assert.Equal(2, attempt);
        Assert.Equal(3f, result[0]);
    }
}
