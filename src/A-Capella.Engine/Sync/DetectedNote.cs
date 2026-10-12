namespace A_Capella.Engine.Sync;

/// <summary>One note found in a recording: where it starts and how long it lasts (seconds from the
/// start of the recording), its average frequency in Hz (0 when it has no clear pitch) and a 0..1
/// loudness.</summary>
public readonly record struct DetectedNote(double StartSeconds, double DurationSeconds, float FrequencyHz, float Volume);

/// <summary>Finds the notes in a recording, or returns null when it can't.</summary>
public interface INoteSource
{
    IReadOnlyList<DetectedNote>? DetectNotes(float[] monoSamples, int sampleRate, string cacheKey);
}
