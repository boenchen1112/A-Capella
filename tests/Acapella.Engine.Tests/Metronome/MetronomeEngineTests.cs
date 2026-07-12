using Acapella.Engine.Metronome;

namespace Acapella.Engine.Tests.Metronome;

public class MetronomeEngineTests
{
    /// <summary>
    /// Regression test for L5: changing BPM mid-beat previously reset the phase discontinuously
    /// (since _sampleIndex % samplesPerBeat is recomputed with the new BPM on the next Read()),
    /// which could fire a click immediately or skip/double a beat. This checks the fix preserves
    /// the fractional position within the current beat: advance partway through a beat, change
    /// BPM, and confirm the next click doesn't fire immediately (which the old bug could cause
    /// depending on the exact phase at the moment of the change).
    /// </summary>
    [Fact]
    public void Bpm_ChangedMidBeat_PreservesPhaseFraction()
    {
        int sampleRate = 44100;
        var metronome = new MetronomeEngine(sampleRate) { Enabled = true, Bpm = 120 };

        double samplesPerBeatAt120 = sampleRate * 60.0 / 120;
        // Advance to the middle of the first beat (well past the initial click's ~30ms window).
        int samplesToMidBeat = (int)(samplesPerBeatAt120 / 2);
        var scratch = new float[samplesToMidBeat];
        metronome.Read(scratch, 0, samplesToMidBeat);

        // Change BPM mid-beat.
        metronome.Bpm = 90;

        // Immediately after the change, we should still be mid-beat (silence), not at a phase
        // that just crossed 0 and re-triggered the click envelope.
        var afterChange = new float[100];
        int read = metronome.Read(afterChange, 0, afterChange.Length);

        Assert.Equal(100, read);
        Assert.All(afterChange, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Bpm_ClampsToValidRange()
    {
        var metronome = new MetronomeEngine();
        metronome.Bpm = 1000;
        Assert.Equal(300, metronome.Bpm);

        metronome.Bpm = 1;
        Assert.Equal(20, metronome.Bpm);
    }
}
