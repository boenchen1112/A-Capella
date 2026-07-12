using Acapella.Engine.Mix;

namespace Acapella.Engine.Project;

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

    /// <summary>Trim in/out points, in ms from the start of SourcePath -- independent of sync
    /// (GetShiftMs): trim defines which portion of the source plays, sync shifts it in time
    /// relative to the other layers. Added for UI_Design_Spec v2's Editor-screen trim control.
    /// Null TrimEndMs means "play to the end of the source".</summary>
    public double TrimStartMs { get; set; }
    public double? TrimEndMs { get; set; }

    /// <summary>Grid cell this layer occupies (per the Data Model Rule: layout id + per-layer cell
    /// index, not hardcoded positions). Defaults to recording order, matching Layout2x2Provider.</summary>
    public int CellIndex { get; set; }

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
}

public class LayerCollection
{
    public const int MaxLayers = 4;

    private readonly List<LayerModel> _layers = new();

    public IReadOnlyList<LayerModel> Layers => _layers;

    public LayerModel Add(LayerKind kind, string sourcePath)
    {
        if (_layers.Count >= MaxLayers)
            throw new InvalidOperationException($"Cannot exceed {MaxLayers} layers.");

        var layer = new LayerModel
        {
            LayerId = _layers.Count,
            Kind = kind,
            SourcePath = sourcePath,
            CellIndex = _layers.Count,
        };
        _layers.Add(layer);
        return layer;
    }

    /// <summary>Replaces the collection's contents wholesale -- used by project load.</summary>
    public void Restore(IEnumerable<LayerModel> layers)
    {
        var list = layers.ToList();
        if (list.Count > MaxLayers)
            throw new InvalidOperationException($"Cannot exceed {MaxLayers} layers.");

        _layers.Clear();
        _layers.AddRange(list);
    }

    /// <summary>Removes the most recently added layer -- used to discard a zombie layer left
    /// behind by a failed recording (see MainWindow.StopRecordButton_Click).</summary>
    public void RemoveLast()
    {
        if (_layers.Count > 0)
            _layers.RemoveAt(_layers.Count - 1);
    }
}
