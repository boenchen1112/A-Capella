using A_Capella.Engine.Metronome;

namespace A_Capella.Engine.Tests.Metronome;

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

    /// <summary>Bug audit #11: the primary fix (RecordSetupWindow always creates _metronomeOutput
    /// and lets Read()'s live _enabled check gate the sound) depends on this already existing:
    /// flipping Enabled from false to true on an engine that's already mid-stream (as checking the
    /// box mid-take does) must start producing clicks from that point on, with no extra call. This
    /// passes today -- it characterizes the assumption the fix relies on, not a new behavior.</summary>
    [Fact]
    public void Enabled_FlippedTrueMidStream_StartsClickingFromThere()
    {
        var metronome = new MetronomeEngine(44100) { Enabled = false, Bpm = 120 };

        // 1.5 beats of silence while Enabled is false (mirrors a take started with the box unchecked).
        var whileDisabled = new float[33075];
        metronome.Read(whileDisabled, 0, whileDisabled.Length);
        Assert.All(whileDisabled, s => Assert.Equal(0f, s));

        metronome.Enabled = true;   // the checkbox, checked mid-take

        // Read past the next beat boundary (22050 samples/beat) without any other call in between.
        var afterEnabled = new float[23000];
        metronome.Read(afterEnabled, 0, afterEnabled.Length);
        Assert.Contains(afterEnabled, s => s != 0f);
    }

    /// <summary>Bug audit #11: Reset() is what RecordButton_Click now calls before every take.
    /// After it, the click envelope starts again from the top of a beat -- proven by finding a
    /// nonzero sample near the start of the very next block, not merely "some nonzero sample
    /// eventually" (a full beat has one click near its start and silence for the rest).</summary>
    [Fact]
    public void Reset_AfterPriorReads_ZeroesThePhase()
    {
        var metronome = new MetronomeEngine(44100) { Enabled = true, Bpm = 120 };

        // Simulate an aborted take: read partway into a beat, deliberately not landing on a
        // multiple of the beat length (22050 samples/beat at 120 BPM).
        var discard = new float[33075]; // 1.5 beats
        metronome.Read(discard, 0, discard.Length);

        metronome.Reset();

        var nextTake = new float[10];
        metronome.Read(nextTake, 0, nextTake.Length);

        // posInBeat == 0 on the very first sample gives sin(0) == 0f, so the envelope's rise shows
        // up over the first few samples, not necessarily at index 0 -- check the block, not one index.
        Assert.Contains(nextTake, s => s != 0f);
    }

    /// <summary>Bug audit #11, ordering subtlety 1: proves the rejected alternative (re-assigning
    /// Bpm to its own current value) does NOT reset the phase, motivating Reset() as its own method
    /// rather than reusing the Bpm setter's rescale path.</summary>
    [Fact]
    public void ReassigningBpmToItself_DoesNotResetThePhase()
    {
        var metronome = new MetronomeEngine(44100) { Enabled = true, Bpm = 120 };

        var discard = new float[33075];
        metronome.Read(discard, 0, discard.Length);

        metronome.Bpm = metronome.Bpm;   // the tempting-looking but wrong "reset"

        var nextTake = new float[10];
        metronome.Read(nextTake, 0, nextTake.Length);

        Assert.All(nextTake, s => Assert.Equal(0f, s));   // still mid-beat: not fixed by this
    }

    /// <summary>Bug audit #11, ordering subtlety 3: Reset() on a never-read engine is a harmless
    /// no-op, so calling it unconditionally on every RecordButton_Click (including the first take
    /// in a dialog) is safe.</summary>
    [Fact]
    public void Reset_OnFreshEngine_IsANoOp()
    {
        var metronome = new MetronomeEngine(44100) { Enabled = true, Bpm = 120 };
        metronome.Reset();

        var firstBlock = new float[10];
        metronome.Read(firstBlock, 0, firstBlock.Length);

        Assert.Contains(firstBlock, s => s != 0f);   // still clicks immediately, same as without the call
    }

    // ----- Time signature -----

    private const int Rate = 44100;

    /// <summary>Reads bars*beats clicks at 120 BPM (22050 samples per quarter note) and returns, per
    /// click, how many times the waveform crosses zero in the 30 ms click window -- a stand-in for pitch
    /// (about 90 for the 1500 Hz accent, 60 for the 1000 Hz click).</summary>
    private static List<int> ClickCrossings(MetronomeEngine metronome, int clicks, double samplesPerClick)
    {
        var all = new float[(int)(samplesPerClick * clicks)];
        metronome.Read(all, 0, all.Length);
        var crossings = new List<int>();
        for (int c = 0; c < clicks; c++)
        {
            int start = (int)Math.Round(c * samplesPerClick);
            int n = 0;
            for (int i = start + 1; i < start + (int)(Rate * 0.03); i++)
                if ((all[i - 1] < 0) != (all[i] < 0) && all[i] != 0) n++;
            crossings.Add(n);
        }
        return crossings;
    }

    [Theory]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    public void TimeSignature_AccentsOnlyTheFirstBeatOfEachBar(int beats, int unit)
    {
        var metronome = new MetronomeEngine(Rate) { Enabled = true, Bpm = 120, TimeSignature = new TimeSignature(beats, unit) };
        var crossings = ClickCrossings(metronome, beats * 3, samplesPerClick: 22050);

        for (int i = 0; i < crossings.Count; i++)
        {
            if (i % beats == 0) Assert.True(crossings[i] > 75, $"click {i} should be the accent, got {crossings[i]} crossings");
            else Assert.True(crossings[i] < 75, $"click {i} should be a normal click, got {crossings[i]} crossings");
        }
    }

    [Fact]
    public void SixEight_ClicksOnEveryEighth_AccentingOnePerBar()
    {
        var metronome = new MetronomeEngine(Rate) { Enabled = true, Bpm = 120, TimeSignature = TimeSignature.SixEight };
        // x/8 clicks twice per quarter: 11025 samples per click at 120 BPM.
        var crossings = ClickCrossings(metronome, 12, samplesPerClick: 11025);

        Assert.Equal(new[] { true, false, false, false, false, false, true, false, false, false, false, false },
            crossings.Select(c => c > 75));
    }

    [Fact]
    public void ChangingTheSignatureMidBeat_KeepsThePhase_AndTheBeatInTheBar()
    {
        var metronome = new MetronomeEngine(Rate) { Enabled = true, Bpm = 120, TimeSignature = TimeSignature.FourFour };
        // Run into the middle of beat 2 of the bar (index 1): 1.5 quarter notes.
        var scratch = new float[33075];
        metronome.Read(scratch, 0, scratch.Length);

        metronome.TimeSignature = TimeSignature.ThreeFour;

        // Still mid-beat: silence, not a re-triggered click.
        var after = new float[100];
        metronome.Read(after, 0, after.Length);
        Assert.All(after, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void TimeSignature_DefaultsTo4_4_AndSanitizesNonsense()
    {
        var metronome = new MetronomeEngine();
        Assert.Equal(TimeSignature.FourFour, metronome.TimeSignature);

        metronome.TimeSignature = new TimeSignature(99, 3);
        Assert.Equal(new TimeSignature(12, 4), metronome.TimeSignature);

        metronome.TimeSignature = new TimeSignature(-4, 8);
        Assert.Equal(new TimeSignature(1, 8), metronome.TimeSignature);
    }

    [Fact]
    public void Presets_AreTheFourOffered()
    {
        Assert.Equal(new[] { "2/4", "3/4", "4/4", "6/8" }, TimeSignature.Presets.Select(t => t.ToString()));
    }
}
