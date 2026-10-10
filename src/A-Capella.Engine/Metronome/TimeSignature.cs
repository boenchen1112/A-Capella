namespace A_Capella.Engine.Metronome;

/// <summary>
/// A meter such as 3/4 or 6/8. The metronome clicks once per beat of the bottom number: quarter-note
/// meters click on every quarter at the stated BPM, eighth-note meters (6/8) on every eighth, which
/// is twice as often. The first beat of each bar is accented.
/// </summary>
public readonly record struct TimeSignature(int BeatsPerBar, int BeatUnit)
{
    public static readonly TimeSignature TwoFour = new(2, 4);
    public static readonly TimeSignature ThreeFour = new(3, 4);
    public static readonly TimeSignature FourFour = new(4, 4);
    public static readonly TimeSignature SixEight = new(6, 8);

    public static IReadOnlyList<TimeSignature> Presets { get; } = new[] { TwoFour, ThreeFour, FourFour, SixEight };

    /// <summary>How many clicks happen per quarter note: 1 for x/4, 2 for x/8.</summary>
    public int ClicksPerQuarter => BeatUnit == 8 ? 2 : 1;

    /// <summary>The same meter with nonsense values (from a hand-edited file) replaced by safe ones.</summary>
    public TimeSignature Sanitized() =>
        new(Math.Clamp(BeatsPerBar, 1, 12), BeatUnit == 8 ? 8 : 4);

    public override string ToString() => $"{BeatsPerBar}/{BeatUnit}";
}
