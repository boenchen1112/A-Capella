using Acapella.Engine.Mix;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Acapella.Engine.Tests.Mix;

public class MixEngineTests
{
    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, int length, float amplitude = 0.5f)
    {
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
    public void BuildMix_ChangingGain_ChangesOutputWaveform()
    {
        int sampleRate = 44100;
        var samples = GenerateSineWave(440, sampleRate, sampleRate);
        var engine = new MixEngine();

        var paramsLow = new LayerMixParameters { GainDb = -20f };
        var mixLow = engine.BuildMix(new[] { new MixLayerInput(0, samples, sampleRate, paramsLow) }, sampleRate);
        var outputLow = ReadAll(mixLow, 2000);

        var paramsHigh = new LayerMixParameters { GainDb = 0f };
        var mixHigh = engine.BuildMix(new[] { new MixLayerInput(0, samples, sampleRate, paramsHigh) }, sampleRate);
        var outputHigh = ReadAll(mixHigh, 2000);

        float maxLow = outputLow.Max(Math.Abs);
        float maxHigh = outputHigh.Max(Math.Abs);

        Assert.True(maxHigh > maxLow * 5, $"Expected 0dB output ({maxHigh}) to be much louder than -20dB output ({maxLow}).");
    }

    [Fact]
    public void BuildMix_Mute_ProducesSilence()
    {
        int sampleRate = 44100;
        var samples = GenerateSineWave(440, sampleRate, sampleRate);
        var engine = new MixEngine();

        var parameters = new LayerMixParameters { Mute = true };
        var mix = engine.BuildMix(new[] { new MixLayerInput(0, samples, sampleRate, parameters) }, sampleRate);
        var output = ReadAll(mix, 2000);

        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void BuildMix_Pan_ChangesChannelBalance()
    {
        int sampleRate = 44100;
        var samples = GenerateSineWave(440, sampleRate, sampleRate);
        var engine = new MixEngine();

        var leftParams = new LayerMixParameters { Pan = -1f };
        var leftMix = engine.BuildMix(new[] { new MixLayerInput(0, samples, sampleRate, leftParams) }, sampleRate);
        var leftOutput = ReadAll(leftMix, 2000);

        var rightParams = new LayerMixParameters { Pan = 1f };
        var rightMix = engine.BuildMix(new[] { new MixLayerInput(0, samples, sampleRate, rightParams) }, sampleRate);
        var rightOutput = ReadAll(rightMix, 2000);

        // Interleaved stereo: even indices = left channel, odd = right channel.
        float leftPannedLeftEnergy = SumAbs(leftOutput, 0);
        float leftPannedRightEnergy = SumAbs(leftOutput, 1);
        float rightPannedLeftEnergy = SumAbs(rightOutput, 0);
        float rightPannedRightEnergy = SumAbs(rightOutput, 1);

        Assert.True(leftPannedLeftEnergy > leftPannedRightEnergy * 5);
        Assert.True(rightPannedRightEnergy > rightPannedLeftEnergy * 5);
    }

    private static float SumAbs(float[] interleaved, int channelOffset)
    {
        float sum = 0;
        for (int i = channelOffset; i < interleaved.Length; i += 2)
            sum += Math.Abs(interleaved[i]);
        return sum;
    }

    /// <summary>
    /// Regression test for M2: MixingSampleProvider just sums its inputs with no headroom
    /// management at all, so multiple full-scale layers could exceed +-1.0 and hard-clip on
    /// encode. A fixed 1/sqrt(N) scale is a statistical heuristic (reduces average level for
    /// uncorrelated real-world layers), not a hard peak limiter, so this verifies the mechanism
    /// applies the expected gain rather than asserting an absolute no-clipping guarantee (a true
    /// guarantee only exists post-mixdown via ExportEngine's peak-normalize pass, tested
    /// separately in ExportEngineTests).
    /// </summary>
    [Fact]
    public void BuildMix_MultipleLayers_AppliesHeadroomScale()
    {
        int sampleRate = 44100;
        var engine = new MixEngine();
        double[] frequencies = { 440, 523, 659, 784 };

        var layers = frequencies.Select((freq, i) =>
            new MixLayerInput(i, GenerateSineWave(freq, sampleRate, sampleRate, amplitude: 1.0f), sampleRate, new LayerMixParameters { GainDb = 0f }))
            .ToList();

        // Un-headroomed reference: sum the same layer chains directly, bypassing BuildMix's
        // headroom wrapper, to isolate the effect of the headroom scale itself.
        var rawMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        foreach (var layer in layers)
            rawMixer.AddMixerInput(engine.BuildLayerChain(layer, anySolo: false, sampleRate));
        float rawPeak = ReadAll(rawMixer, sampleRate * 2).Max(Math.Abs);

        var mix = engine.BuildMix(layers, sampleRate);
        float scaledPeak = ReadAll(mix, sampleRate * 2).Max(Math.Abs);

        float expectedHeadroomGain = (float)(1.0 / Math.Sqrt(layers.Count));
        Assert.Equal(rawPeak * expectedHeadroomGain, scaledPeak, 3);
    }

    [Fact]
    public void LayerParameters_SurviveSwitchingBetweenLayers()
    {
        var layerA = new Acapella.Engine.Project.LayerModel { LayerId = 0, Kind = Acapella.Engine.Project.LayerKind.RecordedAV, SourcePath = "a.mkv" };
        var layerB = new Acapella.Engine.Project.LayerModel { LayerId = 1, Kind = Acapella.Engine.Project.LayerKind.RecordedAV, SourcePath = "b.mkv" };

        layerA.MixParameters.GainDb = -6f;
        layerA.MixParameters.Pan = -0.5f;

        // Simulate switching to layer B and changing its parameters.
        layerB.MixParameters.GainDb = 3f;

        // Switch back to layer A: its earlier changes must still be intact.
        Assert.Equal(-6f, layerA.MixParameters.GainDb);
        Assert.Equal(-0.5f, layerA.MixParameters.Pan);
        Assert.Equal(3f, layerB.MixParameters.GainDb);
    }
}
