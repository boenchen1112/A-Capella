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
    private readonly HostedPluginInstanceCache _cache;
    private readonly IHostedPluginAvailability _availability;
    private readonly IHostedPluginDispatcher _dispatcher;
    private volatile bool _scanned;

    /// <summary>factory defaults to the native JUCE bridge; tests pass a fake so hosted-chain
    /// behavior runs without the native DLL or installed plugins.</summary>
    public HostedPluginService(IHostedPluginAvailability? availability = null, IHostedPluginDispatcher? dispatcher = null, IHostedPluginFactory? factory = null)
    {
        _cache = new HostedPluginInstanceCache(factory ?? NativeHostedPluginFactory.Instance);
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
    public IHostedPlugin GetOrCreateInstance(int layerId, string stage, string pluginLabel, byte[]? initialState, int sampleRate = 44100) =>
        _dispatcher.Invoke(() => _cache.GetOrCreate(layerId, stage, pluginLabel, sampleRate, Mix.HostedPluginSampleProvider.DefaultBlockSize, initialState));

    /// <summary>Non-creating lookup (v7 Q0 task 3, audit B7) -- null if no instance has been
    /// created yet for (layerId, stage) (e.g. the user never opened that stage's editor and it
    /// isn't in the chain). Used to push a loaded/restored state into an already-live instance
    /// without instantiating a plugin nobody has touched.</summary>
    public IHostedPlugin? TryGetLiveInstance(int layerId, string stage) =>
        _dispatcher.Invoke(() => _cache.TryGet(layerId, stage, out var instance) ? instance : null);

    /// <summary>Clears a live instance's internal DSP state (v7 Q0 task 5, audit A5) -- call once
    /// per cached instance wired into a freshly built chain, so a second/subsequent Play doesn't
    /// bleed the previous run's buffered audio into the new one.</summary>
    public void Reset(IHostedPlugin instance) => _dispatcher.Invoke(instance.Reset);

    /// <summary>Pulls the live, current VST3 state chunk (v7 Q0 task 3, audit A2) -- call at the
    /// moments that matter: editor close, project save, export snapshot, undo snapshot capture.</summary>
    public byte[] PullLiveState(IHostedPlugin instance) => _dispatcher.Invoke(instance.GetState);

    /// <summary>Pushes a state blob into a live instance (v7 Q0 task 3, audit B7) -- call on
    /// project load / undo-redo restore so an already-live instance picks up the loaded/restored
    /// state instead of keeping whatever it had before (GetOrCreateInstance's initialState only
    /// ever applies at first creation).</summary>
    public void PushState(IHostedPlugin instance, byte[] state) => _dispatcher.Invoke(() => instance.SetState(state));

    public bool ShowEditor(IHostedPlugin instance, string title) => _dispatcher.Invoke(() => instance.ShowEditorWindow(title));

    public void CloseEditor(IHostedPlugin instance) => _dispatcher.Invoke(instance.CloseEditorWindow);

    /// <summary>Releases and forgets a single (layerId, stage) instance, e.g. on layer removal.</summary>
    public void Release(int layerId, string stage) => _dispatcher.Invoke(() => _cache.Release(layerId, stage));

    // Bug audit A1: persistent per-layer ARA session, mirroring _cache's per-(layerId, stage)
    // model. Unlike a plain hosted instance, an ARA session's audio source must be registered with
    // real content before it's useful -- there's no "blank" session to create ahead of time -- so
    // these are populated by GetOrCreateAraLayerSource (called from MixEngine's pitch stage on
    // every BuildLayerChain), not eagerly. Never released mid-session: this project has no layer-
    // removal feature yet (confirmed by audit; every existing per-layer hosted resource already
    // lives until Dispose()), so these follow the same whole-app-lifetime pattern as _cache.
    private sealed class AraLayerSessionEntry
    {
        public readonly AraHostSession Session;
        public IntPtr AudioSource;
        public string? ContentKey;
        public int EditGeneration;
        public byte[]? LastExportedState;

        public AraLayerSessionEntry(AraHostSession session) => Session = session;
    }

    private readonly Dictionary<int, AraLayerSessionEntry> _araLayerSessions = new();

    /// <summary>Gets this layer's persistent ARA session (creating it on first call), re-registering
    /// its audio source only when contentKey changes from what's currently registered (e.g. a new
    /// recording/trim) rather than on every call -- this is what lets "Edit in Melodyne..." open the
    /// same live, already-analyzed session MixEngine's pitch stage renders through, instead of a
    /// disposable one-shot session. Must be called on this thread or any other -- internally
    /// dispatcher-marshaled.</summary>
    public (AraHostSession Session, IntPtr AudioSource) GetOrCreateAraLayerSource(
        int layerId, string pluginPath, double sampleRate, int maxBlockSize, float[] samples, string contentKey) =>
        _dispatcher.Invoke(() =>
        {
            if (!_araLayerSessions.TryGetValue(layerId, out var entry))
            {
                entry = new AraLayerSessionEntry(AraHostSession.Create(pluginPath, sampleRate, maxBlockSize));
                _araLayerSessions[layerId] = entry;
            }

            if (entry.ContentKey != contentKey)
            {
                if (entry.AudioSource != IntPtr.Zero)
                    entry.Session.ReleaseAudioSource(entry.AudioSource);

                entry.AudioSource = entry.Session.RegisterAudioSource(new[] { samples }, samples.Length, sampleRate, $"acapella-layer-{layerId}");
                entry.Session.AddPlaybackRegion(entry.AudioSource);
                entry.ContentKey = contentKey;
                // Baseline snapshot so PollAraStateChanged's first poll compares against "just
                // registered, no edits yet" rather than null (which would report a spurious change
                // the moment any archive bytes exist at all).
                entry.LastExportedState = entry.Session.ExportState();
            }

            return (entry.Session, entry.AudioSource);
        });

    /// <summary>Opens Melodyne's own editor GUI for this layer's persistent ARA session. False if
    /// no session exists yet for this layer (the layer's chain has never been built with Manual2A
    /// selected) -- callers should tell the user to play the layer once first rather than treating
    /// this as an error.</summary>
    public bool ShowAraEditor(int layerId, string title) => _dispatcher.Invoke(() =>
        _araLayerSessions.TryGetValue(layerId, out var entry) && entry.Session.ShowEditorWindow(title));

    /// <summary>Closes this layer's Melodyne editor if open. Safe to call when none is open.</summary>
    public void CloseAraEditor(int layerId) => _dispatcher.Invoke(() =>
    {
        if (_araLayerSessions.TryGetValue(layerId, out var entry))
            entry.Session.CloseEditorWindow();
    });

    /// <summary>Bug audit A5 ("stale correction cache"): exports this layer's ARA archive and
    /// compares it against the last-seen snapshot, bumping its edit generation on a real
    /// difference -- the same poll-and-diff shape LayerRowViewModel.PollHostedStateChanges already
    /// uses for FabFilter stages (there's no native "editor window closed" event to hook, so this
    /// detects an edit by its actual effect on the document rather than by window lifecycle).
    /// False (no session, or nothing changed) means nothing to do; call this from the same 500ms
    /// poll timer that already drives the FabFilter equivalent.</summary>
    public bool PollAraStateChanged(int layerId) => _dispatcher.Invoke(() =>
    {
        if (!_araLayerSessions.TryGetValue(layerId, out var entry)) return false;

        var newState = entry.Session.ExportState();
        bool changed = entry.LastExportedState is null
            ? newState.Length > 0
            : !newState.AsSpan().SequenceEqual(entry.LastExportedState);

        if (changed)
        {
            entry.LastExportedState = newState;
            entry.EditGeneration++;
        }
        return changed;
    });

    /// <summary>0 if this layer has never had a state change detected; otherwise increments once
    /// per PollAraStateChanged call that found a real difference. MixEngine folds this into the
    /// Manual2A PitchCorrectionCache key so an edit forces a fresh Correct() call.</summary>
    public int GetAraEditGeneration(int layerId) =>
        _araLayerSessions.TryGetValue(layerId, out var entry) ? entry.EditGeneration : 0;

    /// <summary>Releases every cached hosted instance and every per-layer ARA session, leaving the
    /// service usable for the next project. Called when the whole layer set is replaced (File > New,
    /// File > Open): layer ids are positional and restart at 0 per project, so without this the next
    /// project's layer 0 would inherit the previous project's live plugin instances and their state
    /// (bug audit #5).</summary>
    public void ReleaseAll() => _dispatcher.Invoke(() =>
    {
        _cache.ReleaseAll();
        foreach (var entry in _araLayerSessions.Values)
            entry.Session.Dispose();
        _araLayerSessions.Clear();
    });

    public void Dispose() => _dispatcher.Invoke(() =>
    {
        _cache.Dispose();
        foreach (var entry in _araLayerSessions.Values)
            entry.Session.Dispose();
        _araLayerSessions.Clear();
    });
}
