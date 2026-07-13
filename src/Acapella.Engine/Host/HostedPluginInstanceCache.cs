using System.Collections.Concurrent;

namespace Acapella.Engine.Host;

/// <summary>
/// Owns one HostedPluginInstance per (layerId, stage) -- lazily created on first chain build,
/// reused across debounced preview rebuilds so a live plugin editor (deferred task 7) and any
/// internal processing state survive a re-render (mirrors PitchCorrectionCache's reuse pattern
/// from P0, but this cache owns real unmanaged handles so it must be disposed, unlike that one).
/// </summary>
public sealed class HostedPluginInstanceCache : IDisposable
{
    private readonly record struct Key(int LayerId, string Stage);

    private readonly ConcurrentDictionary<Key, HostedPluginInstance> _instances = new();

    /// <summary>Creates the instance on first call for this (layerId, stage) pair, applying
    /// initialState if given; subsequent calls for the same pair return the same live instance
    /// regardless of initialState (state is only ever applied once, at creation -- a caller that
    /// wants to push a state update to an already-live instance should call SetState on the
    /// returned instance directly).</summary>
    public HostedPluginInstance GetOrCreate(int layerId, string stage, string pluginPath, double sampleRate, int maxBlockSize, byte[]? initialState)
    {
        return _instances.GetOrAdd(new Key(layerId, stage), _ =>
        {
            var instance = HostedPluginInstance.Create(pluginPath, sampleRate, maxBlockSize);
            if (initialState is { Length: > 0 })
                instance.SetState(initialState);
            return instance;
        });
    }

    /// <summary>Releases and forgets a single (layerId, stage) instance, e.g. on layer removal.</summary>
    public void Release(int layerId, string stage)
    {
        if (_instances.TryRemove(new Key(layerId, stage), out var instance))
            instance.Dispose();
    }

    public void Dispose()
    {
        foreach (var instance in _instances.Values)
            instance.Dispose();
        _instances.Clear();
    }
}
