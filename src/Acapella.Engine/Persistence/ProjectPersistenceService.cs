using System.Text.Json;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;

namespace Acapella.Engine.Persistence;

/// <summary>
/// Full project save/load. Supersedes Phase 1's narrow settings.json (latency offset only) and
/// Phase 3's in-session-only parameter round-trip -- those still exist for their original
/// narrower purposes (recalibration store, live session state) but a saved project now carries
/// everything needed to restore full editable state on reopen.
/// </summary>
public class ProjectPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ProjectFileDto ToDto(LayerCollection layers, double metronomeBpm, double? latencyOffsetMsUsed, float masterVolumeDb = 0f)
    {
        return new ProjectFileDto
        {
            LayoutId = "2x2",
            MetronomeBpm = metronomeBpm,
            LatencyOffsetMsUsed = latencyOffsetMsUsed,
            MasterVolumeDb = masterVolumeDb,
            Layers = layers.Layers.Select(ToLayerDto).ToList(),
        };
    }

    public (LayerCollection Layers, double MetronomeBpm, double? LatencyOffsetMsUsed, float MasterVolumeDb) FromDto(ProjectFileDto dto)
    {
        var layers = new LayerCollection();
        layers.Restore(dto.Layers.Select(FromLayerDto));
        return (layers, dto.MetronomeBpm, dto.LatencyOffsetMsUsed, dto.MasterVolumeDb);
    }

    public void SaveToFile(ProjectFileDto dto, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public ProjectFileDto LoadFromFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<ProjectFileDto>(json)
            ?? throw new InvalidDataException($"Could not parse project file: {filePath}");
    }

    private static LayerDto ToLayerDto(LayerModel layer) => new()
    {
        LayerId = layer.LayerId,
        CellIndex = layer.CellIndex,
        Kind = layer.Kind.ToString(),
        SourcePath = layer.SourcePath,
        CalibratedOffsetMs = layer.CalibratedOffsetMs,
        ManualOffsetMs = layer.ManualOffsetMs,
        TrimStartMs = layer.TrimStartMs,
        TrimEndMs = layer.TrimEndMs,
        AraArchiveKey = layer.AraArchiveKey,
        MixParameters = ToMixParametersDto(layer.MixParameters),
    };

    private static LayerModel FromLayerDto(LayerDto dto)
    {
        var layer = new LayerModel
        {
            LayerId = dto.LayerId,
            Kind = Enum.Parse<LayerKind>(dto.Kind),
            SourcePath = dto.SourcePath,
            CalibratedOffsetMs = dto.CalibratedOffsetMs,
            ManualOffsetMs = dto.ManualOffsetMs,
            TrimStartMs = dto.TrimStartMs,
            TrimEndMs = dto.TrimEndMs,
            CellIndex = dto.CellIndex,
            AraArchiveKey = dto.AraArchiveKey,
        };
        ApplyMixParametersDto(dto.MixParameters, layer.MixParameters);
        return layer;
    }

    private static MixParametersDto ToMixParametersDto(LayerMixParameters p) => new()
    {
        GainDb = p.GainDb,
        Mute = p.Mute,
        Solo = p.Solo,
        Pan = p.Pan,
        LowShelfGainDb = p.LowShelfGainDb,
        MidBellGainDb = p.MidBellGainDb,
        HighShelfGainDb = p.HighShelfGainDb,
        NoiseGateThresholdDb = p.NoiseGateThresholdDb,
        NoiseGateReleaseMs = p.NoiseGateReleaseMs,
        CompressorEnabled = p.CompressorEnabled,
        CompressorThresholdDb = p.CompressorThresholdDb,
        CompressorRatio = p.CompressorRatio,
        LimiterEnabled = p.LimiterEnabled,
        LimiterCeilingDb = p.LimiterCeilingDb,
        LimiterGainDb = p.LimiterGainDb,
        PitchBackend = p.PitchBackend.ToString(),
    };

    private static void ApplyMixParametersDto(MixParametersDto dto, LayerMixParameters target)
    {
        target.GainDb = dto.GainDb;
        target.Mute = dto.Mute;
        target.Solo = dto.Solo;
        target.Pan = dto.Pan;
        target.LowShelfGainDb = dto.LowShelfGainDb;
        target.MidBellGainDb = dto.MidBellGainDb;
        target.HighShelfGainDb = dto.HighShelfGainDb;
        target.NoiseGateThresholdDb = dto.NoiseGateThresholdDb;
        target.NoiseGateReleaseMs = dto.NoiseGateReleaseMs;
        target.CompressorEnabled = dto.CompressorEnabled;
        target.CompressorThresholdDb = dto.CompressorThresholdDb;
        target.CompressorRatio = dto.CompressorRatio;
        target.LimiterEnabled = dto.LimiterEnabled;
        target.LimiterCeilingDb = dto.LimiterCeilingDb;
        target.LimiterGainDb = dto.LimiterGainDb;
        target.PitchBackend = Enum.Parse<PitchBackendSelection>(dto.PitchBackend);
    }
}
