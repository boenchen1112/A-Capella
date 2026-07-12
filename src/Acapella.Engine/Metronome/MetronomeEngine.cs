using NAudio.Wave;

namespace Acapella.Engine.Metronome;

/// <summary>
/// Click-generating sample provider. Intended to be mixed only into the local monitoring/guide
/// output path, never into the signal path being recorded from the mic.
/// </summary>
public class MetronomeEngine : ISampleProvider
{
    private const double ClickFrequencyHz = 1000.0;
    private const double ClickDurationSeconds = 0.03;

    private double _bpm = 120;
    private bool _enabled;
    private long _sampleIndex;

    public WaveFormat WaveFormat { get; }

    public MetronomeEngine(int sampleRate = 44100)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    }

    public double Bpm
    {
        get => _bpm;
        set
        {
            // L5: _sampleIndex % samplesPerBeat is recomputed with the new BPM on the very next
            // Read() call, which jumps the beat's phase discontinuously (a click could fire
            // immediately, or the current beat could be skipped/doubled). Rescale _sampleIndex to
            // preserve the current fractional position within the beat across the change.
            double newBpm = Math.Clamp(value, 20, 300);
            if (newBpm != _bpm)
            {
                double oldSamplesPerBeat = WaveFormat.SampleRate * 60.0 / _bpm;
                double newSamplesPerBeat = WaveFormat.SampleRate * 60.0 / newBpm;
                double phaseFraction = (_sampleIndex % oldSamplesPerBeat) / oldSamplesPerBeat;
                _sampleIndex = (long)(phaseFraction * newSamplesPerBeat);
            }
            _bpm = newBpm;
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int sampleRate = WaveFormat.SampleRate;
        double samplesPerBeat = sampleRate * 60.0 / _bpm;
        int clickLengthSamples = (int)(sampleRate * ClickDurationSeconds);

        for (int i = 0; i < count; i++)
        {
            float value = 0f;
            if (_enabled)
            {
                double posInBeat = _sampleIndex % samplesPerBeat;
                if (posInBeat < clickLengthSamples)
                {
                    double t = posInBeat / sampleRate;
                    double envelope = 1.0 - (posInBeat / clickLengthSamples);
                    value = (float)(Math.Sin(2 * Math.PI * ClickFrequencyHz * t) * envelope);
                }
            }

            buffer[offset + i] = value;
            _sampleIndex++;
        }

        return count;
    }
}
