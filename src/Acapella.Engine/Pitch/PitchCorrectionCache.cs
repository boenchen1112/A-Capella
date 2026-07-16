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
    private readonly record struct GroupKey(int LayerId, string BackendType);

    // Bug audit C2: whole-track float[] per (layer, sourceKey, backend); every trim/shift nudge is
    // a new key and old entries were never evicted -- a long session leaks hundreds of MB over
    // time. Keeping only the last few keys per (layer, backend) is enough to cover "the user is
    // nudging a trim handle back and forth" without unbounded growth.
    private const int MaxEntriesPerLayerAndBackend = 2;

    private static readonly ConcurrentDictionary<CacheKey, float[]> Cache = new();
    private static readonly ConcurrentDictionary<GroupKey, ConcurrentQueue<string>> RecentKeysByGroup = new();

    /// <summary>sourceKey should uniquely determine the exact input samples (e.g. source path +
    /// mtime + trim + shift). If null, the caller has no stable key to cache against and
    /// correction always runs fresh.</summary>
    public static float[] GetOrCorrect(IPitchCorrectionBackend backend, int layerId, string? sourceKey, float[] samples, int sampleRate)
    {
        if (sourceKey is null)
            return backend.Correct(samples, sampleRate);

        string backendType = backend.GetType().Name;
        var key = new CacheKey(layerId, sourceKey, backendType);
        bool isNewKey = !Cache.ContainsKey(key);
        var result = Cache.GetOrAdd(key, _ => backend.Correct(samples, sampleRate));

        if (isNewKey)
            TrackAndEvictOldest(new GroupKey(layerId, backendType), sourceKey);

        return result;
    }

    private static void TrackAndEvictOldest(GroupKey group, string sourceKey)
    {
        var recentKeys = RecentKeysByGroup.GetOrAdd(group, static _ => new ConcurrentQueue<string>());
        recentKeys.Enqueue(sourceKey);
        while (recentKeys.Count > MaxEntriesPerLayerAndBackend)
        {
            if (recentKeys.TryDequeue(out var oldestSourceKey))
                Cache.TryRemove(new CacheKey(group.LayerId, oldestSourceKey, group.BackendType), out _);
        }
    }
}
