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
        };
        _layers.Add(layer);
        return layer;
    }
}
