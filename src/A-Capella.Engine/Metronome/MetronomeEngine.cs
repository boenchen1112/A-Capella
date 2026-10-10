using NAudio.Wave;

namespace A_Capella.Engine.Metronome;

/// <summary>
/// Click-generating sample provider (accented downbeat per the TimeSignature). Intended to be mixed only into the local monitoring/guide
/// output path, never into the signal path being recorded from the mic.
/// </summary>
public class MetronomeEngine : ISampleProvider
{
    private const double ClickFrequencyHz = 1000.0;
    private const double AccentFrequencyHz = 1500.0;
    private const double ClickDurationSeconds = 0.03;

    private double _bpm = 120;
    private TimeSignature _timeSignature = TimeSignature.FourFour;
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
        set => Retime(Math.Clamp(value, 20, 300), _timeSignature);
    }

    /// <summary>The meter: how many clicks make a bar and how often they come. The first click of
    /// each bar is higher-pitched. Changing it mid-take keeps the position within the current beat.</summary>
    public TimeSignature TimeSignature
    {
        get => _timeSignature;
        set => Retime(_bpm, value.Sanitized());
    }

    private double SamplesPerClick(double bpm, TimeSignature signature) =>
        WaveFormat.SampleRate * 60.0 / (bpm * signature.ClicksPerQuarter);

    /// <summary>L5: _sampleIndex % samplesPerClick is recomputed with the new tempo on the very next
    /// Read() call, which jumps the beat's phase discontinuously (a click could fire immediately, or
    /// the current beat could be skipped/doubled). Rescale _sampleIndex to preserve the current
    /// fractional position within the click interval across the change.</summary>
    private void Retime(double newBpm, TimeSignature newSignature)
    {
        if (newBpm != _bpm || newSignature != _timeSignature)
        {
            double oldSamplesPerClick = SamplesPerClick(_bpm, _timeSignature);
            double newSamplesPerClick = SamplesPerClick(newBpm, newSignature);
            double phaseFraction = (_sampleIndex % oldSamplesPerClick) / oldSamplesPerClick;
            // Stay on the same beat of the bar so a tempo tweak doesn't move the downbeat.
            long beatInBar = (long)(_sampleIndex / oldSamplesPerClick) % newSignature.BeatsPerBar;
            _sampleIndex = (long)((beatInBar + phaseFraction) * newSamplesPerClick);
        }
        _bpm = newBpm;
        _timeSignature = newSignature;
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Zeros the beat phase back to the start of a fresh beat (bug audit #11) -- call at
    /// the start of every new take, not just the first. Unlike the Bpm setter's phase-preserving
    /// rescale (L5, Bug_Audit_2026-07-12.md), this deliberately discards the phase: a new take is
    /// not a continuation of a previous, unrelated take's beat grid, so there is nothing to
    /// preserve. Safe to call whether or not Enabled is currently true, and whether or not this is
    /// actually the first take in this dialog (a no-op then, since _sampleIndex is already 0).</summary>
    public void Reset() => _sampleIndex = 0;

    public int Read(float[] buffer, int offset, int count)
    {
        int sampleRate = WaveFormat.SampleRate;
        double samplesPerClick = SamplesPerClick(_bpm, _timeSignature);
        int beatsPerBar = _timeSignature.BeatsPerBar;
        int clickLengthSamples = (int)(sampleRate * ClickDurationSeconds);

        for (int i = 0; i < count; i++)
        {
            float value = 0f;
            if (_enabled)
            {
                double clickIndex = Math.Floor(_sampleIndex / samplesPerClick);
                double posInClick = _sampleIndex - clickIndex * samplesPerClick;
                if (posInClick < clickLengthSamples)
                {
                    bool accent = (long)clickIndex % beatsPerBar == 0;
                    double t = posInClick / sampleRate;
                    double envelope = 1.0 - (posInClick / clickLengthSamples);
                    value = (float)(Math.Sin(2 * Math.PI * (accent ? AccentFrequencyHz : ClickFrequencyHz) * t) * envelope);
                }
            }

            buffer[offset + i] = value;
            _sampleIndex++;
        }

        return count;
    }
}
