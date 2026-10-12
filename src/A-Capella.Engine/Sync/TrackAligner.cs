namespace A_Capella.Engine.Sync;

public enum AlignMethod { Notes, Energy }

/// <summary>OffsetMs is how late the signal runs against the reference (negative: early). Trusted is
/// false when no single offset clearly beat the alternatives; callers should then leave the layer alone.</summary>
public sealed record AlignmentResult(double OffsetMs, double Confidence, bool Trusted, AlignMethod Method);

/// <summary>
/// Finds how far one recording is shifted against another when both follow the same song. Voices
/// differ, so the raw waveforms are not compared: each recording becomes a curve of note starts
/// (from the plugin's note detection when available, otherwise from loudness jumps) and the curves
/// are matched, coarsely over a wide range and then finely around the winner.
/// </summary>
public static class TrackAligner
{
    private const double AnalysisSeconds = 90;
    private const int CoarseRate = 100;
    private const int FineRate = 1000;
    private const int FineWindowMs = 30;
    private const double BumpSigmaSeconds = 0.010;
    private const int MinNotes = 4;
    private const double MinScore = 0.2;
    private const double MinMargin = 1.25;
    private const double RunnerUpExclusionSeconds = 0.15;

    public static AlignmentResult Align(float[] reference, float[] signal, int sampleRate,
        INoteSource? noteSource = null, string referenceKey = "reference", string signalKey = "signal", double maxLagSeconds = 30)
    {
        var referenceNotes = noteSource?.DetectNotes(Window(reference, sampleRate), sampleRate, referenceKey);
        // No point analysing the signal when the reference gave nothing usable.
        var signalNotes = referenceNotes is { Count: >= MinNotes }
            ? noteSource?.DetectNotes(Window(signal, sampleRate), sampleRate, signalKey)
            : null;
        bool useNotes = referenceNotes is { Count: >= MinNotes } && signalNotes is { Count: >= MinNotes };

        float[] Curve(float[] samples, IReadOnlyList<DetectedNote>? notes, int rate) =>
            useNotes ? CurveFromNotes(notes!, rate) : CurveFromAudio(Window(samples, sampleRate), sampleRate, rate);

        var method = useNotes ? AlignMethod.Notes : AlignMethod.Energy;

        // Coarse: 10 ms steps over the whole allowed range.
        var refCoarse = Curve(reference, referenceNotes, CoarseRate);
        var sigCoarse = Curve(signal, signalNotes, CoarseRate);
        int maxLag = (int)(maxLagSeconds * CoarseRate);
        int minOverlap = Math.Max(2 * CoarseRate, (int)(0.3 * Math.Min(refCoarse.Length, sigCoarse.Length)));
        var scores = CurveCorrelator.Score(refCoarse, sigCoarse, -maxLag, maxLag, minOverlap);
        var (coarseLag, best, runnerUp) = CurveCorrelator.Best(scores, -maxLag, (int)(RunnerUpExclusionSeconds * CoarseRate));

        bool trusted = best >= MinScore && (runnerUp <= 0 || best >= MinMargin * runnerUp);

        // Fine: 1 ms steps just around the coarse winner.
        var refFine = Curve(reference, referenceNotes, FineRate);
        var sigFine = Curve(signal, signalNotes, FineRate);
        int step = FineRate / CoarseRate;
        int centre = coarseLag * step;
        var fineScores = CurveCorrelator.Score(refFine, sigFine, centre - FineWindowMs, centre + FineWindowMs, minOverlap * step);
        var (fineLag, _, _) = CurveCorrelator.Best(fineScores, centre - FineWindowMs, 0);

        return new AlignmentResult(fineLag * 1000.0 / FineRate, Math.Max(0, best), trusted, method);
    }

    private static float[] Window(float[] samples, int sampleRate) =>
        samples.Length <= AnalysisSeconds * sampleRate ? samples : samples[..(int)(AnalysisSeconds * sampleRate)];

    /// <summary>One smooth bump per note start, as tall as the note is loud.</summary>
    private static float[] CurveFromNotes(IReadOnlyList<DetectedNote> notes, int rate)
    {
        var curve = new float[(int)(AnalysisSeconds * rate) + 1];
        double sigma = BumpSigmaSeconds * rate;
        int reach = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        foreach (var note in notes)
        {
            double centre = note.StartSeconds * rate;
            double weight = Math.Clamp(note.Volume, 0.2f, 1f);
            for (int i = Math.Max(0, (int)centre - reach); i <= (int)centre + reach && i < curve.Length; i++)
            {
                double d = (i - centre) / sigma;
                curve[i] += (float)(weight * Math.Exp(-0.5 * d * d));
            }
        }
        return Centred(curve);
    }

    /// <summary>Loudness jumps: rises in the log level from one short frame to the next.</summary>
    private static float[] CurveFromAudio(float[] samples, int sampleRate, int rate)
    {
        int hop = Math.Max(1, sampleRate / rate);
        int frames = samples.Length / hop;
        var curve = new float[Math.Max(frames, 1)];
        double previous = 0;
        for (int f = 0; f < frames; f++)
        {
            double energy = 0;
            for (int i = f * hop; i < (f + 1) * hop; i++) energy += samples[i] * (double)samples[i];
            double level = Math.Log10(1e-6 + Math.Sqrt(energy / hop));
            if (f > 0) curve[f] = (float)Math.Max(0, level - previous);
            previous = level;
        }

        // Smooth over ~10 ms so a one-sample difference between recordings cannot separate them.
        int smooth = Math.Max(1, rate / 100);
        if (smooth > 1)
        {
            var smoothed = new float[curve.Length];
            for (int i = 0; i < curve.Length; i++)
            {
                double sum = 0;
                int n = 0;
                for (int k = Math.Max(0, i - smooth / 2); k <= Math.Min(curve.Length - 1, i + smooth / 2); k++) { sum += curve[k]; n++; }
                smoothed[i] = (float)(sum / n);
            }
            curve = smoothed;
        }
        return Centred(curve);
    }

    private static float[] Centred(float[] curve)
    {
        double mean = curve.Length > 0 ? curve.Average() : 0;
        for (int i = 0; i < curve.Length; i++) curve[i] -= (float)mean;
        return curve;
    }
}
