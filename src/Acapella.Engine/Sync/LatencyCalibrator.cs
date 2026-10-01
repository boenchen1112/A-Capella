using NAudio.CoreAudioApi;
using NAudio.Wave;
using Acapella.Engine.Settings;

namespace Acapella.Engine.Sync;

public class LatencyCalibrator
{
    private readonly SettingsService _settingsService;

    public LatencyCalibrator(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Plays a click through <paramref name="outputDeviceId"/> while recording from
    /// <paramref name="loopbackInputDeviceId"/> (e.g. Stereo Mix), cross-correlates the
    /// captured audio against the known click to find round-trip latency, and persists it.
    /// </summary>
    public double CalibrateAndSave(string outputDeviceId, string loopbackInputDeviceId, int sampleRate = 44100)
    {
        double offsetMs = Calibrate(outputDeviceId, loopbackInputDeviceId, sampleRate);
        _settingsService.SetLatencyOffsetMs(loopbackInputDeviceId, outputDeviceId, offsetMs);
        return offsetMs;
    }

    public double Calibrate(string outputDeviceId, string loopbackInputDeviceId, int sampleRate = 44100)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var outputDevice = enumerator.GetDevice(outputDeviceId);
        using var inputDevice = enumerator.GetDevice(loopbackInputDeviceId);

        var click = ToneGenerator.GenerateClick(sampleRate);
        var reference = new float[sampleRate * 2];
        Array.Copy(click, reference, Math.Min(click.Length, reference.Length));

        var captured = new List<float>();
        using var capture = new WasapiCapture(inputDevice);
        var captureFormat = capture.WaveFormat;

        // A fixed 200ms sleep before Play() previously stood in for "capture has actually
        // started" -- but the capture stream's real t=0 is whenever WASAPI delivers its first
        // buffer, not whenever StartRecording() returns, and device activation delay plus sleep
        // timer inaccuracy (~15ms typical) could eat most of the acceptance tolerance. Timestamp
        // the real relationship instead: the elapsed time at the first DataAvailable callback vs.
        // the elapsed time right before Play() is called. Bounded with a timeout (not an
        // unconditional busy-wait) so a device that never raises DataAvailable fails loudly
        // instead of hanging forever.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        double? captureStartElapsedMs = null;
        using var firstBufferSignal = new ManualResetEventSlim(false);

        capture.DataAvailable += (s, e) =>
        {
            if (captureStartElapsedMs is null)
            {
                captureStartElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                firstBufferSignal.Set();
            }
            var samples = PcmConverter.BytesToFloatSamples(e.Buffer, e.BytesRecorded, captureFormat);
            captured.AddRange(samples);
        };

        using var output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, 50);
        output.Init(ToneGenerator.ToSampleProvider(click, sampleRate));

        capture.StartRecording();
        if (!firstBufferSignal.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException($"Capture device '{loopbackInputDeviceId}' never delivered an audio buffer within 5s.");

        double preRollMs = stopwatch.Elapsed.TotalMilliseconds - captureStartElapsedMs!.Value;
        output.Play();
        Thread.Sleep(2200);
        output.Stop();
        capture.StopRecording();
        Thread.Sleep(100);

        var capturedMono = PcmConverter.DownmixToMono(captured.ToArray(), captureFormat.Channels);
        var capturedResampled = PcmConverter.Resample(capturedMono, captureFormat.SampleRate, sampleRate);

        // Search for where the short click template best matches within the long captured
        // signal. Passing the short click as the "reference" keeps the inner correlation loop
        // bounded by click.Length (not the full captured-signal length) for every candidate
        // offset — passing a full-length padded array here previously made this O(N*M) and
        // effectively hung on real captures.
        int maxLagSamples = Math.Min(capturedResampled.Length, sampleRate * 1);
        int offsetSamples = CrossCorrelator.FindOffsetSamples(click, capturedResampled, maxLagSamples);

        // Subtract the measured pre-roll (real elapsed time between capture actually starting
        // and playback starting) to get the true output-to-input round-trip latency rather than
        // the raw click position.
        double offsetMs = offsetSamples * 1000.0 / sampleRate - preRollMs;
        return offsetMs;
    }
}
