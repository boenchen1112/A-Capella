namespace Acapella.Engine.Mix;

/// <summary>
/// Applies a layer's total sync shift (see LayerModel.GetShiftMs) to its decoded audio: positive
/// shift pads leading silence (delays the layer), negative shift trims samples from the head
/// (the layer started early relative to others, so its recorded latency is skipped).
/// </summary>
public static class AudioShiftHelper
{
    public static float[] ApplyShift(float[] samples, double shiftMs, int sampleRate)
    {
        int shiftSamples = (int)Math.Round(shiftMs / 1000.0 * sampleRate);
        if (shiftSamples == 0)
            return samples;

        if (shiftSamples > 0)
        {
            var padded = new float[samples.Length + shiftSamples];
            Array.Copy(samples, 0, padded, shiftSamples, samples.Length);
            return padded;
        }

        int skip = -shiftSamples;
        return skip >= samples.Length ? Array.Empty<float>() : samples[skip..];
    }
}
