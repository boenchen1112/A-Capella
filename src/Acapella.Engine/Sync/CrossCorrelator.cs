namespace Acapella.Engine.Sync;

public static class CrossCorrelator
{
    /// <summary>
    /// Finds the sample offset at which <paramref name="signal"/> best matches <paramref name="reference"/>.
    /// A positive result means <paramref name="signal"/> lags <paramref name="reference"/> by that many samples.
    /// Searches offsets in [-maxLagSamples, +maxLagSamples].
    /// </summary>
    public static int FindOffsetSamples(float[] reference, float[] signal, int maxLagSamples) =>
        FindOffsetSamplesWithConfidence(reference, signal, maxLagSamples).lagSamples;

    /// <summary>Same search as FindOffsetSamples, but also returns a [0,1]-ish confidence: the
    /// winning lag's correlation normalized by the reference/signal energy in the overlapping
    /// window (1.0 would mean a perfectly scaled match). Used where a caller needs to decide
    /// whether to trust the measured offset at all -- e.g. a headphone-wearing singer whose mic
    /// picks up none of the guide track bleed will correlate near zero regardless of which lag
    /// wins (audit B4's cross-correlation fallback).</summary>
    public static (int lagSamples, double confidence) FindOffsetSamplesWithConfidence(float[] reference, float[] signal, int maxLagSamples)
    {
        int bestLag = 0;
        double bestScore = double.NegativeInfinity;

        for (int lag = -maxLagSamples; lag <= maxLagSamples; lag++)
        {
            double score = ComputeCorrelationAtLag(reference, signal, lag);
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        double confidence = ComputeNormalizedConfidence(reference, signal, bestLag, bestScore);
        return (bestLag, confidence);
    }

    private static double ComputeNormalizedConfidence(float[] reference, float[] signal, int lag, double rawScore)
    {
        int start = Math.Max(0, -lag);
        int end = Math.Min(reference.Length, signal.Length - lag);
        if (end <= start || double.IsNegativeInfinity(rawScore))
            return 0.0;

        double refEnergy = 0, sigEnergy = 0;
        for (int i = start; i < end; i++)
        {
            refEnergy += reference[i] * (double)reference[i];
            sigEnergy += signal[i + lag] * (double)signal[i + lag];
        }

        double denom = Math.Sqrt(refEnergy * sigEnergy);
        return denom > 0 ? Math.Clamp(rawScore / denom, 0.0, 1.0) : 0.0;
    }

    private static double ComputeCorrelationAtLag(float[] reference, float[] signal, int lag)
    {
        int start = Math.Max(0, -lag);
        int end = Math.Min(reference.Length, signal.Length - lag);
        if (end <= start)
            return double.NegativeInfinity;

        double sum = 0;
        for (int i = start; i < end; i++)
        {
            sum += reference[i] * signal[i + lag];
        }

        return sum;
    }
}
