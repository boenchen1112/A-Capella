using A_Capella.Engine.Mix;

namespace A_Capella.Engine.Project;



public enum LayerKind
{
    RecordedAV,
    UploadedVideo,
    UploadedAudioOnly,
}

public class LayerModel
{
    public required int LayerId { get; init; }
    public required LayerKind Kind { get; set; }
    public required string SourcePath { get; set; }
    public double CalibratedOffsetMs { get; set; }
    public double ManualOffsetMs { get; set; }

    /// <summary>How far the manual timing nudge may move a layer either way.</summary>
    public const double MaxManualOffsetMs = 2000;

    /// <summary>Moves this layer later (positive) or earlier (negative) against the others by deltaMs,
    /// clamped to +/-MaxManualOffsetMs. Rounded to a microsecond-free value so repeated 0.1 ms
    /// steps don't accumulate floating-point dust into the saved file.</summary>
    public void NudgeManualOffset(double deltaMs) =>
        ManualOffsetMs = Math.Clamp(Math.Round(ManualOffsetMs + deltaMs, 3), -MaxManualOffsetMs, MaxManualOffsetMs);

    /// <summary>User-editable display name (v5 P2 task 1). Null/blank means "not renamed yet" --
    /// callers fall back to a positional default (e.g. "Layer {CellIndex + 1}") rather than baking
    /// that default in here, since the model doesn't know which UI slot it's shown in.</summary>
    public string? Name { get; set; }

    /// <summary>Trim in/out points, in ms from the start of SourcePath -- independent of sync
    /// (GetShiftMs): trim defines which portion of the source plays, sync shifts it in time
    /// relative to the other layers. Added for the Editor-screen trim control.
    /// Null TrimEndMs means "play to the end of the source".</summary>
    public double TrimStartMs { get; set; }
    public double? TrimEndMs { get; set; }

    /// <summary>Grid cell this layer occupies (per the Data Model Rule: layout id + per-layer cell
    /// index, not hardcoded positions). Defaults to recording order, matching the layout's cell order.</summary>
    public int CellIndex { get; set; }

    /// <summary>Flip this layer's picture left-to-right (selfie-style webcam footage reads naturally
    /// mirrored). Video only; a property of the slot rather than the take, so a retake keeps it.</summary>
    public bool Mirror { get; set; }

    /// <summary>Melodyne ARA archive key, keyed by (layerId, sourceAudioHash) once Phase 2A lands.
    /// Null until then -- this field exists now so Phase 5's persistence format doesn't need to
    /// change shape when Phase 2A adds it.</summary>
    public string? AraArchiveKey { get; set; }

    public LayerMixParameters MixParameters { get; } = new();

    /// <summary>Total sync shift to apply at mix/preview/export time. Positive delays this
    /// layer's audio/video (padding/holding); negative trims from its head (calibration removes
    /// recording round-trip latency, manual is a signed user nudge on top of that).</summary>
    public double GetShiftMs() => ManualOffsetMs - CalibratedOffsetMs;

    /// <summary>A key that uniquely determines this layer's exact post-trim/shift decoded audio
    /// content, for caches keyed on "has the input actually changed" (e.g. PitchCorrectionCache,
    /// audit B3). Two calls return the same key iff the source file is unchanged (by mtime) and
    /// trim/shift are unchanged.</summary>
    public string SourceCacheKey()
    {
        long mtimeTicks = File.Exists(SourcePath) ? File.GetLastWriteTimeUtc(SourcePath).Ticks : 0;
        return $"{SourcePath}|{mtimeTicks}|{TrimStartMs}|{TrimEndMs}|{GetShiftMs()}";
    }

    /// <summary>Retake in place (retake spec D1): points this layer at a new take while keeping its
    /// slot identity (LayerId, CellIndex), Name and MixParameters -- so its hosted FX instances,
    /// keyed by (LayerId, Stage), and their saved state carry over untouched. Resets everything that
    /// described the OLD take: trims (ms into the old file), the manual nudge and calibrated latency
    /// (both per-take), and the ARA archive key (keyed by source-audio hash).</summary>
    public void ReplaceSource(LayerKind kind, string sourcePath, double calibratedOffsetMs)
    {
        Kind = kind;
        SourcePath = sourcePath;
        CalibratedOffsetMs = calibratedOffsetMs;
        ManualOffsetMs = 0;
        TrimStartMs = 0;
        TrimEndMs = null;
        AraArchiveKey = null;
    }
}

public class LayerCollection
{
    public const int MaxLayers = 9;

    private readonly List<LayerModel> _layers = new();

    // Monotonic, never reused within a session -- NOT _layers.Count, because deleting a layer
    // (LayerCollection.Remove) shrinks the list, and a Count-based id would then hand a freshly
    // recorded/uploaded layer the same LayerId as a just-deleted one. Hosted-plugin instances are
    // cached by (LayerId, Stage) and a deleted layer's instances are deliberately never released
    // (closing an FX editor must not risk a use-after-free in a live audio graph), so an id collision
    // would hand the new layer a stale instance still carrying the old layer's FX state.
    private int _nextLayerId;

    public IReadOnlyList<LayerModel> Layers => _layers;

    public LayerModel Add(LayerKind kind, string sourcePath)
    {
        if (_layers.Count >= MaxLayers)
            throw new InvalidOperationException($"Cannot exceed {MaxLayers} layers.");

        var layer = new LayerModel
        {
            LayerId = _nextLayerId++,
            Kind = kind,
            SourcePath = sourcePath,
            CellIndex = _layers.Count,
        };
        _layers.Add(layer);
        return layer;
    }

    /// <summary>Replaces the collection's contents wholesale -- used by project load/undo-redo
    /// restore. Advances the id counter past whatever the restored layers already used, so a layer
    /// added afterward can't collide with one of them either.</summary>
    public void Restore(IEnumerable<LayerModel> layers)
    {
        var list = layers.ToList();
        if (list.Count > MaxLayers)
            throw new InvalidOperationException($"Cannot exceed {MaxLayers} layers.");

        _layers.Clear();
        _layers.AddRange(list);
        // Math.Max against the current counter, not a flat assignment: an undo/redo that restores
        // an older snapshot (fewer/lower-id layers than have existed this session) must not walk the
        // counter backwards, or a layer added after the restore could reclaim an id whose hosted-FX
        // instances are still cached from a layer deleted *after* that snapshot was taken.
        int restoredMax = list.Count > 0 ? list.Max(l => l.LayerId) + 1 : 0;
        _nextLayerId = Math.Max(_nextLayerId, restoredMax);
    }

    /// <summary>Removes one layer (delete-layer): the slot becomes empty and every other layer keeps
    /// its own CellIndex -- this deliberately does not compact remaining layers into lower cells
    /// (see the grid-gap-compaction fix: Layout2x2Provider already renders a non-contiguous CellIndex
    /// correctly). Safe to call with a layer no longer in the collection.</summary>
    public void Remove(LayerModel layer) => _layers.Remove(layer);

    /// <summary>Removes the most recently added layer. Currently unused: M6 probes before Add
    /// (RecordSetupWindow.StopRecording), so no zombie layer is ever added.</summary>
    public void RemoveLast()
    {
        if (_layers.Count > 0)
            _layers.RemoveAt(_layers.Count - 1);
    }
}
