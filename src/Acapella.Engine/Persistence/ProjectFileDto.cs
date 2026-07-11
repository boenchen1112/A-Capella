namespace Acapella.Engine.Persistence;

/// <summary>
/// On-disk project file shape. Deliberately a separate DTO from the live LayerModel/
/// LayerMixParameters engine types so the file format doesn't have to change shape whenever the
/// in-memory model does (LayerModel.MixParameters is a read-only auto-property, which
/// System.Text.Json can't deserialize into directly).
/// </summary>
public class ProjectFileDto
{
    /// <summary>Locked to "2x2" for v1 -- stored explicitly per the Data Model Rule rather than
    /// assumed, so raising the layout options later doesn't require a format migration.</summary>
    public string LayoutId { get; set; } = "2x2";

    public double MetronomeBpm { get; set; } = 120;

    /// <summary>The latency offset (ms) that was actually used when this project's layers were
    /// recorded -- distinct from SettingsService's per-device-pair calibration store, which is
    /// about recalibrating future sessions, not remembering what a saved project used.</summary>
    public double? LatencyOffsetMsUsed { get; set; }

    public List<LayerDto> Layers { get; set; } = new();
}

public class LayerDto
{
    public int LayerId { get; set; }
    public int CellIndex { get; set; }
    public string Kind { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public double CalibratedOffsetMs { get; set; }
    public double ManualOffsetMs { get; set; }
    public string? AraArchiveKey { get; set; }
    public MixParametersDto MixParameters { get; set; } = new();
}

public class MixParametersDto
{
    public float GainDb { get; set; }
    public bool Mute { get; set; }
    public bool Solo { get; set; }
    public float Pan { get; set; }
    public float LowShelfGainDb { get; set; }
    public float MidBellGainDb { get; set; }
    public float HighShelfGainDb { get; set; }
    public float NoiseGateThresholdDb { get; set; } = -60f;
    public float NoiseGateReleaseMs { get; set; } = 100f;
    public string PitchBackend { get; set; } = "None";
}
