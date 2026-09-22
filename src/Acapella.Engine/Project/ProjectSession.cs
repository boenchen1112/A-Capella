using Acapella.Engine.Mix;
using Acapella.Engine.Persistence;

namespace Acapella.Engine.Project;

/// <summary>
/// The open project: its layers, project-level settings, undo history, and file I/O. Every
/// snapshot (undo entry, save, export) first pulls live hosted-plugin state into the layers'
/// parameters (audit A2), and every restore (undo/redo, open) pushes the restored state back into
/// any live plugin instances (audit B7) -- callers get both rules for free instead of re-sequencing
/// them around each operation. Tracks the project's file (CurrentFilePath) and unsaved-edit state
/// (IsDirty); see SaveStateChanged.
/// </summary>
public sealed class ProjectSession
{
    /// <summary>The suffix every dialog-chosen project path is forced to (L7).</summary>
    public const string ProjectFileSuffix = ".acapella.json";

    private readonly MixEngine _mixEngine;
    private readonly ProjectPersistenceService _persistence = new();
    private readonly ProjectUndoStack _undoStack = new();

    public ProjectSession(MixEngine mixEngine)
    {
        _mixEngine = mixEngine;
        _undoStack.Reset(Snapshot());
    }

    /// <summary>The live layer set -- the same instance for the session's whole lifetime, so
    /// callers may hold on to it; Open/Undo/New replace its contents, not the collection.</summary>
    public LayerCollection Layers { get; } = new();

    private double _metronomeBpm = 120;
    private float _masterVolumeDb;

    public double MetronomeBpm
    {
        get => _metronomeBpm;
        set { if (_metronomeBpm == value) return; _metronomeBpm = value; MarkDirty(); }
    }

    /// <summary>The latency offset that was used when this project's layers were recorded.</summary>
    public double? LatencyOffsetMsUsed { get; private set; }

    public float MasterVolumeDb
    {
        get => _masterVolumeDb;
        set { if (_masterVolumeDb == value) return; _masterVolumeDb = value; MarkDirty(); }
    }

    /// <summary>The file this project was last successfully saved to or opened from; null for a
    /// project that has never been saved (fresh app, File > New).</summary>
    public string? CurrentFilePath { get; private set; }

    /// <summary>True once anything that would be written to the project file has changed since the
    /// last successful Save/Open/New. Conservative: undoing back to the saved state leaves it set.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>"Untitled", or the file name with ".acapella.json" (else its last extension) stripped.</summary>
    public string DisplayName => CurrentFilePath is null ? "Untitled" : StripProjectSuffix(Path.GetFileName(CurrentFilePath));

    /// <summary>Raised (on the calling thread) whenever IsDirty or CurrentFilePath actually changes.</summary>
    public event Action? SaveStateChanged;

    private void MarkDirty() => SetSaveState(CurrentFilePath, dirty: true);

    private void SetSaveState(string? filePath, bool dirty)
    {
        if (filePath == CurrentFilePath && dirty == IsDirty) return;
        CurrentFilePath = filePath;
        IsDirty = dirty;
        SaveStateChanged?.Invoke();
    }

    private static string StripProjectSuffix(string fileName) =>
        fileName.EndsWith(ProjectFileSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^ProjectFileSuffix.Length]
            : Path.GetFileNameWithoutExtension(fileName);

    /// <summary>The project as it would be saved right now, including live plugin editor tweaks.</summary>
    public ProjectFileDto Snapshot()
    {
        foreach (var layer in Layers.Layers)
            _mixEngine.SyncLiveStateIntoParameters(layer.LayerId, layer.MixParameters);
        return _persistence.ToDto(Layers, MetronomeBpm, LatencyOffsetMsUsed, MasterVolumeDb);
    }

    /// <summary>A deep copy of the layers and master volume, unaffected by later edits -- for a
    /// background export racing the user (M7).</summary>
    public (LayerCollection Layers, float MasterVolumeDb) SnapshotForExport()
    {
        var (layers, _, _, masterVolumeDb) = _persistence.FromDto(Snapshot());
        return (layers, masterVolumeDb);
    }

    /// <summary>Records one completed user-visible edit as an undo step.</summary>
    public void CommitEdit()
    {
        _undoStack.Push(Snapshot());
        MarkDirty();
    }

    /// <summary>False if there was nothing to undo.</summary>
    public bool Undo()
    {
        if (!Restore(_undoStack.Undo())) return false;
        MarkDirty();   // TODO(polish): clear dirty when undo returns to the saved snapshot (D1)
        return true;
    }

    /// <summary>False if there was nothing to redo.</summary>
    public bool Redo()
    {
        if (!Restore(_undoStack.Redo())) return false;
        MarkDirty();
        return true;
    }

    public void Save(string filePath)
    {
        _persistence.SaveToFile(Snapshot(), filePath);
        SetSaveState(filePath, dirty: false);   // only reached if the write succeeded
    }

    /// <summary>Replaces the session with the project at filePath; undo history starts fresh.
    /// Releases every live hosted-plugin instance and ARA session FIRST, before restoring (bug
    /// audit #5): layer ids are positional and restart at 0 per project, so without this the
    /// opened project's layer 0 would silently inherit the previous project's live plugin
    /// instances -- PushSavedStateIntoLiveInstances only pushes non-null saved state, so a layer
    /// with no hosted state of its own could never clear a stale instance, and Snapshot()'s
    /// SyncLiveStateIntoParameters would then bake the stale state into the opened project's undo
    /// baseline and save file. With the cache empty first, PushSavedStateIntoLiveInstances finds
    /// nothing live to (not) push into, Snapshot() finds nothing live to pull from, and the first
    /// chain build creates a fresh instance seeded from the opened project's own state.</summary>
    public void Open(string filePath)
    {
        _mixEngine.ReleaseAllHostedInstances();          // unchanged, still FIRST (bug audit #5)
        Restore(_persistence.LoadFromFile(filePath));
        _undoStack.Reset(Snapshot());
        SetSaveState(filePath, dirty: false);            // LAST: Restore() assigns MetronomeBpm/MasterVolumeDb through their dirty-marking setters
    }

    /// <summary>Empties the project (keeping the last-used BPM); undo history starts fresh.
    /// Releases every live hosted-plugin instance and ARA session FIRST, for the same reason as
    /// Open (bug audit #5) -- a layer added after New() reuses LayerId 0, which without this would
    /// reconnect to the previous project's live plugin instance instead of getting a fresh one.</summary>
    public void New()
    {
        _mixEngine.ReleaseAllHostedInstances();          // unchanged, still FIRST
        Layers.Restore(Enumerable.Empty<LayerModel>());
        LatencyOffsetMsUsed = null;
        MasterVolumeDb = 0f;
        _undoStack.Reset(Snapshot());
        SetSaveState(null, dirty: false);                // LAST, same reason
    }

    private bool Restore(ProjectFileDto? dto)
    {
        if (dto is null) return false;

        var (layers, bpm, latencyOffset, masterVolumeDb) = _persistence.FromDto(dto);
        Layers.Restore(layers.Layers);
        MetronomeBpm = bpm;
        LatencyOffsetMsUsed = latencyOffset;
        MasterVolumeDb = masterVolumeDb;

        foreach (var layer in Layers.Layers)
            _mixEngine.PushSavedStateIntoLiveInstances(layer.LayerId, layer.MixParameters);
        return true;
    }
}
