using A_Capella.Engine.Mix;
using A_Capella.Engine.Sync;

namespace A_Capella.Engine.Tests.Sync;

public class TrackAlignerTests
{
    private const int SampleRate = 44100;

    /// <summary>A phrase rhythm: note starts with varied gaps, the way a sung line is.</summary>
    private static double[] Rhythm(int seed, double seconds)
    {
        var random = new Random(seed);
        var starts = new List<double>();
        for (double t = 1.0; t < seconds - 2; t += 0.35 + random.NextDouble() * 1.4)
            starts.Add(t);
        return starts.ToArray();
    }

    /// <summary>Detected notes for a track whose rhythm is shifted by shiftSeconds.</summary>
    private static List<DetectedNote> NotesAt(double[] rhythm, double shiftSeconds) =>
        rhythm.Select((t, i) => new DetectedNote(t + shiftSeconds, 0.3, 200 + 20 * (i % 5), 0.8f)).ToList();

    /// <summary>Rendered voice: a harmonic tone burst per note with a soft attack and silence between
    /// bursts, in a different pitch pattern for each voice.</summary>
    private static float[] Voice(double[] rhythm, double shiftSeconds, double seconds, double baseHz, float level = 0.3f)
    {
        var samples = new float[(int)(seconds * SampleRate)];
        for (int n = 0; n < rhythm.Length; n++)
        {
            double start = rhythm[n] + shiftSeconds;
            double length = Math.Min(0.3, (n + 1 < rhythm.Length ? rhythm[n + 1] - rhythm[n] : 0.3) - 0.05);
            double hz = baseHz * (1 + 0.12 * (n % 4));
            int from = (int)(start * SampleRate), to = (int)((start + length) * SampleRate);
            for (int i = Math.Max(0, from); i < to && i < samples.Length; i++)
            {
                double t = (double)i / SampleRate;
                double env = Math.Min(1, (t - start) / 0.01) * Math.Min(1, (start + length - t) / 0.03);
                samples[i] = (float)(level * env * (Math.Sin(2 * Math.PI * hz * t) + 0.4 * Math.Sin(4 * Math.PI * hz * t)));
            }
        }
        return samples;
    }

    private sealed class FakeNotes : INoteSource
    {
        private readonly Dictionary<string, IReadOnlyList<DetectedNote>> _byKey = new();
        public FakeNotes Add(string key, IReadOnlyList<DetectedNote> notes) { _byKey[key] = notes; return this; }
        public IReadOnlyList<DetectedNote>? DetectNotes(float[] monoSamples, int sampleRate, string cacheKey) =>
            _byKey.TryGetValue(cacheKey, out var notes) ? notes : null;
    }

    [Theory]
    [InlineData(1.234)]
    [InlineData(-3.5)]
    [InlineData(0.0)]
    [InlineData(17.777)]
    public void NotePath_RecoversTheOffset_ForDifferentVoicesSharingARhythm(double shiftSeconds)
    {
        var rhythm = Rhythm(7, 80);
        var reference = Voice(rhythm, 0, 85, 220);
        var signal = Voice(rhythm, shiftSeconds, 85, 330);
        var notes = new FakeNotes().Add("ref", NotesAt(rhythm, 0)).Add("sig", NotesAt(rhythm, shiftSeconds));

        var result = TrackAligner.Align(reference, signal, SampleRate, notes, "ref", "sig");

        Assert.True(result.Trusted);
        Assert.Equal(AlignMethod.Notes, result.Method);
        Assert.InRange(result.OffsetMs, shiftSeconds * 1000 - 2, shiftSeconds * 1000 + 2);
    }

    [Theory]
    [InlineData(1.234)]
    [InlineData(-3.5)]
    public void EnergyFallback_RecoversTheOffset_WhenNoNoteSourceIsAvailable(double shiftSeconds)
    {
        var rhythm = Rhythm(11, 80);
        var reference = Voice(rhythm, 0, 85, 220);
        var signal = Voice(rhythm, shiftSeconds, 85, 330, level: 0.15f);   // quieter, different voice

        var result = TrackAligner.Align(reference, signal, SampleRate);

        Assert.True(result.Trusted);
        Assert.Equal(AlignMethod.Energy, result.Method);
        Assert.InRange(result.OffsetMs, shiftSeconds * 1000 - 10, shiftSeconds * 1000 + 10);
    }

