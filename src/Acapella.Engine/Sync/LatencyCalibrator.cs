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

        capture.DataAvailable += (s, e) =>
        {
            var samples = BytesToFloatSamples(e.Buffer, e.BytesRecorded, captureFormat);
            captured.AddRange(samples);
        };

        using var output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, 50);
        output.Init(ToneGenerator.ToSampleProvider(click, sampleRate));

        capture.StartRecording();
        Thread.Sleep(200);
        output.Play();
        Thread.Sleep(2200);
        output.Stop();
        capture.StopRecording();
        Thread.Sleep(100);

        var capturedMono = DownmixToMono(captured.ToArray(), captureFormat.Channels);
        var capturedResampled = captureFormat.SampleRate == sampleRate
            ? capturedMono
            : Resample(capturedMono, captureFormat.SampleRate, sampleRate);

        // Search for where the short click template best matches within the long captured
        // signal. Passing the short click as the "reference" keeps the inner correlation loop
        // bounded by click.Length (not the full captured-signal length) for every candidate
        // offset — passing a full-length padded array here previously made this O(N*M) and
        // effectively hung on real captures.
        int maxLagSamples = Math.Min(capturedResampled.Length, sampleRate * 1);
        int offsetSamples = CrossCorrelator.FindOffsetSamples(click, capturedResampled, maxLagSamples);

        // Subtract the deliberate pre-roll (capture started 200ms before playback) to get the
        // true output-to-input round-trip latency rather than the raw click position.
        double offsetMs = offsetSamples * 1000.0 / sampleRate - 200.0;
        return offsetMs;
    }

    private static float[] BytesToFloatSamples(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int sampleCount = bytesRecorded / bytesPerSample;
        var samples = new float[sampleCount];

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            Buffer.BlockCopy(buffer, 0, samples, 0, bytesRecorded);
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        }

        return samples;
    }

    private static float[] DownmixToMono(float[] samples, int channels)
    {
        if (channels <= 1) return samples;
        var mono = new float[samples.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += samples[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    private static float[] Resample(float[] samples, int fromRate, int toRate)
    {
        int newLength = (int)((long)samples.Length * toRate / fromRate);
        var result = new float[newLength];
        for (int i = 0; i < newLength; i++)
        {
            double srcPos = i * (double)fromRate / toRate;
            int idx = (int)srcPos;
            result[i] = idx < samples.Length ? samples[idx] : 0f;
        }
        return result;
    }
}
