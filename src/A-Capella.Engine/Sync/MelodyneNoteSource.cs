using System.Collections.Concurrent;
using A_Capella.Engine.Host;

namespace A_Capella.Engine.Sync;

/// <summary>
/// Finds the notes in a recording by letting Melodyne analyse it in a throwaway ARA session (no
/// correction is applied and no editor is opened). Returns null when Melodyne is missing, has no ARA
/// support, takes too long, or finds nothing, so callers can fall back to loudness-based timing.
/// Results are kept per cache key, so one reference track is analysed once however many tracks are
/// aligned to it.
/// </summary>
public sealed class MelodyneNoteSource : INoteSource
{
    private const string PluginLabel = "Melodyne";
    private const int AnalysisTimeoutMs = 60_000;

    private readonly HostedPluginService _hostedService;
    private readonly ConcurrentDictionary<string, IReadOnlyList<DetectedNote>> _cache = new();
    private readonly object _analysisLock = new();
    private int _nextSessionId;

    public MelodyneNoteSource(HostedPluginService hostedService) => _hostedService = hostedService;

    public bool IsAvailable => _hostedService.IsAraAvailable(PluginLabel);

    public IReadOnlyList<DetectedNote>? DetectNotes(float[] monoSamples, int sampleRate, string cacheKey)
    {
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        if (monoSamples.Length == 0 || !IsAvailable) return null;

        try
        {
            // One analysis at a time: two layers aligned together would otherwise both analyse the reference.
            lock (_analysisLock)
            {
                if (_cache.TryGetValue(cacheKey, out cached)) return cached;
                var notes = Analyse(monoSamples, sampleRate);
                if (notes is { Count: > 0 })
                    _cache[cacheKey] = notes;
                return notes;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any plugin trouble means no notes; the caller falls back to loudness timing.
            return null;
        }
    }

    // TODO(polish): session setup and polling run on the UI dispatcher; move to a dedicated thread if it stutters.
    private IReadOnlyList<DetectedNote>? Analyse(float[] samples, int sampleRate)
    {
        string path = HostedPluginCatalog.KnownPluginPaths[PluginLabel];
        string persistentId = $"a-capella-align-{Interlocked.Increment(ref _nextSessionId)}";

        var (session, source) = _hostedService.RunOnHostedThread(() =>
        {
            var created = AraHostSession.Create(path, sampleRate, 512);
            try
            {
                var handle = created.RegisterAudioSource(new[] { samples }, samples.Length, sampleRate, persistentId);
                created.AddPlaybackRegion(handle);
                return (created, handle);
            }
            catch
            {
                created.Dispose();
                throw;
            }
        });

        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(AnalysisTimeoutMs);
            while (true)
            {
                float progress = _hostedService.RunOnHostedThread(() =>
                {
                    session.PumpModelUpdates();
                    return session.GetAnalysisProgress(source);
                });
                if (progress >= 1.0f) break;
                if (DateTime.UtcNow >= deadline) return null;
                Thread.Sleep(20);
            }

            return _hostedService.RunOnHostedThread(() => session.ReadNotes(source));
        }
        finally
        {
            _hostedService.RunOnHostedThread(session.Dispose);
        }
    }
}
