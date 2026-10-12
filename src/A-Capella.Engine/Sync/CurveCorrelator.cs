namespace A_Capella.Engine.Sync;

/// <summary>Normalised comparison of two onset curves, for lining up recordings whose waveforms differ
/// (different singers) but whose rhythm is shared.</summary>
public static class CurveCorrelator
{
    /// <summary>Scores every lag in [minLag, maxLag]: the correlation of reference[i] with signal[i + lag]
    /// over the overlap, divided by the overlap's energies (so a score is comparable between lags and
    /// stays within [-1, 1]). A positive best lag means the signal lags the reference. Lags whose overlap
    /// is shorter than minOverlap score -1, so a few chance coincidences at the edges cannot win.</summary>
    public static double[] Score(float[] reference, float[] signal, int minLag, int maxLag, int minOverlap)
    {
        var refEnergy = PrefixEnergy(reference);
        var sigEnergy = PrefixEnergy(signal);
        var scores = new double[maxLag - minLag + 1];

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            int start = Math.Max(0, -lag);
            int end = Math.Min(reference.Length, signal.Length - lag);
            double score = -1;
            if (end - start >= Math.Max(1, minOverlap))
            {
                double sum = 0;
                for (int i = start; i < end; i++)
                    sum += reference[i] * (double)signal[i + lag];

                double denom = Math.Sqrt((refEnergy[end] - refEnergy[start]) * (sigEnergy[end + lag] - sigEnergy[start + lag]));
                score = denom > 0 ? sum / denom : 0;
            }
            scores[lag - minLag] = score;
        }
        return scores;
    }

    /// <summary>The best lag and score, plus the best score found more than excludeLags away from it
    /// (how strong the runner-up is).</summary>
    public static (int Lag, double Score, double RunnerUp) Best(double[] scores, int minLag, int excludeLags)
    {
        int bestIndex = 0;
        for (int i = 1; i < scores.Length; i++)
            if (scores[i] > scores[bestIndex]) bestIndex = i;

        double runnerUp = -1;
        for (int i = 0; i < scores.Length; i++)
            if (Math.Abs(i - bestIndex) > excludeLags && scores[i] > runnerUp) runnerUp = scores[i];

        return (bestIndex + minLag, scores[bestIndex], runnerUp);
    }

    private static double[] PrefixEnergy(float[] x)
    {
        var prefix = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++) prefix[i + 1] = prefix[i] + x[i] * (double)x[i];
        return prefix;
    }
}
