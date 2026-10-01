using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>
/// Brick-wall-ish peak limiter: applies makeup gain, then a fast-attack/release envelope
/// follower clamps any sample that would exceed the ceiling. Sits last in the layer chain (per
/// MixEngine's fixed order) so it caps whatever gain/EQ/pan produced upstream.
/// </summary>
public class LimiterSampleProvider : ISampleProvider
{
    private const float AttackMs = 0.2f;
    private const float ReleaseMs = 50f;

    private readonly ISampleProvider _source;
    private readonly float _ceilingLinear;
    private readonly float _makeupLinear;
    private readonly float _attackCoeff;
    private readonly float _releaseCoeff;
    private float _gain = 1f;

    public LimiterSampleProvider(ISampleProvider source, float ceilingDb, float makeupGainDb)
    {
        _source = source;
        _ceilingLinear = DbToLinear(ceilingDb);
        _makeupLinear = DbToLinear(makeupGainDb);
        _attackCoeff = (float)Math.Exp(-1.0 / (source.WaveFormat.SampleRate * AttackMs / 1000.0));
        _releaseCoeff = (float)Math.Exp(-1.0 / (source.WaveFormat.SampleRate * ReleaseMs / 1000.0));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int channels = _source.WaveFormat.Channels;

        // Stereo-linked envelope (audit B6): processing interleaved L,R,L,R samples with one
        // envelope state per-sample used to give adjacent samples of different channels different
        // gains -- stereo image wobble and added distortion, worst with hard panning (this sits
        // after PanningSampleProvider in the chain). One gain decision per channel-frame, applied
        // identically to every channel in that frame, keeps the stereo image intact.
        for (int frameStart = 0; frameStart + channels <= read; frameStart += channels)
        {
            float peakAbs = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                float sample = buffer[offset + frameStart + ch] * _makeupLinear;
                buffer[offset + frameStart + ch] = sample;
                peakAbs = Math.Max(peakAbs, Math.Abs(sample));
            }

            float targetGain = peakAbs > _ceilingLinear ? _ceilingLinear / peakAbs : 1f;
            _gain = targetGain < _gain
                ? _attackCoeff * _gain + (1 - _attackCoeff) * targetGain
                : _releaseCoeff * _gain + (1 - _releaseCoeff) * targetGain;
            _gain = Math.Min(1f, _gain);

            for (int ch = 0; ch < channels; ch++)
                buffer[offset + frameStart + ch] *= _gain;
        }

        return read;
    }

    private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);
}
