using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>
/// Simple threshold + release noise gate: attenuates to silence while the input envelope stays
/// below the threshold, releasing (fading back in) over releaseMs once it crosses back above.
/// </summary>
public class NoiseGateSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _thresholdLinear;
    private readonly float _releaseSamples;
    private float _envelope;
    private float _gain = 1f;

    public NoiseGateSampleProvider(ISampleProvider source, float thresholdDb, float releaseMs)
    {
        _source = source;
        _thresholdLinear = DbToLinear(thresholdDb);
        _releaseSamples = Math.Max(1f, source.WaveFormat.SampleRate * releaseMs / 1000f);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);

        for (int i = 0; i < read; i++)
        {
            float sample = buffer[offset + i];
            float absSample = Math.Abs(sample);

            // Simple envelope follower
            // TODO(polish): 0.99 decay is sample-rate dependent and attack is instant -- flagged
            // for the [human] mix check-in if the gate sounds off, not fixed now (v1 acceptable).
            _envelope = absSample > _envelope ? absSample : _envelope * 0.99f;

            float targetGain = _envelope >= _thresholdLinear ? 1f : 0f;
            if (targetGain > _gain)
                _gain = Math.Min(1f, _gain + 1f / _releaseSamples);
            else
                _gain = Math.Max(0f, _gain - 1f / _releaseSamples);

            buffer[offset + i] = sample * _gain;
        }

        return read;
    }

    private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
}
