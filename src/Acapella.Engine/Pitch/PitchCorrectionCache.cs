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
///
/// A render for a given key only ever runs once at a time, no matter how many callers ask for it
/// concurrently: GetOrCorrect (blocking -- export, and any caller that doesn't opt into background
/// rendering) and GetOrStartCorrect (non-blocking -- MixEngine's preview path) race for ownership of
/// the same per-key TaskCompletionSource in InFlight; whichever wins actually calls correct(), and
/// everyone else waits on/awaits that same TCS's Task instead of starting a second render. A failed
/// render is dropped from the in-flight table without being cached, so the next request (blocking or
/// not) retries it.
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

    // Per-key renders currently running, as a TaskCompletionSource each caller races to own.
    // ConcurrentDictionary.GetOrAdd(key, factory) isn't used here because its factory can run more
    // than once under contention -- for the Melodyne path that would mean two concurrent renders
    // against one ARA session. GetOrAdd(key, value) with a plain value (a TCS, which does no work
    // on construction) has no such hazard: exactly one caller's TCS ends up stored for a given key,
    // and that caller -- found by reference-comparing what GetOrAdd handed back against its own TCS
    // -- is the one who actually runs correct(). Everyone else just awaits/waits on the stored TCS's
    // Task, which the owner completes when it's done.
    private static readonly ConcurrentDictionary<CacheKey, TaskCompletionSource<float[]>> InFlight = new();

    /// <summary>sourceKey should uniquely determine the exact input samples (e.g. source path +
    /// mtime + trim + shift). If null, the caller has no stable key to cache against and
    /// correction always runs fresh.</summary>
    public static float[] GetOrCorrect(IPitchCorrectionBackend backend, int layerId, string? sourceKey, float[] samples, int sampleRate) =>
        GetOrCorrect(layerId, sourceKey, backend.GetType().Name, () => backend.Correct(samples, sampleRate));

    /// <summary>Same caching shape as the overload above, but for callers (the Manual2A/Melodyne
    /// path) whose correction doesn't fit IPitchCorrectionBackend.Correct's single
    /// (samples, sampleRate) signature -- e.g. it needs the untrimmed source plus a trim window, or
    /// post-processing (shift) applied after the correction itself.
    ///
    /// Blocks until the result is ready. If nobody else is rendering this key yet, correct() runs
    /// synchronously, inline, on the calling thread -- exactly as before this cache had a
    /// background-rendering mode, so export (and every other caller that doesn't opt into it) pays
    /// no extra thread-pool hop. If a background render (GetOrStartCorrect) already owns this key,
    /// this just waits for that same render instead of starting a second one.</summary>
    public static float[] GetOrCorrect(int layerId, string? sourceKey, string backendType, Func<float[]> correct)
    {
        if (sourceKey is null)
            return correct();

        var key = new CacheKey(layerId, sourceKey, backendType);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var mine = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = InFlight.GetOrAdd(key, mine);
        if (!ReferenceEquals(owner, mine))
            return owner.Task.GetAwaiter().GetResult(); // someone else owns this render -- join it

        return RunOwnedInline(key, mine, correct);
    }

    /// <summary>Non-blocking peek: returns the cached result for this key without starting or
    /// waiting on a render.</summary>
    public static bool TryGetCached(int layerId, string? sourceKey, string backendType, out float[] result)
    {
        if (sourceKey is not null && Cache.TryGetValue(new CacheKey(layerId, sourceKey, backendType), out var cached))
        {
            result = cached;
            return true;
        }
        result = Array.Empty<float>();
        return false;
    }

    /// <summary>Starts (or joins) a background render for this key and returns immediately with the
    /// shared Task&lt;float[]&gt; -- the caller is expected to use a fallback (e.g. the layer's
    /// uncorrected samples) until it completes, and to attach its own continuation if it needs to
    /// know when that happens. Never runs the same key's render twice: a concurrent GetOrCorrect or
    /// GetOrStartCorrect call for the same key joins this same render instead of starting another
    /// one.</summary>
    public static Task<float[]> GetOrStartCorrect(int layerId, string sourceKey, string backendType, Func<float[]> correct)
    {
        var key = new CacheKey(layerId, sourceKey, backendType);
        if (Cache.TryGetValue(key, out var cached))
            return Task.FromResult(cached);

        var mine = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = InFlight.GetOrAdd(key, mine);
        if (!ReferenceEquals(owner, mine))
            return owner.Task;

        Task.Run(() =>
        {
            try { RunOwnedInline(key, mine, correct); }
            catch
            {
                // Already recorded on mine's Task via SetException inside RunOwnedInline (that's
                // what every waiter actually observes) -- swallowed here only so a failure doesn't
                // also fault this discarded Task.Run task, which nothing awaits.
            }
        });
        return mine.Task;
    }

    /// <summary>Runs correct() for the key this TCS just won ownership of, on whatever thread calls
    /// this (the calling thread itself for a blocking owner, a thread-pool thread for a background
    /// one -- see the two callers above). Publishes into Cache and completes/clears the in-flight
    /// entry before returning, so anyone unblocking right after (whether from mine.Task or from this
    /// method's own return value) sees that state already settled: a failure is never cached, and
    /// InFlight is already clear for a retry.</summary>
    private static float[] RunOwnedInline(CacheKey key, TaskCompletionSource<float[]> mine, Func<float[]> correct)
    {
        // A render for this exact key can finish, publish to Cache and leave InFlight entirely
        // between this owner's caller checking Cache and winning ownership above -- honor that
        // instead of rendering a second time.
        if (Cache.TryGetValue(key, out var raced))
        {
            InFlight.TryRemove(new KeyValuePair<CacheKey, TaskCompletionSource<float[]>>(key, mine));
            mine.SetResult(raced);
            return raced;
        }

        try
        {
            var result = correct();
            Cache[key] = result;
            TrackAndEvictOldest(new GroupKey(key.LayerId, key.BackendType), key.SourceKey);
            mine.SetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            // Never cached on failure -- only the in-flight entry is cleared, so the next request
            // (blocking or background) retries rather than replaying a stale failure.
            mine.SetException(ex);
            throw;
        }
        finally
        {
            InFlight.TryRemove(new KeyValuePair<CacheKey, TaskCompletionSource<float[]>>(key, mine));
        }
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
