using Acapella.Engine.Sync;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Sync;

public class PcmConverterTests
{
    [Fact]
    public void BytesToFloatSamples_Float32_ConvertsCorrectly()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        var bytes = BitConverter.GetBytes(0.5f);

        var samples = PcmConverter.BytesToFloatSamples(bytes, bytes.Length, format);

        Assert.Equal(new float[] { 0.5f }, samples);
    }

    [Fact]
    public void BytesToFloatSamples_Int16_ConvertsCorrectly()
    {
        var format = new WaveFormat(44100, 16, 1);
        var bytes = BitConverter.GetBytes((short)16384); // 0.5 scaled

        var samples = PcmConverter.BytesToFloatSamples(bytes, bytes.Length, format);

        Assert.Single(samples);
        Assert.Equal(0.5f, samples[0], 3);
    }

    [Fact]
    public void BytesToFloatSamples_Int24_ConvertsCorrectlyAndSignExtends()
    {
        var format = new WaveFormat(44100, 24, 1);

        // Positive value: 4194304 = 0x400000 -> half of full-scale 0x800000.
        var positiveBytes = new byte[] { 0x00, 0x00, 0x40 };
        var positiveSamples = PcmConverter.BytesToFloatSamples(positiveBytes, 3, format);
        Assert.Equal(0.5f, positiveSamples[0], 3);

        // Negative value: -4194304 = 0xC00000 in 24-bit two's complement.
        var negativeBytes = new byte[] { 0x00, 0x00, 0xC0 };
        var negativeSamples = PcmConverter.BytesToFloatSamples(negativeBytes, 3, format);
        Assert.Equal(-0.5f, negativeSamples[0], 3);
    }

    [Fact]
    public void BytesToFloatSamples_Int32_ConvertsCorrectly()
    {
        var format = new WaveFormat(44100, 32, 1);
        var bytes = BitConverter.GetBytes(1073741824); // 2^30 = 0.5 scaled

        var samples = PcmConverter.BytesToFloatSamples(bytes, bytes.Length, format);

        Assert.Single(samples);
        Assert.Equal(0.5f, samples[0], 3);
    }

    /// <summary>
    /// Regression test for a real bug: unsupported capture formats (e.g. a device delivering
    /// 24-bit PCM) previously fell through to an all-zero buffer with no error, silently
    /// "succeeding" calibration with a garbage offset instead of failing loudly.
    /// </summary>
    [Fact]
    public void BytesToFloatSamples_UnsupportedFormat_Throws()
    {
        var format = WaveFormat.CreateALawFormat(8000, 1);
        var bytes = new byte[] { 0x01 };

        Assert.Throws<NotSupportedException>(() => PcmConverter.BytesToFloatSamples(bytes, bytes.Length, format));
    }
}