    [Fact]
    public void NotePath_JitterInDetectedStarts_StillLandsWithinAFewMilliseconds()
    {
        var rhythm = Rhythm(3, 80);
        var random = new Random(5);
        var jittered = rhythm.Select((t, i) => new DetectedNote(t + 2.0 + (random.NextDouble() - 0.5) * 0.01, 0.3, 200, 0.8f)).ToList();
        var notes = new FakeNotes().Add("ref", NotesAt(rhythm, 0)).Add("sig", jittered);

        var result = TrackAligner.Align(Voice(rhythm, 0, 85, 220), Voice(rhythm, 2.0, 85, 330), SampleRate, notes, "ref", "sig");

        Assert.True(result.Trusted);
        Assert.InRange(result.OffsetMs, 1995, 2005);
    }

    [Fact]
    public void UnrelatedRhythms_AreNotTrusted()
    {
        var reference = Voice(Rhythm(1, 80), 0, 85, 220);
        var signal = Voice(Rhythm(99, 80), 0, 85, 330);
        var notes = new FakeNotes().Add("ref", NotesAt(Rhythm(1, 80), 0)).Add("sig", NotesAt(Rhythm(99, 80), 0));

        Assert.False(TrackAligner.Align(reference, signal, SampleRate, notes, "ref", "sig").Trusted);
        Assert.False(TrackAligner.Align(reference, signal, SampleRate).Trusted);
    }

    [Fact]
    public void Noise_IsNotTrusted()
    {
        var random = new Random(2);
        var noise = Enumerable.Range(0, 60 * SampleRate).Select(_ => (float)(random.NextDouble() - 0.5) * 0.2f).ToArray();
        var voice = Voice(Rhythm(4, 60), 0, 60, 220);

        Assert.False(TrackAligner.Align(voice, noise, SampleRate).Trusted);
    }

    [Fact]
    public void LegatoLine_WithNoLoudnessChange_IsAlignedByNotesButNotByEnergy()
    {
        // A held, constant-level line whose only onsets are pitch changes.
        var rhythm = Rhythm(8, 80);
        float[] Held(double shift, double baseHz)
        {
            var samples = new float[85 * SampleRate];
            double phase = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double t = (double)i / SampleRate - shift;
                int n = t < 0 ? 0 : Math.Max(0, Array.FindLastIndex(rhythm, r => r <= t));
                phase += 2 * Math.PI * baseHz * (1 + 0.12 * (n % 4)) / SampleRate;
                samples[i] = (float)(0.3 * Math.Sin(phase));
            }
            return samples;
        }

        var notes = new FakeNotes().Add("ref", NotesAt(rhythm, 0)).Add("sig", NotesAt(rhythm, 1.5));
        var reference = Held(0, 220);
        var signal = Held(1.5, 330);

        var withNotes = TrackAligner.Align(reference, signal, SampleRate, notes, "ref", "sig");
        var energyOnly = TrackAligner.Align(reference, signal, SampleRate);

        Assert.True(withNotes.Trusted);
        Assert.InRange(withNotes.OffsetMs, 1498, 1502);
        Assert.False(energyOnly.Trusted);
    }

    [Fact]
    public void AppliedOffset_PutsBothVoicesOnTheSameSample()
    {
        const double lateBySeconds = 1.234;
        var rhythm = Rhythm(7, 80);
        var reference = Voice(rhythm, 0, 85, 220);
        var signal = Voice(rhythm, lateBySeconds, 85, 330);
        var notes = new FakeNotes().Add("ref", NotesAt(rhythm, 0)).Add("sig", NotesAt(rhythm, lateBySeconds));
        var result = TrackAligner.Align(reference, signal, SampleRate, notes, "ref", "sig");

        // The layer model subtracts the calibrated offset from the layer's shift.
        var layer = new A_Capella.Engine.Project.LayerModel { LayerId = 1, Kind = A_Capella.Engine.Project.LayerKind.UploadedAudioOnly, SourcePath = "x" };
        layer.CalibratedOffsetMs = result.OffsetMs;
        var aligned = AudioShiftHelper.ApplyShift(signal, layer.GetShiftMs(), SampleRate);

        int FirstLoud(float[] x) => Array.FindIndex(x, s => Math.Abs(s) > 0.02f);
        Assert.InRange(Math.Abs(FirstLoud(aligned) - FirstLoud(reference)), 0, SampleRate / 1000 * 2);
    }
}
