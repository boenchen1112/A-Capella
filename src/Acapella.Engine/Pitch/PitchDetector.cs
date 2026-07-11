namespace Acapella.Engine.Pitch;

/// <summary>
/// YIN pitch detection algorithm (de Cheveigne &amp; Kawahara, 2002). Pure managed implementation
/// (no external dependency) since the build plan only requires "a pitch-tracking method", not a
/// specific library.
/// </summary>
public static class PitchDetector
{
    private const double DefaultThreshold = 0.15;

    /// <summary>
    /// Detects the fundamental frequency of a single analysis window. Returns null if no
    /// sufficiently periodic pitch is found (silence/noise/unvoiced).
    /// </summary>
    public static double? DetectPitchHz(ReadOnlySpan<float> samples, int sampleRate, double minHz = 60, double maxHz = 1000)
    {
        int maxLag = (int)(sampleRate / minHz);
        int minLag = (int)(sampleRate / maxHz);
        int halfLength = samples.Length / 2;
        maxLag = Math.Min(maxLag, halfLength - 1);

        if (maxLag <= minLag)
            return null;

        var difference = new double[maxLag + 1];

        // Step 1+2: difference function
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i < halfLength; i++)
            {
                double delta = samples[i] - samples[i + lag];
                sum += delta * delta;
            }
            difference[lag] = sum;
        }

        // Step 3: cumulative mean normalized difference function
        var cmnd = new double[maxLag + 1];
        cmnd[0] = 1.0;
        double runningSum = 0;
        for (int lag = 1; lag <= maxLag; lag++)
        {
            runningSum += difference[lag];
            cmnd[lag] = difference[lag] * lag / runningSum;
        }

        // Step 4: absolute threshold - find first dip below threshold
        int taoEstimate = -1;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            if (cmnd[lag] < DefaultThreshold)
            {
                while (lag + 1 <= maxLag && cmnd[lag + 1] < cmnd[lag])
                    lag++;
                taoEstimate = lag;
                break;
            }
        }

        if (taoEstimate == -1)
            return null;

        // Step 5: parabolic interpolation for sub-sample precision
        double betterTao = taoEstimate;
        if (taoEstimate > 0 && taoEstimate < maxLag)
        {
            double s0 = cmnd[taoEstimate - 1];
            double s1 = cmnd[taoEstimate];
            double s2 = cmnd[taoEstimate + 1];
            double denom = 2 * s1 - s2 - s0;
            if (Math.Abs(denom) > 1e-10)
                betterTao = taoEstimate + (s2 - s0) / (2 * denom);
        }

        if (betterTao <= 0)
            return null;

        return sampleRate / betterTao;
    }
}
