namespace A_Capella.Engine.Persistence;

/// <summary>
/// On-disk project file shape. Deliberately a separate DTO from the live LayerModel/
/// LayerMixParameters engine types so the file format doesn't have to change shape whenever the
/// in-memory model does (LayerModel.MixParameters is a read-only auto-property, which
/// System.Text.Json can't deserialize into directly).
/// </summary>
public class ProjectFileDto
{
    /// <summary>Locked to "2x2" for v1 -- stored explicitly per the Data Model Rule rather than
    /// assumed, so raising the layout options later doesn't require a format migration.
    /// No initializer (bug audit #17): null after a load means the file had no LayoutId, i.e. isn't a project.</summary>
    public string? LayoutId { get; set; }

    /// <summary>Video look (canvas shape, cell fill, gutter). Null in files saved before it existed;
    /// those load as VideoSettings.Legacy so they render exactly as saved.</summary>
    public VideoDto? Video { get; set; }

    public double MetronomeBpm { get; set; } = 120;

    /// <summary>Metronome meter (e.g. 3 and 4 for 3/4). Missing in older files, which are 4/4.</summary>
    public int MetronomeBeatsPerBar { get; set; } = 4;
    public int MetronomeBeatUnit { get; set; } = 4;

    /// <summary>Bus gain applied after summing all layers, before the master limiter (P1 task 4) --
    /// used identically by preview and export so exports sound like the preview.</summary>
    public float MasterVolumeDb { get; set; } = 0f;

    /// <summary>The master limiter's FabFilter factory preset; null = off (a fixed safety ceiling still applies).</summary>
    public Presets.PresetRef? MasterLimiterPreset { get; set; }

    /// <summary>The latency offset (ms) that was actually used when this project's layers were
    /// recorded -- distinct from SettingsService's per-device-pair calibration store, which is
    /// about recalibrating future sessions, not remembering what a saved project used.</summary>
    public double? LatencyOffsetMsUsed { get; set; }

    /// <summary>Project-wide export range, set from the playhead (Set Song In/Out) and applied only
    /// at export -- the preview still plays the whole song. Null means "whole song"; absent entirely
    /// in a file saved before this existed, so an old file loads with both null (no initializer, so
    /// deserializing that absence just leaves the default).</summary>
    public double? SongInMs { get; set; }
    public double? SongOutMs { get; set; }

    public List<LayerDto> Layers { get; set; } = new();
}

public class VideoDto
{
    public string AspectRatio { get; set; } = nameof(Project.AspectRatioOption.Wide16x9);
    public string FillMode { get; set; } = nameof(Composite.CellFit.Fill);
    public double GutterFraction { get; set; } = 0.01;
    public string BackgroundColor { get; set; } = "#000000";
    public string? BackgroundImagePath { get; set; }
    public string BackgroundImageMode { get; set; } = nameof(Composite.BackgroundImageMode.Stretch);
}

public class LayerDto
{
    public int LayerId { get; set; }
    public int CellIndex { get; set; }
    public bool Mirror { get; set; }
    public string Kind { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string? Name { get; set; }
    public double CalibratedOffsetMs { get; set; }
    public double ManualOffsetMs { get; set; }

    /// <summary>Trim in/out points (ms from the start of the source), added for the
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
    // Each effect is a choice of FabFilter factory preset; null = off.
    public Presets.PresetRef? CompressorPreset { get; set; }
    public Presets.PresetRef? EqPreset { get; set; }
    public Presets.PresetRef? DeEsserPreset { get; set; }
    public Presets.PresetRef? ReverbPreset { get; set; }

    /// <summary>Kept so files that carry it still load; the value is ignored (no pitch tool on Windows).</summary>
    public string PitchBackend { get; set; } = "None";

    /// <summary>Only the flag is saved -- the frozen buffer itself is re-rendered after load, not
    /// persisted. Defaults false, so a file saved before this field existed (plain JSON absence)
    /// loads as not frozen.</summary>
    public bool Frozen { get; set; }
}
