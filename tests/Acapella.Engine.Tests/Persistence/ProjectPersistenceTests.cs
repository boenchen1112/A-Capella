using Acapella.Engine.Mix;
using Acapella.Engine.Persistence;
using Acapella.Engine.Project;

namespace Acapella.Engine.Tests.Persistence;

public class ProjectPersistenceTests
{
    private static LayerCollection BuildSampleProject()
    {
        var layers = new LayerCollection();

        var layer0 = layers.Add(LayerKind.RecordedAV, "layer0.mkv");
        layer0.CalibratedOffsetMs = 162.7;
        layer0.ManualOffsetMs = -15.5;
        layer0.MixParameters.GainDb = -6.5f;
        layer0.MixParameters.Pan = -0.3f;
        layer0.MixParameters.Mute = true;
        layer0.MixParameters.LowShelfGainDb = 2.5f;
        layer0.MixParameters.MidBellGainDb = -1.5f;
        layer0.MixParameters.HighShelfGainDb = 3f;
        layer0.MixParameters.NoiseGateThresholdDb = -45f;
        layer0.MixParameters.NoiseGateReleaseMs = 150f;
        layer0.MixParameters.PitchBackend = PitchBackendSelection.Automatic2B;

        var layer1 = layers.Add(LayerKind.UploadedAudioOnly, "layer1.wav");
        layer1.MixParameters.Solo = true;
        layer1.MixParameters.PitchBackend = PitchBackendSelection.Manual2A;
        layer1.AraArchiveKey = "layer1|abcd1234";

        return layers;
    }

    [Fact]
    public void SaveAndReload_RoundTripsEveryFieldByteForByte()
    {
        var service = new ProjectPersistenceService();
        var originalLayers = BuildSampleProject();
        double metronomeBpm = 128.5;
        double? latencyOffset = 162.7;

        string tempPath = Path.Combine(Path.GetTempPath(), $"acapella-test-{Guid.NewGuid()}.json");
        try
        {
            var dto = service.ToDto(originalLayers, metronomeBpm, latencyOffset);
            service.SaveToFile(dto, tempPath);

            var loadedDto = service.LoadFromFile(tempPath);
            var (loadedLayers, loadedBpm, loadedOffset, _) = service.FromDto(loadedDto);

            Assert.Equal(metronomeBpm, loadedBpm);
            Assert.Equal(latencyOffset, loadedOffset);
            Assert.Equal(originalLayers.Layers.Count, loadedLayers.Layers.Count);

            for (int i = 0; i < originalLayers.Layers.Count; i++)
            {
                var original = originalLayers.Layers[i];
                var loaded = loadedLayers.Layers[i];

                Assert.Equal(original.LayerId, loaded.LayerId);
                Assert.Equal(original.Kind, loaded.Kind);
                Assert.Equal(original.SourcePath, loaded.SourcePath);
                Assert.Equal(original.CalibratedOffsetMs, loaded.CalibratedOffsetMs);
                Assert.Equal(original.ManualOffsetMs, loaded.ManualOffsetMs);
                Assert.Equal(original.CellIndex, loaded.CellIndex);
                Assert.Equal(original.AraArchiveKey, loaded.AraArchiveKey);

                Assert.Equal(original.MixParameters.GainDb, loaded.MixParameters.GainDb);
                Assert.Equal(original.MixParameters.Mute, loaded.MixParameters.Mute);
                Assert.Equal(original.MixParameters.Solo, loaded.MixParameters.Solo);
                Assert.Equal(original.MixParameters.Pan, loaded.MixParameters.Pan);
                Assert.Equal(original.MixParameters.LowShelfGainDb, loaded.MixParameters.LowShelfGainDb);
                Assert.Equal(original.MixParameters.MidBellGainDb, loaded.MixParameters.MidBellGainDb);
                Assert.Equal(original.MixParameters.HighShelfGainDb, loaded.MixParameters.HighShelfGainDb);
                Assert.Equal(original.MixParameters.NoiseGateThresholdDb, loaded.MixParameters.NoiseGateThresholdDb);
                Assert.Equal(original.MixParameters.NoiseGateReleaseMs, loaded.MixParameters.NoiseGateReleaseMs);
                Assert.Equal(original.MixParameters.PitchBackend, loaded.MixParameters.PitchBackend);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void SaveAndReload_EmptyProject_RoundTrips()
    {
        var service = new ProjectPersistenceService();
        var layers = new LayerCollection();
        string tempPath = Path.Combine(Path.GetTempPath(), $"acapella-test-{Guid.NewGuid()}.json");

        try
        {
            var dto = service.ToDto(layers, 120, null);
            service.SaveToFile(dto, tempPath);

            var (loadedLayers, loadedBpm, loadedOffset, _) = service.FromDto(service.LoadFromFile(tempPath));

            Assert.Empty(loadedLayers.Layers);
            Assert.Equal(120, loadedBpm);
            Assert.Null(loadedOffset);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
