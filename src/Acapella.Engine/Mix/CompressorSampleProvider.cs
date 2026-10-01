using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>
/// Feed-forward downward compressor: envelope-follows the input, and above threshold applies
/// gain reduction at the given ratio (soft-kneed over kneeDb around the threshold).
/// </summary>
public class CompressorSampleProvider : ISampleProvider
{
    private const float AttackMs = 5f;
    private const float ReleaseMs = 80f;

    private readonly ISampleProvider _source;
    private readonly float _thresholdDb;
    private readonly float _ratio;
    private readonly float _kneeDb;
    private readonly float _attackCoeff;
    private readonly float _releaseCoeff;
    private float _envelopeDb = -120f;

    public CompressorSampleProvider(ISampleProvider source, float thresholdDb, float ratio, float kneeDb = 6f)
    {
        _source = source;
        _thresholdDb = thresholdDb;
        _ratio = Math.Max(1f, ratio);
        _kneeDb = Math.Max(0f, kneeDb);
        _attackCoeff = (float)Math.Exp(-1.0 / (source.WaveFormat.SampleRate * AttackMs / 1000.0));
        _releaseCoeff = (float)Math.Exp(-1.0 / (source.WaveFormat.SampleRate * ReleaseMs / 1000.0));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);

        for (int i = 0; i < read; i++)
        {
            float sample = buffer[offset + i];
            float inputDb = LinearToDb(Math.Abs(sample));

            _envelopeDb = inputDb > _envelopeDb
                ? _attackCoeff * _envelopeDb + (1 - _attackCoeff) * inputDb
                : _releaseCoeff * _envelopeDb + (1 - _releaseCoeff) * inputDb;

            float gainReductionDb = ComputeGainReductionDb(_envelopeDb);
            buffer[offset + i] = sample * DbToLinear(gainReductionDb);
        }

        return read;
    }

    /// <summary>Soft-knee gain computer: below the knee window, no reduction; above it, full
    /// ratio; within it, a quadratic blend between the two (standard soft-knee compressor curve).</summary>
    private float ComputeGainReductionDb(float envelopeDb)
    {
        float overshoot = envelopeDb - _thresholdDb;
        if (_kneeDb <= 0f)
        {
            if (overshoot <= 0f) return 0f;
            return -(overshoot - overshoot / _ratio);
        }

        float halfKnee = _kneeDb / 2f;
        if (overshoot <= -halfKnee) return 0f;
        if (overshoot >= halfKnee) return -(overshoot - overshoot / _ratio);

        float kneeBlend = overshoot + halfKnee;
        float compressedExcess = (kneeBlend * kneeBlend) / (2f * _kneeDb) * (1f - 1f / _ratio);
        return -compressedExcess;
    }

    private static float LinearToDb(float linear) => linear <= 0f ? -120f : (float)(20 * Math.Log10(linear));
    private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
}
