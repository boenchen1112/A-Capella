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

    /// <summary>Bus gain applied after summing all layers, before the master limiter (P1 task 4) --
    /// used identically by preview and export so exports sound like the preview.</summary>
    public float MasterVolumeDb { get; set; } = 0f;

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
    public string? Name { get; set; }
    public double CalibratedOffsetMs { get; set; }
    public double ManualOffsetMs { get; set; }

    /// <summary>Trim in/out points (ms from the start of the source), added for UI_Design_Spec v2's
    /// Editor-screen trim control. Null TrimEndMs means "to the end of the source".</summary>
    public double TrimStartMs { get; set; }
    public double? TrimEndMs { get; set; }

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
    public bool CompressorEnabled { get; set; }
    public float CompressorThresholdDb { get; set; } = -18f;
    public float CompressorRatio { get; set; } = 2f;
    public bool LimiterEnabled { get; set; }
    public float LimiterCeilingDb { get; set; } = -0.3f;
    public float LimiterGainDb { get; set; }
    public string PitchBackend { get; set; } = "None";
}
