namespace Acapella.Engine.Pitch;

/// <summary>
/// Snaps a detected frequency to the nearest note in a 12-tone equal-tempered chromatic scale.
/// A specific target key/scale (per the build plan's "target scale/key") narrows this further by
/// filtering to only that scale's semitone offsets from the root; v1 defaults to full chromatic.
/// </summary>
public static class ScaleQuantizer
{
    private const double A4Frequency = 440.0;

    public static double NearestNoteFrequency(double inputHz, double a4Frequency = A4Frequency)
    {
        double semitonesFromA4 = 12 * Math.Log2(inputHz / a4Frequency);
        double nearestSemitone = Math.Round(semitonesFromA4);
        return a4Frequency * Math.Pow(2, nearestSemitone / 12.0);
    }
}
