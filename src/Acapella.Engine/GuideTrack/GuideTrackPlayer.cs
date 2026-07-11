using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Acapella.Engine.GuideTrack;

/// <summary>
/// Plays back prior layers' audio (summed) through the output device as a guide track while a
/// new layer is being captured. Applies the calibrated latency offset by delaying guide playback
/// start relative to capture start, so the performer hears the guide arriving in sync with
/// their own monitored input despite round-trip output/input latency.
/// </summary>
public class GuideTrackPlayer : IDisposable
{
    private WasapiOut? _output;

    public void PlayDelayed(string outputDeviceId, ISampleProvider guideAudio, double delayMs)
    {
        using var enumerator = new MMDeviceEnumerator();
        var outputDevice = enumerator.GetDevice(outputDeviceId);

        _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, 50);
        _output.Init(guideAudio);

        if (delayMs > 0)
            Thread.Sleep((int)delayMs);

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
