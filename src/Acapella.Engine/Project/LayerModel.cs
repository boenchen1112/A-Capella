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
