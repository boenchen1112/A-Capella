using A_Capella.Engine.Export;
using Xunit;

namespace A_Capella.Engine.Tests.Export;

public class EncodeArgumentsTests
{
    [Fact]
    public void WithMasterLimiter_AddsHeadroomLimiterBeforeAac()
    {
        var args = ExportEngine.EncodeArguments("o.mp4", 64, 64, 10, "a.wav", limited: true);
        int af = args.IndexOf("-af");
        Assert.True(af >= 0);
        Assert.Contains("alimiter", args[af + 1]);
        Assert.True(af < args.IndexOf("-c:a"));
    }

    [Fact]
    public void WithoutMasterLimiter_HasNoAudioFilter()
    {
        var args = ExportEngine.EncodeArguments("o.mp4", 64, 64, 10, "a.wav", limited: false);
        Assert.DoesNotContain("-af", args);
        Assert.Contains("aac", args);
    }
}
