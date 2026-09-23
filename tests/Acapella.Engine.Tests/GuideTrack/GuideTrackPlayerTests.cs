using Acapella.Engine.GuideTrack;
using Acapella.Engine.Mix;
using Xunit;

namespace Acapella.Engine.Tests.GuideTrack;

/// <summary>Bug audit #12: RecordSetupWindow's new catch around GuideTrackPlayer.Play relies on
/// Dispose() being safe to call after a failed Play() -- this proves that directly, without any
/// WPF or dialog involved, using a device id that was never registered at all (so this throws at
/// the GetDevice line specifically; a real unplugged/disabled endpoint can instead throw one line
/// later, at the WasapiOut constructor or Init -- see the doc's root cause 1). Either way,
/// GuideTrackPlayer's _output field is never assigned, so Dispose() -> Stop() is a safe no-op.</summary>
public class GuideTrackPlayerTests
{
    [Fact]
    public void Play_WithNonexistentDeviceId_ThrowsAndLeavesNoOutputToDispose()
    {
        var player = new GuideTrackPlayer();
        var guideAudio = new ArraySampleProvider(new float[10], 44100);

        Assert.ThrowsAny<Exception>(() => player.Play("{00000000-0000-0000-0000-000000000000}", guideAudio));

        // Characterizes the assumption RecordSetupWindow's catch block relies on (§Proposed fix):
        // cleanup after a failed Play() must not itself throw.
        player.Dispose();
    }
}
