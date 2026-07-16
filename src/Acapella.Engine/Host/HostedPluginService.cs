namespace Acapella.Engine.Host;

/// <summary>
/// Single hosting service for the whole app session (v7 Q0 task 1, fixing audit A1): owns the one
/// HostedPluginInstanceCache and IHostedPluginAvailability that MainWindow's launcher UI,
/// PreviewPlaybackEngine, and ExportEngine all consume. Before this, each of the three owned its
/// own MixEngine (and therefore its own cache), so the instance the "Open Pro-Q 4..." button
/// edited was never the instance actually processing audio -- tweaking a plugin was inaudible, and
/// export matched neither the editor nor the preview. One service, injected everywhere, means
/// GetOrCreateInstance(layerId, stage, ...) always returns the exact same live object no matter who
/// asks.
///
/// Every lifecycle call (scan/create/get-state/set-state/editor open-close/reset/release) marshals
/// through IHostedPluginDispatcher (A3/B8): production wires a WPF-dispatcher-backed
/// implementation so all JUCE calls land on the one thread that called
/// HostedPluginInstance.Initialize(), regardless of which thread (command thread, Task.Run,
/// threadpool) asked. ProcessBlock is the only call that stays off this thread, by design.
/// </summary>
public sealed class HostedPluginService : IDisposable
{
    private readonly HostedPluginInstanceCache _cache = new();
    private readonly IHostedPluginAvailability _availability;
    private readonly IHostedPluginDispatcher _dispatcher;
    private volatile bool _scanned;

    public HostedPluginService(IHostedPluginAvailability? availability = null, IHostedPluginDispatcher? dispatcher = null)
    {
        _availability = availability ?? new HostedPluginAvailability();
        _dispatcher = dispatcher ?? InlineHostedPluginDispatcher.Instance;
    }

    /// <summary>Runs the plugin-existence scan once, on the dispatcher thread (A3) -- call at app
    /// startup (off the UI thread's critical Play path, B2) so the first real chain build doesn't
    /// stall on it. Safe to call more than once or concurrently; only the first call scans.</summary>
    public void EnsureScanned()
    {
        if (_scanned) return;
        _dispatcher.Invoke(() =>
        {
            if (_scanned) return;
            foreach (var label in HostedPluginCatalog.KnownPluginPaths.Keys)
                _availability.IsAvailable(label);
            _scanned = true;
        });
    }

    public bool IsAvailable(string pluginLabel) => _availability.IsAvailable(pluginLabel);

    // v7 2A task 41: cached per plugin label -- TryScanAraCapability does its own file-system +
    // VST3-factory-metadata read each call, no need to repeat that per layer/per BuildLayerChain
    // call.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _araAvailability = new();

    /// <summary>True only if pluginLabel is both installed and reports an ARA factory (Melodyne's
    /// tier can be plain-VST3-only) -- the fallback-without-crash gate for the Manual2A pitch
    /// backend (task 41): BuildLayerChain checks this before ever touching AraHostSession, so a
    /// machine without an ARA-capable Melodyne install just silently skips pitch correction for
    /// that layer instead of throwing mid-chain-build.
    ///
    /// Bug audit C4: originally claimed safe from any thread on the theory that a factory-metadata
    /// read never touches JUCE's MessageManager -- true, but VST3PluginFormat::findAllTypesForFile
    /// still loads the plugin module to read it, which can race a UI-thread scan/load of the same
    /// binary. Routed through the dispatcher like every other scan in this class; the per-label
    /// cache means this only actually costs a dispatcher round-trip once per plugin label.</summary>
    public bool IsAraAvailable(string pluginLabel) => _araAvailability.GetOrAdd(pluginLabel, label =>
        _dispatcher.Invoke(() =>
            HostedPluginCatalog.KnownPluginPaths.TryGetValue(label, out var path)
            && HostedPluginInstance.TryScanAraCapability(path, out var description)
            && description is { IsAraCapable: true }));

    /// <summary>v7 2A task 39: generic passthrough onto the same dispatcher thread every other
    /// lifecycle call above uses -- for callers (MelodyneAraPitchCorrector) whose native work
    /// doesn't fit this class's existing per-stage cache/state methods but still must run on the
    /// one JUCE-initialized thread, not whatever thread happened to call in.</summary>
    public T RunOnHostedThread<T>(Func<T> func) => _dispatcher.Invoke(func);

    /// <summary>Void overload -- bug audit A4: the analysis-wait loop needs many short dispatcher
    /// round-trips (pump + check) rather than one call wrapping a whole blocking wait, so this
    /// exists to avoid every caller needing a throwaway return value.</summary>
    public void RunOnHostedThread(Action action) => _dispatcher.Invoke(action);

    /// <summary>Fetches (lazily creating, on the dispatcher thread) the single live instance for
    /// (layerId, stage) -- the same object whether the caller is the chain builder or the UI's
    /// "Open Pro-X..." launcher button.</summary>
    public HostedPluginInstance GetOrCreateInstance(int layerId, string stage, string pluginLabel, byte[]? initialState, int sampleRate = 44100) =>
        _dispatcher.Invoke(() => _cache.GetOrCreate(layerId, stage, HostedPluginCatalog.KnownPluginPaths[pluginLabel], sampleRate, Mix.HostedPluginSampleProvider.DefaultBlockSize, initialState));

    /// <summary>Non-creating lookup (v7 Q0 task 3, audit B7) -- null if no instance has been
    /// created yet for (layerId, stage) (e.g. the user never opened that stage's editor and it
    /// isn't in the chain). Used to push a loaded/restored state into an already-live instance
    /// without instantiating a plugin nobody has touched.</summary>
    public HostedPluginInstance? TryGetLiveInstance(int layerId, string stage) =>
        _dispatcher.Invoke(() => _cache.TryGet(layerId, stage, out var instance) ? instance : null);

    /// <summary>Clears a live instance's internal DSP state (v7 Q0 task 5, audit A5) -- call once
    /// per cached instance wired into a freshly built chain, so a second/subsequent Play doesn't
    /// bleed the previous run's buffered audio into the new one.</summary>
    public void Reset(HostedPluginInstance instance) => _dispatcher.Invoke(instance.Reset);

    /// <summary>Pulls the live, current VST3 state chunk (v7 Q0 task 3, audit A2) -- call at the
    /// moments that matter: editor close, project save, export snapshot, undo snapshot capture.</summary>
    public byte[] PullLiveState(HostedPluginInstance instance) => _dispatcher.Invoke(instance.GetState);

    /// <summary>Pushes a state blob into a live instance (v7 Q0 task 3, audit B7) -- call on
    /// project load / undo-redo restore so an already-live instance picks up the loaded/restored
    /// state instead of keeping whatever it had before (GetOrCreateInstance's initialState only
    /// ever applies at first creation).</summary>
    public void PushState(HostedPluginInstance instance, byte[] state) => _dispatcher.Invoke(() => instance.SetState(state));

    public bool ShowEditor(HostedPluginInstance instance, string title) => _dispatcher.Invoke(() => instance.ShowEditorWindow(title));

    public void CloseEditor(HostedPluginInstance instance) => _dispatcher.Invoke(instance.CloseEditorWindow);

    /// <summary>Releases and forgets a single (layerId, stage) instance, e.g. on layer removal.</summary>
    public void Release(int layerId, string stage) => _dispatcher.Invoke(() => _cache.Release(layerId, stage));

    public void Dispose() => _dispatcher.Invoke(_cache.Dispose);
}
