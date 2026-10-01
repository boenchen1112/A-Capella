using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;

namespace Acapella.Engine.Tests.Project;

/// <summary>Retake-in-place spec (Feature_Spec_2026-09-24_RetakeInPlace.md): LayerModel.ReplaceSource
/// and RecordTakeRules. Plain facts -- no WPF, no native DLL, no devices.</summary>
public class LayerRetakeTests
{
    private static float[] Sine(double frequencyHz, float amplitude, int sampleRate, int seconds = 1)
    {
        int length = sampleRate * seconds;
        var samples = new float[length];
        for (int i = 0; i < length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
        return samples;
    }

    private static float[] ReadAll(NAudio.Wave.ISampleProvider provider, int count)
    {
        var buffer = new float[count];
        int read = 0;
        while (read < count)
        {
            int n = provider.Read(buffer, read, count - read);
            if (n == 0) break;
            read += n;
        }
        return buffer;
    }

    [Fact]
    public void ReplaceSource_SwapsKindAndPath_AndResetsTakeSpecificTiming()
    {
        var layers = new LayerCollection();
        var layer = layers.Add(LayerKind.UploadedVideo, "old.mp4");
        layer.TrimStartMs = 300;
        layer.TrimEndMs = 9000;
        layer.CalibratedOffsetMs = 120;
        layer.ManualOffsetMs = 15;
        layer.AraArchiveKey = "k";

        layer.ReplaceSource(LayerKind.RecordedAV, "new.mkv", 40);

        Assert.Equal(LayerKind.RecordedAV, layer.Kind);
        Assert.Equal("new.mkv", layer.SourcePath);
        Assert.Equal(0, layer.TrimStartMs);
        Assert.Null(layer.TrimEndMs);
        Assert.Equal(40, layer.CalibratedOffsetMs);
        Assert.Equal(0, layer.ManualOffsetMs);
        Assert.Null(layer.AraArchiveKey);
        Assert.Equal(-40, layer.GetShiftMs());
    }

    [Fact]
    public void ReplaceSource_KeepsSlotIdentityNameAndMix()
    {
        var layers = new LayerCollection();
        layers.Add(LayerKind.RecordedAV, "a.mkv");
        var layer = layers.Add(LayerKind.RecordedAV, "b.mkv");
        layer.CellIndex = 3;
        layer.Name = "Alto";
        layer.MixParameters.GainDb = -6f;
        layer.MixParameters.Pan = 0.5f;
        layer.MixParameters.Solo = true;
        layer.MixParameters.EqHostedState = new byte[] { 7 };
        var mix = layer.MixParameters;

        layer.ReplaceSource(LayerKind.UploadedAudioOnly, "x.wav", 0);

        Assert.Equal(1, layer.LayerId);
        Assert.Equal(3, layer.CellIndex);
        Assert.Equal("Alto", layer.Name);
        Assert.Same(mix, layer.MixParameters);
        Assert.Equal(-6f, layer.MixParameters.GainDb);
        Assert.Equal(0.5f, layer.MixParameters.Pan);
        Assert.True(layer.MixParameters.Solo);
        Assert.Equal(new byte[] { 7 }, layer.MixParameters.EqHostedState);
    }

    [Fact]
    public void ReplaceSource_ChangesSourceCacheKey()
    {
        var layers = new LayerCollection();
        var layer = layers.Add(LayerKind.RecordedAV, "old.mkv");
        string keyBefore = layer.SourceCacheKey();

        layer.ReplaceSource(LayerKind.RecordedAV, "new.mkv", 0);

        Assert.NotEqual(keyBefore, layer.SourceCacheKey());
    }

    [Fact]
    public void ReplaceSource_OnAFullProject_LeavesTheLayerCountAndIdsUnchanged()
    {
        var layers = new LayerCollection();
        for (int i = 0; i < LayerCollection.MaxLayers; i++)
            layers.Add(LayerKind.RecordedAV, $"layer{i}.mkv");
        var originals = layers.Layers.ToList();

        originals[2].ReplaceSource(LayerKind.RecordedAV, "retake.mkv", 0);

        Assert.Equal(LayerCollection.MaxLayers, layers.Layers.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, layers.Layers.Select(l => l.LayerId));
        for (int i = 0; i < originals.Count; i++)
            Assert.Same(originals[i], layers.Layers[i]);
    }

    [Fact]
    public void IsBlockedByLayerCap_NewTakeAtTheCap_IsBlocked()
    {
        Assert.True(RecordTakeRules.IsBlockedByLayerCap(LayerCollection.MaxLayers, null));
    }

    [Fact]
    public void IsBlockedByLayerCap_RetakeAtTheCap_IsNotBlocked()
    {
        Assert.False(RecordTakeRules.IsBlockedByLayerCap(LayerCollection.MaxLayers, 2));
    }

    [Fact]
    public void IsBlockedByLayerCap_NewTakeBelowTheCap_IsNotBlocked()
    {
        Assert.False(RecordTakeRules.IsBlockedByLayerCap(LayerCollection.MaxLayers - 1, null));
        Assert.False(RecordTakeRules.IsBlockedByLayerCap(0, null));
    }

    [Fact]
    public void GuideLayers_NewTake_IsEveryLayerInOrder()
    {
        var layers = new LayerCollection();
        layers.Add(LayerKind.RecordedAV, "a.mkv");
        layers.Add(LayerKind.RecordedAV, "b.mkv");
        layers.Add(LayerKind.RecordedAV, "c.mkv");

        var guide = RecordTakeRules.GuideLayers(layers.Layers, null);

        Assert.Equal(layers.Layers, guide);
    }

    [Fact]
    public void GuideLayers_Retake_ExcludesExactlyTheTarget()
    {
        var layers = new LayerCollection();
        layers.Add(LayerKind.RecordedAV, "a.mkv");
        layers.Add(LayerKind.RecordedAV, "b.mkv");
        layers.Add(LayerKind.RecordedAV, "c.mkv");

        var guide = RecordTakeRules.GuideLayers(layers.Layers, 1);

        Assert.Equal(new[] { layers.Layers[0], layers.Layers[2] }, guide);
    }

    [Fact]
    public void GuideLayers_RetakeOfTheOnlyLayer_IsEmpty()
    {
        var layers = new LayerCollection();
        layers.Add(LayerKind.RecordedAV, "a.mkv");

        var guide = RecordTakeRules.GuideLayers(layers.Layers, 0);

        Assert.Empty(guide);
    }

    [Fact]
    public void GuideMix_RetakeOfTheOnlySoloedLayer_IsNotSilent()
    {
        int sampleRate = 44100;
        var layers = new LayerCollection();
        var layer0 = layers.Add(LayerKind.RecordedAV, "a.mkv");
        layers.Add(LayerKind.RecordedAV, "b.mkv");
        layer0.MixParameters.Solo = true;

        var engine = new MixEngine(NoHostedPluginsAvailable.Instance);

        var guideLayers = RecordTakeRules.GuideLayers(layers.Layers, retakeLayerId: 0);
        var inputs = guideLayers.Select(l => new MixLayerInput(l.LayerId, Sine(440, 0.3f, sampleRate), sampleRate, l.MixParameters)).ToList();
        var mix = engine.BuildMix(inputs, sampleRate);
        float peak = ReadAll(mix, sampleRate).Max(Math.Abs);

        Assert.True(peak > 0.01f, $"Expected the guide to be audible; peak was {peak}.");

        // Contrast half: "mute instead of exclude" would keep anySolo true (layer0.Solo) and mute
        // layer0 itself, silencing the whole guide (spec subtlety (b)). Guards against a future
        // "simplification" from exclusion to muting.
        layer0.MixParameters.Mute = true;
        var mutedInputs = layers.Layers.Select(l => new MixLayerInput(l.LayerId, Sine(440, 0.3f, sampleRate), sampleRate, l.MixParameters)).ToList();
        var mutedMix = engine.BuildMix(mutedInputs, sampleRate);
        float mutedPeak = ReadAll(mutedMix, sampleRate).Max(Math.Abs);

        Assert.Equal(0f, mutedPeak, 6);
    }
}
