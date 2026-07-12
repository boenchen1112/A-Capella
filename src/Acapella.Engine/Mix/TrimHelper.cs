namespace Acapella.Engine.Mix;

/// <summary>
/// Applies a layer's trim in/out points (LayerModel.TrimStartMs/TrimEndMs) to decoded audio.
/// Trim is independent of sync (AudioShiftHelper): trim selects which portion of the source
/// plays; shift is applied afterward to align that portion in time with other layers.
/// </summary>
public static class TrimHelper
{
    public static float[] ApplyTrim(float[] samples, double trimStartMs, double? trimEndMs, int sampleRate)
    {
        int startSample = Math.Clamp((int)Math.Round(trimStartMs / 1000.0 * sampleRate), 0, samples.Length);
        int endSample = trimEndMs is double end
            ? Math.Clamp((int)Math.Round(end / 1000.0 * sampleRate), startSample, samples.Length)
            : samples.Length;

        return startSample >= endSample ? Array.Empty<float>() : samples[startSample..endSample];
    }
}
