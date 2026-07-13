namespace Acapella.Engine.Host;

/// <summary>
/// Reports which of the hard-limited hosted plugin vendors (FabFilter Pro-Q 4/Pro-C 3/Pro-L 2/
/// Pro-G/Pro-R 2, Melodyne) are actually installed on this machine, so chain-building can
/// auto-select Hosted vs. Native per v6 P3 task 1 without re-scanning the VST3 folder on every
/// mix rebuild. Labels match HostedPluginCatalog.KnownPluginPaths' keys exactly.
/// </summary>
public interface IHostedPluginAvailability
{
    bool IsAvailable(string pluginLabel);
}

/// <summary>Scans HostedPluginCatalog's known paths once (lazily, on first query) and caches the
/// per-plugin found/not-found result for this instance's lifetime -- plugin installs don't change
/// mid-session.</summary>
public sealed class HostedPluginAvailability : IHostedPluginAvailability
{
    private readonly Lazy<IReadOnlyDictionary<string, bool>> _scanResults = new(() =>
        HostedPluginCatalog.KnownPluginPaths.ToDictionary(
            kv => kv.Key,
            kv => HostedPluginInstance.TryScan(kv.Value, out _)));

    public bool IsAvailable(string pluginLabel) =>
        _scanResults.Value.TryGetValue(pluginLabel, out var found) && found;
}

/// <summary>Always reports every plugin unavailable. Used to force native-only processing --
/// production code that explicitly wants no hosted stages, and unit tests whose assertions target
/// MixEngine's own mechanics (gain/pan/headroom/master volume) rather than hosted-plugin behavior,
/// so those tests stay deterministic regardless of what happens to be installed on the machine
/// running them.</summary>
public sealed class NoHostedPluginsAvailable : IHostedPluginAvailability
{
    public static readonly NoHostedPluginsAvailable Instance = new();

    public bool IsAvailable(string pluginLabel) => false;
}
