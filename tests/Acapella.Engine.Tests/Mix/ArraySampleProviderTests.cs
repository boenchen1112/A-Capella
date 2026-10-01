using Acapella.Engine.Mix;

namespace Acapella.Engine.Tests.Mix;

public class ArraySampleProviderTests
{
    /// <summary>
    /// Regression test for M5: Read() previously always returned `count`, padding with infinite
    /// silence past the end of the buffer instead of signaling end-of-stream (0), so preview
    /// playback never finished on its own. Standard ISampleProvider contract: return fewer than
    /// requested (down to 0) once exhausted.
    /// </summary>
    [Fact]
    public void Read_PastEndOfBuffer_ReturnsZeroInsteadOfPaddingSilence()
    {
        var samples = new float[] { 1f, 2f, 3f };
        var provider = new ArraySampleProvider(samples, 44100);
        var buffer = new float[10];

        int firstRead = provider.Read(buffer, 0, 10);
        Assert.Equal(3, firstRead);
        Assert.Equal(new float[] { 1f, 2f, 3f }, buffer[..3]);

        int secondRead = provider.Read(buffer, 0, 10);
        Assert.Equal(0, secondRead);
    }

    [Fact]
    public void Read_PartialThenExact_ReturnsCorrectCounts()
    {
        var samples = new float[] { 1f, 2f, 3f, 4f, 5f };
        var provider = new ArraySampleProvider(samples, 44100);
        var buffer = new float[3];

        Assert.Equal(3, provider.Read(buffer, 0, 3));
        Assert.Equal(2, provider.Read(buffer, 0, 3));
        Assert.Equal(0, provider.Read(buffer, 0, 3));
    }
}
