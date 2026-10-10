using System.Text.Json;
using A_Capella.Engine.Composite;
using A_Capella.Engine.Metronome;
using A_Capella.Engine.Mix;
using A_Capella.Engine.Project;

namespace A_Capella.Engine.Persistence;

/// <summary>
/// Full project save/load. Supersedes Phase 1's narrow settings.json (latency offset only) and
/// Phase 3's in-session-only parameter round-trip -- those still exist for their original
/// narrower purposes (recalibration store, live session state) but a saved project now carries
/// everything needed to restore full editable state on reopen.
/// </summary>
public class ProjectPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ProjectFileDto ToDto(LayerCollection layers, double metronomeBpm, double? latencyOffsetMsUsed, float masterVolumeDb = 0f, double? songInMs = null, double? songOutMs = null, VideoSettings? video = null, TimeSignature? timeSignature = null, Presets.PresetRef? masterLimiter = null)
    {
        video ??= new VideoSettings();
        return new ProjectFileDto
        {
            LayoutId = video.LayoutId,
            Video = new VideoDto
            {
                AspectRatio = video.AspectRatio.ToString(),
                FillMode = video.Fit.ToString(),
                GutterFraction = video.GutterFraction,
                BackgroundColor = video.Background.Color,
                BackgroundImagePath = video.Background.ImagePath,
                BackgroundImageMode = video.Background.ImageMode.ToString(),
            },
            MetronomeBpm = metronomeBpm,
            MetronomeBeatsPerBar = (timeSignature ?? TimeSignature.FourFour).BeatsPerBar,
            MetronomeBeatUnit = (timeSignature ?? TimeSignature.FourFour).BeatUnit,
            LatencyOffsetMsUsed = latencyOffsetMsUsed,
            MasterVolumeDb = masterVolumeDb,
            MasterLimiterPreset = masterLimiter,
            SongInMs = songInMs,
            SongOutMs = songOutMs,
            Layers = layers.Layers.Select(ToLayerDto).ToList(),
        };
    }

    public (LayerCollection Layers, double MetronomeBpm, double? LatencyOffsetMsUsed, float MasterVolumeDb, VideoSettings Video, TimeSignature TimeSignature) FromDto(ProjectFileDto dto)
    {
        var layers = new LayerCollection();
        layers.Restore(dto.Layers.Select(FromLayerDto));
        // Unknown ids (hand-edited or from a newer build) fall back to the 2x2 layout, which is also what every pre-layout project is.
        string layoutId = GridLayoutCatalog.Get(dto.LayoutId).Id;
        var video = dto.Video is null
            ? VideoSettings.Legacy(layoutId)
            : new VideoSettings
            {
                LayoutId = layoutId,
                AspectRatio = Enum.TryParse<AspectRatioOption>(dto.Video.AspectRatio, out var ratio) ? ratio : AspectRatioOption.Wide16x9,
                Fit = Enum.TryParse<CellFit>(dto.Video.FillMode, out var fit) ? fit : CellFit.Fill,
                GutterFraction = Math.Clamp(Finite(dto.Video.GutterFraction, 0), 0, VideoSettings.MaxGutterFraction),
                Background = new BackgroundSettings
                {
                    Color = dto.Video.BackgroundColor ?? "#000000",
                    ImagePath = dto.Video.BackgroundImagePath,
                    ImageMode = Enum.TryParse<BackgroundImageMode>(dto.Video.BackgroundImageMode, out var mode) ? mode : BackgroundImageMode.Stretch,
                },
            };
        return (layers, dto.MetronomeBpm, dto.LatencyOffsetMsUsed, dto.MasterVolumeDb, video, new TimeSignature(dto.MetronomeBeatsPerBar, dto.MetronomeBeatUnit).Sanitized());
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

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
        var dto = JsonSerializer.Deserialize<ProjectFileDto>(json)
            ?? throw new InvalidDataException($"Could not parse project file: {filePath}");
        // Bug audit #17: every save since the first (56a7577) writes LayoutId; a JSON object without it isn't a project.
        return dto.LayoutId is not null ? dto
            : throw new InvalidDataException($"Not an A-Capella project file: {Path.GetFileName(filePath)}");
    }

    private static LayerDto ToLayerDto(LayerModel layer) => new()
    {
        LayerId = layer.LayerId,
        CellIndex = layer.CellIndex,
        Mirror = layer.Mirror,
        Kind = layer.Kind.ToString(),
        SourcePath = layer.SourcePath,
        Name = layer.Name,
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
            Name = dto.Name,
            CalibratedOffsetMs = dto.CalibratedOffsetMs,
            ManualOffsetMs = dto.ManualOffsetMs,
            TrimStartMs = dto.TrimStartMs,
            TrimEndMs = dto.TrimEndMs,
            CellIndex = dto.CellIndex,
            Mirror = dto.Mirror,
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
        CompressorPreset = p.CompressorPreset,
        EqPreset = p.EqPreset,
        DeEsserPreset = p.DeEsserPreset,
        ReverbPreset = p.ReverbPreset,
        PitchBackend = p.PitchBackend.ToString(),
        Frozen = p.Frozen,
    };

    private static void ApplyMixParametersDto(MixParametersDto dto, LayerMixParameters target)
    {
        target.GainDb = dto.GainDb;
        target.Mute = dto.Mute;
        target.Solo = dto.Solo;
        target.Pan = dto.Pan;
        target.CompressorPreset = dto.CompressorPreset;
        target.EqPreset = dto.EqPreset;
        target.DeEsserPreset = dto.DeEsserPreset;
        target.ReverbPreset = dto.ReverbPreset;
        target.PitchBackend = Enum.TryParse<PitchBackendSelection>(dto.PitchBackend, out var backend) ? backend : PitchBackendSelection.None;
        target.Frozen = dto.Frozen;
    }
}
