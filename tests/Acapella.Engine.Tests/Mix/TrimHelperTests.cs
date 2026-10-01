using Acapella.Engine.Mix;

namespace Acapella.Engine.Tests.Mix;

public class TrimHelperTests
{
    [Fact]
    public void ApplyTrim_StartAndEnd_ReturnsOnlyThatSlice()
    {
        int sampleRate = 1000;
        var samples = Enumerable.Range(0, 1000).Select(i => (float)i).ToArray(); // 1s of samples, value == index

        var trimmed = TrimHelper.ApplyTrim(samples, trimStartMs: 100, trimEndMs: 300, sampleRate);

        Assert.Equal(200, trimmed.Length);
        Assert.Equal(100f, trimmed[0]);
        Assert.Equal(299f, trimmed[^1]);
    }

    [Fact]
    public void ApplyTrim_NullEnd_KeepsToEndOfSource()
    {
        int sampleRate = 1000;
        var samples = Enumerable.Range(0, 1000).Select(i => (float)i).ToArray();

        var trimmed = TrimHelper.ApplyTrim(samples, trimStartMs: 900, trimEndMs: null, sampleRate);

        Assert.Equal(100, trimmed.Length);
        Assert.Equal(900f, trimmed[0]);
        Assert.Equal(999f, trimmed[^1]);
    }

    [Fact]
    public void ApplyTrim_StartAtOrAfterEnd_ReturnsEmpty()
    {
        int sampleRate = 1000;
        var samples = Enumerable.Range(0, 1000).Select(i => (float)i).ToArray();

        var trimmed = TrimHelper.ApplyTrim(samples, trimStartMs: 500, trimEndMs: 300, sampleRate);

        Assert.Empty(trimmed);
    }

    [Fact]
    public void ApplyTrim_NoTrim_ReturnsFullSource()
    {
        int sampleRate = 1000;
        var samples = Enumerable.Range(0, 1000).Select(i => (float)i).ToArray();

        var trimmed = TrimHelper.ApplyTrim(samples, trimStartMs: 0, trimEndMs: null, sampleRate);

        Assert.Equal(samples, trimmed);
    }
}
