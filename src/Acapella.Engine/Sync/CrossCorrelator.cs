namespace Acapella.Engine.Sync;

public static class CrossCorrelator
{
    /// <summary>
    /// Finds the sample offset at which <paramref name="signal"/> best matches <paramref name="reference"/>.
    /// A positive result means <paramref name="signal"/> lags <paramref name="reference"/> by that many samples.
    /// Searches offsets in [-maxLagSamples, +maxLagSamples].
    /// </summary>
    public static int FindOffsetSamples(float[] reference, float[] signal, int maxLagSamples)
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

        return bestLag;
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
