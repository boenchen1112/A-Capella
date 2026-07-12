using System.Collections.Concurrent;

namespace Acapella.Engine.Pitch;

/// <summary>
/// Caches AutoPitchCorrector's YIN-detect + Rubber Band correction, keyed by (layerId, a caller-
/// supplied source key covering everything that changes the input samples, backend type). Automatic
/// pitch correction re-ran over the full track on every mix rebuild -- every debounced FX/trim
/// slider move (audit B3) -- which is seconds of stall since none of it was cached. FX parameters
/// downstream of pitch correction in the chain (noise gate, EQ, pan, gain, dynamics) don't affect
/// this cache; only a source or trim/shift change (which the caller folds into the source key)
/// invalidates it.
/// </summary>
public static class PitchCorrectionCache
{
    private readonly record struct CacheKey(int LayerId, string SourceKey, string BackendType);

    private static readonly ConcurrentDictionary<CacheKey, float[]> Cache = new();

    /// <summary>sourceKey should uniquely determine the exact input samples (e.g. source path +
    /// mtime + trim + shift). If null, the caller has no stable key to cache against and
    /// correction always runs fresh.</summary>
    public static float[] GetOrCorrect(IPitchCorrectionBackend backend, int layerId, string? sourceKey, float[] samples, int sampleRate)
    {
        if (sourceKey is null)
            return backend.Correct(samples, sampleRate);

        var key = new CacheKey(layerId, sourceKey, backend.GetType().Name);
        return Cache.GetOrAdd(key, _ => backend.Correct(samples, sampleRate));
    }
}
