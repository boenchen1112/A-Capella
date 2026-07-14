using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>
/// Non-destructive pass-through that measures each block it forwards -- inserted into the chain
/// purely for metering (Q1 task 3), never alters the audio. Peak/Rms are per-block snapshots
/// (the most recent Read call), not a decaying envelope: at a ~30Hz UI poll against a steady
/// command-thread block rate, that's frequent enough to read as a live meter without extra
/// smoothing state to get wrong. Fields are volatile so the UI-thread poll and the command-thread
/// Read() can safely run concurrently without a lock.
/// </summary>
public sealed class MeterTapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile float _peakLinear;
    private volatile float _rmsLinear;

    public MeterTapSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Linear peak amplitude (0..~1+) observed in the most recently read block.</summary>
    public float PeakLinear => _peakLinear;

    /// <summary>Linear RMS amplitude observed in the most recently read block.</summary>
    public float RmsLinear => _rmsLinear;

    public float PeakDb => LinearToDb(_peakLinear);
    public float RmsDb => LinearToDb(_rmsLinear);

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            _peakLinear = 0f;
            _rmsLinear = 0f;
            return read;
        }

        float peak = 0f;
        double sumSquares = 0.0;
        for (int i = 0; i < read; i++)
        {
            float sample = buffer[offset + i];
            float abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
            sumSquares += (double)sample * sample;
        }

        _peakLinear = peak;
        _rmsLinear = (float)Math.Sqrt(sumSquares / read);
        return read;
    }

    public static float LinearToDb(float linear) => linear <= 0f ? float.NegativeInfinity : (float)(20.0 * Math.Log10(linear));
}
