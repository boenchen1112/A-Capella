using Acapella.Engine.Pitch;

namespace Acapella.Engine.Tests.Pitch;

public class PitchCorrectionCacheTests
{
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
}
