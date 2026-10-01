using NAudio.Wave;

namespace Acapella.Engine.Sync;

/// <summary>
/// Produces a single short click/tone burst as float samples, used both for latency calibration
/// playback and as the reference signal for cross-correlation against the captured recording.
/// </summary>
public static class ToneGenerator
{
    public static float[] GenerateClick(int sampleRate, double frequencyHz = 1000.0, double durationSeconds = 0.05)
    {
        int length = (int)(sampleRate * durationSeconds);
        var samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)sampleRate;
            double envelope = 1.0 - (i / (double)length);
            samples[i] = (float)(Math.Sin(2 * Math.PI * frequencyHz * t) * envelope);
        }
        return samples;
    }

    public static ISampleProvider ToSampleProvider(float[] samples, int sampleRate)
        => new PaddedSampleSource(samples, sampleRate, 2.0);

    private class PaddedSampleSource : ISampleProvider
    {
        private readonly float[] _samples;
        private readonly int _totalLength;
        private int _position;

        public PaddedSampleSource(float[] samples, int sampleRate, double totalSeconds)
        {
            _samples = samples;
            _totalLength = (int)(sampleRate * totalSeconds);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count && _position < _totalLength)
            {
                buffer[offset + written] = _position < _samples.Length ? _samples[_position] : 0f;
                _position++;
                written++;
            }
            return written;
        }
    }
}
