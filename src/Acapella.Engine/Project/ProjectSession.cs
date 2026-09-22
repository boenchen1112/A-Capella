using Acapella.Engine.Mix;
using Acapella.Engine.Persistence;

namespace Acapella.Engine.Project;

/// <summary>
/// The open project: its layers, project-level settings, undo history, and file I/O. Every
/// snapshot (undo entry, save, export) first pulls live hosted-plugin state into the layers'
/// parameters (audit A2), and every restore (undo/redo, open) pushes the restored state back into
/// any live plugin instances (audit B7) -- callers get both rules for free instead of re-sequencing
/// them around each operation.
/// </summary>
public sealed class ProjectSession
{
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

    public double MetronomeBpm { get; set; } = 120;

    /// <summary>The latency offset that was used when this project's layers were recorded.</summary>
    public double? LatencyOffsetMsUsed { get; private set; }

    public float MasterVolumeDb { get; set; }

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
    public void CommitEdit() => _undoStack.Push(Snapshot());

    /// <summary>False if there was nothing to undo.</summary>
    public bool Undo() => Restore(_undoStack.Undo());

    /// <summary>False if there was nothing to redo.</summary>
    public bool Redo() => Restore(_undoStack.Redo());

    public void Save(string filePath) => _persistence.SaveToFile(Snapshot(), filePath);

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
        _mixEngine.ReleaseAllHostedInstances();
        Restore(_persistence.LoadFromFile(filePath));
        _undoStack.Reset(Snapshot());
    }

    /// <summary>Empties the project (keeping the last-used BPM); undo history starts fresh.
    /// Releases every live hosted-plugin instance and ARA session FIRST, for the same reason as
    /// Open (bug audit #5) -- a layer added after New() reuses LayerId 0, which without this would
    /// reconnect to the previous project's live plugin instance instead of getting a fresh one.</summary>
    public void New()
    {
        _mixEngine.ReleaseAllHostedInstances();
        Layers.Restore(Enumerable.Empty<LayerModel>());
        LatencyOffsetMsUsed = null;
        MasterVolumeDb = 0f;
        _undoStack.Reset(Snapshot());
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
