using System.Collections.Concurrent;

namespace Acapella.Engine.Mix;

/// <summary>
/// Caches AudioDecoder's full-file decode, keyed by (path, last-write-time, sample rate), so
/// repeated preview rebuilds (every debounced FX/trim edit triggers SetLayers -> Seek/Play, audit
/// B2) and export don't each re-run a full ffmpeg decode of unchanged source files. Trim/shift are
/// cheap array ops applied by callers on top of the cached raw decode.
/// </summary>
public static class AudioDecodeCache
{
    private readonly record struct CacheKey(string Path, long WriteTimeTicks, int SampleRate);

    private static readonly ConcurrentDictionary<CacheKey, float[]> Cache = new();

    public static float[] GetOrDecode(string mediaPath, int sampleRate, string ffmpegPath = "ffmpeg")
    {
        long mtimeTicks = File.Exists(mediaPath) ? File.GetLastWriteTimeUtc(mediaPath).Ticks : 0;
        var key = new CacheKey(mediaPath, mtimeTicks, sampleRate);
        return Cache.GetOrAdd(key, _ => AudioDecoder.DecodeToMonoFloat(mediaPath, sampleRate, ffmpegPath));
    }

    /// <summary>Test/diagnostic hook: not used by production code paths (mtime already
    /// invalidates stale entries on file change).</summary>
    public static void Clear() => Cache.Clear();
}
