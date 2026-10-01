using Acapella.Engine.Settings;

namespace Acapella.Engine.Tests.Settings;

/// <summary>Candidate 4 (Improvement_Proposal_2026-09-24): the last-used camera/mic/metronome
/// choice persists in settings.json across app launches, not just within a session.</summary>
public class SettingsServiceTests
{
    private static SettingsService NewService(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"acapella-settings-test-{Guid.NewGuid()}.json");
        return new SettingsService(path);
    }

    [Fact]
    public void GetLastRecordingSetup_WithNoPriorSave_ReturnsNullNamesAndMetronomeOff()
    {
        var service = NewService(out _);

        var (camera, mic, metronome) = service.GetLastRecordingSetup();

        Assert.Null(camera);
        Assert.Null(mic);
        Assert.False(metronome);
    }

    [Fact]
    public void SetLastRecordingSetup_RoundTripsThroughANewServiceInstance()
    {
        var service = NewService(out var path);

        service.SetLastRecordingSetup(cameraName: "HD Webcam", micName: "USB Microphone", metronomeEnabled: true);

        var reloaded = new SettingsService(path);
        var (camera, mic, metronome) = reloaded.GetLastRecordingSetup();

        Assert.Equal("HD Webcam", camera);
        Assert.Equal("USB Microphone", mic);
        Assert.True(metronome);
    }

    [Fact]
    public void SetLastRecordingSetup_DoesNotClobberExistingLatencyOffsets()
    {
        var service = NewService(out _);
        service.SetLatencyOffsetMs("in1", "out1", 42.5);

        service.SetLastRecordingSetup("Cam", "Mic", metronomeEnabled: false);

        Assert.Equal(42.5, service.GetLatencyOffsetMs("in1", "out1"));
    }
}
