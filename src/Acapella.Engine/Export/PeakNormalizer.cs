namespace Acapella.Engine.Export;

/// <summary>
/// Scales a full mixdown buffer down (never up) so its peak sample never exceeds +-1.0 full
/// scale, preventing a hard clip on the AAC encode. Only applies when actually clipping, so an
/// already-safe mix is left untouched.
/// </summary>
public static class PeakNormalizer
{
    public static void NormalizeIfClipping(float[] samples)
    {
        if (samples.Length == 0) return;

        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = Math.Abs(samples[i]);
            if (abs > peak) peak = abs;
        }

        if (peak <= 1.0f) return;

        float gain = 1.0f / peak;
        for (int i = 0; i < samples.Length; i++)
            samples[i] *= gain;
    }
}
