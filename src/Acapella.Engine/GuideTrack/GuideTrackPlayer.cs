using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Acapella.Engine.GuideTrack;

/// <summary>
/// Plays back prior layers' audio (summed) through the output device as a guide track while a
/// new layer is being captured. Starts with zero added delay: the performer's own round-trip
/// output+input latency already delays what lands in the recording relative to the guide, and
/// adding another wait here would only make that misalignment worse. Instead, the calibrated
/// round-trip is recorded as the new layer's CalibratedOffsetMs and trimmed from the head of the
/// recording at mix/preview/export time (see LayerModel.GetShiftMs, AudioShiftHelper).
/// </summary>
public class GuideTrackPlayer : IDisposable
{
    private WasapiOut? _output;

    public void Play(string outputDeviceId, ISampleProvider guideAudio)
    {
        using var enumerator = new MMDeviceEnumerator();
        var outputDevice = enumerator.GetDevice(outputDeviceId);

        _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, 50);
        _output.Init(guideAudio);
        _output.Play();
    }

    public void Stop()
    {
        _output?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _output?.Dispose();
    }
}
